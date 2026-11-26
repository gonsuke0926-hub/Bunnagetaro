using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bunnagetaro5.Services;
using Bunnagetaro5.Status;

namespace Bunnagetaro5.Session;

/// <summary>ThrowController が、窓・解答欄・バー側（CaptureSession）へお願いすること</summary>
internal interface IThrowOps
{
    /// <summary>いま開いているキャプチャ窓（無ければ null）</summary>
    CaptureWindow? Capture { get; }
    /// <summary>ぶん投げの開始（撮る前）: 案内を下ろし、前の答えを空にして解答欄を即隠す</summary>
    void BeginRun();
    /// <summary>ストリームを今の result_place の側（解答欄 or 帯）へ流す。撮影中の inside は黙る。
    /// 流したら「本文より思考が先に来ている」か、流さなかったら null</summary>
    bool? Stream(string think, string body, bool capturePending);
    /// <summary>確定した答えを今の result_place の側へ出す</summary>
    void ShowFinal(string text);
    /// <summary>失敗・やめた時: 解答欄も帯も下ろす（内外どちらも）</summary>
    void HideAnswers();
    /// <summary>バーの「ぶん投げる」ボタンの形（ぶん投げ中は「やめる」に化ける）</summary>
    void SetBarBusy(bool busy, bool cancelling);
    /// <summary>内蔵 OCR の段落の位置を解答欄と帯へ渡す（null は位置の反映をやめる）</summary>
    /// <summary>段落（区画）の位置を渡す。区画がある時は、文字を待たずに今の置き場所の窓を出す（撮影中は黙る）</summary>
    void SetLayout(IReadOnlyList<OcrBlock>? blocks, bool capturePending = false);
}

/// <summary>
/// ぶん投げの実行。手動・自動・追加の問い、やめる、失敗の扱い、
/// モデルの準備と前提チェック（ThrowController.Env.cs）、busy/thinking などの状態。
/// 窓や解答欄は知らない（IThrowOps 越し）。UIスレッド上の制約として、
/// busy はワーカーが走る前に UI スレッドで立て、ワーカーは窓そのものへ触れず HWND だけで扱う。
/// </summary>
internal sealed partial class ThrowController
{
    private readonly ConfigStore _cfg;
    private readonly LogStore _store;
    private readonly Pages.LogPage _logPage;
    private readonly Action<string> _setStatus;
    private readonly IThrowOps _ops;
    private readonly Dispatcher _dispatcher;

    /// <summary>状態欄の印（ぶん投げ中・思考中・前提の問題・温め中）。文言を決めるのは StatusModel</summary>
    internal StatusModel Status { get; }

    /// <summary>ぶん投げのやめ要求。バーの「やめる」から叩く</summary>
    private CancellationTokenSource? _cts;
    private bool _cancelRequested;

    /// <summary>busy予約から撮影が終わるまで立つ。立っている間、ストリームの
    /// 反映は黙る（撮影に自分が写らないための段取り）</summary>
    internal bool CapturePending { get; private set; }

    /// <summary>ストリームの書き置き（worker側が「これまでの全文」を置いていく。
    /// UI側が Interlocked で空けながら流す）</summary>
    private string _stPartial = "";
    private string _stThink = "";

    /// <summary>直近で確定した答え（追加の問いが対にするもの。失敗時は空へ戻る。
    /// done の確定の瞬間だけ更新する）</summary>
    private string _answerText = "";

    /// <summary>直前に撮って黒チェックを通った写真（追加の問いで「画像も」OFFの時の
    /// 使い回し。フリーズ済みBitmapSourceなので裏⇄UIの受け渡しは安全）</summary>
    private BitmapSource? _lastImg;

    public ThrowController(ConfigStore cfg, LogStore store, Pages.LogPage logPage, StatusModel status,
                           IThrowOps ops, Action<string> setStatus, Dispatcher dispatcher)
    {
        _cfg = cfg;
        _store = store;
        _logPage = logPage;
        Status = status;
        _ops = ops;
        _setStatus = setStatus;
        _dispatcher = dispatcher;
    }

    // ---------------- 手動ぶん投げ ----------------

    // メイン窓の「ぶん投げる」ボタンは無い（口はバーの「ぶん投げる」・ホットキー・クイック）

    /// <summary>今のぶん投げの種類（manual / auto / follow。ぶん投げ中だけ）。自動の最中だけ画面の変化で打ち切れる</summary>
    private string _runKind = "";

    /// <summary>自動で始めたぶん投げが走っていて、まだやめ要求が出ていない（打ち切って投げ直してよい状態）</summary>
    internal bool AutoRunInFlight => Status.Busy && _runKind == "auto" && !_cancelRequested;

    /// <summary>打ち切りの後、終わりに1回だけ自動で投げ直す印</summary>
    private bool _restartAuto;

    /// <summary>投げ直してよいか（自動トリガが有効か）。MainWindow が結ぶ</summary>
    internal Func<bool> AutoAllowed { get; set; } = () => true;

    /// <summary>
    /// 自動トリガの発火。busy でなければ自動で投げる。自動で始めた最中なら打ち切り、
    /// 終わったあとに1回投げ直す。手動・追加の問いの最中は何もしない（false）
    /// </summary>
    internal bool AutoFire()
    {
        if (!AutoAllowed()) return false;
        if (!Status.Busy)
        {
            TranslateNow("auto");
            return true;
        }
        if (!AutoRunInFlight) return false;
        _restartAuto = true;
        DoCancel();
        return true;
    }

    /// <summary>ぶん投げ1回の文言（ログ条目にそのまま残す1行）</summary>
    private const string CancelText = "（ぶん投げをやめました）";

    /// <summary>
    /// ぶん投げ開始（translate_now）。窓が無ければ案内だけ、予約済みなら待つ。
    /// busy はワーカーが走る前にこのUIスレッドで立てる（連打で2件積まれるのを防ぐ）。
    /// kind はログ条目の source（manual / auto / follow）
    /// </summary>
    public void TranslateNow(string kind = "manual")
    {
        var cap = _ops.Capture;
        if (cap is null)
        {
            _setStatus("先にキャプチャ窓を開いてください");
            return;
        }
        if (Status.Busy)
        {
            _setStatus("ぶん投げ中です…");
            return;
        }
        Status.Busy = true;
        _runKind = kind;
        _cancelRequested = false;
        _restartAuto = false;
        _cts = new CancellationTokenSource();
        // v1.6の順序保証: 予約 → 解答欄を即消す → 撮る。
        // 解答欄は新しいぶん投げなら上から（前の答えのスクロール位置は引き継がない）
        CapturePending = true;
        _stPartial = "";
        _stThink = "";
        Status.Thinking = false;
        _ops.BeginRun();   // 案内を下ろし、前の答えを空にして解答欄を撮影前に即隠す（人が閉じるときのフェードとは別）
        IntPtr hwnd = new WindowInteropHelper(cap).Handle;
        // 設定は積んだ瞬間のスナップショット（ワーカー側で設定を読まない・書かない）
        JsonObject cfg = _cfg.EffectiveConfig();
        var t0 = Stopwatch.GetTimestamp();
        _setStatus(kind == "auto" ? "自動でぶん投げ開始…" : "ぶん投げ開始…");
        var token = _cts.Token;
        _workerTask = Task.Run(() => TranslateWorkerAsync(hwnd, cfg, t0, token, cap, kind));
        SyncBarButton();   // ボタンは「やめる」に化ける（_sync_translate_btn）
    }

    /// <summary>終了時にぶん投げ中ならやめる処理を呼んで、ログの条目を「（ぶん投げをやめました）」で確定する。</summary>
    internal void CancelOnQuit(int waitMs = 1500)
    {
        _quitting = true;
        if (Status.Busy) CancelTranslate();
        var t = _workerTask;
        if (t is not null && !t.IsCompleted)
        {
            try { t.Wait(waitMs); } catch { }
        }
    }

    /// <summary>ぶん投げをやめ要求にする（cancel_translate）。要求済みや未実行のときは何もしない</summary>
    public void CancelTranslate()
    {
        _restartAuto = false;   // 人のやめるは、自動の投げ直しの予約も取り消す
        DoCancel();
    }

    private void DoCancel()
    {
        if (!Status.Busy || _cts is null || _cancelRequested) return;
        _cancelRequested = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _setStatus("中断中…");
        SyncBarButton();   // 「やめる」は押せない状態にする
    }

    /// <summary>
    /// 追加の問いをぶん投げる。前の答え（_answerText）と
    /// たずねる文だけを送る。□「画像も」ONはその瞬間の枠を撮り直して添える
    /// （撮る前に解答欄を消す＝順番の決まりはぶん投げと同じ）。OFFは直前に撮った
    /// 写真を使い回してモデルへは画像を送らない。使い回しが無い時だけ撮り直す
    /// （□は外れたままでもたずねられる）。送れたら true（欄を空にする合図）
    /// </summary>
    public bool FollowupSend(string qRaw, bool withImg)
    {
        string q = (qRaw ?? "").Trim();
        if (q.Length == 0)
        {
            _setStatus("たずねる文を書いてEnter");
            return false;
        }
        if (_answerText.Trim().Length == 0)
        {
            _setStatus("答えが出てからです");
            return false;
        }
        var cap = _ops.Capture;
        if (cap is null)
        {
            _setStatus("キャプチャ窓が開いていません");
            return false;
        }
        if (Status.Busy)
        {
            _setStatus("ぶん投げ中です…");
            return false;
        }
        BitmapSource? reuse = withImg ? null : _lastImg;
        if (reuse is null) withImg = true;   // 使い回す写真が無いので撮り直す

        Status.Busy = true;
        _runKind = "follow";
        _cancelRequested = false;
        _restartAuto = false;
        _cts = new CancellationTokenSource();
        if (withImg)
        {
            // 撮り直す時だけ撮影クリティカル（解答欄を消してから撮る順番）
            CapturePending = true;
            _stPartial = "";
            _stThink = "";
            Status.Thinking = false;
            _ops.BeginRun();
        }
        IntPtr hwnd = new WindowInteropHelper(cap).Handle;
        JsonObject cfg = _cfg.EffectiveConfig();
        var t0 = Stopwatch.GetTimestamp();
        _setStatus("追加の問いを送信中…");
        var token = _cts.Token;
        try
        {
            _workerTask = Task.Run(() => TranslateWorkerAsync(hwnd, cfg, t0, token, cap,
                "follow", q, _answerText, withImg, reuse));
        }
        catch (Exception ex)
        {
            AppLog.Error("follow enqueue failed", ex);
            Status.Busy = false;
            CapturePending = false;
            SyncBarButton();
            _setStatus("追加の問いを始められませんでした（ログ参照）");
            return false;
        }
        SyncBarButton();
        return true;
    }

    /// <summary>バーのぶん投げるボタンの形（ラベルと押せるかどうか）をbusyから作り直す</summary>
    private void SyncBarButton() => _ops.SetBarBusy(Status.Busy, Status.Busy && _cancelRequested);

    /// <summary>
    /// 撮影→条目作成→ぶん投げ→確定（TranslateWorker の手動・追加の問い経路）。
    /// 撮影は待ってから実矩形を取り直して撮る（v1.6/v4.5の順序）。
    /// 待った後に窓が消えていたら古い映像で勝手にぶん投げない。
    /// ワーカーは CaptureWindow の窓そのものへ触れない（HWNDだけでやる）
    /// </summary>
    private async Task TranslateWorkerAsync(IntPtr hwnd, JsonObject cfg, long t0,
                                            CancellationToken ct, CaptureWindow capRef,
                                            string kind = "manual", string followQ = "",
                                            string followPrev = "", bool followWithImg = false,
                                            BitmapSource? reuseImg = null)
    {
        JsonObject? entry = null;
        Task? persistTask = null;
        List<object?>? rectNow = null;
        var diag = new Dictionary<string, object?>();
        // 解答欄へ流す便（inside のときだけ）。payloadは毎回「これまでの全文」。
        // worker側で最短40msに間引き、UIは Interlocked で最新的なものだけ流す
        // ストリームの便は place を問わず必ず付ける。流す先は受け手側でイベントごとに
        // 今の result_place を見て切り替える（実行中に place を切り替えると流れもその場で行き先が変わる）
        Action<string>? onDelta = null;
        Action<string>? onThink = null;
        void Push()
        {
            string p = Interlocked.Exchange(ref _stPartial, "") ?? "";
            string s = Interlocked.Exchange(ref _stThink, "") ?? "";
            if (p.Length == 0 && s.Length == 0) return;
            Ui(() =>
            {
                // 途中で窓を作り直されていたときは古いストリームを新しい解答欄へ流さない
                if (!ReferenceEquals(_ops.Capture, capRef)) return;
                // 流す先（inside=解答欄 / outside=帯。撮影中の inside は黙る）は _ops が今の result_place で決める
                bool? th = _ops.Stream(s, p, CapturePending);
                if (th is { } v) Status.Thinking = v;
            });
        }
        onDelta = v => { Interlocked.Exchange(ref _stPartial, v); Push(); };
        long lastThinkPushMs = long.MinValue;
        onThink = v =>
        {
            Interlocked.Exchange(ref _stThink, v);
            // 思考欄の入れ直し（折り返しの TextBlock に末尾4000字。1回 25〜45ms のレイアウト）は
            // 本文と同じ40ms刻みだとUIスレッドをほぼ塞ぐ。思考は 200ms に1回だけ画面へ（間の分は最新が残り、次の push で出る）
            if (!ShouldPushThink(Environment.TickCount64, ref lastThinkPushMs)) return;
            Push();
        };
        try
        {
            if (!Native.IsWindow(hwnd))
            {
                WarnResult("ぶん投げる前にキャプチャ窓が閉じられました。スキップします");
                return;
            }
            // 解答欄は TranslateNow が撮る前に即隠している。
            // WaitScreenSettled（DwmFlush）はその隠れが画面へ出るのを待てるので十分
            Native.WaitScreenSettled(2);
            if (!Native.IsWindow(hwnd))
            {
                WarnResult("ぶん投げる前にキャプチャ窓が閉じられました。スキップします");
                return;
            }
            BitmapSource img;
            if (kind == "follow" && reuseImg is not null)
            {
                // 追加の問い □「画像も」OFF: 枠を撮り直さず直前の写真を使い回す。
                // 覚えた時に黒チェックを通ったものなので確かめ直しはしない
                img = reuseImg;
            }
            else
            {
                if (!CaptureWindow.TryGetCaptureAreaFor(hwnd, out var area))
                    throw new InvalidOperationException("キャプチャ窓が開いていないか、領域が画面外です");
                using var bmp = ScreenShot.GrabRegion(area);
                img = ScreenShot.ToBitmapSource(bmp);   // 凍結済み・裏スレッドで扱える
                if (ImagePrep.IsBlackFrame(img))
                {
                    WarnResult("画像が真っ黒でした（排他フルスクリーン等は撮れません）。スキップしました");
                    return;
                }
                rectNow = new List<object?> { area.Left, area.Top, area.Right, area.Bottom };
                // 黒チェックを通った写真だけ使い回しに覚える（黒い映像を次の問いへ添えない）
                Ui(() => _lastImg = img);
            }
            {
                // SendPrep と PNG エンコードは 1 回だけ行い、entries.json 等の書き出しは裏へ回して送信開始を先へ進める
                var prepared = ImagePrep.SendPrep(img);
                var sendPng = ScreenShot.PngBytes(prepared);
                diag["raw_size"] = new List<object?> { img.PixelWidth, img.PixelHeight };
                diag["sent_size"] = new List<object?> { prepared.PixelWidth, prepared.PixelHeight };

                // 条目は撮影の瞬間に作る（写真は先に出し、訳文は Finish で後から追いつく）。
                // 追加の問いは source=follow（ログには「追加」と出る）。
                // 使い回しの段は rect_now を残さない
                var extra = new Dictionary<string, object?>
                {
                    ["kind"] = kind,
                    ["rect_now"] = rectNow,
                    ["queued_sec"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3),
                };
                string logModel = TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeBuiltinOcr
                    ? TranslatePipeline.BuiltinOcrModelOf(cfg)
                    : CfgStr(cfg, "model");
                (entry, persistTask) = _store.AddPendingDeferred(
                    img, logModel, kind, extra, prepared, sendPng);
                _ = persistTask.ContinueWith(_ => Ui(() =>
                {
                    _logPage.AppendLiveCard(entry!);
                    // 撮影直後の一言。追加の問いは撮り直さないこともあるので「撮影完了」は出さない
                    _setStatus(kind == "follow" ? "追加の問いをぶん投げ中"
                        : "撮影完了・ぶん投げ中（" + (kind == "auto" ? "自動" : "手動") + "）");
                }), TaskScheduler.Default);
                // 写真の確保まで済んだので解答欄解禁（workerがgrab後pendingを
                // 下ろすタイミングと同じ。以降のdeltaは解答欄へ流れていい）
                Ui(() => CapturePending = false);

                diag["entry_id"] = LogStore.GetStr(entry, "id") ?? "";
                diag["ocr_split_mode"] = CfgBool(cfg, "ocr_split_mode", true);
                // 画像の準備（base64）はモデルへ画像を送る時だけ。
                // 追加の問いで □「画像も」OFFは文字だけでたずねる
                string? b64 = (kind != "follow" || followWithImg)
                    ? Convert.ToBase64String(sendPng)
                    : null;

                // 解答欄へストリーム表示を行い、やめ要求は CancellationToken で伝わる
                string text;
                if (kind == "follow")
                {
                    // 追加の問いは前の答えとたずねる文だけ送る（ぶん投げ文は足さない）。
                    // 写真の添付は □ ON の時だけ。pipeline名・follow_q・prev_charsは
                    // FollowupAsync 側が残す
                    diag["follow"] = new Dictionary<string, object?>
                    {
                        ["q"] = followQ,
                        ["with_image"] = followWithImg,
                        ["prev_chars"] = followPrev.Trim().Length,
                    };
                    text = await TranslatePipeline.FollowupAsync(
                        cfg, followQ, followPrev, followWithImg ? b64 : null,
                        onDelta, onThink, ct, 0.04, diag);
                }
                else
                {
                    // 書き起こし文を訳で上書きする速さを、ログに残る直近の訳の長さから覚える
                    var lenRatio = TranslatePipeline.LearnedLengthRatio(_store.Snapshot(), CfgStr(cfg, "model"));
                    cfg[TranslatePipeline.LengthRatioKey] = lenRatio;
                    diag["len_ratio"] = Math.Round(lenRatio, 3);
                    // 前の答えの位置は外す。位置の反映が ON なら、読んだ段落の位置を本文より先に渡す
                    // （Ui は順に流れるので、位置 → 原文 → 訳の上書き の順に届く）
                    Ui(() => { if (ReferenceEquals(_ops.Capture, capRef)) _ops.SetLayout(null); });
                    text = await TranslatePipeline.TranslateImageAsync(
                        cfg, b64!, onDelta, onThink,
                        ct: ct, deltaMinIntervalSec: 0.04, diag: diag,
                        onLayout: blocks => Ui(() => { if (ReferenceEquals(_ops.Capture, capRef)) _ops.SetLayout(blocks, CapturePending); }));
                }

                diag["total_sec"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
                if (persistTask is not null) { try { await persistTask; } catch { } }
                if (text.Trim().Length == 0)
                {
                    const string empty = "モデルの応答が空でした。モデルとぶん投げ文を確認してください";
                    _store.Finish(entry, empty, "failed", LogStore.ToNode(diag) as JsonObject);
                    // status!=ok の done: 解答欄は下ろす（drainと同じ。書き置きも捨てる）。
                    // place を問わず inside/strip の両方を下ろす（Pythondone failed same）
                    Ui(() =>
                    {
                        _logPage.RefreshOrAppend(entry!);
                        _answerText = "";   // 失敗時は問いの対手を空へ戻す（drainと同じ）
                        _setStatus("ぶん投げ失敗: " + empty);
                        _ops.HideAnswers();
                    });
                    return;
                }
                _store.Finish(entry, text, "ok", LogStore.ToNode(diag) as JsonObject);
                // done ok: 解答欄の仕上げ（keep_view）と clip_answer 自動コピー。
                // 自動コピーは状態欄を書き換えない（完了の流れを邪魔しない）
                Ui(() =>
                {
                    _logPage.RefreshOrAppend(entry!);
                    // 追加の問いが対にする答えは、枠に見えている文字ではなく
                    // ここで確定したもの
                    _answerText = text;
                    // 正常完了時の仕上げは今の place 側の窓へ。窓を作り直されていた時は
                    // 古いstreamの完成をどちらにも出さない
                    if (ReferenceEquals(_ops.Capture, capRef))
                        _ops.ShowFinal(text);
                    // ここまで来た＝ぶん投げが通った。前提の理由（AIサーバー未起動など）はもう事実でないので消す
                    // （起動直後に Ollama がまだ立っていなかった時、ぶん投げ1本で状態欄の文言が元に戻る）
                    Status.EnvProblem = "";
                    // 完了の一言。追加の問いは「追加の問い」と分かるように
                    var kindLabel = kind == "follow" ? "追加" : kind == "auto" ? "自動" : "手動";
                    var doneNote = diag.TryGetValue("truncated", out var tr) && tr is string trReason && trReason.Length > 0
                        ? $"完了（{kindLabel}・上限で打ち切り）"
                        : $"完了（{kindLabel}）";
                    _setStatus(doneNote);
                    // 自動コピーの文言は状態欄を書き換えない（結果の見え方を優先）
                    if (CfgBool(cfg, "clip_answer", false) && text.Trim().Length > 0)
                    {
                        try
                        {
                            Clipboard.SetText(text);
                            AppLog.Info("clip_answer: 答えを自動でクリップボードへ");
                        }
                        catch (Exception ex) { AppLog.Error("clip_answer 自動コピー失敗", ex); }
                    }
                });
            }
        }
        catch (OllamaCancelledException)
        {
            // 「やめる」からのやめ経路。条目には CancelText を残す
            diag["total_sec"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
            diag["cancelled"] = true;
            AppLog.Info("手動ぶん投げをやめました");
            if (entry is not null)
            {
                if (persistTask is not null) { try { await persistTask; } catch { } }
                _store.Finish(entry, CancelText, "failed", LogStore.ToNode(diag) as JsonObject);
                // 中断も done failed 扱い: 解答欄は下ろす（drainと同じ。内外どちらも）
                Ui(() =>
                {
                    _logPage.RefreshOrAppend(entry);
                    _answerText = "";   // やめた答えは問いの対手にしない
                    _setStatus("中断しました");
                    _ops.HideAnswers();
                });
            }
        }
        catch (HttpRequestException ex)
        {
            if (persistTask is not null) { try { await persistTask; } catch { } }
            FailTranslate(entry, $"Ollama に接続できません（{OllamaClient.BaseOf(cfg)}）。" +
                "Ollama を起動するか設定のURLを確認してください", diag, ex);
        }
        catch (TaskCanceledException ex)
        {
            if (persistTask is not null) { try { await persistTask; } catch { } }
            FailTranslate(entry, $"AIサーバーに接続できません（時間切れ: {OllamaClient.BaseOf(cfg)}）", diag, ex);
        }
        catch (Exception ex)
        {
            if (persistTask is not null) { try { await persistTask; } catch { } }
            // 条目本文には素の失敗理由、状態欄の一行だけ「ぶん投げ失敗: 」を冠る（worker._fail）
            FailTranslate(entry, ex.Message, diag, ex);
        }
        finally
        {
            Ui(() =>
            {
                Status.Busy = false;
                _runKind = "";
                _cancelRequested = false;
                CapturePending = false;   // どこで失敗しても取り逃がし無し（backstop）
                Status.Thinking = false;
                var c = _cts;
                _cts = null;
                c?.Dispose();
                SyncBarButton();   // ボタンを「ぶん投げる」へ戻す
                // 自動の打ち切りで終わったら、新しい画面で1回だけ投げ直す
                if (_restartAuto)
                {
                    _restartAuto = false;
                    if (AutoAllowed() && _ops.Capture is not null) TranslateNow("auto");
                }
            });
        }
    }

    /// <summary>条目を作った後の失敗確定（worker._fail と同じ形）</summary>
    private void FailTranslate(JsonObject? entry, string msg,
                               Dictionary<string, object?> diag, Exception ex)
    {
        string eid = entry is null ? "" : LogStore.GetStr(entry, "id") ?? "";
        AppLog.Error($"[{eid}] translate error: {msg}", ex);
        if (entry is not null)
        {
            _store.Finish(entry, msg, "failed", LogStore.ToNode(diag) as JsonObject);
            // 失敗の done: 解答欄は下ろす（drainの done failed 分岐と同じ。内外どちらも）
            Ui(() =>
            {
                _logPage.RefreshOrAppend(entry);
                _answerText = "";   // 失敗時は問いの対手を空へ戻す（drainと同じ）
                _setStatus("ぶん投げ失敗: " + (msg.Length > 60 ? msg[..60] : msg));
                _ops.HideAnswers();
            });
        }
        else
        {
            Ui(() => _setStatus("ぶん投げ失敗: " + (msg.Length > 60 ? msg[..60] : msg)));
        }
    }

    private void WarnResult(string msg)
    {
        AppLog.Info(msg);
        Ui(() => _setStatus(msg));
    }

    private Task? _workerTask;
    private volatile bool _quitting;

    private void Ui(Action a)
    {
        if (_quitting || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.Invoke(a);
    }

    private static string CfgStr(JsonObject o, string k, string def = "") =>
        o[k] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : def;

    private static bool CfgBool(JsonObject o, string k, bool def)
    {
        JsonNode? n = o[k];
        if (n is null) return def;
        return n.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => def,
        };
    }

    /// <summary>思考欄の更新の最短間隔（ms）。測定: 4000字の入れ直しのレイアウトが 1回 25〜45ms</summary>
    internal const int ThinkPushMinMs = 200;

    /// <summary>思考の更新を画面へ出してよいか。前回から <see cref="ThinkPushMinMs"/> 以上たっていれば true で、前回時刻を更新する</summary>
    internal static bool ShouldPushThink(long nowMs, ref long lastMs)
    {
        if (lastMs != long.MinValue && nowMs - lastMs < ThinkPushMinMs) return false;
        lastMs = nowMs;
        return true;
    }
}

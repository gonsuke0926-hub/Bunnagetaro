using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Input;

/// <summary>QuickThrowController が、キャプチャ窓まわり・ぶん投げ側へお願いすること</summary>
internal interface IQuickOps
{
    (CaptureWindow Window, IntPtr Hwnd)? EnsureCaptureForQuick();
    bool CaptureAlive();
    /// <summary>描き始めに案内・旧訳（解答欄・帯）を薄れさせて消す</summary>
    void QuickHidePanels(int px, int py);
    void QuickHideWindow(int px, int py);
    /// <summary>キャプチャ窓とバーを消している最中か</summary>
    bool QuickHidden { get; }
    /// <summary>窓が死んでいた時に「消している」印だけ下ろす</summary>
    void ForgetQuickHidden();
    /// <summary>窓が無い所からクイックで開いて、まだ一度も出していない窓なら閉じて true（取り消し＝押す前へ戻す）</summary>
    bool DiscardQuickOpened();
    void QuickShowWindow(bool fade = false);
    void QuickPlace(int l, int t, int w, int h, bool fade = false);
    void PositionChrome();
    /// <summary>本物の窓が画面に出てから a を呼ぶ（新しい大きさで描けるまで隠している間は待つ）</summary>
    void AfterQuickShown(Action a);
    bool ThrowBusy { get; }
    void ThrowManual();
}

/// <summary>
/// クイックぶん投げ。フックの反映、巡回、四角の追跡、確定・取り消し。
/// 割り当てたマウスボタンの長押し、またはキーを押しているあいだマウスを動かして
/// 四角を描く → 離した瞬間にキャプチャ窓（とバー）をその四角へ寄せてぶん投げる。
/// 入力フックが教えるのは「押した/離した」という点だけ。窓を触るのは
/// 必ずこのUI側の巡回で行う。
/// 四角が quick_min_w/h 未満なら実行せず、押す前の位置・大きさへ戻すだけ。
/// 窓の出し入れ・配置は IQuickOps（CaptureSession）、ぶん投げは ThrowController が持つ。
/// </summary>
internal sealed class QuickThrowController : IBindingHost
{
    private readonly ConfigStore _cfg;
    private readonly Action<string> _setStatus;
    private readonly IQuickOps _ops;

    /// <summary>低レベル入力フック。長押し中は割り当て入力を握り、
    /// 四角の確定は下の巡回がドラッグ状態から読み取る</summary>
    internal QuickInputWatcher Input { get; } = new();

    /// <summary>「登録」「解除」（組み立て側が後から付ける）</summary>
    internal BindingRegistrar Binds { get; set; } = null!;

    /// <summary>状態欄の後ろに残す一言（チャタリングの知らせ。組み立て側が付ける）</summary>
    internal Action<string>? SetNote { get; set; }
    // 知らせが出ている間（20秒）のチャタリングの回数。20秒起きなければ0から数え直す

    private bool _applying;

    public QuickThrowController(ConfigStore cfg, IQuickOps ops, Action<string> setStatus)
    {
        _cfg = cfg;
        _ops = ops;
        _setStatus = setStatus;
        _cfg.Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        switch (e.PropertyName)
        {
            case "":
            case nameof(AppSettings.QuickEnabled):
            case nameof(AppSettings.QuickButton):
            case nameof(AppSettings.QuickMinW):
            case nameof(AppSettings.QuickMinH):
                Apply(fromUi: true);
                break;
        }
    }

    /// <summary>フックスレッドを始め、いまの設定を反映する（窓ができた時に1度）</summary>
    public void Start()
    {
        Input.Start();
        try { Apply(); }
        catch (Exception e) { AppLog.Error("quick input initial apply failed", e); }
    }

    /// <summary>終了時: 巡回を止め、フックを剥がす（他アプリへ割り込みを残さない）</summary>
    public void Stop()
    {
        _cfg.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        SnipCancel("終了");
        SetFollow(false);
        _quickTimer?.Stop();
        try { _marker?.Dispose(); } catch { }
        _marker = null;
        Input.Stop();
    }

    /// <summary>窓の巡回タイマー（テスト・確認用に公開）</summary>
    internal DispatcherTimer? PollTimer => _quickTimer;
    internal (int W, int H) Geo => _quickGeo;
    /// <summary>四角を描き始めている（押してから離すまで）</summary>
    internal bool Following => _quickFollow;

    // ---- IBindingHost ----
    string IBindingHost.QuickKeyTok { get => _quickKeyTok; set => _quickKeyTok = value; }
    string IBindingHost.HotkeyTok { get => _hotkeyTok; set => _hotkeyTok = value; }
    string IBindingHost.SnipHotkeyTok { get => _snipHotTok; set => _snipHotTok = value; }
    void IBindingHost.QuickApply(bool fromUi) => Apply(fromUi);
    void IBindingHost.EnsureQuickPoll() => EnsureQuickPoll();
    bool IBindingHost.QuickSessionActive => _quickFollow || _ops.QuickHidden || _quickEngaged;
    bool IBindingHost.CaptureAlive() => _ops.CaptureAlive();
    void IBindingHost.ThrowManual() => _ops.ThrowManual();

    // ---------------- クイックぶん投げ ----------------
    //
    // 割り当てたマウスボタンの長押し、またはキーを押しているあいだマウスを動かして
    // 四角を描く → 離した瞬間にキャプチャ窓（とバー）をその四角へ寄せてぶん投げる。
    // 入力フックが教えるのは「押した/離した」という点だけ。窓を触るのは
    // 必ずこのUI側の巡回で行う。
    // 四角が quick_min_w/h 未満なら実行せず、押す前の位置・大きさへ戻すだけ。

    /// <summary>キー・ボタンの見張り巡回（20ms・Input優先度）。四角を描いている間の追従は
    /// UI スレッドではなく専用スレッドのダミー枠（QuickMarker）がやる</summary>
    private DispatcherTimer? _quickTimer;

    // ---- 追跡セッションの状態（すべてUIスレッド。巡回のなかでだけ変わる） ----
    private bool _quickFollow;            // 四角を描き始めた（押してから離すまで）
    private bool _lostUpSeen;             // 前の Poll で「ドラッグも離しも無い」を1回見た（2回続いたら取り消す）
    private QuickMarker? _marker;         // 四角を描いている間のダミー枠（遅延生成）
    private bool _markerOn;               // いまの四角でダミー枠を使っているか（範囲ドラッグは使わない）
    private bool _quickKdown;             // キー起動: いま開始キーを押しっぱなし
    private bool _quickEngaged;           // 窓を一度でも動かし始めた（もう見える側）
    private (int X, int Y)? _quickDownPt; // 四角の始点（物理px。押した位置）
    private Native.RECT? _quickPreRect;   // 押す前のキャプチャ窓矩形（取り消しでここへ戻る）
    private string _quickKeyTok = "";     // 有効な quick_key（""=未設定。検証済みトークン）
    private string _snipHotTok = "";      // 有効な範囲ドラッグのホットキー（""=未設定。検証済みトークン）
    private string _hotkeyTok = "";       // 有効なぶん投げホットキー（""=未設定。検証済みトークン）
    private (int W, int H) _quickGeo = (30, 30);   // 最小四角（クランプ済み・追跡中の下限）
    private DateTime _quickReloadAt = DateTime.MinValue;  // quick_* 読み直しの1秒スロットル
    private string? _warnedBadKey, _warnedBadHot, _warnedBadSnip;          // 最後に警告した不正な quick_key / hotkey（同じ値は書き直さない）

    /// <summary>quick_* と hotkey をメモリcfgから読み、検証・クランプする。
    /// btn は既知の語でなければ既定 wheel、
    /// quick_key はキー以外・不正形・危険単独キーなら未設定扱い、
    /// hotkey は正規化して不正なら未設定扱い（hotkeyはキーでもボタンでも届く）</summary>
    private (bool En, string Btn, string Kkey, string Hot, string Snip, int MinW, int MinH) ReadCfg()
    {
        bool en = _cfg.Settings.QuickEnabled;
        string btn = _cfg.Settings.QuickButton;
        if (btn is not ("off" or "wheel" or "back" or "forward" or "both"))
            btn = "wheel";
        string raw = _cfg.Settings.QuickKey;
        string kkey = ValidKeyBinding(raw);
        if (raw.Length > 0 && kkey.Length == 0 && Binding.Normalize(raw).Length > 0)
        {
            // Poll が1秒ごとに読み直すので、同じ不正値は1回だけ書く（毎秒 app.log に出さない）
            if (raw != _warnedBadKey) AppLog.Warning($"quick_key設定が無効のため無視: {raw}");
            _warnedBadKey = raw;
        }
        else _warnedBadKey = null;
        string rawHot = _cfg.Settings.Hotkey;
        string hot = Binding.Normalize(rawHot);
        if (hot.Length > 0 && Binding.Validate(hot).Length > 0)
        {
            if (rawHot != _warnedBadHot) AppLog.Warning($"hotkey設定が無効のため無視: {rawHot}");
            _warnedBadHot = rawHot;
            hot = "";
        }
        else _warnedBadHot = null;
        // 範囲ドラッグのホットキーも同じ読み方（不正なら未設定扱い・同じ値の警告は1回だけ）
        string rawSnip = _cfg.Settings.SnipHotkey;
        string snip = Binding.Normalize(rawSnip);
        if (snip.Length > 0 && Binding.Validate(snip).Length > 0)
        {
            if (rawSnip != _warnedBadSnip) AppLog.Warning($"snip_hotkey設定が無効のため無視: {rawSnip}");
            _warnedBadSnip = rawSnip;
            snip = "";
        }
        else _warnedBadSnip = null;
        int mw = (int)Math.Clamp(_cfg.Settings.QuickMinW, 30, 4000);
        int mh = (int)Math.Clamp(_cfg.Settings.QuickMinH, 30, 4000);
        return (en, btn, kkey, hot, snip, mw, mh);
    }

    /// <summary>キー起動のバインドへ正規化。キー以外（マウスボタン）・形が不正・
    /// 危険な単独キーは空＝未設定扱い</summary>
    private static string ValidKeyBinding(string val)
    {
        string tok = Binding.Normalize(val);
        if (tok.Length == 0 || Binding.Validate(tok).Length > 0 || !Binding.IsKey(tok))
            return "";
        return tok;
    }

    /// <summary>巡回を張っておくべき状態か（「登録」待ちとホットキー発火も巡回が必要）</summary>
    private bool QuickPollOn(bool en, string btn)
        => (en && (btn != "off" || _quickKeyTok.Length > 0)) || _hotkeyTok.Length > 0 || _snipHotTok.Length > 0
           || Binds.Catching || Input.Enabled();

    /// <summary>cfg の quick_*/hotkey をクランプ／検証してフックへ反映 + 巡回の張り替え。
    /// キー起動とホットキーが同じキーならキー起動を優先して発火待ちを外す</summary>
    internal void Apply(bool fromUi = false)
    {
        _applying = true;
        try
        {
            ApplyCore(fromUi);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyCore(bool fromUi)
    {
        var (en, btn, kkey, hot, snip, mw, mh) = ReadCfg();
        string hotLost = "";
        if (kkey.Length > 0 && hot == kkey)
        {
            // 押すたびに発火すると四角が描けない。長押し起動を優先する
            hotLost = hot;
            hot = "";
        }
        _quickKeyTok = kkey;
        _hotkeyTok = hot;
        _snipHotTok = snip;
        _quickGeo = (mw, mh);
        bool want = en && (btn != "off" || kkey.Length > 0);
        try
        {
            Input.ChatterMs = (int)Math.Clamp(_cfg.Settings.QuickChatterMs, 0, 200);
            Input.Apply(want, btn, hot, kkey, snip);
        }
        catch (Exception e)
        {
            AppLog.Error("quick input apply failed", e);
            want = false;
        }
        if (QuickPollOn(en, btn))
        {
            if (_quickTimer is null)
            {
                _quickTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
                _quickTimer.Tick += (_, _) => Poll();
            }
            if (!_quickTimer.IsEnabled) _quickTimer.Start();
        }
        else
        {
            if (_quickTimer is not null)
            {
                _quickTimer.Stop();
                _quickTimer = null;
            }
            // OFFにされた瞬間、ドラッグ最中でも追跡を終える。窓を消した直後で
            // 消えたままにならないよう、必ず元へ戻して見せる（Pythonと同じ）
            if (_quickFollow || _ops.QuickHidden || _quickEngaged)
            {
                try { CancelRestore(); }
                catch (Exception e) { AppLog.Error("quick cancel on OFF failed", e); }
            }
            SetFollow(false);
            _quickKdown = _quickEngaged = false;
            _quickPreRect = null;
        }
        if (fromUi)
        {
            if (want)
            {
                var parts = new List<string>();
                if (btn != "off") parts.Add(QuickButtonLabel(btn));
                if (kkey.Length > 0) parts.Add(Binding.Label(kkey) + " 長押し");
                _setStatus($"クイックぶん投げON: {string.Join("・", parts)}");
            }
            else
            {
                _setStatus("クイックぶん投げOFF");
            }
            // ホットキーを設定したのに発火待ちが残らなかった場合は、その理由を後から出す
            // （最後のSetStatusが見える）
            if (hot.Length > 0 && Input.Hotkey() != hot)
                _setStatus($"ぶん投げホットキー: {Binding.Label(hot)} はクイックぶん投げの起動に使用中のため発火しません");
            else if (hotLost.Length > 0)
                _setStatus($"ぶん投げホットキー: {Binding.Label(hotLost)} はキー起動に使用中のため発火しません");
            if (snip.Length > 0 && Input.SnipHotkey() != snip)
                _setStatus($"範囲ドラッグのキー: {Binding.Label(snip)} は他の入力に使用中のため発火しません");
        }
    }

    /// <summary>巡回が生きていなければ張る。Applyを通さずpollだけ必要な場面
    /// （「登録」待ち）でも同じ入口を使う</summary>
    private void EnsureQuickPoll()
    {
        if (_quickTimer is null)
        {
            _quickTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
            _quickTimer.Tick += (_, _) => Poll();
        }
        if (!_quickTimer.IsEnabled) _quickTimer.Start();
    }

    /// <summary>追跡中かの印。止める時はダミー枠も必ず消す（Release だけは本物の窓を出してから消すので
    /// ここを通らず、自分で _quickFollow を落として StopMarker を呼ぶ）</summary>
    private void SetFollow(bool on)
    {
        _quickFollow = on;
        if (!on) StopMarker();
    }

    private void StopMarker()
    {
        try { _marker?.Stop(); }
        catch (Exception ex) { AppLog.Error("QuickMarker stop failed", ex); }
    }

    /// <summary>ダミー枠の有無（テスト用）</summary>
    internal QuickMarker? Marker => _marker;

    private void StartMarker(int x, int y)
    {
        try
        {
            if (_marker is null)
            {
                _marker = new QuickMarker(0x00, 0x67, 0xC0, CaptureBorder.DefaultPx);
            }
            // 描き始めの時点の設定（色・不透明度・太さ）でダミー枠を描く
            var c = CaptureBorder.Color;
            _marker.SetStyle(c.R, c.G, c.B, CaptureBorder.AlphaByte(CaptureBorder.Alpha), CaptureBorder.Px);
            _marker.SetAllowCapture(_cfg.Settings.AllowCaptureOverlay);
            // マウスで描く時だけ、フックが離しを受け取ったら離した点で止める（キーで描く時はキーを離すまで追う）。
            // 「離した」は、描き始めてから離しが1回以上来て、いまドラッグ中でないこと（チャタリングで押し直したらまた追う）
            if (_quickKdown) { _marker.Released = null; _marker.ReleasedEnd = null; }
            else
            {
                var input = Input;
                int upAtStart = input.UpCount;
                _marker.Released = () => input.Drag is null && input.UpCount != upAtStart;
                _marker.ReleasedEnd = () => input.ReleasedEnd;
            }
            // 失敗（Failed）の時はダミー無し＝窓は離すまで隠れたまま、離した位置に出る
            _marker.Start(x, y, _quickGeo.W, _quickGeo.H);
        }
        catch (Exception ex) { AppLog.Error("QuickMarker start failed", ex); }
    }

    /// <summary>設定ページの選択肢と同じラベル。
    /// ON直後の一言が画面と違う語だと、別の設定に見えて紛らわしい</summary>
    private static string QuickButtonLabel(string btn) => btn switch
    {
        "wheel" => "ホイールクリック（中央）",
        "back" => "マウス「戻る」ボタン",
        "forward" => "マウス「進む」ボタン",
        "both" => "戻る・進む 両方",
        _ => btn,
    };

    /// <summary>20ms poll: ドラッグ中は実窓を追跡矩形へ追随させ、
    /// 離したら確定させて翻訳する。
    /// どの分岐で例外が飛んでも巡回自体は生き続ける</summary>
    internal void Poll()
    {
        // 「登録」待ちのあいだは cfg の読み直し（Apply）を止める。登録中の張り替えで
        // 掴んだ入力や発火を取りこぼす恐れがある。
        // 四角を描いている間（_quickFollow／_quickKdown／QuickHidden）も止める。
        if (!Binds.Catching
            && !(_quickFollow || _quickKdown || _ops.QuickHidden)
            && (DateTime.UtcNow - _quickReloadAt).TotalSeconds >= 1.0)
        {
            _quickReloadAt = DateTime.UtcNow;
            try { Apply(); }
            catch (Exception e) { AppLog.Error("quick config reload failed", e); }
        }
        QuickInputWatcher.RectResult? rel = null;
        QuickInputWatcher.DragStart? drag = null;
        int kd = QuickInputWatcher.KdNone;
        string? cap = null;      // 「登録」で掴まれた入力（1個だけ）
        string? hotTok = null;   // ぶん投げホットキーが押された合図
        string? snipTok = null;  // 範囲ドラッグのホットキーが押された合図
        try
        {
            rel = Input.TakeResult();
            drag = Input.Drag;
            kd = Input.KdState;
            cap = Input.TakeCaptured();
            hotTok = Input.TakeHot();
            snipTok = Input.TakeSnipHot();
            if (_snipActive) { rel = null; drag = null; kd = QuickInputWatcher.KdNone; }   // 範囲ドラッグの最中はフックの押し・離しを使わない
            if (Input.TakeChatter() is { } chat)
            {
                if (chat.Absorbed)
                {
                    AppLog.Info($"quick chatter: ボタンが一瞬離れた（{chat.GapMs}ms）。離しを無視して描き続けた（quick_chatter_ms={chat.ChatterMs}）");
                }
                else
                {
                    AppLog.Info($"quick chatter?: 離して {chat.GapMs}ms で押し直し（quick_chatter_ms={chat.ChatterMs} を超えたので別の四角として扱った）");
                }
                // 回数・ms は app.log だけ。状態欄の後ろには固定の一言（20秒残る）
                SetNote?.Invoke("⚠ チャタリングしてるかも！");
            }
        }
        catch (Exception e)
        {
            AppLog.Error("quick input poll failed", e);
        }
        bool lostBefore = _lostUpSeen;   // 前の Poll で「離しの取り損ねかも」と見たか
        _lostUpSeen = false;
        try
        {
            // 「登録」待ちが最優先。押されたら欄へ反映、8秒押されなければ中止して
            // フックを設定どおりへ戻す
            if (Binds.HandlePoll(cap))
            {
            }
            else if (hotTok is not null)
            {
                try { Binds.HotkeyFire(hotTok); }
                catch (Exception e) { AppLog.Error("hotkey fire failed", e); }
            }
            else if (snipTok is not null)
            {
                // 範囲ドラッグのホットキー。クイックがOFFでも始める（範囲ドラッグか長押しドラッグの最中なら SnipStart が何もしない）
                try { SnipStart(); }
                catch (Exception e) { AppLog.Error("snip hotkey fire failed", e); }
            }
            else if (rel is not null)
            {
                try { Release(rel.Value); }
                catch (Exception e)
                {
                    AppLog.Error("quick release failed", e);
                    _setStatus("クイック失敗（ログ参照）");
                }
            }
            else if (kd == QuickInputWatcher.KdDown && _quickKeyTok.Length > 0
                     && _quickDownPt is null && drag is null)
            {
                // 左手でキーを押した瞬間。ここから右手でマウスを動かして四角を描く
                try { BeginKey(); }
                catch (Exception e) { AppLog.Error("quick key begin failed", e); }
            }
            else if (drag is not null)
            {
                // 押点から四角を描き始める（追従はダミー枠 QuickMarker の専用スレッドが行う）
                if (!_quickFollow) Begin(drag.Value);
            }
            else if (_quickKdown && _quickFollow && kd == QuickInputWatcher.KdDown)
            {
                // キーを押しっぱなしのあいだ、マウスを動かしているだけ（追従はダミー枠側）
            }
            else if (_quickKdown && kd != QuickInputWatcher.KdDown)
            {
                // キーを離した。そのときのカーソルを対角として四角を確定する
                _quickKdown = false;
                if (_quickFollow && _quickDownPt is not null)
                {
                    var p = Geom.CursorPos();
                    if (p is null)
                    {
                        AppLog.Warning("quick key up but cursor position unknown");
                        CancelRestore();
                        SetFollow(false);
                        _quickEngaged = false;
                    }
                    else
                    {
                        var (x0, y0) = _quickDownPt.Value;
                        try
                        {
                            Release(new QuickInputWatcher.RectResult(x0, y0, p.Value.X, p.Value.Y));
                        }
                        catch (Exception e)
                        {
                            AppLog.Error("quick key release failed", e);
                            _setStatus("クイック失敗（ログ参照）");
                        }
                    }
                }
                else
                {
                    // 開始できなかった（キャプチャ窓を開けない等）なら何もせず切る
                    _quickDownPt = null;
                }
            }
            else if (_snipActive)
            {
                // 範囲ドラッグ（覆いが押し・離しを受ける）。フックには押しも離しも見えないので、取り損ねと見なさない
            }
            else if (_quickFollow && Input.UpPending)
            {
                // 離しを預かっている最中（チャタリングか見ている。quick_chatter_ms 以内に確定する）
            }
            else if (_quickFollow && !lostBefore)
            {
                // 1回目は見送る。フックが離しを書いている最中（または TakeResult の後に書いた）こともあるので、
                // 次の Poll（20ms 後）でもまだドラッグも離しも無い時だけ取り損ねと見なす
                _lostUpSeen = true;
            }
            else if (_quickFollow)
            {
                // up事件を取り損ねた例外経路（他プロセスの吸収等）。窓を消したまま
                // 放置するのが最悪なので、必ず押す前へ戻して見せる
                AppLog.Warning("quick drag ended without up event");
                try { CancelRestore(); }
                catch (Exception e) { AppLog.Error("quick cancel failed", e); }
                SetFollow(false);
                _quickKdown = false;
                _quickEngaged = false;
            }
        }
        catch (Exception e)
        {
            AppLog.Error("quick tick handling failed", e);
        }
        finally
        {
            // 消した窓を置き去りにしない（releaseもcancelもshowで消すので、ここは保険）
            if (!_quickFollow && _ops.QuickHidden)
            {
                try { CancelRestore(); }
                catch (Exception e) { AppLog.Error("quick safety show failed", e); }
            }
        }
    }

    // ---------------- 画面を少し暗くして範囲ドラッグ ----------------

    private SnipOverlay? _snip;
    private bool _snipActive;      // 覆いを出してから、離す・取り消すまで（Poll の取り損ね取り消しを止める）
    private bool _snipDragging;    // 覆いの上で左ボタンを押した後
    /// <summary>押さないまま待つ長さ（テストで短くする）</summary>
    internal TimeSpan SnipTimeout { get; set; } = TimeSpan.FromSeconds(15);
    internal bool SnipActive => _snipActive;
    internal SnipOverlay? Snip => _snip;

    /// <summary>覆いを出す。すでに出ている・クイックの四角を描いている最中なら何もしない（false）</summary>
    internal bool SnipStart()
    {
        if (_snipActive || _quickFollow || _quickKdown || _ops.QuickHidden) return false;
        _snipActive = true;
        _snipDragging = false;
        try
        {
            var o = new SnipOverlay(SnipTimeout, _cfg.Settings.AllowCaptureOverlay);
            o.Began = SnipBegin;
            o.Released = SnipRelease;
            o.Cancelled = r => SnipCancel(r);
            _snip = o;
            o.Run();
            AppLog.Info("snip: 開始");
            return true;
        }
        catch (Exception e)
        {
            AppLog.Error("snip start failed", e);
            SnipCancel("開始に失敗");
            return false;
        }
    }

    /// <summary>覆いの上で左ボタンを押した（物理px）。本物の窓を消してダミー枠を付ける（クイックと同じ）</summary>
    internal void SnipBegin(int x, int y)
    {
        if (!_snipActive || _snipDragging) return;
        _snipDragging = true;
        if (_snip is { } so) so.Dragging = true;   // 覆いが外れても取り消さない側へ
        _snip?.BeginSelection(x, y, _quickGeo.W, _quickGeo.H, CaptureBorder.Px);   // 四角の中は暗くしない（ダミー枠と同じ計算）
        _quickKdown = false;
        BeginAt(x, y, marker: false);   // 枠は覆いが描く
        if (!_quickFollow) SnipCancel("四角を描き始められない");
    }

    /// <summary>覆いの上で左ボタンを離した（物理px）。覆いを閉じ、画面の書き換えを待ってから確定する</summary>
    internal void SnipRelease(int x1, int y1)
    {
        if (!_snipActive || !_snipDragging || _quickDownPt is not { } d) return;
        var o = _snip;
        _snip = null;
        try
        {
            o?.Finish();
            try { Native.DwmFlush(); } catch { }
            AppLog.Info($"snip: 離した {d.X},{d.Y} -> {x1},{y1}");
            Release(new QuickInputWatcher.RectResult(d.X, d.Y, x1, y1));
        }
        catch (Exception e)
        {
            AppLog.Error("snip release failed", e);
            _setStatus("クイック失敗（ログ参照）");
        }
        finally
        {
            _snipActive = _snipDragging = false;
        }
    }

    /// <summary>取り消す。覆いを閉じ、四角を描き始めていたら窓を元へ戻す</summary>
    internal void SnipCancel(string reason)
    {
        if (!_snipActive) return;
        bool dragging = _snipDragging;
        var o = _snip;
        _snip = null;
        _snipActive = _snipDragging = false;
        o?.Finish();
        AppLog.Info($"snip: 取り消し（{reason}）");
        if (dragging)
        {
            try { CancelRestore(); }
            catch (Exception e) { AppLog.Error("snip cancel restore failed", e); }
            SetFollow(false);
            _quickEngaged = false;
            _quickDownPt = null;
            _quickPreRect = null;
        }
    }

    private void Begin(QuickInputWatcher.DragStart drag)
    {
        _quickKdown = false;
        BeginAt(drag.X0, drag.Y0);
    }

    private void BeginKey()
    {
        // 左手でキーを押しているあいだ右手でマウスを動かし、キーを離した位置が対角。
        // 始点は押したときのポインタ（フックは超軽量なので位置はここで読む）
        var p = Geom.CursorPos();
        if (p is null) return;
        _quickKdown = true;
        BeginAt(p.Value.X, p.Value.Y);
    }

    /// <summary>始点 (x,y)（物理px）で四角を描き始める。窓・バーをこの場で消して、
    /// 押している間は桌面が見えるようにする（窓が自分の対象を隠さない）</summary>
    internal void BeginAt(int x, int y, bool marker = true)
    {
        SetFollow(true);
        _quickEngaged = false;
        _quickDownPt = (x, y);
        _quickPreRect = null;
        // 案内・旧訳と窓外の旧訳stripも描き始めには消す。窓と同じ100msの薄れ
        // （撮影前の隠しは TranslateNow が即でやる。どちらも撮影には元から写らない＝affinity）
        _ops.QuickHidePanels(x, y);
        var cap = _ops.EnsureCaptureForQuick();
        if (cap is null)
        {
            // 窓が未開でも使えるのがこの機能の肝。開いた窓は同じtick内で消すので
            // 跳滅は見えない
            _setStatus("クイック失敗（ログ参照）");
            SetFollow(false);
            return;
        }
        if (cap.Value.Window is { } w && w.WindowState == WindowState.Minimized)
        {
            // 最小化中にSetWindowPosを当てても効かないので先に復元
            w.WindowState = WindowState.Normal;
        }
        if (Native.GetWindowRect(cap.Value.Hwnd, out var r))
            _quickPreRect = r;
        _ops.QuickHideWindow(x, y);
        // 範囲ドラッグ（marker=false）はダミー枠を出さない。枠は覆いが明るい所と同じ描画の中で描く（別の窓だとずれる）。
        // 前のクイックで作った _marker が残っていても止めたまま使わない
        _markerOn = marker;
        if (marker) StartMarker(x, y);   // 四角を描く間はダミー枠だけが付いてくる（本物の窓は離すまで隠したまま）
        else StopMarker();
    }

    /// <summary>取り消し・終了時: 窓を消した前へ戻して必ず見せる。
    /// 消える側と同じく戻る側もふわっとフェードで揃える</summary>
    internal void CancelRestore()
    {
        StopMarker();
        if (!_ops.CaptureAlive())
        {
            _ops.ForgetQuickHidden();
            return;
        }
        // 窓が無い所からこの押しで開いた（まだ一度も出していない）なら、押す前＝窓が無い状態へ戻す
        if (_ops.DiscardQuickOpened())
            return;
        if (_quickPreRect is Native.RECT r)
        {
            _ops.QuickPlace(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, fade: true);
        }
        else
        {
            _ops.QuickShowWindow(fade: true);
            _ops.PositionChrome();
        }
    }

    /// <summary>四角を離した瞬間: 窓をその矩形へ確定させて翻訳を積む。rel は
    /// (x0,y0,x1,y1) 物理px。w,hが quick_min_w/h 未満なら追跡を取りやめ、押す前へ
    /// 戻すだけ（誤爆防止で翻訳は積まない）</summary>
    private void Release(QuickInputWatcher.RectResult rel)
    {
        _quickFollow = false;   // ダミー枠は本物の窓を出してから消す（空白を作らない）
        _quickKdown = false;
        _quickDownPt = null;
        _quickEngaged = false;
        var (l, t, w, h) = Geom.NormRect(rel.X0, rel.Y0, rel.X1, rel.Y1);
        var (minW, minH) = _quickGeo;
        if (w < minW || h < minH)
        {
            // 消した窓（追随していた場合は動かした窓）を必ず前へ戻して見せる
            CancelRestore();
            _quickPreRect = null;
            _setStatus($"クイックぶん投げ: 四角が小さいので取り消しました（{minW}x{minH} px 以上。" +
                      "窓は元の位置・サイズへ戻しています）");
            return;
        }
        var cap = _ops.EnsureCaptureForQuick();
        if (cap is null)
        {
            // 押してすぐ離して追跡が始まらなかった場合も、この経路で窓を開いて寄せる
            _setStatus("クイック失敗（ログ参照）");
            StopMarker();
            return;
        }
        if (cap.Value.Window.WindowState == WindowState.Minimized)
            cap.Value.Window.WindowState = WindowState.Normal;   // 復元してから寄せる
        var (x, y, cw, ch) = Geom.ClampToVirtual(l, t, w, h);
        // 確定位置がまれに左上へずれる件の調べ用。フックが拾った四角・置く四角・ダミー枠が出していた四角
        try
        {
            var shown = _markerOn ? _marker?.ShownRect : null;
            var cur = Geom.CursorPos();
            AppLog.Info($"quick release: hook={rel.X0},{rel.Y0}->{rel.X1},{rel.Y1} place={x},{y} {cw}x{ch} " +
                        $"marker={(shown is { } m ? $"{m.X},{m.Y} {m.W}x{m.H}" : "-")} " +
                        $"cursor={(cur is { } c ? $"{c.X},{c.Y}" : "?")}");
        }
        catch { }
        _ops.QuickPlace(x, y, cw, ch);
        _quickPreRect = null;
        try
        {
            // 動かした結果がDWM合成へ反映されてから撮らないと、古い位置の影や
            // ツールバーが撮影に写り込む
            Native.DwmFlush();
        }
        catch { /* DwmFlushが無い環境では撮影側の待ちでカバー */ }
        // 本物が出てからダミーを消す（一瞬の二重表示は可・空白は不可）。本物は新しい大きさで描けるまで
        // DWM で隠している（最長 100ms）ので、出た時に消す。ダミーは撮影に写らない設定（WDA_EXCLUDEFROMCAPTURE）
        var marker = _markerOn ? _marker : null;
        _ops.AfterQuickShown(() =>
        {
            try { marker?.StopAndWaitHidden(); }
            catch (Exception ex) { AppLog.Error("QuickMarker stop failed", ex); }
        });
        bool busyBefore = _ops.ThrowBusy;
        _ops.ThrowManual();   // クイックも manual 源
        if (busyBefore)
            _setStatus($"クイックぶん投げ: 領域 ({l},{t}) {w}x{h} へ移動（ぶん投げ中のため実行は「ぶん投げる」ボタンで）");
        else
            _setStatus($"クイックぶん投げ: 領域 ({l},{t}) {w}x{h} をぶん投げ開始");
    }
}

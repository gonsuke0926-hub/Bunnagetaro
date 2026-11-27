using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5.Input;
using Bunnagetaro5.Services;
using Bunnagetaro5.Status;

namespace Bunnagetaro5.Session;

/// <summary>バー（ChromeBar）が上へ頼むこと。バーは MainWindow を知らない</summary>
internal interface IBarActions
{
    void BarAutoTrig(bool on);
    /// <summary>プリセット選択。設定タブの書きかけが拒否されたら false（バーは選択を直前に戻す）</summary>
    bool BarPresetSelect(string name);
    void BarTranslatePressed();
    /// <summary>バーだけが（くっつけるOFF のドラッグで）この矩形へ動く直前。外の解答欄を同じフレームで置く</summary>
    void BarMovingTo(Native.RECT newBarRect);
    void PlaceBar(Native.RECT? target = null);
    void CloseCaptureAll();
    void SyncPanelPassthru(bool on);
    void SetStatus(string msg);
}

/// <summary>解答欄（AnswerPanel）・帯（AnswerStrip）が上へ頼むこと。どちらも MainWindow を知らない</summary>
internal interface IAnswerHost
{
    bool FollowupSend(string question, bool withImage);
    void TouchPanelGen();
    void SetStatus(string msg);
    Native.RECT? BarRectNow();
    void PlacePanel(Native.RECT? target = null);
    void PlaceStrip(Native.RECT? barRect);
    bool DeferPanelWhileDragging(Action again);
    void ResetPanelRun();
    bool RequestPanelShow();
    bool IsPanelFadingOut();
    void HidePanelForCapture();
    void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null);
    void RequestStripShow();
    void HideStripNow();
    void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null);
}

/// <summary>バー・設定ページの橋（設定ページが実装する）。バーの操作が設定ページの書きかけとぶつからないように</summary>
internal interface IBarSettingsView
{
    /// <summary>設定タブの書きかけを先に反映。拒否なら理由、通れば null</summary>
    string? TryCommitForExternal();
    void SyncOcrOnly(bool enabled);
    void SetBarRefreshSuppressed(bool on);
}

/// <summary>
/// キャプチャ窓・バー・解答欄（inside）・帯（outside）をまとめて開く/閉じる/位置合わせ/フェード/案内する。
/// 窓と同時に生まれ同時に閉じる。
/// ぶん投げの実行は ThrowController（IThrowOps 越し）、クイックぶん投げの窓の出し入れは
/// QuickThrowController（IQuickOps 越し）から頼まれる。
/// </summary>
internal sealed class CaptureSession : IThrowOps, IQuickOps, IBarActions, IAnswerHost
{
    private readonly ConfigStore _cfg;
    private readonly Window _owner;   // 子の窓（キャプチャ窓・バー・解答欄・帯）の Owner
    private readonly StatusModel Status;
    private readonly Action<string> _setStatus;
    private readonly Func<string> _statusText;

    /// <summary>ぶん投げ・クイックぶん投げ・設定ページ（組み立て側が後から付ける）</summary>
    internal ThrowController Throw { get; set; } = null!;
    internal QuickThrowController Quick { get; set; } = null!;
    internal IBarSettingsView? SettingsView { get; set; }

    private CaptureWindow? _capture;
    /// <summary>バー。キャプチャ窓と同時に生まれ同時に閉じる</summary>
    private ChromeBar? _bar;
    /// <summary>解答欄 inside。キャプチャ窓と同時に生まれ、
    /// 中身ができる（最初のdelta・done）まで表示は出さない</summary>
    private AnswerPanel? _panel;
    /// <summary>解答欄 outside。キャプチャ窓の外側へ付く帯。
    /// 同じく窓と同時に生まれ、表示と配置は250ms巡回（Sync）が every 決める</summary>
    private AnswerStrip? _strip;
    /// <summary>bar配置と帯の保存・配置を確かめる巡回（250ms）。
    /// キャプチャ窓が開いている間だけ回る</summary>
    private DispatcherTimer? _chromeSync;

    /// <summary>自動トリガのON/OFF（バーのトグルと同期）。巡回そのものは MainWindow の TriggerWatcher</summary>
    internal bool AutoMode { get; set; }

    /// <summary>自分の表示を変えた回数。「自分の表示で発火しない」の下準備
    /// （解答欄の表示・非表示・本文・思考・スタイル適用のたびに増える）</summary>
    public int PanelGen { get; private set; }
    public void TouchPanelGen() => PanelGen++;

    /// <summary>キャプチャ窓（無ければ null）</summary>
    internal CaptureWindow? CaptureWin => _capture;

    public CaptureSession(ConfigStore cfg, Window owner, StatusModel status,
                          Action<string> setStatus, Func<string> statusText)
    {
        _cfg = cfg;
        _owner = owner;
        Status = status;
        _setStatus = setStatus;
        _statusText = statusText;
        _lastPlace = cfg.EffectiveResultPlace();
        _cfg.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _placer = new WindowPlacer(cfg);
        _vis = new WindowVisibility(cfg, _placer, TouchPanelGen, () => Throw?.CapturePending == true);
        _placer.SetVisibility(_vis);
        _hint = new HintController(
            cfg,
            canShow: () => _capture is not null && !_vis.QuickHidden && !Quick.Following && !Throw.CapturePending && !Status.Busy,
            canHide: () => !Status.Busy && !Throw.CapturePending,
            () => _panel,
            () => _strip);
        _quickReveal = new QuickReveal(
            cfg,
            _vis,
            () => _capture,
            () => _bar,
            () => OpenCaptureCore(animate: false, forQuick: true));
    }

    private readonly WindowPlacer _placer;
    private readonly WindowVisibility _vis;
    private readonly HintController _hint;
    private readonly QuickReveal _quickReveal;
    internal WindowPlacer PlacerForTests => _placer;
    internal WindowVisibility VisibilityForTests => _vis;
    internal HintController HintForTests => _hint;
    internal TaskbarFront TaskbarFrontForTests => _placer.TaskbarFront;

    private string _lastPlace;
    private int _selfWriteDepth;
    private bool _syncingBar;

    /// <summary>AppSettings.PropertyChanged の購読と置き場所・表示の係を外す</summary>
    public void Dispose()
    {
        _cfg.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _vis.Dispose();
        _placer.Dispose();
    }

    private void WriteSelf(Action body)
    {
        _selfWriteDepth++;
        try { body(); }
        finally { _selfWriteDepth--; }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selfWriteDepth > 0 || _syncingBar) return;
        switch (e.PropertyName)
        {
            case "":
            case nameof(AppSettings.Mode):
            case nameof(AppSettings.CaptureBorderColor):
            case nameof(AppSettings.CaptureBorderAlpha):
            case nameof(AppSettings.CaptureBorderPx):
            case nameof(AppSettings.OverlayBg):
            case nameof(AppSettings.OverlayFg):
            case nameof(AppSettings.OverlayAlpha):
            case nameof(AppSettings.StripBg):      // 外窓の色・不透明度
            case nameof(AppSettings.StripFg):
            case nameof(AppSettings.StripAlpha):
            case nameof(AppSettings.LayoutTextBg):
            case nameof(AppSettings.OverlayFontFamily):
            case nameof(AppSettings.OverlayFontSize):
            case nameof(AppSettings.ResultPlace):
            case nameof(AppSettings.LatexCopy):
            case nameof(AppSettings.AnswerParaSpacing):
            case nameof(AppSettings.AnswerLineSpacing):
            case nameof(AppSettings.ConvertOtslTable):
            case nameof(AppSettings.ConvertHtmlTable):
            case nameof(AppSettings.ConvertLatexMath):
            case nameof(AppSettings.ConvertMarkdown):
            case nameof(AppSettings.ShowFollowRow):
            case nameof(AppSettings.PromptActive):
            case nameof(AppSettings.Prompt):
            case nameof(AppSettings.Prompts):
            case nameof(AppSettings.OcrSplitMode):
            case nameof(AppSettings.AllowCaptureOverlay):
                SyncBarFromCfg();
                break;
        }
    }

    /// <summary>状態欄へ一言（バー・解答欄・帯から）</summary>
    public void SetStatus(string msg) => _setStatus(msg);

    /// <summary>追加の問い（解答欄・帯から）。ぶん投げ側へ流すだけ</summary>
    public bool FollowupSend(string q, bool withImg) => Throw.FollowupSend(q, withImg);

    /// <summary>状態欄と同じ言葉をバーの2行目へ</summary>
    internal void SetBarStatus(string msg) => _bar?.SetStatusText(msg);

    /// <summary>バーの透過が今ONか（外から読まれる）</summary>
    internal bool? BarPassthru => _bar?.PassthruActive;

    /// <summary>
    /// キャプチャ窓を開く。開いているときは前に出すだけ。
    /// 窓が外側で壊されていた場合は作り直す（死んだHWNDの残留で次回が無言で壊れないように）
    /// </summary>
    public void OpenCapture(bool animate = true) => OpenCaptureCore(animate, forQuick: false);

    /// <summary>窓を開く本体。forQuick: クイックが窓の無い所から開く時。窓とバーは画面に一度も出さない
    /// （DWM の cloak を掛けたまま Show して初期化だけ済ませ、すぐ隠して cloak を外す）。開いた直後の案内も出さない。
    /// 離した時に新しい四角へ置いて初めて出る（「一瞬出てから消える」を無くす）</summary>
    private void OpenCaptureCore(bool animate, bool forQuick)
    {
        if (_capture is not null)
        {
            var h = new WindowInteropHelper(_capture).Handle;
            if (h != IntPtr.Zero && Native.IsWindow(h))
            {
                _capture.Activate();
                return;
            }
            _capture = null;
        }
        // メイン窓を最小化してもキャプチャ窓が隠れないよう Owner は付けない（Alt+Tab 除外は各窓の WS_EX_TOOLWINDOW）
        var w = new CaptureWindow(_cfg, _setStatus);
        w.Closed += (_, _) =>
        {
            _vis.Detach();
            _placer.Detach();
            _capture = null;
            Status.CaptureOpen = false;
            _hint.DropOnClose();   // 開いていた間の案内の消し予約は無効
            var b = _bar;
            _bar = null;
            var pn = _panel;
            _panel = null;
            var sp = _strip;
            _strip = null;
            var tm = _chromeSync;
            _chromeSync = null;
            tm?.Stop();   // 帯の巡回は窓と最期まで（窓が無いのに置かない）
            // フェードで先に閉じていることもある（CloseCaptureAll 経由）。二重Closeは握る
            try { b?.Close(); } catch (InvalidOperationException) { }
            try { pn?.Close(); } catch (InvalidOperationException) { }
            try { sp?.Close(); } catch (InvalidOperationException) { }
            _setStatus("キャプチャ窓を閉じました");
        };
        _capture = w;
        var bar = new ChromeBar(_cfg, this, w);
        bar.Closed += (_, _) => { if (_bar == bar) _bar = null; };
        var panel = new AnswerPanel(_cfg, this, w);
        panel.Closed += (_, _) => { if (_panel == panel) _panel = null; };
        // 解答欄が出ている間だけ、キャプチャ窓の中を素通しへ戻す。
        // 解答欄はこの窓より下にいることがあるので、膜で取ったクリックを返す
        w.InteriorBlocked = () => panel.IsVisible;
        var strip = new AnswerStrip(_cfg, this);
        strip.Closed += (_, _) => { if (_strip == strip) _strip = null; };
        _bar = bar;
        _panel = panel;
        _strip = strip;
        _placer.Attach(w, bar, panel, strip);
        _vis.Attach(w, bar, panel, strip);

        _vis.BeginOpen(animate, forQuick, QuickReveal.ShowCloaked, QuickReveal.Uncloak);
        Status.CaptureOpen = true;
        _setStatus("キャプチャ窓を開きました");
        bar.SetStatusText(_statusText());
        _chromeSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _chromeSync.Tick += (_, _) => _placer.TickInsurance();
        _chromeSync.Start();
        // 窓を開いた=使い始めた合図。ここで裏からモデルを載せに行く（初回ぶん投げで読み込みを待たせない）
        _ = Throw.WarmAiAsync("open_capture");
        if (forQuick)
            return;   // 案内は出さない。同時のふわっと表示も要らない（動き無し）
        // 訳文が出ていない場所へ、窓を開いた直後の案内を出す
        ShowHint();
        if (animate) _vis.CompleteOpenAnimated();
    }

    // ---------------- 窓を開いた直後の案内（本体は HintController） ----------------

    public const string HintText = HintController.HintText;
    public const int HintMs = HintController.HintMs;
    public static string HintTextFor(bool quickOn, string quickButton) => HintController.HintTextFor(quickOn, quickButton);
    public void ShowHint() => _hint.ShowHint();
    public void HideHint(int tok) => _hint.HideHint(tok);

    /// <summary>キャプチャ窓とバーと解答欄をまとめて閉じる（バーの「閉じる」）</summary>
    public void CloseCaptureAll() => _vis.BeginClose();

    /// <summary>アプリ終了時、見えている関連窓をバーの「閉じる」と同じ薄れ＋縮みで隠し始める（待たない）。
    /// 1つでも始めたら true</summary>
    public bool FadeAllForQuit(int ms) => _vis.BeginQuitFade(ms);

    /// <summary>アプリ終了時: モデル降ろし待ちで窓が残って見えないよう、関連窓を即座に隠す。</summary>
    public void HideAllForQuit()
    {
        foreach (Window? w in new Window?[] { _panel, _strip, _bar, _capture })
        {
            if (w is null) continue;
            try { w.Hide(); } catch { }
        }
    }

    /// <summary>メイン窓が閉じた時に Owner を持たない関連窓をすべて即時閉じる。</summary>
    public void CloseAllNow()
    {
        foreach (Window? w in new Window?[] { _panel, _strip, _bar, _capture })
        {
            if (w is null) continue;
            try { w.Close(); } catch { }
        }
    }

    // ---------------- バーからの操作 ----------------

    /// <summary>バーのぶん投げるボタン。ぶん投げ中なら「やめる」に化けている（_translate_btn_pressed）</summary>
    public void BarTranslatePressed()
    {
        if (Status.Busy) Throw.CancelTranslate();
        else Throw.TranslateNow();
    }

    /// <summary>バーの自動トリガ。mode を即保存し、設定タブのチェック欄にも反映</summary>
    public void BarAutoTrig(bool on)
    {
        AutoMode = on;   // 出ていた答えは消さない（切替で答えは動かさない）
        WriteSelf(() => _cfg.Settings.Mode = on ? "auto" : "manual");
        _cfg.Save();
        // 解答欄の撮る前消しは manual/auto 問わず ThrowController.TranslateNow が消す（自動発火も同じ経路）
        _setStatus(on ? "自動トリガON（監視開始）" : "自動トリガOFF（「ぶん投げる」ボタンで都度）");
    }

    /// <summary>
    /// バーのプリセット選択。
    /// 設定タブの書きかけが拒否されたら false（バーは選択を直前に戻す）
    /// </summary>
    public bool BarPresetSelect(string name)
    {
        string? note = SettingsView?.TryCommitForExternal();
        if (note is not null)
        {
            _setStatus("プリセットを切り替えませんでした（設定タブ: " + note + "）");
            return false;
        }
        if (name == _cfg.Settings.PromptActive) return true;
        bool ok = false;
        WriteSelf(() => ok = _cfg.Settings.SelectPreset(name));
        if (!ok)
        {
            // 一覧に無い名前にしない（バー側だけcfgへ追いつかせる）
            _bar?.SyncWidgets();
            return true;
        }
        _cfg.Save();
        // プリセット専用の表示の設定（「たずねる」欄・表や式の変換・色など）を解答欄へ反映する。
        // WriteSelf の中の書き換えは OnSettingsPropertyChanged を通らないので、ここで設定ページからの切り替えと同じ塗り直しをする
        SyncBarFromCfg();
        Throw.AfterRunCfgChange();   // プリセットでモデル・載せ方が変わる時の載せ直し（_after_run_cfg_change）
        _setStatus("プリセット: " + name);
        return true;
    }

    /// <summary>設定タブ側で cfg が変わった。バーの表示を cfg から塗り直し、
    /// 解答欄のスタイル（色・透明度・フォント）も即反映</summary>
    public void SyncBarFromCfg()
    {
        if (_syncingBar) return;
        _syncingBar = true;
        try
        {
            AutoMode = _cfg.Settings.Mode == "auto";   // 切り替えても出ていた答えは消さない
            _capture?.ApplyBorderSettings(_cfg.Settings);   // 枠の色・不透明度・太さ
            _capture?.ApplyCaptureAffinity();               // 配信・録画に映す切替
            _bar?.ApplyCaptureAffinity();
            _panel?.ApplyCaptureAffinity();
            _strip?.ApplyCaptureAffinity();
            _bar?.SyncWidgets();
            _panel?.ApplyStyle();
            _strip?.ApplyStyle();
            // result_place が outside に変わったときは表示中の解答欄（inside側）を下ろす。
            // outside側の帯は即出さない（次の delta/done か、巡回が収まる場所を見つけてから）。
            // place が inside に戻る限りは巡回（Sync）の方が毎度帯を隠すので这里是非で足りる
            string place = _cfg.EffectiveResultPlace();
            if (place != _lastPlace)
            {
                // 窓に重ねる⇔外の解答欄を切り替えても、出ていた答えは新しい側へ移して出す
                bool toOutside = place == "outside";
                _lastPlace = place;
                if (toOutside)
                {
                    if (_panel is { IsVisible: true } p && p.View.ModelText.Length + p.View.ThinkText.Length > 0)
                        _strip?.AdoptFrom(p.View);
                }
                else if (_strip is { IsVisible: true } st && st.View.ModelText.Length + st.View.ThinkText.Length > 0)
                    _panel?.AdoptFrom(st.View);
            }
            _vis.OnResultPlaceChanged(place);
            SyncPanelPassthru(_bar?.PassthruActive ?? _cfg.Settings.ChromePassthru);
        }
        finally
        {
            _syncingBar = false;
        }
    }

    /// <summary>解答欄の素通しをバーと同じ状態へ（_sync_panel_passthru）。
    /// バーの透過切換と設定タブの保存の両方から来る</summary>
    public void SyncPanelPassthru(bool on) => _panel?.ApplyPassthru(on);

    /// <summary>バーの現在の物理矩形（出ていなければ null）。帯の配置と
    /// strip_offset_x の保存が使う</summary>
    public Native.RECT? BarRectNow() => _bar?.BarRectNow();

    void IAnswerHost.PlacePanel(Native.RECT? target) => _placer.PlacePanel(target);
    void IAnswerHost.PlaceStrip(Native.RECT? barRect) => _placer.PlaceStrip(barRect, syncVisibility: true);
    bool IAnswerHost.DeferPanelWhileDragging(Action again) => _vis.DeferPanelWhileDragging(again);
    void IAnswerHost.ResetPanelRun() => _vis.ResetPanelRun();
    bool IAnswerHost.RequestPanelShow() => _vis.RequestPanelShow();
    bool IAnswerHost.IsPanelFadingOut() => _vis.IsPanelFadingOut;
    void IAnswerHost.HidePanelForCapture() => _vis.HidePanelForCapture();
    void IAnswerHost.HidePanelFade(int ms, Action? onHidden) => _vis.HidePanelFade(ms, onHidden);
    void IAnswerHost.RequestStripShow() => _vis.RequestStripShow();
    void IAnswerHost.HideStripNow() => _vis.HideStripNow();
    void IAnswerHost.HideStripFade(int ms, Action? onHidden) => _vis.HideStripFade(ms, onHidden);


    // ---------------- ぶん投げが頼む窓・解答欄（IThrowOps） ----------------

    CaptureWindow? IThrowOps.Capture => _capture;

    void IThrowOps.BeginRun()
    {
        _hint.DropForRun();
        _panel?.ResetRun();
        _vis.HidePanelForCapture();   // 消え方は即（撮影用。人が閉じるときのフェードとは別）
    }

    bool? IThrowOps.Stream(string s, string p, bool capturePending)
    {
        // ストリームの便は place を問わず必ず付ける。流す先はイベントごとに今の result_place を見て切り替える
        if (_cfg.EffectiveResultPlace() != "outside")
        {
            // 撮影中は黙る（drainの capture_pending チェックと同じ）
            if (capturePending) return null;
            var panel = _panel;
            if (panel is null) return null;
            if (s.Length > 0) panel.ApplyThink(s);
            if (p.Length > 0) panel.ApplyBody(p);
            // 本文より思考が先に来ている段だけ「思考中」の印（drainと同じ）
            return s.Length > 0 && panel.ModelText.Length == 0;
        }
        // outside: 帯は撮影矩形と絶対に重ならないので pending を待たない
        var strip = _strip;
        if (strip is null) return null;
        if (s.Length > 0 || p.Length > 0) _hint.DropStripHintForAnswer(strip);
        if (s.Length > 0) strip.ApplyThink(s);
        if (p.Length > 0) strip.ApplyBody(p);
        return s.Length > 0 && strip.ModelText.Length == 0;
    }

    void IThrowOps.ShowFinal(string text)
    {
        // done ok の仕上げは今の place 側の窓へ
        if (_cfg.EffectiveResultPlace() != "outside") _panel?.ShowFinal(text);
        else if (_strip is { } sp)
        {
            _hint.DropStripHintForAnswer(sp);
            sp.ShowFinal(text);
        }
    }

    void IThrowOps.HideAnswers()
    {
        // 失敗・やめた・空の答えの時は撮った後なので、窓の中の解答欄は薄れて消える。
        // 外の解答欄は消さずに、前の中身（案内・途中まで届いた答え）を残す
        _vis.HidePanelFade();
    }

    void IThrowOps.SetBarBusy(bool busy, bool cancelling) => _bar?.SetBusy(busy, cancelling);

    /// <summary>段落の位置は窓の中の解答欄だけへ渡す（外窓は位置配置せず読む順で上から並べる）</summary>
    void IThrowOps.SetLayout(IReadOnlyList<OcrBlock>? blocks, bool capturePending)
    {
        // 区画が分かった時点で、窓の中の解答欄を出す（撮影中は黙る）。
        // 外窓（AnswerStrip）は位置配置・マスクを行わず、文字が届いた時に読む順の通常表示で出す
        bool has = blocks is { Count: > 0 };
        bool outside = _cfg.EffectiveResultPlace() == "outside";
        _panel?.View.SetLayout(blocks);
        if (!has || outside) return;
        if (!capturePending) _panel?.RevealForLayout();
    }

    // ---------------- クイック・バーへの薄い窓口（本体は QuickReveal / WindowVisibility / WindowPlacer） ----------------

    internal const int RevealMaxMs = QuickReveal.RevealMaxMs;
    internal bool RevealPending => _quickReveal.RevealPending;
    bool IQuickOps.QuickHidden => _vis.QuickHidden;
    internal bool QuickOpenedHidden => _vis.QuickOpenedHidden;   // テスト用
    void IQuickOps.ForgetQuickHidden() => _vis.ForgetQuickHidden();
    bool IQuickOps.DiscardQuickOpened() => _vis.DiscardQuickOpened();
    bool IQuickOps.CaptureAlive() => _quickReveal.CaptureAlive();
    void IQuickOps.QuickHidePanels(int px, int py) => _quickReveal.QuickHidePanels(px, py);
    void IBarActions.BarMovingTo(Native.RECT newBarRect) => _placer.PlaceStripForBar(newBarRect);
    void IBarActions.PlaceBar(Native.RECT? target) => _placer.PlaceBar(target);
    void IQuickOps.PositionChrome() => _placer.PlaceBar();
    bool IQuickOps.ThrowBusy => Status.Busy;
    void IQuickOps.ThrowManual() => Throw.TranslateNow("manual");

    public (CaptureWindow Window, IntPtr Hwnd)? EnsureCaptureForQuick() => _quickReveal.EnsureCaptureForQuick();
    public void QuickHideWindow(int px, int py) => _quickReveal.QuickHideWindow(px, py);
    public void QuickShowWindow(bool fade = false) => _quickReveal.QuickShowWindow(fade);
    void IQuickOps.AfterQuickShown(Action a) => _quickReveal.AfterQuickShown(a);
    public void QuickPlace(int l, int t, int w, int h, bool fade = false) => _quickReveal.QuickPlace(l, t, w, h, fade);
}

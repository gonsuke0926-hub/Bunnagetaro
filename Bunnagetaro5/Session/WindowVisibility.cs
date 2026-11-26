using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Session;

/// <summary>キャプチャ窓グループ全体の開閉状態（修正J90・段階3）</summary>
internal enum WindowPhase
{
    Closed,
    Opening,
    Normal,
    Closing,
    Quitting,
}

/// <summary>
/// 修正J90（段階3・出す・隠すの係）: キャプチャ窓・バー・窓の中の解答欄・外の解答欄の
/// 「出したい」、全体の段階（開く／通常／閉じる／終了）、一時抑止（クイック・撮影中・最小化・ダミー枠ドラッグ）、
/// および WindowMotion の呼び出しを1か所にまとめる。
/// </summary>
internal sealed class WindowVisibility : IDisposable
{
    private readonly ConfigStore _cfg;
    private readonly WindowPlacer _placer;
    private readonly Action _touchPanelGen;
    private readonly Func<bool> _isCapturePending;

    private CaptureWindow? _cap;
    private ChromeBar? _bar;
    private AnswerPanel? _panel;
    private AnswerStrip? _strip;

    private WindowPhase _phase = WindowPhase.Closed;
    private long _openStartedAt;
    private DispatcherTimer? _openGuard;

    private bool _barWanted;
    private bool _panelWanted;
    private bool _stripWanted;

    private bool _quickHidden;
    private bool _quickBarHeld;
    private bool _quickOpenedHidden;
    private bool _stripResumeAfterQuick;
    private bool _panelResumeAfterQuick;

    private bool _barFits = true;
    private bool _capMinimized;
    private bool _panelMinHidden;

    private bool _panelDragHidden;
    private bool _panelDragWasActive;
    private readonly List<Action> _panelDragDeferred = new();

    internal WindowPhase Phase => _phase;
    internal bool StripWanted => _stripWanted;
    internal bool QuickHidden => _quickHidden;
    internal bool QuickBarHeld => _quickBarHeld;
    internal bool QuickOpenedHidden => _quickOpenedHidden;
    internal bool BarFits => _barFits;
    internal bool AllowPlacement => _phase is WindowPhase.Opening or WindowPhase.Normal;
    internal bool IsCapturing => _isCapturePending();
    internal bool IsPanelFadingOut => _panel is not null && WindowMotion.IsFadingOut(_panel);

    internal WindowVisibility(ConfigStore cfg, WindowPlacer placer, Action touchPanelGen, Func<bool> isCapturePending)
    {
        _cfg = cfg;
        _placer = placer;
        _touchPanelGen = touchPanelGen;
        _isCapturePending = isCapturePending;
    }

    internal void Attach(CaptureWindow cap, ChromeBar? bar, AnswerPanel? panel, AnswerStrip? strip)
    {
        Detach();
        _cap = cap;
        _bar = bar;
        _panel = panel;
        _strip = strip;

        cap.StateChanged += OnCapStateChanged;
        cap.DummyDragChanged += OnCapDummyDragChanged;
        _capMinimized = cap.WindowState == WindowState.Minimized;
        // 単体テストなどで先に cap.Show() 済みの状態からつないだ場合も通常状態として扱う
        if (cap.IsVisible)
        {
            _phase = WindowPhase.Normal;
            _barWanted = bar is not null;
        }
    }

    internal void Detach()
    {
        _openGuard?.Stop();
        _openGuard = null;
        if (_cap is { } cap)
        {
            cap.StateChanged -= OnCapStateChanged;
            cap.DummyDragChanged -= OnCapDummyDragChanged;
        }
        _cap = null;
        _bar = null;
        _panel = null;
        _strip = null;
        _phase = WindowPhase.Closed;
        _barWanted = false;
        _panelWanted = false;
        _stripWanted = false;
        _quickHidden = false;
        _quickBarHeld = false;
        _quickOpenedHidden = false;
        _stripResumeAfterQuick = false;
        _panelResumeAfterQuick = false;
        _barFits = true;
        _capMinimized = false;
        _panelMinHidden = false;
        _panelDragHidden = false;
        _panelDragWasActive = false;
        _panelDragDeferred.Clear();
    }

    public void Dispose() => Detach();

    // ---------------- 開く・閉じる・終わる ----------------

    internal void BeginOpen(bool animate, bool forQuick, Action<Window> showCloaked, Action<Window> uncloak)
    {
        var cap = _cap;
        var bar = _bar;
        var strip = _strip;
        if (cap is null) return;

        _barWanted = true;
        _panelWanted = false;
        _stripWanted = false;
        _barFits = true;
        _capMinimized = cap.WindowState == WindowState.Minimized;
        _panelMinHidden = false;
        _openStartedAt = WindowMotion.Clock();

        if (forQuick)
        {
            _phase = WindowPhase.Normal;
            showCloaked(cap);
            if (bar is not null)
            {
                bar.PlaceNearCapture();
                showCloaked(bar);
                _placer.PlaceBar();
            }
            EnsureStripHandle(strip);
            WindowMotion.HideNow(cap, 1.0);
            if (bar is not null) WindowMotion.HideNow(bar, ChromeBar.RestOpacity);
            uncloak(cap);
            if (bar is not null) uncloak(bar);
            _quickHidden = true;
            _quickBarHeld = true;
            _quickOpenedHidden = true;
            return;
        }

        if (animate)
        {
            _phase = WindowPhase.Opening;
            WindowMotion.ShowFade(cap, 1.0, WindowMotion.OpenMs, WindowMotion.OpenCloseScale, WindowMotion.Held);
            if (bar is not null)
            {
                bar.PlaceNearCapture();
                WindowMotion.ShowFade(bar, ChromeBar.RestOpacity, WindowMotion.OpenMs, WindowMotion.OpenCloseScale, WindowMotion.Held);
                _placer.PlaceBar();
            }
            EnsureStripHandle(strip);
            return;
        }

        _phase = WindowPhase.Normal;
        cap.Show();
        if (bar is not null)
        {
            bar.PlaceNearCapture();
            bar.Show();
            _placer.PlaceBar();
        }
        EnsureStripHandle(strip);
    }

    internal void CompleteOpenAnimated()
    {
        var cap = _cap;
        if (cap is null || _phase != WindowPhase.Opening) return;

        try { _bar?.UpdateLayout(); } catch (Exception ex) { AppLog.Error("bar layout on open", ex); }
        bool stripNow = false;
        if (_bar?.BarRectNow() is { } br0 && br0.Right - br0.Left >= 60)
        {
            _placer.PlaceBar();
            SyncStripForOpen(_bar.BarRectNow(), WindowMotion.Held);
            stripNow = true;
        }
        if (stripNow) ReleaseOpen();

        cap.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_cap != cap || _phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
            _placer.PlaceBar();
            bool held = _phase == WindowPhase.Opening || WindowMotion.IsHeld(cap);
            SyncStripForOpen(_bar?.BarRectNow(), held ? WindowMotion.Held : _openStartedAt);
            if (held) ReleaseOpen();
        }), DispatcherPriority.Loaded);

        if (_phase == WindowPhase.Opening)
        {
            _openGuard?.Stop();
            var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                if (ReferenceEquals(_openGuard, guard)) _openGuard = null;
                ReleaseOpen();
            };
            _openGuard = guard;
            guard.Start();
        }
    }

    internal void ReleaseOpen()
    {
        if (_phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        _openGuard?.Stop();
        _openGuard = null;
        long now = WindowMotion.Clock();
        _openStartedAt = now;
        foreach (var x in new Window?[] { _cap, _bar, _strip })
            if (x is not null && WindowMotion.IsHeld(x)) WindowMotion.Restart(x, now);
        if (_phase == WindowPhase.Opening) _phase = WindowPhase.Normal;
    }

    /// <summary>J83: 閉じ始める段階を Closing にして見回り等の再表示を止め、見えている透過窓を薄れて閉じる</summary>
    internal void BeginClose()
    {
        _openGuard?.Stop();
        _openGuard = null;
        _phase = WindowPhase.Closing;
        foreach (var (w, rest) in new (Window?, double)[]
                 { (_panel, 1.0), (_strip, 1.0), (_bar, ChromeBar.RestOpacity), (_cap, 1.0) })
        {
            if (w is not null) WindowMotion.CloseFade(w, rest, WindowMotion.CloseMs, WindowMotion.OpenCloseScale);
        }
    }

    /// <summary>J82: 終了段階を Quitting にして見回り等の再表示を止め、見えている関連窓を薄れて隠す</summary>
    internal bool BeginQuitFade(int ms)
    {
        _openGuard?.Stop();
        _openGuard = null;
        _phase = WindowPhase.Quitting;
        bool any = false;
        foreach (var (w, rest) in new (Window?, double)[]
                 { (_panel, 1.0), (_strip, 1.0), (_bar, ChromeBar.RestOpacity), (_cap, 1.0) })
        {
            if (w is not { IsVisible: true }) continue;
            try { WindowMotion.HideFade(w, rest, ms, null, WindowMotion.OpenCloseScale); any = true; }
            catch (Exception ex) { AppLog.Error("quit fade window", ex); }
        }
        return any;
    }

    private static void EnsureStripHandle(AnswerStrip? strip)
    {
        if (strip is null) return;
        // J85: 窓（HWND）と窓の設定を、出す前の今のうちに作っておく
        try { new WindowInteropHelper(strip).EnsureHandle(); }
        catch (Exception ex) { AppLog.Error("strip ensure handle", ex); }
    }

    // ---------------- バー ----------------

    internal void OnBarPlacement(bool fits)
    {
        _barFits = fits;
        var bar = _bar;
        if (bar is null || _phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        if (_quickHidden && _quickBarHeld) return;

        if (!fits)
        {
            if (bar.IsVisible) WindowMotion.HideFade(bar, ChromeBar.RestOpacity, WindowMotion.PanelFadeMs);
            return;
        }
        if (_barWanted && _phase == WindowPhase.Normal)
        {
            if (!bar.IsVisible) bar.Show();
            else if (WindowMotion.IsFadingOut(bar)) WindowMotion.ShowFade(bar, ChromeBar.RestOpacity, WindowMotion.PanelFadeMs);
        }
    }

    // ---------------- 窓の中の解答欄 ----------------

    internal bool DeferPanelWhileDragging(Action again)
    {
        if (_cap is not { SizeDragging: true } || _panel is { IsVisible: true }) return false;
        _panelDragDeferred.Add(again);
        return true;
    }

    internal void ResetPanelRun()
    {
        _panelMinHidden = false;
        _panelResumeAfterQuick = false;
        _panelDragDeferred.Clear();
    }

    internal bool RequestPanelShow()
    {
        var panel = _panel;
        if (panel is null || _phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return false;
        if (_quickHidden || IsCapturing || _capMinimized) return false;

        _panelWanted = true;
        if (!_placer.TryGetCapClientRect(out var capHwnd, out var r)) return false;

        if (!panel.IsVisible)
        {
            PositionWindowDip(panel, capHwnd, r);
            WindowMotion.ShowFade(panel, 1.0, WindowMotion.PanelFadeMs);
            _touchPanelGen();
        }
        else if (WindowMotion.IsFadingOut(panel))
        {
            WindowMotion.ShowFade(panel, 1.0, WindowMotion.PanelFadeMs);
            _touchPanelGen();
        }
        _placer.PlacePanel();
        return true;
    }

    internal void HidePanelForCapture()
    {
        _panelWanted = false;
        _panelDragHidden = false;
        _panelMinHidden = false;
        _panelResumeAfterQuick = false;
        _panelDragDeferred.Clear();
        if (_panel is null) return;
        bool vis = _panel.IsVisible;
        WindowMotion.HideNow(_panel, 1.0);   // 7.2: 撮影前の隠しは動き無しで即
        if (vis) _touchPanelGen();
    }

    internal void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null)
    {
        _panelWanted = false;
        _panelDragHidden = false;
        _panelMinHidden = false;
        _panelResumeAfterQuick = false;
        HideOverlayFade(_panel, ms, onHidden);
    }

    internal void OnPanelNoPlace()
    {
        if (_phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        if (_panel is not { IsVisible: true }) return;
        _panelMinHidden = true;
        HideOverlayFade(_panel, WindowMotion.PanelFadeMs, null);
    }

    private void OnCapStateChanged(object? sender, EventArgs e)
    {
        if (_cap is null) return;
        if (_cap.WindowState == WindowState.Minimized)
        {
            _capMinimized = true;
            if (_phase != WindowPhase.Normal) return;
            if (_panel is { IsVisible: true })
            {
                _panelMinHidden = true;
                HideOverlayFade(_panel, WindowMotion.PanelFadeMs, null);
            }
            if (_strip is { IsVisible: true })
                HideOverlayFade(_strip, WindowMotion.PanelFadeMs, null);
            return;
        }

        _capMinimized = false;
        if (_phase != WindowPhase.Normal || _quickHidden) return;
        if (_panelMinHidden)
        {
            _panelMinHidden = false;
            if (_panelWanted && !IsCapturing && _panel is { } p && (p.View.ModelText.Length > 0 || p.View.ThinkText.Length > 0))
                RequestPanelShow();
        }
        if (_stripWanted && _cfg.EffectiveResultPlace() == "outside")
            SyncStripVisibility();
    }

    private void OnCapDummyDragChanged(bool active)
    {
        var cap = _cap;
        var panel = _panel;
        if (cap is null || panel is null) return;

        if (active)
        {
            if (!panel.IsVisible) return;
            // J66: 解答欄がアクティブのまま隠すとアクティブが他アプリへ逃げ、窓ごと移動が止まる。先にキャプチャ窓へ移す
            _panelDragWasActive = panel.IsActive;
            if (_panelDragWasActive)
            {
                try { cap.Activate(); }
                catch (Exception ex) { AppLog.Error("panel drag activate cap", ex); }
            }
            _panelDragHidden = true;
            WindowMotion.HideNow(panel, 1.0);
            _touchPanelGen();
            return;
        }

        bool restore = _panelDragHidden;
        _panelDragHidden = false;
        var deferred = _panelDragDeferred.ToArray();
        _panelDragDeferred.Clear();
        if (restore && !panel.IsVisible && _phase == WindowPhase.Normal && !_quickHidden && !IsCapturing && !_capMinimized)
            ShowPanelNow();
        if (restore && _panelDragWasActive && panel.IsVisible)
        {
            try { panel.Activate(); }
            catch (Exception ex) { AppLog.Error("panel drag reactivate", ex); }
        }
        _panelDragWasActive = false;
        foreach (var a in deferred) a();
    }

    private void ShowPanelNow()
    {
        var panel = _panel;
        if (panel is null || !_placer.TryGetCapClientRect(out var capHwnd, out var r)) return;
        PositionWindowDip(panel, capHwnd, r);
        WindowMotion.Restore(panel, 1.0);
        panel.Show();
        _touchPanelGen();
        _placer.PlacePanel();
    }

    // ---------------- 外の解答欄（250ms 巡回を待たない） ----------------

    internal void RequestStripShow()
    {
        _stripWanted = true;
        if (_phase != WindowPhase.Normal) return;   // Opening 中は CompleteOpenAnimated が Held で揃えて出す
        if (_quickHidden && _quickBarHeld) return;
        SyncStripVisibility();
    }

    internal void HideStripNow()
    {
        _stripWanted = false;
        if (_strip is null) return;
        bool vis = _strip.IsVisible;
        WindowMotion.HideNow(_strip, 1.0);
        if (vis) _touchPanelGen();
    }

    internal void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null)
    {
        _stripWanted = false;
        HideOverlayFade(_strip, ms, onHidden);
    }

    internal void SyncStripVisibility(Native.RECT? barRectOpt = null, bool openFade = false, long? openStartedAt = null)
    {
        var strip = _strip;
        if (strip is null || _phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        if (_quickHidden && _quickBarHeld) return;

        if (_cfg.EffectiveResultPlace() != "outside" || !_stripWanted || _capMinimized)
        {
            HideOverlayFade(strip, WindowMotion.PanelFadeMs, null);
            return;
        }
        if (!_placer.TryPickStripRect(barRectOpt, out var capHwnd, out var rect))
        {
            HideOverlayFade(strip, WindowMotion.PanelFadeMs, null);
            return;
        }
        ShowStripCore(strip, capHwnd, rect, openFade, openStartedAt);
        if (!strip.SelfSizing) _placer.ApplyStripRect(strip, rect);
        strip.UpdateThinkMaxHeight();
    }

    internal void SyncStripForOpen(Native.RECT? barRect, long? openStartedAt)
    {
        if (_phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        if (_cfg.EffectiveResultPlace() != "outside" || !_stripWanted) return;
        SyncStripVisibility(barRect, openFade: true, openStartedAt: openStartedAt);
    }

    internal void OnStripPlacement(IntPtr capHwnd, Native.RECT? rect, bool allowVisibilityChange)
    {
        var strip = _strip;
        if (strip is null || _phase != WindowPhase.Normal) return;
        if (_quickHidden && _quickBarHeld) return;

        if (_cfg.EffectiveResultPlace() != "outside" || !_stripWanted || _capMinimized)
        {
            if (allowVisibilityChange) HideOverlayFade(strip, WindowMotion.PanelFadeMs, null);
            return;
        }
        if (rect is null)
        {
            if (allowVisibilityChange) HideOverlayFade(strip, WindowMotion.PanelFadeMs, null);
            return;
        }
        if (allowVisibilityChange && (!strip.IsVisible || WindowMotion.IsFadingOut(strip)))
            ShowStripCore(strip, capHwnd, rect.Value, openFade: false, openStartedAt: null);
        if (strip.IsVisible && !strip.SelfSizing)
        {
            _placer.ApplyStripRect(strip, rect.Value);
            strip.UpdateThinkMaxHeight();
        }
    }

    internal void OnResultPlaceChanged(string place)
    {
        if (_phase is WindowPhase.Closing or WindowPhase.Quitting or WindowPhase.Closed) return;
        if (place == "outside")
        {
            HidePanelFade(WindowMotion.PanelFadeMs, null);
            if (_phase == WindowPhase.Normal && _stripWanted) SyncStripVisibility();
            return;
        }
        // outside -> inside: 外の解答欄は即座に薄れて隠すが、_stripWanted は残す（戻したら再表示できる）
        if (_phase is WindowPhase.Normal or WindowPhase.Opening)
            HideOverlayFade(_strip, WindowMotion.PanelFadeMs, null);
    }

    private void ShowStripCore(AnswerStrip strip, IntPtr capHwnd, Native.RECT rect, bool openFade, long? openStartedAt)
    {
        if (strip.IsVisible && WindowMotion.IsFadingOut(strip))
        {
            WindowMotion.ShowFade(strip, 1.0, WindowMotion.PanelFadeMs);
            _touchPanelGen();
        }
        if (!strip.IsVisible)
        {
            PositionWindowDip(strip, capHwnd, rect);
            WindowMotion.ShowFade(strip, 1.0, openFade ? WindowMotion.OpenMs : WindowMotion.PanelFadeMs,
                openFade ? WindowMotion.OpenCloseScale : 1.0,
                openFade ? openStartedAt : null);
            _touchPanelGen();
        }
    }

    // ---------------- クイック・復元 ----------------

    internal void QuickHidePanels(int px, int py, bool barFollowsQuick)
    {
        // 修正J92: クイック開始前に窓の中の解答欄が見えていた（消えかけでない）なら、取り消し時に戻せるよう覚える
        bool hadPanel = _panel is { IsVisible: true } && !WindowMotion.IsFadingOut(_panel);
        HidePanelFade(WindowMotion.QuickPanelFadeMs, null);
        _panelResumeAfterQuick = hadPanel;
        if (!barFollowsQuick || _strip is not { } sp) return;
        bool had = _stripWanted && sp.IsVisible;
        _stripWanted = false;
        if (!sp.IsVisible) return;
        _stripResumeAfterQuick = had;
        _touchPanelGen();
        WindowMotion.SuckIn(sp, px, py, 1.0, WindowMotion.QuickMs, () => _touchPanelGen());
    }

    internal void QuickHideWindow(int px, int py, bool barFollowsQuick, Action finishReveal)
    {
        finishReveal();
        if (_quickHidden) return;
        _quickHidden = true;
        try
        {
            if (_cap is { } c) WindowMotion.SuckIn(c, px, py, 1.0, WindowMotion.QuickMs);
        }
        catch { try { _cap?.Hide(); } catch { } }

        try
        {
            if (barFollowsQuick && _bar is { } b)
            {
                _quickBarHeld = true;
                WindowMotion.SuckIn(b, px, py, ChromeBar.RestOpacity, WindowMotion.QuickMs);
            }
        }
        catch { try { _bar?.Hide(); } catch { } }
    }

    internal void QuickShowWindow(bool fade, Action<CaptureWindow> showCloakedUntilRendered)
    {
        if (!_quickHidden) return;
        try
        {
            if (_cap is { } c)
            {
                if (fade) WindowMotion.ShowFade(c, 1.0, WindowMotion.QuickRestoreMs, WindowMotion.OpenCloseScale);
                else showCloakedUntilRendered(c);
            }
        }
        catch { }

        if (_quickBarHeld)
        {
            if (_bar is { } b)
            {
                try
                {
                    WindowMotion.HideNow(b, ChromeBar.RestOpacity);
                    _placer.PlaceBar();   // _quickBarHeld の間は非表示のまま新しい位置と _barFits を決める
                    _quickBarHeld = false;
                    if (_barFits)
                    {
                        WindowMotion.ShowFade(b, ChromeBar.RestOpacity, WindowMotion.OpenMs, WindowMotion.OpenCloseScale);
                        if (_stripResumeAfterQuick)
                        {
                            _stripResumeAfterQuick = false;
                            if (!_stripWanted && _cfg.EffectiveResultPlace() == "outside" && _strip is { } sp)
                            {
                                _stripWanted = true;
                                WindowMotion.HideNow(sp, 1.0);
                                SyncStripVisibility(b.BarRectNow(), openFade: true, openStartedAt: null);
                            }
                        }
                    }
                }
                catch
                {
                    _quickBarHeld = false;
                    try { b.Show(); } catch { }
                }
            }
            _quickBarHeld = false;
        }
        bool resumePanel = _panelResumeAfterQuick;
        _quickHidden = false;
        _quickOpenedHidden = false;
        _stripResumeAfterQuick = false;
        _panelResumeAfterQuick = false;
        // 修正J92: 取り消し（fade=true）で元の窓へ戻す時だけ、クイック前に出ていた窓の中の解答欄もふわっと戻す
        // （確定 fade=false の時は直後に ThrowManual → 撮影が始まるので戻さない）
        if (fade && resumePanel && !IsCapturing && _cfg.EffectiveResultPlace() != "outside"
            && _panel is { } pn && (pn.View.ModelText.Length > 0 || pn.View.ThinkText.Length > 0))
        {
            RequestPanelShow();
        }
    }

    internal void ForgetQuickHidden()
    {
        _quickHidden = false;
        _quickBarHeld = false;
        _quickOpenedHidden = false;
        _stripResumeAfterQuick = false;
        _panelResumeAfterQuick = false;
    }

    internal bool DiscardQuickOpened()
    {
        if (!_quickOpenedHidden) return false;
        _quickOpenedHidden = false;
        _quickHidden = false;
        _quickBarHeld = false;
        _stripResumeAfterQuick = false;
        _panelResumeAfterQuick = false;
        try { _cap?.Close(); } catch (InvalidOperationException) { }
        return true;
    }

    internal void RestoreForReveal(CaptureWindow c) => WindowMotion.Restore(c, 1.0);

    // ---------------- 共通のお助け ----------------

    private void HideOverlayFade(Window? w, int ms, Action? onHidden)
    {
        if (w is null || !w.IsVisible) return;
        _touchPanelGen();
        WindowMotion.HideFade(w, 1.0, ms, () =>
        {
            _touchPanelGen();
            onHidden?.Invoke();
        });
    }

    private static void PositionWindowDip(Window w, IntPtr capHwnd, Native.RECT rect)
    {
        int dpi = (int)Native.GetDpiForWindow(capHwnd);
        double k = dpi <= 0 ? 1.0 : 96.0 / dpi;
        w.Left = rect.Left * k;
        w.Top = rect.Top * k;
        w.Width = (rect.Right - rect.Left) * k;
        w.Height = (rect.Bottom - rect.Top) * k;
    }
}

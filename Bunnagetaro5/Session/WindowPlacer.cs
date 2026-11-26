using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Session;

/// <summary>
/// 修正J89（段階2・置き場所の係）: キャプチャ窓の移動・大きさ変更を受けて、
/// ① バー → ② 窓の中の解答欄 → ③ 外の解答欄 の順に物理pxで位置を決める。
/// タスクバーより手前に出す処理（RaiseIfOutsideWorkArea と TaskbarFront）もここにまとめる。
/// 表示・非表示の判断自体は WindowVisibility へ通知して任せる（修正J90）。
/// </summary>
internal sealed class WindowPlacer : IDisposable
{
    private readonly ConfigStore _cfg;
    private readonly TaskbarFront _taskbarFront;

    private CaptureWindow? _cap;
    private ChromeBar? _bar;
    private AnswerPanel? _panel;
    private AnswerStrip? _strip;
    private WindowVisibility? _vis;
    private bool _placing;
    private Native.RECT? _lastCapRect;

    internal TaskbarFront TaskbarFront => _taskbarFront;

    internal WindowPlacer(ConfigStore cfg)
    {
        _cfg = cfg;
        // J72・J90: 下→上の順（キャプチャ窓 → 窓の中の解答欄 → バー → 外の解答欄）でタスクバーより手前を保つ
        _taskbarFront = new TaskbarFront(TaskbarFrontWindows);
    }

    internal void SetVisibility(WindowVisibility vis) => _vis = vis;

    internal void Attach(CaptureWindow cap, ChromeBar? bar, AnswerPanel? panel, AnswerStrip? strip)
    {
        Detach();
        _cap = cap;
        _bar = bar;
        _panel = panel;
        _strip = strip;

        cap.LocationChanged += OnCapBoundsChanged;
        cap.SizeChanged += OnCapBoundsChanged;
        cap.Moving += OnCapMoving;
        cap.IsVisibleChanged += OnChildVisibleChanged;
        if (bar is not null) bar.IsVisibleChanged += OnChildVisibleChanged;
        if (panel is not null) panel.IsVisibleChanged += OnChildVisibleChanged;
        if (strip is not null) strip.IsVisibleChanged += OnChildVisibleChanged;

        if (cap.IsVisible)
        {
            var capHwnd = RootHwnd(cap);
            if (capHwnd != IntPtr.Zero && Native.GetWindowRect(capHwnd, out var capRc))
            {
                _lastCapRect = capRc;
                cap.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (_cap == cap && cap.IsVisible) RaiseAboveTaskbarPreservingOrder(cap);
                }));
            }
        }
    }

    internal void Detach()
    {
        if (_cap is { } cap)
        {
            cap.LocationChanged -= OnCapBoundsChanged;
            cap.SizeChanged -= OnCapBoundsChanged;
            cap.Moving -= OnCapMoving;
            cap.IsVisibleChanged -= OnChildVisibleChanged;
        }
        if (_bar is { } bar) bar.IsVisibleChanged -= OnChildVisibleChanged;
        if (_panel is { } panel) panel.IsVisibleChanged -= OnChildVisibleChanged;
        if (_strip is { } strip) strip.IsVisibleChanged -= OnChildVisibleChanged;
        _cap = null;
        _bar = null;
        _panel = null;
        _strip = null;
        _lastCapRect = null;
    }

    public void Dispose()
    {
        Detach();
        _taskbarFront.Dispose();
    }

    private IEnumerable<IntPtr> TaskbarFrontWindows()
    {
        foreach (var w in new Window?[] { _cap, _panel, _bar, _strip })
        {
            if (w is null || !w.IsVisible) continue;
            var h = RootHwnd(w);
            if (h != IntPtr.Zero) yield return h;
        }
    }

    private void OnCapBoundsChanged(object? sender, EventArgs e) => PlaceAll(null, syncStripVisibility: true);

    private void OnCapMoving(Native.RECT wr) => PlaceAll(wr, syncStripVisibility: false);

    /// <summary>J71: 出した時（置き場所が前と同じでも）、タスクバーに重なっていたら描画タイミングで手前へ出す</summary>
    private void OnChildVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not Window w) return;
        if (ReferenceEquals(w, _cap))
        {
            var ch = RootHwnd(w);
            if (ch != IntPtr.Zero && Native.GetWindowRect(ch, out var r0)) _lastCapRect = r0;
        }
        w.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!w.IsVisible) return;
            var h = RootHwnd(w);
            if (h == IntPtr.Zero) return;
            if (ReferenceEquals(w, _cap) && Native.GetWindowRect(h, out var r))
                _lastCapRect = r;
            RaiseAboveTaskbarPreservingOrder(w);
        }));
    }

    /// <summary>J70・J90: 下→上（キャプチャ窓 → 窓の中の解答欄 → バー → 外の解答欄）の前後順を保ったままタスクバーより手前へ出す</summary>
    private void RaiseAboveTaskbarPreservingOrder(Window target, Native.RECT? rectOpt = null)
    {
        if (!target.IsVisible) return;
        var h = RootHwnd(target);
        if (h == IntPtr.Zero) return;
        Native.RECT r;
        if (rectOpt is { } supplied) r = supplied;
        else if (!Native.GetWindowRect(h, out r)) return;
        if (!Native.OutsideWorkArea(r)) return;

        Native.RaiseIfOutsideWorkArea(h, r);
        bool after = false;
        foreach (var w in new Window?[] { _cap, _panel, _bar, _strip })
        {
            if (ReferenceEquals(w, target)) { after = true; continue; }
            if (!after || w is not { IsVisible: true }) continue;
            var wh = RootHwnd(w);
            if (wh == IntPtr.Zero) continue;
            Native.SetWindowPos(wh, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }
    }

    private bool TryResolveCap(Native.RECT? target, out IntPtr capHwnd, out Native.RECT? wrOpt, out Native.RECT? clientOpt)
    {
        capHwnd = IntPtr.Zero;
        wrOpt = null;
        clientOpt = null;
        var cap = _cap;
        if (cap is null) return false;
        capHwnd = RootHwnd(cap);
        if (capHwnd == IntPtr.Zero || !Native.IsWindow(capHwnd)) return false;

        Native.RECT? effTarget = target ?? cap.DragRect;   // H6: 縁・窓ごとドラッグ中はダミー枠の矩形を最優先
        clientOpt = effTarget ?? Native.ClientRectScreen(capHwnd);
        wrOpt = effTarget is { } tg
            ? tg
            : (Native.GetWindowRect(capHwnd, out var curWr) ? curWr : null);
        return true;
    }

    internal bool TryGetCapClientRect(out IntPtr capHwnd, out Native.RECT rect)
    {
        rect = default;
        if (!TryResolveCap(null, out capHwnd, out _, out var clientOpt) || clientOpt is null) return false;
        rect = clientOpt.Value;
        return true;
    }

    internal bool TryPickStripRect(Native.RECT? barRectOpt, out IntPtr capHwnd, out Native.RECT rect)
    {
        rect = default;
        if (!TryResolveCap(null, out capHwnd, out var wrOpt, out var clientOpt)
            || wrOpt is not { } wr || clientOpt is not { } client)
            return false;
        var barRect = barRectOpt ?? _bar?.BarRectNow();
        var picked = AnswerStrip.Pick(wr, client, barRect, Native.VirtualDesktop(),
            _cfg.Settings.StripWidth, _cfg.Settings.StripHeight);
        if (picked is null) return false;
        rect = picked.Value;
        return true;
    }

    /// <summary>決まった順（① バー → ② 窓の中の解答欄 → ③ 外の解答欄）で置き直す</summary>
    internal void PlaceAll(Native.RECT? target = null, bool syncStripVisibility = false)
    {
        if (_placing || (_vis is not null && !_vis.AllowPlacement)) return;
        if (!TryResolveCap(target, out var capHwnd, out var wrOpt, out var clientOpt)) return;
        _placing = true;
        try
        {
            if (_cap?.DragRect is null && _cap is { IsVisible: true }
                && Native.GetWindowRect(capHwnd, out var curCap)
                && (_lastCapRect is null || !SameRect(_lastCapRect.Value, curCap)))
            {
                _lastCapRect = curCap;
                RaiseAboveTaskbarPreservingOrder(_cap, curCap);
            }
            var barRect = PlaceBarCore(wrOpt, clientOpt);
            PlacePanelCore(clientOpt);
            PlaceStripCore(capHwnd, wrOpt, clientOpt, barRect, syncStripVisibility);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>250ms 巡回の保険: 外の解答欄のリサイズ保存待ちを消化し、念のため位置を揃える</summary>
    internal void TickInsurance()
    {
        _strip?.FlushPendingSizeSave();
        if (_vis is not null && !_vis.AllowPlacement) return;
        PlaceAll(null, syncStripVisibility: true);
    }

    internal void PlaceBar(Native.RECT? target = null)
    {
        if (_placing || (_vis is not null && !_vis.AllowPlacement)) return;
        if (!TryResolveCap(target, out _, out var wrOpt, out var clientOpt)) return;
        _placing = true;
        try { PlaceBarCore(wrOpt, clientOpt); }
        finally { _placing = false; }
    }

    internal void PlacePanel(Native.RECT? target = null)
    {
        if (_vis is not null && !_vis.AllowPlacement) return;
        if (!TryResolveCap(target, out _, out _, out var clientOpt)) return;
        PlacePanelCore(clientOpt);
    }

    internal void PlaceStrip(Native.RECT? barRect, bool syncVisibility = true)
    {
        _strip?.FlushPendingSizeSave();
        if (_vis is not null && !_vis.AllowPlacement) return;
        if (!TryResolveCap(null, out var capHwnd, out var wrOpt, out var clientOpt)) return;
        PlaceStripCore(capHwnd, wrOpt, clientOpt, barRect, syncVisibility);
    }

    /// <summary>F3: バーだけが newBarRect へ動く直前に、外の解答欄をそのバーに合わせて同じフレームで置く</summary>
    internal void PlaceStripForBar(Native.RECT newBarRect)
    {
        try
        {
            if (_vis is not null && (!_vis.AllowPlacement || !_vis.StripWanted)) return;
            var strip = _strip;
            if (strip is null || strip.SelfSizing || !strip.IsVisible) return;
            if (_cfg.EffectiveResultPlace() != "outside") return;
            if (!TryResolveCap(null, out _, out var wrOpt, out var clientOpt)) return;
            if (wrOpt is not { } wr) return;
            var client = clientOpt ?? wr;
            var pick = AnswerStrip.Pick(wr, client, newBarRect, Native.VirtualDesktop(),
                _cfg.Settings.StripWidth, _cfg.Settings.StripHeight);
            if (pick is { } rect) ApplyStripRect(strip, rect);
        }
        catch (Exception ex) { AppLog.Error("strip placeAtBar", ex); }
    }

    private Native.RECT? PlaceBarCore(Native.RECT? wrOpt, Native.RECT? clientOpt)
    {
        var bar = _bar;
        if (bar is null) return null;
        var barHwnd = RootHwnd(bar);
        if (barHwnd == IntPtr.Zero) return null;

        if (clientOpt is null)
        {
            _vis?.OnBarPlacement(fits: false);
            return null;
        }
        if (wrOpt is not { } wr || !Native.GetWindowRect(barHwnd, out var br)) return null;
        int bw = br.Right - br.Left, bh = br.Bottom - br.Top;
        if (bw < 60)
        {
            // 初回: レイアウト前の極小矩形。レイアウト確定後に一度やり直す
            bar.Dispatcher.BeginInvoke(new Action(() => PlaceAll(null)), DispatcherPriority.Loaded);
            return null;
        }

        var pick = bar.PickPosition(wr, clientOpt.Value, br);
        if (pick is null)
        {
            _vis?.OnBarPlacement(fits: false);
            return null;
        }
        _vis?.OnBarPlacement(fits: true);
        var newRect = ChromeBar.RectAt(pick.Value.X, pick.Value.Y, bw, bh);
        // 同じ位置なら動かさない（Moving と LocationChanged の二重呼びで再描画を連鎖させない）
        if (br.Left != pick.Value.X || br.Top != pick.Value.Y)
        {
            Native.SetWindowPos(barHwnd, IntPtr.Zero, pick.Value.X, pick.Value.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            // J69/J71: 置き場所が変わった時と出した時だけ手前へ出す（同じ場所のままでは出し直さない）
            if (bar.IsVisible) RaiseAboveTaskbarPreservingOrder(bar, newRect);
        }
        bar.RememberPos(pick.Value.X, pick.Value.Y);
        return bar.IsVisible ? newRect : null;
    }

    private void PlacePanelCore(Native.RECT? clientOpt)
    {
        var panel = _panel;
        if (panel is null) return;
        if (clientOpt is null)
        {
            _vis?.OnPanelNoPlace();
            return;
        }
        if (!panel.IsVisible) return;
        var hwnd = RootHwnd(panel);
        if (hwnd == IntPtr.Zero) return;

        var r = clientOpt.Value;
        if (!Native.GetWindowRect(hwnd, out var cur) || !SameRect(cur, r))
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            // J70/J71: 置き場所が変わった時と出した時だけ手前へ出す（同じ場所のままでは出し直さない）
            RaiseAboveTaskbarPreservingOrder(panel, r);
        }
        panel.UpdateThinkMaxHeight();
    }

    private void PlaceStripCore(IntPtr capHwnd, Native.RECT? wrOpt, Native.RECT? clientOpt,
                                Native.RECT? barRect, bool allowVisibilityChange)
    {
        var strip = _strip;
        if (strip is null) return;
        Native.RECT? picked = wrOpt is { } wr && clientOpt is { } client
            ? AnswerStrip.Pick(wr, client, barRect, Native.VirtualDesktop(),
                _cfg.Settings.StripWidth, _cfg.Settings.StripHeight)
            : null;
        _vis?.OnStripPlacement(capHwnd, picked, allowVisibilityChange);
    }

    internal void ApplyStripRect(AnswerStrip strip, Native.RECT rect)
    {
        var own = RootHwnd(strip);
        if (own == IntPtr.Zero) return;
        // 同じ矩形のままでは動かさず、タスクバーより手前にも出し直さない（J71・J74）
        if (Native.GetWindowRect(own, out var cur) && SameRect(cur, rect)) return;
        Native.SetWindowPos(own, IntPtr.Zero, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        RaiseAboveTaskbarPreservingOrder(strip, rect);
    }

    private static bool SameRect(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static IntPtr RootHwnd(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        return h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
    }
}

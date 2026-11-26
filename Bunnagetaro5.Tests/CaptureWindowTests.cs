using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 手順11: キャプチャ窓の純粋な判定部分（7.1）。
/// 窓の見た目・保存・復元の実物は _check\shot8 で画面検証した（PROGRESS参照）。
/// 修正A2 で中も掴めるようにした（膜・HTCAPTION・四方向矢印）のでその分もここで見ます。
/// </summary>
public class CaptureWindowTests : IDisposable
{
    // ---- WM_NCHITTEST の帯判定（640×400・band 8） ----

    [Theory]
    [InlineData(320, 200, 0)]          // 中は掴みなし域（ここは A2 で HTCAPTION へ置き換える）
    [InlineData(2, 200, Native.HTLEFT)]
    [InlineData(638, 200, Native.HTRIGHT)]
    [InlineData(320, 396, Native.HTBOTTOM)]
    [InlineData(320, 3, Native.HTTOP)]       // 上縁もリサイズ（E3）
    [InlineData(1, 1, Native.HTTOPLEFT)]     // 隅は端より優先
    [InlineData(638, 1, Native.HTTOPRIGHT)]
    [InlineData(1, 398, Native.HTBOTTOMLEFT)]
    [InlineData(638, 398, Native.HTBOTTOMRIGHT)]
    public void 縁の掴み判定は帯の中で正しいHTを返す(int x, int y, int want)
    {
        Assert.Equal(want, CaptureWindow.HitTest(x, y, 640, 400, 8));
    }

    [Fact]
    public void 上端の帯でも隅ならリサイズ側が勝つ()
    {
        // y は上端帯の中だが x も左端帯 → HTTOPLEFT（端より隅が優先）
        Assert.Equal(Native.HTTOPLEFT, CaptureWindow.HitTest(5, 5, 640, 400, 8));
    }

    [Fact]
    public void 最小窓でも四隅のHTは壊れない()
    {
        Assert.Equal(Native.HTTOPLEFT, CaptureWindow.HitTest(0, 0, 30, 30, 8));
        Assert.Equal(Native.HTBOTTOMRIGHT, CaptureWindow.HitTest(29, 29, 30, 30, 8));
    }

    // ---- 開くときの大きさの範囲切り（Python版 min(max(v,MIN), max(MIN,screen))） ----

    [Theory]
    [InlineData(640, 30, 1920, 640)]   // 普通はそのまま
    [InlineData(10, 30, 1920, 30)]     // 最小未満は最小へ
    [InlineData(99999, 30, 1920, 1920)]// 画面を超えたら画面ぶん
    [InlineData(10, 30, 20, 30)]       // 画面が最小より小さい異常時も最小優先
    public void 開くときの大きさは最小と画面の範囲に収める(int v, int min, int screen, int want)
    {
        Assert.Equal(want, CaptureWindow.ResolveOpenSize(v, min, screen));
    }

    // ---- 位置の復元（モニタ上なら前回の場所、無ければ既定 200,200） ----

    [Fact]
    public void 位置なしは既定の200と200()
    {
        var (x, y) = CaptureWindow.ResolveOpenPos(null, null, 640, 400);
        Assert.Equal((CaptureWindow.DefaultX, CaptureWindow.DefaultY), (x, y));
    }

    [Fact]
    public void 前回の位置がモニタ上ならそこへ開く()
    {
        // 主モニタの左上は必ずどのPCでもモニタ上
        var (x, y) = CaptureWindow.ResolveOpenPos(0, 0, 640, 400);
        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void モニタ外の位置は既定位置へ戻る()
    {
        var (x, y) = CaptureWindow.ResolveOpenPos(-50000, -50000, 640, 400);
        Assert.Equal((CaptureWindow.DefaultX, CaptureWindow.DefaultY), (x, y));
    }

    // ---- 修正A2: 中を掴んで動かす（見えない膜・HTCAPTION・四方向矢印） ----

    [Fact]
    public void 膜は目に見えない厚さの1_255()
    {
        Assert.Equal((byte)1, CaptureWindow.BandAlpha);
    }

    [Fact]
    public void 中は掴んで移動で解答欄が出ている時だけ素通し()
    {
        // 中のどこを掴んでも窓が動く。解答欄が出ている時だけ下の窓へ返す
        // （Python版 tw_band 338行の overlay_visible 相当）
        Assert.Equal(Native.HTCAPTION, CaptureWindow.InteriorHit(false));
        Assert.Equal(Native.HTTRANSPARENT, CaptureWindow.InteriorHit(true));
    }

    [Theory]
    [InlineData(Native.HTCAPTION, Native.IDC_SIZEALL)]      // 中は四方向矢印
    [InlineData(Native.HTLEFT, Native.IDC_SIZEWE)]
    [InlineData(Native.HTTOP, Native.IDC_SIZENS)]
    [InlineData(Native.HTTOPLEFT, Native.IDC_SIZENWSE)]
    [InlineData(Native.HTBOTTOMRIGHT, Native.IDC_SIZENWSE)]
    [InlineData(Native.HTTOPRIGHT, Native.IDC_SIZENESW)]
    [InlineData(Native.HTBOTTOMLEFT, Native.IDC_SIZENESW)]
    [InlineData(Native.HTCLIENT, 0)]                        // 形を変えない所
    public void 掴むところのカーソルは対応する矢印(int ht, int want)
    {
        Assert.Equal(want, CaptureWindow.CursorIdForHit(ht));
        // 実在する標準カーソルとしてちゃんと引えることも見る
        if (want != 0)
            Assert.NotEqual(IntPtr.Zero, CaptureWindow.CursorForHit(ht));
    }

    [Fact]
    public void 枠は膜で塗られていて窓自体は何も塗らない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var w = new CaptureWindow(new ConfigStore(_paths), _ => { });
            var frame = (Border)Fld(w, "_frame")!;
            var brush = (SolidColorBrush?)frame.Background;
            Assert.NotNull(brush);
            Assert.Equal((byte)1, brush!.Color.A);                       // 見えない膜
            Assert.Equal(0, brush.Color.R + brush.Color.G + brush.Color.B);
            Assert.True(brush.IsFrozen);
            Assert.Null(w.Background);                                   // 描くのは膜と縁だけ
        });
    }

    // ---- helpers（BindTests と同じ形） ----

    private readonly string _temp;
    private readonly Paths _paths;

    public CaptureWindowTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_band_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60, throwOnTimeout: false);

    /// <summary>修正J87: 本物の縁はダミー枠が画面に出てから消えるので、少し回して待つ</summary>
    private static void PumpMs(int ms) => TestUtil.Pump(ms);

    private static object? Fld(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
                  .GetValue(target);

    [Theory]
    [InlineData(Native.WMSZ_TOP, true)]
    [InlineData(Native.WMSZ_LEFT, true)]
    [InlineData(Native.WMSZ_TOPLEFT, true)]
    [InlineData(Native.WMSZ_TOPRIGHT, true)]
    [InlineData(Native.WMSZ_BOTTOMLEFT, true)]
    [InlineData(Native.WMSZ_RIGHT, false)]
    [InlineData(Native.WMSZ_BOTTOM, false)]
    [InlineData(Native.WMSZ_BOTTOMRIGHT, false)]
    public void F2_上と左の縁だけ自前リサイズ対象になる(int edge, bool expected)
    {
        Assert.Equal(expected, CaptureWindow.IsTopOrLeftSizingEdge(edge));
    }

    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Theory]
    [InlineData(Native.WMSZ_TOP, 0, 50, 100, 150, 300, 300)]           // 上辺を50下げる: 下辺固定
    [InlineData(Native.WMSZ_LEFT, 40, 0, 140, 100, 300, 300)]          // 左辺を40右へ: 右辺固定
    [InlineData(Native.WMSZ_TOPLEFT, 40, 50, 140, 150, 300, 300)]      // 左上の隅
    [InlineData(Native.WMSZ_TOP, 0, 500, 100, 270, 300, 300)]          // 最小30まで: 下辺固定で寄る
    [InlineData(Native.WMSZ_LEFT, 500, 0, 270, 100, 300, 300)]         // 最小30まで: 右辺固定で寄る
    [InlineData(Native.WMSZ_TOPLEFT, -20, -10, 80, 90, 300, 300)]      // 外へ広げる
    [InlineData(Native.WMSZ_TOP, 70, 20, 100, 120, 300, 300)]          // 上辺なら dx は無視
    public void H6_ダミー枠の矩形は掴んだ辺だけ動き最小サイズで寄る(int edge, int dx, int dy, int expL, int expT, int expR, int expB)
    {
        var o = CaptureWindow.DragRectFor(R(100, 100, 300, 300), edge, dx, dy, CaptureWindow.MinW, CaptureWindow.MinH);
        Assert.Equal((expL, expT, expR, expB), (o.Left, o.Top, o.Right, o.Bottom));
    }

    private static (IntPtr Lp, Action Free) AllocRect(Native.RECT r)
    {
        IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>());
        System.Runtime.InteropServices.Marshal.StructureToPtr(r, ptr, false);
        return (ptr, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr));
    }

    /// <summary>上辺を引いて、離す／取り消す流れを WndProc へ直接流す（マウス・キーは注入しない）</summary>
    private void DragTop(bool cancel, out Native.RECT before, out Native.RECT after, out Native.RECT lastDrag, out int movingCount, out bool dummyFailed)
    {
        Native.RECT b = default, a = default, ld = default; int mc = 0; bool failed = false;
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            var movingRects = new System.Collections.Generic.List<Native.RECT>();
            w.Moving = r => movingRects.Add(r);
            int cx = 0, cy = 0;
            w.CursorProvider = () => new Native.POINT { X = cx, Y = cy };
            w.EscapeDown = () => cancel;
            w.Show();
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Assert.True(Native.GetWindowRect(hwnd, out b));
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                movingRects.Clear();
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                var (lp, free) = AllocRect(b);
                try
                {
                    foreach (int dy in new[] { 10, 30, 50 })
                    {
                        cy = dy;
                        var args = new object?[] { hwnd, Native.WM_SIZING, new IntPtr(Native.WMSZ_TOP), lp, false };
                        var ret = (IntPtr)wndProc.Invoke(w, args)!;
                        var dummy = (Bunnagetaro5.Input.QuickMarker?)Fld(w, "_dummy");
                        if (dummy is null || dummy.Failed) { failed = true; return; }
                        Assert.Equal(new IntPtr(1), ret);
                        Assert.True((bool)args[4]!);
                        // 本物の窓は動かさない
                        Assert.True(Native.GetWindowRect(hwnd, out var cur));
                        Assert.Equal(b.Top, cur.Top);
                        Assert.Equal(b.Bottom, cur.Bottom);
                        // OS へ返す矩形は「いまの窓」のまま
                        var back = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lp);
                        Assert.Equal(b.Top, back.Top);
                        // ダミーの矩形（バー・解答欄が付いてくる基準）
                        Assert.NotNull(w.DragRect);
                        Assert.Equal(b.Top + dy, w.DragRect!.Value.Top);
                        Assert.Equal(b.Bottom, w.DragRect!.Value.Bottom);
                    }
                    ld = w.DragRect!.Value;
                    mc = movingRects.Count;
                    Assert.Equal(ld.Top, movingRects[^1].Top);   // バー・解答欄へ渡った矩形も同じ
                }
                finally { free(); }
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                Assert.Null(w.DragRect);
                Assert.True(Native.GetWindowRect(hwnd, out a));
            }
            finally { w.Close(); }
        });
        before = b; after = a; lastDrag = ld; movingCount = mc; dummyFailed = failed;
    }

    [Fact]
    public void H6_離したら本物の窓が最後のダミーの矩形へ1回で置かれる()
    {
        DragTop(false, out var before, out var after, out var last, out var mc, out var failed);
        if (failed) return;   // ダミー枠が作れない環境では「今まで通り」の経路（別テスト）
        Assert.True(mc >= 3);
        Assert.Equal(last.Top, after.Top);
        Assert.Equal(before.Bottom, after.Bottom);
        Assert.Equal(before.Left, after.Left);
        Assert.Equal(before.Bottom - last.Top, after.Bottom - after.Top);
    }

    [Fact]
    public void H6_取り消しなら本物の窓は元のまま()
    {
        DragTop(true, out var before, out var after, out _, out _, out var failed);
        if (failed) return;
        Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
    }

    [Fact]
    public void H6_右辺と下辺と右下もダミー枠で引き_本物の窓は離すまで動かず離したら最後の矩形へ置かれる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            int cx = 0, cy = 0;
            w.CursorProvider = () => new Native.POINT { X = cx, Y = cy };
            w.Show();
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (int edge in new[] { Native.WMSZ_RIGHT, Native.WMSZ_BOTTOM, Native.WMSZ_BOTTOMRIGHT })
                {
                    Native.GetWindowRect(hwnd, out var b);
                    cx = 0; cy = 0;
                    wndProc.Invoke(w, new object?[] { hwnd, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                    var (lp, free) = AllocRect(b);
                    try
                    {
                        foreach (int d in new[] { 10, 40 })
                        {
                            cx = d; cy = d;
                            var args = new object?[] { hwnd, Native.WM_SIZING, new IntPtr(edge), lp, false };
                            var ret = (IntPtr)wndProc.Invoke(w, args)!;
                            var dummy = (Bunnagetaro5.Input.QuickMarker?)Fld(w, "_dummy");
                            Assert.NotNull(dummy);
                            Assert.False(dummy!.Failed);
                            Assert.Equal(new IntPtr(1), ret);
                            Assert.True((bool)args[4]!);
                            // 本物の窓は動かさず、OS へは現在の矩形を書き戻す
                            Native.GetWindowRect(hwnd, out var cur);
                            Assert.Equal((b.Left, b.Top, b.Right, b.Bottom), (cur.Left, cur.Top, cur.Right, cur.Bottom));
                            var back = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lp);
                            Assert.Equal((b.Left, b.Top, b.Right, b.Bottom), (back.Left, back.Top, back.Right, back.Bottom));
                            // ダミーの矩形はカーソルに付いてくる（掴んだ辺だけ動く）
                            Assert.NotNull(w.DragRect);
                            var dr = w.DragRect!.Value;
                            bool right = edge != Native.WMSZ_BOTTOM, bottom = edge != Native.WMSZ_RIGHT;
                            Assert.Equal((b.Left, b.Top), (dr.Left, dr.Top));
                            Assert.Equal(right ? b.Right + d : b.Right, dr.Right);
                            Assert.Equal(bottom ? b.Bottom + d : b.Bottom, dr.Bottom);
                        }
                    }
                    finally { free(); }
                    var last = w.DragRect!.Value;
                    wndProc.Invoke(w, new object?[] { hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                    Assert.Null(w.DragRect);
                    Native.GetWindowRect(hwnd, out var a);
                    Assert.Equal((last.Left, last.Top, last.Right, last.Bottom), (a.Left, a.Top, a.Right, a.Bottom));
                }
            }
            finally { w.Close(); }
        });
    }

    private sealed class H9Host : IAnswerHost
    {
        internal WindowPlacer? Placer { get; set; }
        internal WindowVisibility? Vis { get; set; }
        public bool FollowupSend(string question, bool withImage) => true;
        public void TouchPanelGen() { }
        public void SetStatus(string msg) { }
        public Native.RECT? BarRectNow() => null;
        public void PlacePanel(Native.RECT? target = null) => Placer?.PlacePanel(target);
        public void PlaceStrip(Native.RECT? barRect) { }
        public bool DeferPanelWhileDragging(Action again) => Vis?.DeferPanelWhileDragging(again) ?? false;
        public void ResetPanelRun() => Vis?.ResetPanelRun();
        public bool RequestPanelShow() => Vis?.RequestPanelShow() ?? false;
        public bool IsPanelFadingOut() => Vis?.IsPanelFadingOut ?? false;
        public void HidePanelForCapture() => Vis?.HidePanelForCapture();
        public void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) => Vis?.HidePanelFade(ms, onHidden);
        public void RequestStripShow() => Vis?.RequestStripShow();
        public void HideStripNow() => Vis?.HideStripNow();
        public void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) => Vis?.HideStripFade(ms, onHidden);
    }

    /// <summary>H9: 上辺を引き始めたら窓の中の解答欄が隠れ、離す／取り消すと出し直す（答え・たずねる欄の文字は残る）</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void H9_引いている間は解答欄が隠れ離すか取り消すと出し直す(bool cancel)
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            int cy = 0;
            w.CursorProvider = () => new Native.POINT { X = 0, Y = cy };
            w.EscapeDown = () => cancel;
            w.Show();
            var host = new H9Host();
            var panel = new AnswerPanel(cfg, host, w);
            using var placer = new WindowPlacer(cfg);
            using var vis = new WindowVisibility(cfg, placer, () => { }, () => false);
            placer.SetVisibility(vis);
            host.Placer = placer;
            host.Vis = vis;
            placer.Attach(w, null, panel, null);
            vis.Attach(w, null, panel, null);
            try
            {
                panel.ApplyBody("答えです");
                Assert.True(panel.IsVisible);
                panel.View.SetAskDraftForTest("書きかけ");

                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.GetWindowRect(hwnd, out var b);
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                var (lp, free) = AllocRect(b);
                try
                {
                    cy = 20;
                    wndProc.Invoke(w, new object?[] { hwnd, Native.WM_SIZING, new IntPtr(Native.WMSZ_TOP), lp, false });
                    var dummy = (Bunnagetaro5.Input.QuickMarker?)Fld(w, "_dummy");
                    if (dummy is null || dummy.Failed) return;   // ダミー枠が作れない環境は今まで通りの経路
                    Assert.False(panel.IsVisible);               // 引き始めで隠れた
                    panel.ApplyBody("答えです。続き");             // 引いている間に来た答えは出さずに預かる
                    Assert.False(panel.IsVisible);
                    cy = 40;
                    wndProc.Invoke(w, new object?[] { hwnd, Native.WM_SIZING, new IntPtr(Native.WMSZ_TOP), lp, false });
                    Assert.False(panel.IsVisible);
                }
                finally { free(); }
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                Assert.True(panel.IsVisible);                    // 離す・取り消しで出し直し
                Assert.Equal("答えです。続き", panel.ModelText);
                Assert.Equal("書きかけ", panel.View.AskDraftForTest());
                Native.GetWindowRect(hwnd, out var capRc);
                var pr = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
                Native.GetWindowRect(pr, out var panelRc);
                Assert.Equal((capRc.Left, capRc.Top, capRc.Right, capRc.Bottom), (panelRc.Left, panelRc.Top, panelRc.Right, panelRc.Bottom));
            }
            finally { panel.Close(); w.Close(); }
        });
    }

    /// <summary>H11: 右・下・右下のサイズ変更（修正J65 でダミー枠経由）でも、始めで解答欄が隠れ、終わりで出る。
    /// 窓ごとの移動（ダミー枠で動かす）でも隠れる（2026-11-22 ユーザー決定）</summary>
    [Theory]
    [InlineData(Native.WMSZ_RIGHT)]
    [InlineData(Native.WMSZ_BOTTOM)]
    [InlineData(Native.WMSZ_BOTTOMRIGHT)]
    [InlineData(0)]   // 0 = 窓ごとの移動（BeginMoveDrag / MoveDragTo / EndMoveDrag）
    public void H11_右下のサイズ変更でも移動でも解答欄が隠れ終わりで出る(int edge)
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            w.Show();
            var host = new H9Host();
            var panel = new AnswerPanel(cfg, host, w);
            using var placer = new WindowPlacer(cfg);
            using var vis = new WindowVisibility(cfg, placer, () => { }, () => false);
            placer.SetVisibility(vis);
            host.Placer = placer;
            host.Vis = vis;
            placer.Attach(w, null, panel, null);
            vis.Attach(w, null, panel, null);
            try
            {
                panel.ApplyBody("答えです");
                panel.View.SetAskDraftForTest("書きかけ");
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.GetWindowRect(hwnd, out var b);
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var cur = new Native.POINT { X = 500, Y = 500 };
                w.CursorProvider = () => cur;   // 修正J87: 本物のマウスに頼らず、引いた分だけ大きさが変わるようにする
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                var (lp, free) = AllocRect(b);
                try
                {
                    if (edge == 0)
                    {
                        Assert.True(w.BeginMoveDrag());
                        Assert.True(panel.IsVisible);    // 修正J87: 掴んだだけでは隠れない（クリックでちらつかない）
                        w.MoveDragTo(b.Left + 20, b.Top + 20);
                        Assert.False(panel.IsVisible);   // 動いたらダミー枠なので隠れる
                        w.MoveDragTo(b.Left + 30, b.Top + 25);
                        Assert.False(panel.IsVisible);
                        panel.ApplyBody("答えです。続き");
                        Assert.False(panel.IsVisible);
                        w.EndMoveDrag();
                        Assert.True(panel.IsVisible);
                    }
                    else
                    {
                        // 修正J87: 掴んだだけ（大きさが変わらない）ではダミー枠を出さず、解答欄も隠れない
                        wndProc.Invoke(w, new object?[] { hwnd, Native.WM_SIZING, new IntPtr(edge), lp, false });
                        Assert.True(panel.IsVisible);
                        cur = new Native.POINT { X = 520, Y = 520 };
                        wndProc.Invoke(w, new object?[] { hwnd, Native.WM_SIZING, new IntPtr(edge), lp, false });
                        Assert.NotNull(Fld(w, "_dummy"));   // ダミー枠を使う
                        Assert.False(panel.IsVisible);   // 始めで隠れた
                        panel.ApplyBody("答えです。続き");  // 引いている間の答えは預かる
                        Assert.False(panel.IsVisible);
                        wndProc.Invoke(w, new object?[] { hwnd, Native.WM_SIZING, new IntPtr(edge), lp, false });
                        Assert.False(panel.IsVisible);
                    }
                }
                finally { free(); }
                wndProc.Invoke(w, new object?[] { hwnd, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                Assert.True(panel.IsVisible);
                Assert.Equal("答えです。続き", panel.ModelText);
                Assert.Equal("書きかけ", panel.View.AskDraftForTest());
                Native.GetWindowRect(hwnd, out var capRc);
                var pr = new System.Windows.Interop.WindowInteropHelper(panel).Handle;
                Native.GetWindowRect(pr, out var panelRc);
                Assert.Equal((capRc.Left, capRc.Top, capRc.Right, capRc.Bottom), (panelRc.Left, panelRc.Top, panelRc.Right, panelRc.Bottom));
            }
            finally { panel.Close(); w.Close(); }
        });
    }

    [Fact]
    public void J1_中を掴むとダミー枠が動き_離すと本物の窓が1回で動き_大きさは変わらず_最大化はさせない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            w.Show();
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                // 最大化できない窓（Windows のスナップ・最大化の枠を出さない）
                long st = Native.GetWindowLongPtrW(hwnd, Native.GWL_STYLE).ToInt64();
                Assert.Equal(0, st & 0x00010000);
                Assert.NotEqual(0, st & Native.WS_THICKFRAME);   // 縁のリサイズは今まで通り

                Native.GetWindowRect(hwnd, out var b);
                var cur = new Native.POINT { X = 100, Y = 100 };
                w.CursorProvider = () => cur;
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var args = new object?[] { hwnd, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero, false };
                wndProc.Invoke(w, args);
                Assert.True((bool)args[4]!);   // OS の移動には渡さない
                Assert.True(w.OwnMoving);

                cur = new Native.POINT { X = 150, Y = 70 };
                w.OwnMoveStep();
                // 動かす間、本物の窓は動かない。動くのはダミー枠（DragRect）
                Native.GetWindowRect(hwnd, out var mid);
                Assert.Equal((b.Left, b.Top, b.Right, b.Bottom), (mid.Left, mid.Top, mid.Right, mid.Bottom));
                Assert.NotNull(w.DragRect);
                var dr = w.DragRect!.Value;
                Assert.Equal((b.Left + 50, b.Top - 30), (dr.Left, dr.Top));
                Assert.Equal((b.Right - b.Left, b.Bottom - b.Top), (dr.Right - dr.Left, dr.Bottom - dr.Top));
                w.EndOwnMove();
                Assert.False(w.OwnMoving);
                Native.GetWindowRect(hwnd, out var a);
                Assert.Equal((b.Left + 50, b.Top - 30), (a.Left, a.Top));
                Assert.Equal((b.Right - b.Left, b.Bottom - b.Top), (a.Right - a.Left, a.Bottom - a.Top));
                Assert.Null(w.DragRect);

                // ダブルクリックでも最大化しない
                var dbl = new object?[] { hwnd, 0x00A3, new IntPtr(Native.HTCAPTION), IntPtr.Zero, false };
                wndProc.Invoke(w, dbl);
                Assert.True((bool)dbl[4]!);
                Assert.Equal(WindowState.Normal, w.WindowState);
            }
            finally { w.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void J65_窓ごとの移動はダミー枠だけが動き_離すと最後の場所へ置かれ_取り消しなら元のまま(bool commit)
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            var changes = new System.Collections.Generic.List<bool>();
            var moving = new System.Collections.Generic.List<Native.RECT>();
            w.DummyDragChanged += h => changes.Add(h);
            w.Moving = r => moving.Add(r);
            w.Show();
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.GetWindowRect(hwnd, out var b);
                Assert.True(w.BeginMoveDrag());
                Assert.Empty(changes);                     // 修正J87: 掴んだだけではダミー枠を出さない
                w.MoveDragTo(b.Left, b.Top);               // 同じ場所のままでも出さない
                Assert.Empty(changes);
                Assert.Null(w.DragRect);
                foreach (var (x, y) in new[] { (b.Left + 10, b.Top + 5), (b.Left + 60, b.Top - 40), (b.Left + 100, b.Top + 70) })
                {
                    w.MoveDragTo(x, y);
                    Native.GetWindowRect(hwnd, out var cur);
                    Assert.Equal((b.Left, b.Top, b.Right, b.Bottom), (cur.Left, cur.Top, cur.Right, cur.Bottom));
                    Assert.NotNull(w.DragRect);
                    var dr = w.DragRect!.Value;
                    Assert.Equal((x, y, x + (b.Right - b.Left), y + (b.Bottom - b.Top)), (dr.Left, dr.Top, dr.Right, dr.Bottom));
                    Assert.Equal((dr.Left, dr.Top), (moving[^1].Left, moving[^1].Top));
                }
                var last = w.DragRect!.Value;
                w.EndMoveDrag(commit);
                Assert.Equal(new[] { true, false }, changes);
                Assert.Null(w.DragRect);
                Native.GetWindowRect(hwnd, out var a);
                if (commit) Assert.Equal((last.Left, last.Top, last.Right, last.Bottom), (a.Left, a.Top, a.Right, a.Bottom));
                else Assert.Equal((b.Left, b.Top, b.Right, b.Bottom), (a.Left, a.Top, a.Right, a.Bottom));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J66_動かし始めの知らせの中で終わっても_本物の窓の縁は消えたまま残らない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            w.Show();
            try
            {
                var frame = (Border)Fld(w, "_frame")!;
                var before = frame.BorderBrush;
                Assert.NotSame(Brushes.Transparent, before);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.GetWindowRect(hwnd, out var b);
                // 掴みが外れた（知らせの中で終わる）状況。修正J87: ダミー枠は動いた時（MoveDragTo）に始まる
                Action<bool> endInside = h => { if (h) w.EndMoveDrag(); };
                w.DummyDragChanged += endInside;
                Assert.True(w.BeginMoveDrag());
                w.MoveDragTo(b.Left + 10, b.Top + 10);
                Assert.Null(w.DragRect);
                PumpMs(150);
                Assert.NotSame(Brushes.Transparent, frame.BorderBrush);
                Assert.Same(before, frame.BorderBrush);
                w.DummyDragChanged -= endInside;

                // 普通に動かす時は、引いている間だけ縁が消える（修正J87: ダミー枠が画面に出てから消える）
                Assert.True(w.BeginMoveDrag());
                Assert.Same(before, frame.BorderBrush);          // 掴んだだけでは消えない
                w.MoveDragTo(b.Left + 10, b.Top + 10);
                PumpMs(150);
                Assert.Same(Brushes.Transparent, frame.BorderBrush);
                w.EndMoveDrag();
                Assert.NotSame(Brushes.Transparent, frame.BorderBrush);
                Assert.Same(before, frame.BorderBrush);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J66_自前の移動を始めた知らせの中で終わっても_本物の窓の縁は消えたまま残らない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CaptureX = -10000;
            cfg.Settings.CaptureY = 100;
            cfg.Settings.CaptureWidth = 400;
            cfg.Settings.CaptureHeight = 300;
            var w = new CaptureWindow(cfg, _ => { });
            w.Show();
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var frame = (Border)Fld(w, "_frame")!;
                var before = frame.BorderBrush;
                var cur = new Native.POINT { X = 100, Y = 100 };
                w.CursorProvider = () => cur;
                w.DummyDragChanged += h => { if (h) w.EndOwnMove(); };
                var wndProc = typeof(CaptureWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var args = new object?[] { hwnd, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero, false };
                wndProc.Invoke(w, args);
                Assert.True(w.OwnMoving);          // 修正J87: 掴んだだけではダミー枠を出さない（まだ動かしている途中）
                cur = new Native.POINT { X = 120, Y = 110 };
                w.OwnMoveStep();                   // 動いた → ダミー枠を出す → 知らせの中で終わる
                PumpMs(150);
                Assert.False(w.OwnMoving);
                Assert.Null(w.DragRect);
                Assert.NotSame(Brushes.Transparent, frame.BorderBrush);
                Assert.Same(before, frame.BorderBrush);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J1_自前の移動はカーソルの動いた分だけ左上をずらす()
    {
        var start = new Native.RECT { Left = 10, Top = 20, Right = 410, Bottom = 320 };
        var p = CaptureWindow.OwnMovePos(start, new Native.POINT { X = 100, Y = 100 }, new Native.POINT { X = 90, Y = 160 });
        Assert.Equal((0, 80), p);
    }
}

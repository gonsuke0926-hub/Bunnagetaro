using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Input;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 画面を少し暗くして範囲ドラッグ。マウスは動かさない（覆いの Esc／離し・フックを通らず、
/// QuickThrowController の SnipStart/SnipBegin/SnipRelease/SnipCancel を直接呼ぶ）。
/// ぶん投げの先は届かない URL にして、本物の AI へは送らない
/// </summary>
public class SnipTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public SnipTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_snip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    /// <summary>条件がそろうまで画面の描き直しを回す（最大 ms）。明るい所・枠は CompositionTarget.Rendering で
    /// 直るので、テストを全部流して PC が忙しい時は 100ms では回ってこないことがあった（決め打ちで待たない）</summary>
    private static void PumpUntil(Func<bool> cond, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) Pump(16);
    }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    private MainWindow NewWindow()
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetBool("quick_enabled", false);
        cfg.SetStr("base_url", "http://127.0.0.1:1");   // 届かない先（本物の AI へ送らない）
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(200);
        return w;
    }

    private static Native.RECT Rect(IntPtr h)
    {
        Native.GetWindowRect(h, out var r);
        return r;
    }

    [Fact]
    public void 範囲ドラッグを始めると画面全体を覆う窓が出て_撮影に写らない設定になる()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                Pump(100);
                var h = o.Hwnd;
                var v = Native.VirtualDesktop();
                var r = Rect(h);
                Assert.Equal((v.Left, v.Top, v.Right, v.Bottom), (r.Left, r.Top, r.Right, r.Bottom));
                Assert.True(Native.GetWindowDisplayAffinity(h, out uint aff));
                Assert.Equal(0x11u, aff);   // WDA_EXCLUDEFROMCAPTURE
                long ex = (long)Native.GetWindowLongPtrW(h, -20);
                Assert.True((ex & 0x80) != 0);   // WS_EX_TOOLWINDOW（タスクバーに出ない）
                Assert.True((ex & 0x8) != 0);    // WS_EX_TOPMOST
                Assert.True(o.IsVisible);
                Assert.Equal((uint)SnipOverlay.DimAlpha << 24, o.SampleDibPixel(v.Left + 10, v.Top + 10));
                w.Quick.SnipCancel("test");
                Assert.False(w.Quick.SnipActive);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Escで覆いが閉じ_開いているキャプチャ窓は変わらない()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.Session.OpenCapture(animate: false);
                Pump(400);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var ch = new WindowInteropHelper(cap).Handle;
                var before = Rect(ch);
                Assert.True(cap.IsVisible);
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                bool closed = false;
                o.Closed += (_, _) => closed = true;
                o.CancelFromKey();   // Esc と同じ道
                Pump(100);
                Assert.True(closed);
                Assert.Null(w.Quick.Snip);
                Assert.False(w.Quick.SnipActive);
                Assert.True(cap.IsVisible);
                var after = Rect(ch);
                Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 押さないまま時間が過ぎると覆いが閉じる_既定は15秒()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.Equal(TimeSpan.FromSeconds(15), w.Quick.SnipTimeout);
                w.Quick.SnipTimeout = TimeSpan.FromMilliseconds(200);
                Assert.True(w.Quick.SnipStart());
                Assert.NotNull(w.Quick.Snip);
                Pump(700);
                Assert.Null(w.Quick.Snip);
                Assert.False(w.Quick.SnipActive);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 押して離すと大きい四角へキャプチャ窓が置かれ_覆いは閉じる()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                w.Quick.SnipBegin(500, 300);
                Assert.True(w.Quick.Following);
                Pump(100);
                w.Quick.SnipRelease(920, 560);
                Assert.Null(w.Quick.Snip);
                Assert.False(w.Quick.SnipActive);
                Assert.False(w.Quick.Following);
                Pump(500);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                Assert.True(cap.IsVisible);
                var r = Rect(new WindowInteropHelper(cap).Handle);
                Assert.Equal((500, 300, 920, 560), (r.Left, r.Top, r.Right, r.Bottom));
                Assert.False(o.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 小さい四角は取り消して窓を作らない()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                w.Quick.SnipBegin(500, 300);
                Pump(100);
                w.Quick.SnipRelease(505, 305);
                Pump(300);
                Assert.False(w.Quick.SnipActive);
                Assert.False(w.Quick.Following);
                Assert.Null(Get<CaptureWindow?>(w.Session, "_capture"));   // 窓が無い所から始めたので、押す前（窓なし）へ戻る
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 四角を描いている間のPollは取り消さない_取り消せば元へ戻る()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                w.Quick.SnipBegin(600, 400);
                Assert.True(w.Quick.Following);
                w.Quick.Poll();
                w.Quick.Poll();
                w.Quick.Poll();
                Assert.True(w.Quick.Following);   // 取り損ね取り消しは範囲ドラッグでは働かない
                Assert.True(w.Quick.SnipActive);
                w.Quick.SnipCancel("test");
                Assert.False(w.Quick.Following);
                Assert.False(w.Quick.SnipActive);
                Assert.Null(w.Quick.Snip);
                Pump(300);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 二重に始めても覆いは1つ_クイックの最中は始まらない()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip;
                Assert.False(w.Quick.SnipStart());
                Assert.Same(o, w.Quick.Snip);
                w.Quick.SnipCancel("test");

                w.Quick.BeginAt(600, 400);   // クイックの四角を描いている最中
                Assert.False(w.Quick.SnipStart());
                Assert.Null(w.Quick.Snip);
                w.Quick.CancelRestore();
                Pump(300);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void メイン窓にキャプチャ窓ボタンの右隣の範囲ドラッグボタンがある()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                var cap = (Button)w.FindName("CaptureButton");
                var snip = (Button)w.FindName("SnipButton");
                Assert.NotNull(snip);
                Assert.Equal("範囲ドラッグ", snip.Content);
                var panel = (Panel)cap.Parent;
                Assert.Same(panel, snip.Parent);
                Assert.Equal(panel.Children.IndexOf(cap) + 1, panel.Children.IndexOf(snip));
                var a = cap.TranslatePoint(new Point(0, 0), w);
                var b = snip.TranslatePoint(new Point(0, 0), w);
                Assert.True(b.X > a.X + cap.ActualWidth - 1);
                snip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(w.Quick.SnipActive);   // ボタンで始まる
                w.Quick.SnipCancel("test");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void トレイの1回押しは待ってから始まり_二重押しなら始まらずに窓を出す()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                int wait = System.Windows.Forms.SystemInformation.DoubleClickTime + 300;
                w.Tray!.PendLeftClick();
                Pump(100);
                Assert.False(w.Quick.SnipActive);   // まだ待っている
                w.Tray.OnDoubleClick();
                Pump(wait);
                Assert.False(w.Quick.SnipActive);   // 二重押しで捨てた

                w.Tray.PendLeftClick();
                Pump(wait);
                Assert.True(w.Quick.SnipActive);    // 二重押しが来なければ始まる
                w.Tray.OnDoubleClick();             // 始まった後の二重押しは取り消す
                Assert.False(w.Quick.SnipActive);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J53_暗い膜は0からふわっと濃くなり_約100msで元の濃さになる()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                Assert.True(o.DimProgress < 1.0);   // 出始めは薄い（最後の濃さより下）
                var v = Native.VirtualDesktop();
                Assert.Equal((uint)SnipOverlay.DimAlpha << 24, o.SampleDibPixel(v.Left + 10, v.Top + 10));   // 最後の濃さは変わらない
                PumpUntil(() => o.DimProgress == 1.0, 1000);
                Assert.Equal(1.0, o.DimProgress);
                Assert.True(Native.GetWindowDisplayAffinity(o.Hwnd, out uint aff));
                Assert.Equal(0x11u, aff);
                w.Quick.SnipCancel("test");
                Pump(100);
                Assert.False(o.ThreadAlive);
                Assert.Equal(IntPtr.Zero, o.Hwnd);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J56_押す前は四角なし_押すとダミー枠と同じ四角が抜ける()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                (int X, int Y)? cur = (500, 300);
                o.CursorProvider = () => cur;
                Pump(100);
                Assert.Null(o.HoleRect);                                            // 押す前は全面が暗い
                uint dimPx = (uint)SnipOverlay.DimAlpha << 24;
                Assert.Equal(dimPx, o.SampleDibPixel(700, 500));
                w.Quick.SnipBegin(500, 300);
                cur = (900, 700);
                var (minW, minH) = w.Quick.Geo;
                var (want, _) = Geom.QuickDragRect(500, 300, 900, 700, minW, minH, CaptureBorder.Px);
                PumpUntil(() => o.HoleRect == want);
                Assert.Equal(want, o.HoleRect);
                Assert.Equal(0x01000000u, o.SampleDibPixel(want.X + want.W / 2, want.Y + want.H / 2));   // 四角の中は穴
                Assert.Equal(dimPx, o.SampleDibPixel(want.X + want.W + 50, want.Y + want.H + 50));      // 外は暗い
                cur = (700, 500);
                (want, _) = Geom.QuickDragRect(500, 300, 700, 500, minW, minH, CaptureBorder.Px);
                PumpUntil(() => o.HoleRect == want);
                Assert.Equal(want, o.HoleRect);
                w.Quick.SnipCancel("test");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J58_範囲ドラッグのキーを押すとクイックがOFFでも範囲ドラッグが始まる()
    {
        OnSta(() =>
        {
            var w = NewWindow();   // quick_enabled=false
            try
            {
                var cfg = (ConfigStore)typeof(MainWindow).GetField("_cfg", F)!.GetValue(w)!;
                cfg.SetStr("snip_hotkey", "f7");
                w.Quick.Apply(false);
                Assert.Equal("f7", w.Quick.Input.SnipHotkey());
                // フックの合図は合成した押下で出す（本物のキーは押さない）
                var p = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<QuickInputWatcher.KBDLLHOOKSTRUCT>());
                try
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(new QuickInputWatcher.KBDLLHOOKSTRUCT { vkCode = 0x76 }, p, false);
                    Assert.Equal(1, w.Quick.Input.KeyProc(0, (IntPtr)QuickInputWatcher.WmKeyDown, p).ToInt32());
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); }
                w.Quick.Poll();
                Assert.True(w.Quick.SnipActive);
                Assert.NotNull(w.Quick.Snip);
                w.Quick.SnipCancel("test");
                Assert.False(w.Quick.SnipActive);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J60_範囲ドラッグ中はダミー枠を出さず_枠は覆いが明るい所と同じ四角で描く()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                (int X, int Y)? cur = (500, 300);
                o.CursorProvider = () => cur;
                Pump(100);
                Assert.False(o.BorderVisible);                       // 押す前・動かす前は枠なし
                w.Quick.SnipBegin(500, 300);
                Assert.True(w.Quick.Marker is null || !w.Quick.Marker.IsShown);   // ダミー枠は出さない
                Pump(100);
                Assert.False(o.BorderVisible);                       // 2px 以内は枠なし
                var (minW, minH) = w.Quick.Geo;
                foreach (var end in new[] { (1100, 800), (800, 600) })
                {
                    cur = end;
                    var (want, _) = Geom.QuickDragRect(500, 300, end.Item1, end.Item2, minW, minH, CaptureBorder.Px);
                    PumpUntil(() => o.BorderVisible && o.BorderRect == want);
                    Assert.True(o.BorderVisible);
                    Assert.NotNull(o.HoleRect);
                    Assert.Equal(o.HoleRect, o.BorderRect);          // 明るい所と枠は同じ四角
                    Assert.Equal(want, o.BorderRect);
                    Assert.Equal(CaptureBorder.AlphaByte(CaptureBorder.Alpha), o.BorderAlpha);
                    Assert.True(w.Quick.Marker is null || !w.Quick.Marker.IsShown);
                }
                // 最小未満の間は半分の不透明度
                cur = (510, 310);
                byte half = (byte)(CaptureBorder.AlphaByte(CaptureBorder.Alpha) / 2);
                PumpUntil(() => o.BorderVisible && o.BorderAlpha == half);
                Assert.True(o.BorderVisible);
                Assert.Equal((byte)(CaptureBorder.AlphaByte(CaptureBorder.Alpha) / 2), o.BorderAlpha);
                w.Quick.SnipCancel("test");
            }
            finally { w.Close(); }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);

    [Fact]
    public void J64_覆いの窓はWPFの窓ではなく_閉じると窓も描画スレッドも残らない()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                var h = o.Hwnd;
                Assert.NotEqual(IntPtr.Zero, h);
                Assert.Null(HwndSource.FromHwnd(h));   // WPF の窓ではない
                Assert.True(o.ThreadAlive);
                o.CancelFromKey();
                Pump(100);
                Assert.Null(w.Quick.Snip);
                Assert.False(w.Quick.SnipActive);
                Assert.False(o.ThreadAlive);
                Assert.False(IsWindow(h));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J60_ふつうのクイックぶん投げはダミー枠を出す()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                w.Quick.BeginAt(500, 300);
                Assert.NotNull(w.Quick.Marker);
                w.Quick.CancelRestore();
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J63_範囲ドラッグのDIBは外が暗い膜_縁が枠の色_中が透明で_動かすと前の場所が暗い膜に戻る()
    {
        OnSta(() =>
        {
            var w = NewWindow();
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                (int X, int Y)? cur = (500, 300);
                o.CursorProvider = () => cur;
                w.Quick.SnipBegin(500, 300);
                cur = (900, 700);
                var (minW, minH) = w.Quick.Geo;
                var (want, _) = Geom.QuickDragRect(500, 300, 900, 700, minW, minH, CaptureBorder.Px);
                PumpUntil(() => o.BorderVisible && o.BorderRect == want);
                uint dimPx = (uint)SnipOverlay.DimAlpha << 24;
                var c = CaptureBorder.Color;
                uint colorRef = (uint)(c.R | (c.G << 8) | (c.B << 16));
                uint borderPx = QuickMarker.PremulArgb(colorRef, CaptureBorder.AlphaByte(CaptureBorder.Alpha));
                Assert.Equal(dimPx, o.SampleDibPixel(400, 200));      // 外は暗い膜
                Assert.Equal(borderPx, o.SampleDibPixel(501, 301));   // 縁は枠の色
                Assert.Equal(0x01000000u, o.SampleDibPixel(700, 500));// 中は明るい穴（alpha=1）

                cur = (700, 500);
                (want, _) = Geom.QuickDragRect(500, 300, 700, 500, minW, minH, CaptureBorder.Px);
                PumpUntil(() => o.BorderRect == want);
                Assert.Equal(dimPx, o.SampleDibPixel(850, 650));      // 前の四角が通り過ぎた跡は暗い膜へ戻る
                w.Quick.SnipCancel("test");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J120_allowCaptureがtrueのSnipOverlayはWDA_NONEになる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            cfg.SetStr("base_url", "http://127.0.0.1:1");
            cfg.SetBool("allow_capture_overlay", true);
            var w = new MainWindow(_paths, cfg);
            w.Show();
            Pump(200);
            try
            {
                Assert.True(w.Quick.SnipStart());
                var o = w.Quick.Snip!;
                Assert.True(Native.GetWindowDisplayAffinity(o.Hwnd, out uint affOn));
                Assert.Equal(Native.WDA_NONE, affOn);
                w.Quick.SnipCancel("test");
                Pump(100);

                cfg.SetBool("allow_capture_overlay", false);
                Assert.True(w.Quick.SnipStart());
                o = w.Quick.Snip!;
                Assert.True(Native.GetWindowDisplayAffinity(o.Hwnd, out uint affOff));
                Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, affOff);
                w.Quick.SnipCancel("test");
                Pump(100);
            }
            finally { w.Close(); }
        });
    }
}

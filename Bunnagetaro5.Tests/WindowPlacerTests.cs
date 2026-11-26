using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J89（段階2・置き場所の係）: WindowPlacer の追従順序・タスクバー重なり順・保存保険の動きを確かめる</summary>
public class WindowPlacerTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public WindowPlacerTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_placer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    private static Native.RECT RectOf(System.Windows.Window w)
    {
        var h = Native.GetAncestor(new WindowInteropHelper(w).Handle, Native.GA_ROOT);
        Native.GetWindowRect(h, out var r);
        return r;
    }

    private static bool TrayIsAbove(IntPtr tray, IntPtr h)
    {
        const uint GW_HWNDPREV = 3;
        for (var x = GetWindow(h, GW_HWNDPREV); x != IntPtr.Zero; x = GetWindow(x, GW_HWNDPREV))
            if (x == tray) return true;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [Fact]
    public void J89_キャプチャ窓のMovingでバーと窓の中と外の解答欄が同じフレームで追従し外の左端がバーに揃う()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            cfg.Settings.CaptureX = 160;
            cfg.Settings.CaptureY = 120;
            cfg.Settings.CaptureWidth = 360;
            cfg.Settings.CaptureHeight = 200;
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000), "バーが出ない");
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                var panel = Get<AnswerPanel>(w.Session, "_panel");

                strip.ShowFinal("答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");

                // 250ms 巡回に頼らず Moving（ダミー枠移動相当）だけで同じ呼び出し内で揃うことを確かめる
                var b0 = RectOf(cap);
                int nx = b0.Left + 80, ny = b0.Top + 40;
                var target = new Native.RECT { Left = nx, Top = ny, Right = nx + (b0.Right - b0.Left), Bottom = ny + (b0.Bottom - b0.Top) };
                w.Session.PlacerForTests.PlaceAll(target);

                var br = RectOf(bar);
                var sr = RectOf(strip);
                Assert.Equal(target.Left, br.Left);
                Assert.Equal(target.Bottom, br.Top);
                Assert.Equal(br.Left, sr.Left);
                Assert.Equal(br.Bottom + 4, sr.Top);

                // inside でも SetWindowPos 直後に窓の中の解答欄とバーが即揃う
                CfgTestUtil.SetPlace(cfg, "inside");
                panel.ShowFinal("中の答え");
                Assert.True(TestUtil.WaitUntil(() => panel.IsVisible, 3000), "窓の中の解答欄が出ない");
                var capH = new WindowInteropHelper(cap).Handle;
                Native.SetWindowPos(capH, IntPtr.Zero, nx + 20, ny + 15, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                var cr = RectOf(cap);
                var pr = RectOf(panel);
                var br2 = RectOf(bar);
                Assert.Equal((cr.Left, cr.Top, cr.Right, cr.Bottom), (pr.Left, pr.Top, pr.Right, pr.Bottom));
                Assert.Equal(cr.Bottom, br2.Top);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J89_同じ位置のまま保険の見回りが回ってもタスクバーの裏のままで位置変更で手前に出る()
    {
        TestUtil.OnSta(() =>
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
            if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return;   // 下のタスクバーが無い環境

            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000), "バーが出ない");
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");

                var capH = new WindowInteropHelper(cap).Handle;
                var stripH = Native.GetAncestor(new WindowInteropHelper(strip).Handle, Native.GA_ROOT);
                Native.GetWindowRect(capH, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom - 5 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                TestUtil.Pump(300);
                Native.GetWindowRect(stripH, out var sr);
                if (!Native.OutsideWorkArea(sr)) return;

                // タスクバーを手前へ出す（タスクバーを押した後と同じ状態）。できない環境では抜ける
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                if (!TrayIsAbove(tray, stripH)) return;

                // 同じ位置のまま 250ms 保険が回ってもタスクバーの裏のまま（J71）
                w.Session.PlacerForTests.TickInsurance();
                TestUtil.Pump(150);
                Assert.True(TrayIsAbove(tray, stripH));

                // 窓を 1px 動かして位置が変わると、タスクバーより手前へ出る（J70・J71）
                Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                TestUtil.Pump(200);
                Assert.False(TrayIsAbove(tray, stripH));
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J89_外の解答欄のリサイズ完了印が保険の見回りで設定に保存される()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            cfg.Settings.StripWidth = 520;
            cfg.Settings.StripHeight = 140;
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");

                var stripH = Native.GetAncestor(new WindowInteropHelper(strip).Handle, Native.GA_ROOT);
                Native.GetWindowRect(stripH, out var r0);
                var wndProc = typeof(AnswerStrip).GetMethod("WndProc", F)!;

                wndProc.Invoke(strip, new object?[] { stripH, Native.WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });
                Native.SetWindowPos(stripH, IntPtr.Zero, r0.Left, r0.Top, 420, 180,
                    Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                wndProc.Invoke(strip, new object?[] { stripH, Native.WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero, false });

                // WM_EXITSIZEMOVE の中ではまだ保存していない
                Assert.Equal(520, cfg.Settings.StripWidth);
                Assert.Equal(140, cfg.Settings.StripHeight);

                // 250ms 保険が走ると保存される
                w.Session.PlacerForTests.TickInsurance();
                Assert.Equal(420, cfg.Settings.StripWidth);
                Assert.Equal(180, cfg.Settings.StripHeight);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J90_キャプチャ窓の枠もタスクバーに重なると手前に出て窓の中の解答欄より後ろを保つ()
    {
        TestUtil.OnSta(() =>
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
            if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return;   // 下のタスクバーが無い環境

            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000), "バーが出ない");
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("外の答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");
                var capH = new WindowInteropHelper(cap).Handle;
                var stripH = Native.GetAncestor(new WindowInteropHelper(strip).Handle, Native.GA_ROOT);

                // 1) outside: キャプチャ窓をタスクバーに重なる位置へ動かしたら、枠もタスクバーより手前に出続け、外の解答欄より後ろを保つ（J89d・J89e）
                Native.GetWindowRect(capH, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom + 40 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                TestUtil.Pump(300);
                Native.GetWindowRect(capH, out var cr2);
                if (!Native.OutsideWorkArea(cr2)) return;

                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                if (!TrayIsAbove(tray, capH)) return;

                // このアプリの窓がアクティブになったら（TaskbarFront）、キャプチャ枠も手前へ戻り、外の解答欄はさらに手前を保つ
                var front = w.Session.TaskbarFrontForTests;
                front.AppIsForeground = () => true;
                front.OnForeground(capH);
                Assert.True(TestUtil.WaitUntil(() => !TrayIsAbove(tray, capH), 1000), "outside でキャプチャ枠がタスクバーより手前へ戻らない");
                Assert.True(TrayIsAbove(stripH, capH), "outside で外の解答欄がキャプチャ枠より後ろに回っている");

                // 窓を 1px 動かした時もキャプチャ枠が手前へ出続け、外の解答欄との前後順を保つ（離した直後の再現）
                front.AppIsForeground = () => false;
                front.OnForeground(tray);
                Assert.True(TestUtil.WaitUntil(() => !front.Watching, 1000));
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                if (TrayIsAbove(tray, capH))
                {
                    Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    Assert.True(TestUtil.WaitUntil(() => !TrayIsAbove(tray, capH), 1000),
                        "outside の 1px 移動でキャプチャ枠がタスクバーより手前へ戻らない");
                    Assert.True(TrayIsAbove(stripH, capH),
                        "outside の 1px 移動で外の解答欄がキャプチャ枠より後ろに回っている");
                }

                // タスクバーを押した後は同じ場所の保険巡回が回っても裏のまま（J71・J74）
                front.AppIsForeground = () => false;
                front.OnForeground(tray);
                TestUtil.Pump(50);
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                if (TrayIsAbove(tray, capH))
                {
                    w.Session.PlacerForTests.TickInsurance();
                    TestUtil.Pump(150);
                    Assert.True(TrayIsAbove(tray, capH));
                }

                // 2) inside: 窓の中の解答欄を出した状態で手前に出しても、解答欄が常にキャプチャ枠より手前に保たれる（J70）
                CfgTestUtil.SetPlace(cfg, "inside");
                panel.ShowFinal("中の答え");
                Assert.True(TestUtil.WaitUntil(() => panel.IsVisible, 3000), "窓の中の解答欄が出ない");
                var panelH = new WindowInteropHelper(panel).Handle;

                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                front.AppIsForeground = () => true;
                front.OnForeground(capH);
                Assert.True(TestUtil.WaitUntil(() => !TrayIsAbove(tray, capH) && !TrayIsAbove(tray, panelH), 1000),
                    "inside でキャプチャ枠・解答欄がタスクバーより手前に出ない");
                Assert.True(TrayIsAbove(panelH, capH), "窓の中の解答欄がキャプチャ枠より後ろに回っている");

                // cap の下端だけが作業領域から 2px はみ出す境界（panel は作業領域内になりやすい）でも、解答欄が手前を保つ
                front.AppIsForeground = () => false;
                front.OnForeground(tray);
                Assert.True(TestUtil.WaitUntil(() => !front.Watching, 1000));
                int ny2 = work0.Value.Bottom + 2 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny2, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                TestUtil.Pump(100);
                if (TrayIsAbove(tray, capH))
                {
                    Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny2, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    Assert.True(TestUtil.WaitUntil(() => !TrayIsAbove(tray, capH), 1000),
                        "2px はみ出し位置でキャプチャ枠がタスクバーより手前へ出ない");
                    Assert.True(TrayIsAbove(panelH, capH),
                        "2px はみ出し位置で窓の中の解答欄がキャプチャ枠より後ろに回っている");
                }
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }
}

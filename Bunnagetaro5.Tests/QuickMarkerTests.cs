using System;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bunnagetaro5.Input;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND4 E9: クイックの四角のダミー枠（専用スレッドの Win32 窓）と、四角の計算の純関数</summary>
public class QuickMarkerTests
{
    // ---- 純関数（今の FollowTo と同じ値） ----

    [Fact]
    public void 四角の計算は始点と終点の正規化で逆向きも同じ()
    {
        var a = Geom.QuickDragRect(100, 200, 400, 500, 30, 30, 4, 0, 0, 1920, 1080);
        var b = Geom.QuickDragRect(400, 500, 100, 200, 30, 30, 4, 0, 0, 1920, 1080);
        Assert.Equal(((100, 200, 300, 300), false), a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void F31_四角の計算は設定の最小で広げない_見た目の最小だけ確保し_最小未満を知らせる()
    {
        // 5x3 のまま付いてくる（設定の最小 30x40 では広げない）。ただし枠の太さ4 → 見た目の最小 10px は確保
        var (r, below) = Geom.QuickDragRect(100, 200, 105, 203, 30, 40, 4, 0, 0, 1920, 1080);
        Assert.Equal((100, 200, 10, 10), r);
        Assert.True(below);
        Assert.Equal(10, Geom.QuickMarkerVisualMin(4));
        // 見た目の最小より大きければそのまま。最小未満の判定は幅・高さのどちらか一方でも
        var (r2, b2) = Geom.QuickDragRect(100, 200, 130, 230, 30, 40, 4, 0, 0, 1920, 1080);
        Assert.Equal((100, 200, 30, 30), r2);
        Assert.True(b2);                                   // 高さ 30 < 40
        var (r3, b3) = Geom.QuickDragRect(100, 200, 130, 240, 30, 40, 4, 0, 0, 1920, 1080);
        Assert.Equal((100, 200, 30, 40), r3);
        Assert.False(b3);                                  // ちょうど最小は取り消しにならない（離した時の判定と同じ）
        // 離した時の判定（Release）と同じ基準: NormRect の幅・高さが最小未満
        var (_, _, w, h) = Geom.NormRect(100, 200, 129, 240);
        Assert.True(w < 30 || h < 40);
        Assert.True(Geom.QuickDragRect(100, 200, 129, 240, 30, 40, 4, 0, 0, 1920, 1080).BelowMin);
    }

    [Fact]
    public void 四角の計算は仮想画面に収める()
    {
        // 右下からはみ出す → 寄せる
        Assert.Equal((1920 - 300, 1080 - 300, 300, 300), Geom.QuickDragRect(1800, 1000, 2100, 1300, 30, 30, 4, 0, 0, 1920, 1080).Rect);
        // 左（負の仮想原点）
        Assert.Equal((-1920, 0, 100, 100), Geom.QuickDragRect(-3000, 0, -2900, 100, 30, 30, 4, -1920, 0, 3840, 1080).Rect);
        // 画面より大きい → 画面の大きさまで
        var big = Geom.QuickDragRect(-50, -50, 5000, 4000, 30, 30, 4, 0, 0, 1920, 1080).Rect;
        Assert.Equal((0, 0, 1920, 1080), big);
    }

    [Fact]
    public void 実環境の純関数も同じ式で呼べる()
    {
        var (r, below) = Geom.QuickDragRect(10, 10, 15, 15, 30, 30, 4);
        Assert.True(r.W >= 10 && r.H >= 10);   // 見た目の最小（枠×2＋2）
        Assert.True(below);
    }

    // ---- ダミー枠 ----

    private static bool WaitFor(Func<bool> c, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!c() && DateTime.UtcNow < end) Thread.Sleep(10);
        return c();
    }

    [Fact]
    public void J75_ダミー枠の窓の矩形は仮想デスクトップの四方を1pxずつ小さくする()
    {
        var r = QuickMarker.SurfaceRect(new Native.RECT { Left = -1920, Top = 0, Right = 2560, Bottom = 1600 });
        Assert.Equal((-1919, 1, 2559, 1599), (r.Left, r.Top, r.Right, r.Bottom));
        var tiny = QuickMarker.SurfaceRect(new Native.RECT { Left = 0, Top = 0, Right = 2, Bottom = 2 });
        Assert.Equal((0, 0, 2, 2), (tiny.Left, tiny.Top, tiny.Right, tiny.Bottom));
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [Fact]
    public void J75_ダミー枠を出している間もどの画面もぴったりおおわず_Windowsが全画面のアプリと見なさない()
    {
        SHQueryUserNotificationState(out int before);
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            Assert.True(m.StartExternal(200, 200, 400, 300));
            Assert.True(WaitFor(() => m.IsShown), "窓が出ない");
            Assert.True(Native.GetWindowRect(m.Hwnd, out var wr));
            foreach (var mon in EnvInfo.Monitors())
                Assert.False(wr.Left <= mon.X && wr.Top <= mon.Y && wr.Right >= mon.X + mon.W && wr.Bottom >= mon.Y + mon.H,
                             $"画面 {mon.X},{mon.Y} {mon.W}x{mon.H} をぴったりおおっている（窓 {wr.Left},{wr.Top},{wr.Right},{wr.Bottom}）");
            // 前から全画面のアプリが無い時（5 = QUNS_ACCEPTS_NOTIFICATIONS）だけ、Windows の見立ても確かめる
            if (before == 5)
            {
                Thread.Sleep(300);
                SHQueryUserNotificationState(out int during);
                Assert.NotEqual(2 /* QUNS_BUSY: 全画面のアプリ */, during);
            }
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void 開始で窓が出て停止で隠れ破棄でスレッドが終わる()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            // 現在のカーソル位置から 10px 以上離れた始点にして、すぐ出る状態にする（マウスは動かさない）
            var p = Geom.CursorPos()!.Value;
            Assert.True(m.Start(p.X - 200, p.Y - 200, 30, 30));
            Assert.True(WaitFor(() => m.IsShown), "窓が出ない");
            Assert.True(m.ThreadAlive);
            // 2回続けて Start しても窓は1つ（同じハンドル）
            var h = m.Hwnd;
            Assert.True(m.Start(p.X - 200, p.Y - 200, 30, 30));
            Thread.Sleep(100);
            Assert.Equal(h, m.Hwnd);
            m.Stop();
            Assert.True(WaitFor(() => !m.IsShown), "隠れない");
            Assert.False(m.Failed);
        }
        finally
        {
            var th = m.ThreadAlive;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            m.Dispose();
            Assert.True(sw.ElapsedMilliseconds < 1500);
            Assert.False(m.ThreadAlive);
        }
    }

    [Fact]
    public void J49_離したらカーソルを追わず離した点で止まり_押し直したらまた追う()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            // 修正J60b: 本物のマウスに頼らない（テスト中に人がマウスを動かす・画面の端にあると落ちていた）。
            // 決まったカーソル (700,600) を「離した後に動いた先」に見立てる
            var p = (X: 700, Y: 600);
            m.CursorProvider = () => (p.X, p.Y);
            bool released = false;
            (int X, int Y)? end = null;
            m.Released = () => released;
            m.ReleasedEnd = () => end;
            Assert.True(m.Start(p.X - 200, p.Y - 200, 30, 30));
            Assert.True(WaitFor(() => m.ShownRect == (p.X - 200, p.Y - 200, 200, 200)), $"カーソルまで描かない {m.ShownRect}");
            // 離した: 離した点（カーソルより手前）で止まる
            end = (p.X - 50, p.Y - 60);
            released = true;
            Assert.True(WaitFor(() => m.ShownRect == (p.X - 200, p.Y - 200, 150, 140)), $"離した点で止まらない {m.ShownRect}");
            // UI が拾った後（終点が読めない）は最後の枠のまま
            end = null;
            Thread.Sleep(100);
            Assert.Equal((p.X - 200, p.Y - 200, 150, 140), m.ShownRect);
            // チャタリングで押し直した: またカーソルを追う
            released = false;
            Assert.True(WaitFor(() => m.ShownRect == (p.X - 200, p.Y - 200, 200, 200)), $"また追わない {m.ShownRect}");
            m.Stop();
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void F31_最小未満の間は半透明_最小以上で元の濃さ_マウスは動かさない()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            var p = Geom.CursorPos()!.Value;
            // 始点がカーソルから 20px（最小 100 未満）→ 半透明
            Assert.True(m.Start(p.X - 20, p.Y - 20, 100, 100));
            Assert.True(WaitFor(() => m.IsShown), "窓が出ない");
            int full = 0xB3;
            Assert.True(WaitFor(() => m.DrawnAlpha == full / 2), $"半透明にならない alpha={m.DrawnAlpha}");
            // 最小以上（200px 離れる）→ 元の濃さへ
            Assert.True(m.Start(p.X - 200, p.Y - 200, 100, 100));
            Assert.True(WaitFor(() => m.DrawnAlpha == full), $"元の濃さに戻らない alpha={m.DrawnAlpha}");
            m.Stop();
            Assert.True(WaitFor(() => !m.IsShown), "隠れない");
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void 窓は画面に写らず操作を素通しする()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            var p = Geom.CursorPos()!.Value;
            m.Start(p.X - 200, p.Y - 200, 30, 30);
            Assert.True(WaitFor(() => m.IsShown));
            Assert.True(Native.GetWindowDisplayAffinity(m.Hwnd, out uint aff));
            Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, aff);
            long ex = GetWindowLongPtr(m.Hwnd, -20).ToInt64();
            Assert.True((ex & 0x20) != 0 && (ex & 0x08000000) != 0 && (ex & 0x80) != 0 && (ex & 0x8) != 0 && (ex & 0x80000) != 0);
        }
        finally { m.Dispose(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);

    [Fact]
    public void StopAndWaitHiddenは撮る前に消えていることを確かめる()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            var p = Geom.CursorPos()!.Value;
            m.Start(p.X - 200, p.Y - 200, 30, 30);
            Assert.True(WaitFor(() => m.IsShown));
            Assert.True(m.StopAndWaitHidden());
            Assert.False(m.IsShown);
        }
        finally { m.Dispose(); }
    }

    [Fact]
    public void 追従の購読はUIスレッドのRenderingを使わない()
    {
        var f = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var t = typeof(Bunnagetaro5.Input.QuickThrowController);
        Assert.Null(t.GetField("_renderHooked", f));
        Assert.Null(t.GetField("_inRendering", f));
        Assert.Null(t.GetMethod("OnRendering", f));
        Assert.Null(t.GetMethod("FollowNow", f));
    }

    // ---- 制御側（QuickThrowController）: どの終わり方でもダミーが止まる ----

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60, throwOnTimeout: false);

    private static void Pump(int ms) => TestUtil.Pump(ms);

    [Fact]
    public void 取り消し_OFF_終了のどれでもダミーが止まる()
    {
        OnSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
            }
            string dir = Path.Combine(Path.GetTempPath(), "b5_qm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var paths = new Paths(Path.Combine(dir, "Bunnagetaro5"));
            AppLog.Init(paths);
            var cfg = new ConfigStore(paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(500);
                // 取り消し
                w.Quick.BeginAt(500, 500);
                Pump(200);
                Assert.True(w.Quick.Marker is { Active: true });
                w.Quick.CancelRestore();
                Pump(300);
                Assert.False(w.Quick.Marker!.Active);
                Assert.False(w.Quick.Marker.IsShown);
                // 終了（Stop）でスレッドも終わる
                w.Quick.BeginAt(500, 500);
                Pump(200);
                var m = w.Quick.Marker!;
                w.Quick.Stop();
                Assert.False(m.ThreadAlive);
            }
            finally { w.Close(); try { Directory.Delete(dir, true); } catch { } }
        });
    }

    [Fact]
    public void J120_SetAllowCaptureでDisplayAffinityをWDA_NONEとWDA_EXCLUDEFROMCAPTUREに切り替えられる()
    {
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            var v = Native.VirtualDesktop();
            m.SetAllowCapture(true);
            Assert.True(m.StartExternal(v.Left + 50, v.Top + 50, 200, 200));
            Assert.True(WaitFor(() => m.IsShown));
            Assert.True(Native.GetWindowDisplayAffinity(m.Hwnd, out uint affOn));
            Assert.Equal(Native.WDA_NONE, affOn);

            Assert.True(m.StopAndWaitHidden());
            m.SetAllowCapture(false);
            Assert.True(m.StartExternal(v.Left + 50, v.Top + 50, 200, 200));
            Assert.True(WaitFor(() => m.IsShown));
            Assert.True(Native.GetWindowDisplayAffinity(m.Hwnd, out uint affOff));
            Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, affOff);
            m.Stop();
        }
        finally { m.Dispose(); }
    }
}

internal static class LetExt
{
    public static TR Let<T, TR>(this T v, Func<T, TR> f) => f(v);
}

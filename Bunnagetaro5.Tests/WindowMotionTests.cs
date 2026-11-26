using System.Windows;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>ANIM_ROUND1: 動きの部品（補間・イージング・途中取り消し・連打）</summary>
[Collection("WindowMotion")]
public class WindowMotionTests
{
    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void 進み具合は時間切れで必ず1()
    {
        Assert.Equal(0.0, WindowMotion.Progress(0, 100));
        Assert.Equal(0.5, WindowMotion.Progress(50, 100));
        Assert.Equal(1.0, WindowMotion.Progress(100, 100));
        Assert.Equal(1.0, WindowMotion.Progress(5000, 100));
        Assert.Equal(1.0, WindowMotion.Progress(10, 0));
    }

    [Fact]
    public void イージングは両端が0と1_EaseInは遅い始まり_EaseOutは速い始まり()
    {
        foreach (Func<double, double> f in new Func<double, double>[] { WindowMotion.EaseIn, WindowMotion.EaseOut })
        {
            Assert.Equal(0.0, f(0), 9);
            Assert.Equal(1.0, f(1), 9);
        }
        Assert.True(WindowMotion.EaseIn(0.5) < 0.5);
        Assert.True(WindowMotion.EaseOut(0.5) > 0.5);
    }

    [Fact]
    public void 矩形の補間は端点で一致し_点への吸い込みは最小矩形になる()
    {
        var a = R(100, 200, 700, 600);
        var to = WindowMotion.SquareAt(50, 60, 30);
        Assert.Equal((35, 45, 65, 75), (to.Left, to.Top, to.Right, to.Bottom));
        var s = WindowMotion.LerpRect(a, to, 0);
        Assert.Equal((100, 200, 700, 600), (s.Left, s.Top, s.Right, s.Bottom));
        var e = WindowMotion.LerpRect(a, to, 1);
        Assert.Equal((35, 45, 65, 75), (e.Left, e.Top, e.Right, e.Bottom));
        var m = WindowMotion.LerpRect(a, to, 0.5);
        Assert.Equal((68, 123, 383, 338), (m.Left, m.Top, m.Right, m.Bottom));
    }

    [Fact]
    public void 時間切れで最終値が渡りDoneが一度だけ呼ばれる_打ち切るとDoneは呼ばれない()
    {
        long now = 1000;
        WindowMotion.Clock = () => now;
        var key = new object();
        double last = -1; int done = 0;
        WindowMotion.Run(key, 100, WindowMotion.EaseOut, p => last = p, () => done++);
        Assert.Equal(0.0, last);
        now = 1050; WindowMotion.Tick(now);
        Assert.InRange(last, 0.01, 0.99);
        Assert.Equal(0, done);
        now = 1500; WindowMotion.Tick(now);   // 大きく飛んでも最終値
        Assert.Equal(1.0, last);
        Assert.Equal(1, done);
        now = 1600; WindowMotion.Tick(now);
        Assert.Equal(1, done);

        var k2 = new object();
        int done2 = 0;
        WindowMotion.Run(k2, 100, WindowMotion.EaseOut, _ => { }, () => done2++);
        Assert.True(WindowMotion.Cancel(k2));
        now = 5000; WindowMotion.Tick(now);
        Assert.Equal(0, done2);
        Assert.False(WindowMotion.IsRunning(k2));
    }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static Window NewWin() => new()
    {
        WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false,
        Width = 100, Height = 80, Left = -3000, Top = 0, ShowActivated = false,
        Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Red },
    };

    [Fact]
    public void 出すは0から設定値へ_終わると等倍と設定値()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                WindowMotion.ShowFade(w, 0.92, 180, 0.96);
                Assert.True(w.IsVisible);
                Assert.Equal(0.0, w.Opacity, 3);
                now = 90; WindowMotion.Tick(now);
                Assert.InRange(w.Opacity, 0.2, 0.92);
                now = 400; WindowMotion.Tick(now);
                Assert.Equal(0.92, w.Opacity, 3);
                var tr = ((FrameworkElement)w.Content).RenderTransform as System.Windows.Media.ScaleTransform;
                Assert.True(tr is null || Math.Abs(tr.ScaleX - 1.0) < 1e-6);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void 消えかけに出すが来たら止めて_見えている設定透明度になる()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                w.Show();
                WindowMotion.HideFade(w, 1.0, 150);
                Assert.True(WindowMotion.IsFadingOut(w));
                now = 75; WindowMotion.Tick(now);
                Assert.True(w.Opacity < 1.0);
                WindowMotion.ShowFade(w, 1.0, 150);          // 取り消し
                Assert.False(WindowMotion.IsFadingOut(w));
                now = 1000; WindowMotion.Tick(now);
                Assert.True(w.IsVisible);                     // Hide されていない
                Assert.Equal(1.0, w.Opacity, 3);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void 消すは最後にHideされ_次に出す時は設定透明度へ戻っている()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                w.Show();
                int hidden = 0;
                WindowMotion.HideFade(w, 1.0, 150, () => hidden++);
                WindowMotion.HideFade(w, 1.0, 150, () => hidden++);   // 重ねても二重に始めない
                now = 1000; WindowMotion.Tick(now);
                Assert.False(w.IsVisible);
                Assert.Equal(1, hidden);
                Assert.Equal(1.0, w.Opacity, 3);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void 撮影前の隠しは動き無しで即消え_走っていた動きは打ち切られる()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                WindowMotion.ShowFade(w, 1.0, 150);
                now = 30; WindowMotion.Tick(now);
                WindowMotion.HideNow(w, 1.0);
                Assert.False(w.IsVisible);                    // Tick を待たず即
                Assert.False(WindowMotion.IsRunning(w));
                Assert.Equal(1.0, w.Opacity, 3);
                now = 1000; WindowMotion.Tick(now);
                Assert.False(w.IsVisible);                    // 後から出し直されない
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void 閉じるの連打で二重にCloseしない()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            int closed = 0;
            w.Closed += (_, _) => closed++;
            w.Show();
            WindowMotion.CloseFade(w, 1.0, 180, 0.96);
            WindowMotion.CloseFade(w, 1.0, 180, 0.96);
            now = 60; WindowMotion.Tick(now);
            WindowMotion.CloseFade(w, 1.0, 180, 0.96);
            Assert.True(w.IsVisible);                         // 途中では閉じない
            now = 1000; WindowMotion.Tick(now);
            Assert.Equal(1, closed);
            // 閉じた後にもう一度頼んでも例外にならない
            WindowMotion.CloseFade(w, 1.0, 180, 0.96);
        });
    }

    [Fact]
    public void Applyの中でTickが再入しても動きは時間通りに進む()
    {
        // SetWindowPos が同期メッセージを回して Rendering が入れ子で来る実測の再現
        long now = 0;
        WindowMotion.Clock = () => now;
        var key = new object();
        int done = 0; double last = -1;
        bool reentered = false;
        WindowMotion.Run(key, 100, WindowMotion.EaseOut, p =>
        {
            last = p;
            if (!reentered && p > 0 && p < 1) { reentered = true; WindowMotion.Tick(now + 5000); }   // 入れ子（無視されるべき）
        }, () => done++);
        now = 30; WindowMotion.Tick(now);
        Assert.True(reentered);
        Assert.Equal(0, done);                // 入れ子で終わらされていない
        Assert.True(last < 1.0);
        now = 200; WindowMotion.Tick(now);
        Assert.Equal(1, done);
    }

    [Fact]
    public void F28_吸い込みの曲線_大きさは1から015へEaseIn_透明度は前半そのまま後半で一気に()
    {
        Assert.Equal(0.15, WindowMotion.QuickScaleTo);
        Assert.Equal(1.0, WindowMotion.SuckScale(1.0, 0), 6);
        Assert.Equal(0.15, WindowMotion.SuckScale(1.0, 1), 6);
        // ease-in: 半分の時間ではまだ半分も縮んでいない（だんだん速く）
        Assert.True(WindowMotion.SuckScale(1.0, 0.5) > (1.0 + 0.15) / 2);
        double prev = 1.0, prevStep = 0;
        for (int i = 1; i <= 10; i++)
        {
            double v = WindowMotion.SuckScale(1.0, i / 10.0);
            double step = prev - v;
            Assert.True(step >= prevStep - 1e-9);   // 1コマごとの縮みが増えていく
            prev = v; prevStep = step;
        }
        Assert.Equal(1.0, WindowMotion.SuckAlpha(1.0, 0), 6);
        Assert.Equal(1.0, WindowMotion.SuckAlpha(1.0, WindowMotion.QuickFadeStart), 6);   // 前半は薄れない
        Assert.True(WindowMotion.SuckAlpha(1.0, 0.75) < 0.6);
        Assert.Equal(0.0, WindowMotion.SuckAlpha(1.0, 1), 6);
        Assert.Equal(0.46, WindowMotion.SuckAlpha(0.92, 0.75), 6);   // 設定の透明度から始まる
        Assert.InRange(WindowMotion.QuickMs, 100, 140);
        Assert.Equal(90, WindowMotion.QuickPanelFadeMs);   // F28(追加): 解答欄の薄れは吸い込みと別の定数で、元の 90ms
    }

    [Fact]
    public void 吸い込みは矩形を動かさず縮みはEaseIn_薄れは後半で一気_時間切れでHide_途中Restoreで元に戻る()
    {
        OnSta(() =>
        {
            long now = 0; WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                w.Show();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Native.GetWindowRect(hwnd, out var r0);
                int hidden = 0;
                int px = (r0.Left + r0.Right) / 2, py = (r0.Top + r0.Bottom) / 2;
                WindowMotion.SuckIn(w, px, py, 0.92, WindowMotion.QuickMs, () => hidden++);

                // F28: 前半（30ms）は透明度がほぼそのまま・縮みはまだわずか、矩形は不変
                now = 30; WindowMotion.Tick(now);
                Assert.True(w.Opacity > 0.9, $"30ms 時点の Opacity={w.Opacity} がほぼそのままではない");
                var trMid = ((FrameworkElement)w.Content).RenderTransform as System.Windows.Media.ScaleTransform;
                Assert.NotNull(trMid);
                Assert.InRange(trMid!.ScaleX, 0.9, 1.0);
                // 後半（90ms）は透明度が一気に落ち、縮みも進む
                now = 90; WindowMotion.Tick(now);
                Assert.True(w.Opacity < 0.5, $"90ms 時点の Opacity={w.Opacity} が 0.5 未満ではない");
                trMid = ((FrameworkElement)w.Content).RenderTransform as System.Windows.Media.ScaleTransform;
                Assert.InRange(trMid!.ScaleX, WindowMotion.QuickScaleTo, 0.75);
                Native.GetWindowRect(hwnd, out var rMid);
                Assert.Equal((r0.Left, r0.Top, r0.Right, r0.Bottom), (rMid.Left, rMid.Top, rMid.Right, rMid.Bottom));

                // 時間切れ：Hide され、透明度と大きさが元へ戻る、矩形も始めと同じ
                now = 200; WindowMotion.Tick(now);
                Assert.False(w.IsVisible);
                Assert.Equal(1, hidden);
                Assert.Equal(0.92, w.Opacity, 3);
                var trEnd = ((FrameworkElement)w.Content).RenderTransform as System.Windows.Media.ScaleTransform;
                Assert.True(trEnd is null || Math.Abs(trEnd.ScaleX - 1.0) < 1e-6);
                Native.GetWindowRect(hwnd, out var rEnd);
                Assert.Equal((r0.Left, r0.Top, r0.Right, r0.Bottom), (rEnd.Left, rEnd.Top, rEnd.Right, rEnd.Bottom));

                // 途中で Restore が来たら：見えている・設定透明度・大きさ 1.0
                w.Show();
                now = 300;
                WindowMotion.SuckIn(w, px, py, 0.92, WindowMotion.QuickMs);
                now = 390; WindowMotion.Tick(now);
                Assert.True(w.Opacity < 0.5);
                WindowMotion.Restore(w, 0.92);
                now = 500; WindowMotion.Tick(now);
                Assert.True(w.IsVisible);
                Assert.Equal(0.92, w.Opacity, 3);
                var trRest = ((FrameworkElement)w.Content).RenderTransform as System.Windows.Media.ScaleTransform;
                Assert.True(trRest is null || Math.Abs(trRest.ScaleX - 1.0) < 1e-6);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    private static Window NewOpaqueWin() => new()
    {
        Width = 200, Height = 150, Left = -3000, Top = 0,
        ShowInTaskbar = false, ShowActivated = false,
        Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White },
    };

    [Fact]
    public void J88_透過しない普通の窓はWS_EX_LAYEREDが付かずSetOpacityが効かない()
    {
        OnSta(() =>
        {
            var w = NewOpaqueWin();
            try
            {
                w.Show();
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Assert.NotEqual(IntPtr.Zero, h);
                WindowMotion.SetOpacity(w, 0.5);
                long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
                Assert.True((ex & Native.WS_EX_LAYERED) == 0, $"透過しない窓に WS_EX_LAYERED が付いた（ex=0x{ex:X}）");
                // Win32 で直接 WS_EX_LAYERED を足そうとしても WPF の通常窓では付かない前提を固定する
                Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_LAYERED));
                long ex2 = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
                Assert.True((ex2 & Native.WS_EX_LAYERED) == 0, $"Win32 直接指定で WS_EX_LAYERED が付いた（ex=0x{ex2:X}）");
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J88_透過しない窓のShowFadeとHideFadeは待たずにすぐ出て隠れ_onHiddenが呼ばれる()
    {
        OnSta(() =>
        {
            long now = 0;
            WindowMotion.Clock = () => now;
            var w = NewOpaqueWin();
            try
            {
                WindowMotion.ShowFade(w, 1.0, WindowMotion.OpenMs, WindowMotion.OpenCloseScale);
                Assert.True(w.IsVisible);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.False(((FrameworkElement)w.Content).RenderTransform is System.Windows.Media.ScaleTransform);

                int hidden = 0;
                WindowMotion.HideFade(w, 1.0, WindowMotion.PanelFadeMs, () => hidden++, WindowMotion.OpenCloseScale);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.False(WindowMotion.IsFadingOut(w));
                Assert.Equal(1, hidden);

                // すでに隠れていれば onHidden は重ねて呼ばれない
                WindowMotion.HideFade(w, 1.0, WindowMotion.PanelFadeMs, () => hidden++, WindowMotion.OpenCloseScale);
                Assert.Equal(1, hidden);
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; w.Close(); }
        });
    }

    [Fact]
    public void J88_透過しない窓のCloseFadeは待たずにすぐ閉じ_beforeCloseが呼ばれ_二重に閉じない()
    {
        OnSta(() =>
        {
            long now = 0;
            WindowMotion.Clock = () => now;
            var w = NewOpaqueWin();
            int before = 0, closed = 0;
            w.Closed += (_, _) => closed++;
            try
            {
                w.Show();
                WindowMotion.CloseFade(w, 1.0, WindowMotion.CloseMs, 1.0, () => before++);
                Assert.Equal(1, before);
                Assert.Equal(1, closed);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.False(WindowMotion.IsFadingOut(w));

                // 閉じた後にもう一度頼んでも例外にならず、二重に閉じない
                WindowMotion.CloseFade(w, 1.0, WindowMotion.CloseMs, 1.0);
                Assert.Equal(1, closed);
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; if (closed == 0) w.Close(); }
        });
    }

    [Fact]
    public void J88_透過する窓のHideFadeは今まで通り薄れてから隠れる()
    {
        OnSta(() =>
        {
            long now = 0;
            WindowMotion.Clock = () => now;
            var w = NewWin();
            try
            {
                w.Show();
                int hidden = 0;
                WindowMotion.HideFade(w, 1.0, 150, () => hidden++);
                Assert.True(w.IsVisible);
                Assert.True(WindowMotion.IsFadingOut(w));
                Assert.Equal(0, hidden);

                now = 75;
                WindowMotion.Tick(now);
                Assert.True(w.IsVisible);
                Assert.InRange(w.Opacity, 0.01, 0.9);
                Assert.Equal(0, hidden);

                now = 200;
                WindowMotion.Tick(now);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsFadingOut(w));
                Assert.Equal(1, hidden);
                Assert.Equal(1.0, w.Opacity, 3);
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; w.Close(); }
        });
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);

    [Fact]
    public void J91_透過窓を隠してからShowFadeで出し直すと_描画直後はcloakされて0透明を保ち_その後解除されて1まで出る()
    {
        OnSta(() =>
        {
            WindowMotion.Clock = null!;
            var w = NewWin();
            try
            {
                w.Show();
                TestUtil.Pump(50);
                WindowMotion.HideNow(w, 1.0);
                Assert.False(w.IsVisible);

                WindowMotion.ShowFade(w, 1.0, WindowMotion.PanelFadeMs);
                Assert.True(w.IsVisible);
                Assert.Equal(0.0, w.Opacity, 3);
                Assert.True(WindowMotion.IsCloaked(w), "ShowFade 直後に DWM cloak されていない");

                Assert.True(TestUtil.WaitUntil(() => !WindowMotion.IsCloaked(w) && !WindowMotion.IsRunning(w), 3000),
                    "ShowFade の cloak 解除またはフェードインが完了しない");
                Assert.False(WindowMotion.IsCloaked(w));
                Assert.True(w.IsVisible);
                Assert.Equal(1.0, w.Opacity, 3);
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; w.Close(); }
        });
    }

    [Fact]
    public void J91_cloak待ちの途中でHideNowしても隠しが残らず即座に隠れる()
    {
        OnSta(() =>
        {
            WindowMotion.Clock = null!;
            var w = NewWin();
            try
            {
                w.Show();
                TestUtil.Pump(50);
                WindowMotion.HideNow(w, 1.0);
                WindowMotion.ShowFade(w, 1.0, WindowMotion.PanelFadeMs);
                Assert.True(WindowMotion.IsCloaked(w));

                WindowMotion.HideNow(w, 1.0);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsCloaked(w));
                Assert.False(WindowMotion.IsRunning(w));
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                Assert.NotEqual(IntPtr.Zero, h);
                int cloakedAttr = -1;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloakedAttr, sizeof(int)) == 0)
                    Assert.Equal(0, cloakedAttr);
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; w.Close(); }
        });
    }
}

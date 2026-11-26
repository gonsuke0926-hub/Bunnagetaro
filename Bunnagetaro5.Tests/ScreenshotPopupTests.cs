using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>原寸窓は 画像1px=画面1物理px・上限は作業領域の8割（縦横比保持）</summary>
public class ScreenshotPopupTests
{
    [Theory]
    [InlineData(1.0, 800, 600)]
    [InlineData(1.5, 533.333, 400)]
    [InlineData(2.0, 400, 300)]
    public void 小さい画像は物理pxと同じ大きさ(double scale, double w, double h)
    {
        var (rw, rh) = ScreenshotPopup.FitSize(800, 600, scale, scale, 5000, 5000);
        Assert.Equal(w, rw, 2);
        Assert.Equal(h, rh, 2);
    }

    [Fact]
    public void 大きい画像は上限に収まるよう縦横比を保って縮む()
    {
        var (w, h) = ScreenshotPopup.FitSize(4000, 2000, 1.0, 1.0, 1000, 1000);
        Assert.Equal(1000, w, 3);
        Assert.Equal(500, h, 3);
        var (w2, h2) = ScreenshotPopup.FitSize(2000, 4000, 2.0, 2.0, 1000, 800);
        Assert.Equal(400, w2, 3);
        Assert.Equal(800, h2, 3);
    }

    [Fact]
    public void 空の画像は0()
        => Assert.Equal((0.0, 0.0), ScreenshotPopup.FitSize(0, 10, 1, 1, 100, 100));

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static void Pump(int ms) => TestUtil.Pump(ms);

    /// <summary>初回は原寸（SizeToContent）→ Manual へ移り、広げ縮めできて、原寸へ戻せる</summary>
    [Fact]
    public void 原寸窓は広げ縮めでき_原寸へ戻せる()
    {
        OnSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
            }
            string png = Path.Combine(Path.GetTempPath(), "b5_popup_" + Guid.NewGuid().ToString("N") + ".png");
            var src = System.Windows.Media.Imaging.BitmapSource.Create(400, 200, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[400 * 3 * 200], 400 * 3);
            File.WriteAllBytes(png, Bunnagetaro5.Services.ScreenShot.PngBytes(src));
            var w = new ScreenshotPopup(png, "t", _ => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 };
            try
            {
                Assert.Equal(ResizeMode.CanResize, w.ResizeMode);
                w.Show();
                Pump(300);
                Assert.Equal(SizeToContent.Manual, w.SizeToContent);
                double w0 = w.ActualWidth, h0 = w.ActualHeight;
                w.Width = w0 + 300; w.Height = h0 + 100;
                Pump(200);
                Assert.True(w.ActualWidth > w0 + 200, "広げられない");
                w.Width = w0 - 100; w.Height = h0 - 50;
                Pump(200);
                Assert.True(w.ActualWidth < w0 - 50, "縮められない");
                typeof(ScreenshotPopup).GetMethod("ResetToOriginal", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(w, null);
                Pump(200);
                Assert.Equal(w0, w.ActualWidth, 1);
                Assert.Equal(h0, w.ActualHeight, 1);
            }
            finally { w.Close(); try { File.Delete(png); } catch { } }
        });
    }

    [Fact]
    public void F15_親窓の中央へ置き_作業領域からはみ出す時はずらす()
    {
        // 中央に収まる
        var (l1, t1) = ScreenshotPopup.CenterInWorkArea(400, 300, 200, 100, 800, 600, 0, 0, 1920, 1040);
        Assert.Equal(400, l1, 1);
        Assert.Equal(250, t1, 1);

        // 右下へはみ出す → 作業領域の右下に収める
        var (l2, t2) = ScreenshotPopup.CenterInWorkArea(400, 300, 1700, 900, 400, 300, 0, 0, 1920, 1040);
        Assert.Equal(1520, l2, 1);
        Assert.Equal(740, t2, 1);

        // 左上へはみ出す（副モニタの負座標含む）→ 作業領域の左上に収める
        var (l3, t3) = ScreenshotPopup.CenterInWorkArea(400, 300, -1950, -50, 200, 200, -1920, 0, 1920, 1040);
        Assert.Equal(-1920, l3, 1);
        Assert.Equal(0, t3, 1);
    }

    [Fact]
    public void F15_原寸窓はOwnerの中央に出る()
    {
        OnSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
            }
            string png = Path.Combine(Path.GetTempPath(), "b5_popup_" + Guid.NewGuid().ToString("N") + ".png");
            var src = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[200 * 3 * 100], 200 * 3);
            File.WriteAllBytes(png, Bunnagetaro5.Services.ScreenShot.PngBytes(src));
            var owner = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100, Top = 100, Width = 600, Height = 500,
                ShowInTaskbar = false, Opacity = 0, AllowsTransparency = true, WindowStyle = WindowStyle.None,
            };
            owner.Show();
            var w = new ScreenshotPopup(png, "t", _ => { }) { Owner = owner };
            try
            {
                w.Show();
                Pump(300);
                double ocx = owner.Left + owner.ActualWidth / 2;
                double ocy = owner.Top + owner.ActualHeight / 2;
                double wcx = w.Left + w.ActualWidth / 2;
                double wcy = w.Top + w.ActualHeight / 2;
                Assert.True(Math.Abs(ocx - wcx) <= 2.0, $"X中心がずれている: owner={ocx}, popup={wcx}");
                Assert.True(Math.Abs(ocy - wcy) <= 2.0, $"Y中心がずれている: owner={ocy}, popup={wcy}");
            }
            finally { w.Close(); owner.Close(); try { File.Delete(png); } catch { } }
        });
    }

    [Fact]
    public void H8_開く最初の瞬間にもう最終の位置にいる()
    {
        OnSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
            }
            string png = Path.Combine(Path.GetTempPath(), "b5_popup_" + Guid.NewGuid().ToString("N") + ".png");
            var src = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[200 * 3 * 100], 200 * 3);
            File.WriteAllBytes(png, Bunnagetaro5.Services.ScreenShot.PngBytes(src));
            var owner = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100, Top = 100, Width = 600, Height = 500,
                ShowInTaskbar = false, Opacity = 0, AllowsTransparency = true, WindowStyle = WindowStyle.None,
            };
            owner.Show();
            var w = new ScreenshotPopup(png, "t", _ => { }) { Owner = owner };
            try
            {
                w.ShowAnimated();
                // Show 直後（最初の描画の前）の位置
                double l0 = w.Left, t0 = w.Top;
                Assert.False(double.IsNaN(l0) || double.IsNaN(t0));
                Pump(300);
                Assert.True(Math.Abs(l0 - w.Left) <= 1.0, $"Left が動いた: {l0} -> {w.Left}");
                Assert.True(Math.Abs(t0 - w.Top) <= 1.0, $"Top が動いた: {t0} -> {w.Top}");
            }
            finally
            {
                Bunnagetaro5.Services.WindowMotion.Cancel(w);
                w.Close(); owner.Close(); try { File.Delete(png); } catch { }
            }
        });
    }

    [Fact]
    public void J88_原寸窓は透過しない窓なので待たずにすぐ出てすぐ閉じる()
    {
        OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            string png = Path.Combine(Path.GetTempPath(), "b5_popup_" + Guid.NewGuid().ToString("N") + ".png");
            var src = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[200 * 3 * 100], 200 * 3);
            File.WriteAllBytes(png, Bunnagetaro5.Services.ScreenShot.PngBytes(src));
            var w = new ScreenshotPopup(png, "t", _ => { }) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 };
            int closed = 0;
            w.Closed += (_, _) => closed++;
            try
            {
                Assert.False(w.AllowsTransparency);
                w.ShowAnimated();
                Assert.True(w.IsVisible);
                Assert.False(Bunnagetaro5.Services.WindowMotion.IsRunning(w));
                Assert.False(((FrameworkElement)w.Content).RenderTransform is System.Windows.Media.ScaleTransform);

                // 閉じる: 待たずに即閉じる（OnClosing の再入で閉じずに残らない）
                w.Close();
                Assert.Equal(1, closed);
                Assert.False(Bunnagetaro5.Services.WindowMotion.IsFadingOut(w));
                Assert.False(Bunnagetaro5.Services.WindowMotion.IsRunning(w));

                w.Close();
                Assert.Equal(1, closed);
            }
            finally
            {
                Bunnagetaro5.Services.WindowMotion.Cancel(w);
                if (closed == 0) w.Close();
                try { File.Delete(png); } catch { }
            }
        });
    }

    [Fact]
    public void F14_原寸窓は下のコピー行が無く_右クリックにスクショと答えのメニューがある()
    {
        OnSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
            }
            string png = Path.Combine(Path.GetTempPath(), "b5_popup_" + Guid.NewGuid().ToString("N") + ".png");
            var src = System.Windows.Media.Imaging.BitmapSource.Create(200, 100, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[200 * 3 * 100], 200 * 3);
            File.WriteAllBytes(png, Bunnagetaro5.Services.ScreenShot.PngBytes(src));
            var w1 = new ScreenshotPopup(png, "t", _ => { }, "テスト答え") { WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 };
            var w2 = new ScreenshotPopup(png, "t", _ => { }, null) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 };
            try
            {
                Assert.IsType<Image>(w1.Content);
                var menu1 = w1.ContextMenu!;
                Assert.NotNull(menu1);
                var items1 = menu1.Items.OfType<MenuItem>().ToList();
                Assert.Single(items1);   // スクショをコピーだけ
                Assert.Equal("スクショをコピー", items1[0].Header);
                Assert.True(items1[0].IsEnabled);

                var items2 = w2.ContextMenu!.Items.OfType<MenuItem>().ToList();
                Assert.Single(items2);
            }
            finally { w1.Close(); w2.Close(); try { File.Delete(png); } catch { } }
        });
    }

    [Fact]
    public void F14_BuildShotDataはPNGとBitmapの2形式を含みクリップボードを触らない()
    {
        OnSta(() =>
        {
            var src = System.Windows.Media.Imaging.BitmapSource.Create(120, 80, 96, 96,
                System.Windows.Media.PixelFormats.Bgr24, null, new byte[120 * 3 * 80], 120 * 3);
            src.Freeze();
            var data = LogPage.BuildShotData(src);
            Assert.True(data.GetDataPresent("PNG"));
            Assert.True(data.GetDataPresent(DataFormats.Bitmap));
            var ms = Assert.IsAssignableFrom<MemoryStream>(data.GetData("PNG"));
            ms.Position = 0;
            var dec = System.Windows.Media.Imaging.BitmapFrame.Create(ms);
            Assert.Equal(120, dec.PixelWidth);
            Assert.Equal(80, dec.PixelHeight);
        });
    }
}

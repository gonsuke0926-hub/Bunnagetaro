using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND4 E11: キャプチャ窓の枠の色・不透明度・太さ。既定値で今の見た目・撮る範囲・掴める幅が変わらない</summary>
public class CaptureBorderTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public CaptureBorderTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_cb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
        CaptureBorder.Set(CaptureBorder.DefaultColor, CaptureBorder.DefaultAlpha, CaptureBorder.DefaultPx);
    }

    public void Dispose()
    {
        CaptureBorder.Set(CaptureBorder.DefaultColor, CaptureBorder.DefaultAlpha, CaptureBorder.DefaultPx);
        try { Directory.Delete(_temp, true); } catch { }
    }

    private ConfigStore Cfg()
    {
        var c = new ConfigStore(_paths);
        c.Load();
        return c;
    }

    // ---- 設定キー ----

    [Fact]
    public void 既定値は今の見た目と同じ()
    {
        var s = Cfg().Settings;
        Assert.Equal("#0067C0", s.CaptureBorderColor);
        Assert.Equal(0.70, s.CaptureBorderAlpha, 6);
        Assert.Equal(4, s.CaptureBorderPx);
        CaptureBorder.Update(s);
        var b = CaptureBorder.Brush();
        Assert.Equal(Color.FromArgb(0xB3, 0x00, 0x67, 0xC0), b.Color);   // 従来の AccentColor の 0xB3
        Assert.Equal(4, CaptureBorder.Px);
        Assert.Equal(8, CaptureBorder.HitBand);                          // 従来の HitBand = 8
        Assert.Equal(4, CaptureWindow.BorderPhys);
    }

    [Fact]
    public void 書いた値が読め範囲外は切り詰め不正な色は既定へ()
    {
        var c = Cfg();
        var s = c.Settings;
        s.CaptureBorderColor = "#FF8800"; s.CaptureBorderAlpha = 0.5; s.CaptureBorderPx = 9;
        Assert.Equal(("#FF8800", 0.5, 9), (s.CaptureBorderColor, s.CaptureBorderAlpha, s.CaptureBorderPx));
        s.CaptureBorderAlpha = 0.01; s.CaptureBorderPx = 0;
        Assert.Equal((0.15, 1), (s.CaptureBorderAlpha, s.CaptureBorderPx));
        s.CaptureBorderAlpha = 5; s.CaptureBorderPx = 99;
        Assert.Equal((1.0, 16), (s.CaptureBorderAlpha, s.CaptureBorderPx));
        s.CaptureBorderColor = "red";
        Assert.Equal("#0067C0", s.CaptureBorderColor);
        // 保存して読み直しても残る
        s.CaptureBorderColor = "#112233"; s.CaptureBorderPx = 7; s.CaptureBorderAlpha = 0.33;
        c.Save();
        var s2 = Cfg().Settings;
        Assert.Equal(("#112233", 7, 0.33), (s2.CaptureBorderColor, s2.CaptureBorderPx, s2.CaptureBorderAlpha));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(4, 8)]
    [InlineData(5, 9)]
    [InlineData(16, 20)]
    public void 掴める幅は太さ足す4で最低8(int px, int want)
    {
        Assert.Equal(want, CaptureBorder.HitBandFor(px));
        CaptureBorder.Set("#0067C0", 0.7, px);
        Assert.Equal(want, CaptureBorder.HitBand);
        Assert.Equal(want, CaptureWindow.HitBand);
    }

    [Theory]
    [InlineData(0.70, 0xB3)]
    [InlineData(1.0, 0xFF)]
    [InlineData(0.15, 0x26)]
    public void 不透明度は0から255へ丸める(double a, int want)
        => Assert.Equal((byte)want, CaptureBorder.AlphaByte(a));

    // ---- 窓（撮る範囲の削り幅・縁の描画） ----

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60, throwOnTimeout: false);

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    [Fact]
    public void 太さを変えると撮る範囲の削り幅と縁と掴める幅が追従し既定では従来どおり()
    {
        OnSta(() =>
        {
            EnsureApp();
            var cfg = Cfg();
            var w = new MainWindow(_paths, cfg);
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(500);
                var cap = (CaptureWindow)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_capture", F)!.GetValue(w.Session)!;
                var hwnd = new WindowInteropHelper(cap).Handle;
                var frame = (Border)typeof(CaptureWindow).GetField("_frame", F)!.GetValue(cap)!;
                Native.GetWindowRect(hwnd, out var r);

                // 既定: 従来どおり 4px 削る・掴める幅 8・縁 #B30067C0
                Assert.True(CaptureWindow.TryGetCaptureAreaFor(hwnd, out var a4));
                Assert.Equal((r.Left + 4, r.Top + 4, r.Right - 4, r.Bottom - 4), (a4.Left, a4.Top, a4.Right, a4.Bottom));
                Assert.Equal(8, CaptureWindow.HitBand);
                Assert.Equal(Color.FromArgb(0xB3, 0x00, 0x67, 0xC0), ((SolidColorBrush)frame.BorderBrush).Color);

                foreach (int px in new[] { 1, 16 })
                {
                    cfg.Settings.CaptureBorderPx = px;
                    cfg.Settings.CaptureBorderColor = "#FF8800";
                    cfg.Settings.CaptureBorderAlpha = 0.5;
                    w.Session.SyncBarFromCfg();
                    Pump(100);
                    Assert.True(CaptureWindow.TryGetCaptureAreaFor(hwnd, out var a));
                    Assert.Equal((r.Left + px, r.Top + px, r.Right - px, r.Bottom - px), (a.Left, a.Top, a.Right, a.Bottom));
                    Assert.Equal(Math.Max(8, px + 4), CaptureWindow.HitBand);
                    Assert.Equal(Color.FromArgb(0x80, 0xFF, 0x88, 0x00), ((SolidColorBrush)frame.BorderBrush).Color);
                    double dpi = Native.GetDpiForWindow(hwnd);
                    Assert.Equal(px * 96.0 / dpi, frame.BorderThickness.Left, 6);
                }
            }
            finally { w.Close(); }
        });
    }
}

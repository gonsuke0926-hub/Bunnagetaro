using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>主ボタンは淡い青（Soft 系トークン）。バーの「文字起こし」は左の隙間6</summary>
public class SoftPrimaryTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public SoftPrimaryTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_soft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60, throwOnTimeout: false);

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static double Lum(Color c)
    {
        static double L(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double x = Lum(a), y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Color Tok(string key) => (Color)Application.Current.FindResource(key);

    [Fact]
    public void 主ボタンの文字と地のコントラストは通常も乗せた時も押した時も6対1以上()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var text = Tok("AccentSoftTextColor");
            Assert.True(Contrast(text, Tok("AccentSoftColor")) >= 6.0);
            Assert.True(Contrast(text, Tok("AccentSoftHoverColor")) >= 6.0);
            Assert.True(Contrast(text, Tok("AccentSoftPressColor")) >= 6.0);
            // AccentColor 自体は据え置き
            Assert.Equal(Color.FromRgb(0x00, 0x67, 0xC0), Tok("AccentColor"));
        });
    }

    [Fact]
    public void バーに文字起こしボタンは無くぶん投げるは淡い青()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(250);
                w.Session.OpenCapture();
                Pump(250);
                var bar = (Window)Fld(w.Session, "_bar")!;
                // バーから「文字起こし」「画像そのまま」ラジオボタンが削除されている
                Assert.Null(bar.GetType().GetField("_ocrOn", BindingFlags.NonPublic | BindingFlags.Instance));
                Assert.Null(bar.GetType().GetField("_ocrOff", BindingFlags.NonPublic | BindingFlags.Instance));
                var btn = (Button)Fld(bar, "_translateBtn")!;
                Assert.Equal(Tok("AccentSoftColor"), ((SolidColorBrush)btn.Background).Color);
                Assert.Equal(Tok("AccentSoftBorderColor"), ((SolidColorBrush)btn.BorderBrush).Color);
                var label = (TextBlock)Fld(bar, "_translateLabel")!;
                Assert.Equal(Tok("AccentSoftTextColor"), ((SolidColorBrush)label.Foreground).Color);
            }
            finally { w.Close(); }
        });
    }
}

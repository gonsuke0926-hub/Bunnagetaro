using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bunnagetaro5;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND1 A6: アプリのアイコン（窓の Icon・バージョン情報の絵・素材が元のまま）。</summary>
public class AppIconTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public AppIconTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_icon_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    [Fact]
    public void アイコンの素材は7つの大きさが入ったicoで取り込める()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(
                new Uri("pack://application:,,,/Bunnagetaro;component/Assets/app.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var sizes = dec.Frames.Select(f => f.PixelWidth).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);
            Assert.Equal(256, ((System.Windows.Media.Imaging.BitmapSource)AppIcon.Window()).PixelWidth);
            Assert.Equal(256, ((System.Windows.Media.Imaging.BitmapSource)AppIcon.Png256()).PixelWidth);
        });
    }

    [Fact]
    public void メイン窓にIconが設定されている()
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
                Assert.NotNull(w.Icon);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void バージョン情報の窓にもIconと幅64の絵がある()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var a = new AboutWindow();
            try
            {
                a.Show();
                Assert.NotNull(a.Icon);
                var img = ((StackPanel)a.Content).Children.OfType<StackPanel>().First()
                    .Children.OfType<Image>().First();
                Assert.Equal(64, img.Width);
                Assert.NotNull(img.Source);
            }
            finally { a.Close(); }
        });
    }
}

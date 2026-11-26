using System;
using System.IO;
using System.Linq;
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

/// <summary>
/// ☰メニューの「バーの使い方」。注釈図（bar_help.png）が設定ページに入っていて、
/// メニューから押すと設定ページのその節までスクロールする（以前は注釈図が無く、設定ページへ行くだけだった）。
/// </summary>
public class BarHelpTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public BarHelpTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_barhelp_" + Guid.NewGuid().ToString("N"));
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

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t && t.Name == name) return t;
            var d = Find<T>(ch, name);
            if (d is not null) return d;
        }
        return null;
    }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    [Fact]
    public void バーの使い方を選ぶと設定ページの注釈図の節までスクロールする()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Width = 940; w.Height = 600;
                w.Show();
                Pump(200);
                typeof(MainWindow).GetMethod("DoDrawerAction", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(w, new object[] { "help" });
                Pump(400);
                var img = Find<Image>(w, "BarHelpImage");
                Assert.NotNull(img);
                Assert.NotNull(img!.Source);
                Assert.True(img.Width > 0 || img.ActualWidth > 0, "注釈図が描かれていない");
                var scroll = Find<ScrollViewer>(w, "BodyScroll")!;
                Assert.True(scroll.VerticalOffset > 100, "節までスクロールしていない: " + scroll.VerticalOffset);
                // 節の見出しが見える範囲に入っている
                var head = Find<TextBlock>(w, "BarHelpHeading")!;
                var y = head.TranslatePoint(new Point(0, 0), scroll).Y;
                Assert.InRange(y, -1, scroll.ViewportHeight);
            }
            finally { w.Close(); }
        });
    }
}

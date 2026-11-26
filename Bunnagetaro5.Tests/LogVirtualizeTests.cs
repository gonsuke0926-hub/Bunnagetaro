using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND2 C2: ログの ListBox の仮想化が実際に効いている（全カードを実体化しない）。</summary>
public class LogVirtualizeTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public LogVirtualizeTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_logv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static void Pump(Dispatcher d)
        => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static int CountVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        int n = root is T ? 1 : 0;
        for (int i = 0, c = VisualTreeHelper.GetChildrenCount(root); i < c; i++)
            n += CountVisuals<T>(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    [Fact]
    public void 画面に収まらない分のカードは実体化されずスクロールは下端へ行ける()
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
            var store = new LogStore(_paths);
            for (int i = 0; i < 60; i++)
                store.InsertAndDump(new JsonObject
                {
                    ["id"] = "id" + i,
                    ["ts"] = $"2026-11-12 01:{i:00}:00",
                    ["status"] = "ok",
                    ["text"] = "答え " + i + " 長い文章 ".PadRight(120, '長'),
                });
            var page = new LogPage(store, _ => { });
            var win = new Window { Width = 700, Height = 500, Content = page,
                                   ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                                   Left = -5000, Top = 0 };
            win.Show();
            Pump(win.Dispatcher);
            Pump(win.Dispatcher);

            var lst = (ListBox)page.FindName("Lst");
            Assert.True(VirtualizingPanel.GetIsVirtualizing(lst));
            Assert.True(ScrollViewer.GetCanContentScroll(lst));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(lst));
            int cards = CountVisuals<ListBoxItem>(lst);
            Assert.InRange(cards, 1, 30);   // 61個（もっと見るボタン含む）を全部は作っていない

            // 幅を変えても全部は作られない（リサイズでの測り直しが可視分だけになる）
            win.Width = 520;
            Pump(win.Dispatcher);
            Assert.InRange(CountVisuals<ListBoxItem>(lst), 1, 30);
            win.Close();
        });
    }
}

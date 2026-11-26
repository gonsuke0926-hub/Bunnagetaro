using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

/// <summary>FIX_ROUND5 F23〜F24: ログの下付き・読んでいる位置の保持。J14: 台帳のログを全部出す（F25 の「古いログを表示」はやめた）</summary>
public class LogScrollTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public LogScrollTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 90);

    private static void Pump(Dispatcher d)
    {
        d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    private static JsonObject Entry(int i) => new()
    {
        ["id"] = "id" + i,
        ["ts"] = $"2026-11-12 {i / 3600:00}:{i / 60 % 60:00}:{i % 60:00}",
        ["status"] = "ok",
        ["text"] = "答え " + i + " " + new string('長', 40 + i % 7 * 30),
    };

    private LogStore MakeStore(int n)
    {
        var store = new LogStore(_paths);
        for (int i = 0; i < n; i++) store.InsertAndDump(Entry(i));
        return store;
    }

    private static ScrollViewer Sc(LogPage page)
    {
        var lst = (ListBox)page.FindName("Lst");
        return (ScrollViewer)typeof(LogPage).GetProperty("Sc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    }

    private static Window Host(LogPage page) => new()
    {
        Width = 700, Height = 500, Content = page, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0,
    };

    [Fact]
    public void F23_一番下に居る時に新しいカードが来たら_配置が終わった後も一番下にいる()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = MakeStore(30);
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Show();
            Pump(win.Dispatcher);
            var sc = Sc(page);
            sc.ScrollToEnd();
            Pump(win.Dispatcher);
            Assert.True(sc.ScrollableHeight - sc.VerticalOffset < 2);

            for (int k = 100; k < 104; k++)
            {
                var e = Entry(k);
                store.InsertAndDump(e);
                page.AppendLiveCard(e);
                Pump(win.Dispatcher);
                Assert.True(sc.ScrollableHeight - sc.VerticalOffset < 2,
                    $"一番下から外れた: extent={sc.ExtentHeight} off={sc.VerticalOffset} h={sc.ViewportHeight}");
            }
            win.Close();
        });
    }

    [Fact]
    public void F24_上を読んでいる時_カードが足されても見えている先頭のカードの位置を保つ()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = MakeStore(40);
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Show();
            Pump(win.Dispatcher);
            var sc = Sc(page);
            sc.ScrollToEnd();
            Pump(win.Dispatcher);
            sc.ScrollToVerticalOffset(sc.VerticalOffset / 2);
            Pump(win.Dispatcher);
            Assert.True(sc.ScrollableHeight - sc.VerticalOffset > 100, "下にいない前提");

            var before = page.CaptureAnchor()!.Value;
            for (int k = 100; k < 103; k++)
            {
                var e = Entry(k);
                store.InsertAndDump(e);
                page.AppendLiveCard(e);
                Pump(win.Dispatcher);
                var now = page.CaptureAnchor()!.Value;
                Assert.Same(before.Item, now.Item);
                Assert.Equal(before.Top, now.Top, 1);
            }
            win.Close();
        });
    }

    private static System.Collections.ObjectModel.ObservableCollection<object> Items(LogPage page)
        => (System.Collections.ObjectModel.ObservableCollection<object>)
            typeof(LogPage).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    [Fact]
    public void J14_台帳のログを全部カードにする_古いログを表示のボタンは無い()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = MakeStore(LogStore.MaxEntries);   // 前は起動時60件・最大120件だった
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Show();
            Pump(win.Dispatcher);
            var items = Items(page);
            Assert.Equal(LogStore.MaxEntries, items.Count);
            Assert.All(items, o => Assert.IsType<LogCardVm>(o));
            // 上が古い・下が新しい
            Assert.Equal("id0", ((LogCardVm)items[0]).Id);
            Assert.Equal("id" + (LogStore.MaxEntries - 1), ((LogCardVm)items[^1]).Id);
            // 一番上まで行ける（いちばん古いカードが見える）
            var sc = Sc(page);
            sc.ScrollToVerticalOffset(0);
            Pump(win.Dispatcher);
            Assert.Same(items[0], page.CaptureAnchor()!.Value.Item);
            win.Close();
        });
    }

    [Fact]
    public void J14_起動中に足して台帳の上限を超えたら_いちばん古いカードから外す()
    {
        OnSta(() =>
        {
            EnsureApp();
            int max = LogStore.MaxEntries;
            var store = MakeStore(max);
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Show();
            Pump(win.Dispatcher);
            for (int k = max; k < max + 3; k++)
            {
                var e = Entry(k);
                store.InsertAndDump(e);
                page.AppendLiveCard(e);
            }
            Pump(win.Dispatcher);
            var items = Items(page);
            Assert.Equal(max, items.Count);
            Assert.Equal("id3", ((LogCardVm)items[0]).Id);
            Assert.Equal("id" + (max + 2), ((LogCardVm)items[^1]).Id);
            win.Close();
        });
    }

    [Fact]
    public void J15_つまみを引いている間は中身を動きごとに動かさず_入力を片付けた後で追いつき_離すとその所へ行く()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = MakeStore(LogStore.MaxEntries);
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Show();
            Pump(win.Dispatcher);
            var sc = Sc(page);
            Assert.True(sc.IsDeferredScrollingEnabled);
            var sb = (System.Windows.Controls.Primitives.ScrollBar)sc.Template.FindName("PART_VerticalScrollBar", sc);
            var thumb = sb.Track.Thumb;
            double start = sc.VerticalOffset;   // 開いた時は一番下
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -(sb.Track.ActualHeight - thumb.ActualHeight) / 2));
            win.UpdateLayout();
            Assert.True(sb.Value < start - 100, "つまみは動く");
            Assert.Equal(start, sc.VerticalOffset, 1);   // 中身はまだ動かない
            // 入力を片付けた後で、つまみの所まで追いつく（離す前）
            var until = DateTime.UtcNow.AddMilliseconds(2000);
            while (Math.Abs(sb.Value - sc.VerticalOffset) > 1 && DateTime.UtcNow < until)
            {
                Thread.Sleep(10);
                Pump(win.Dispatcher);
            }
            Assert.Equal(sb.Value, sc.VerticalOffset, 1);
            // 離すとその所
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -20));
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false));
            Pump(win.Dispatcher);
            Assert.Equal(sb.Value, sc.VerticalOffset, 1);
            win.Close();
        });
    }

    private static System.Collections.Generic.List<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        var acc = new System.Collections.Generic.List<T>();
        void Walk(DependencyObject d)
        {
            if (d is T t) acc.Add(t);
            for (int i = 0, c = VisualTreeHelper.GetChildrenCount(d); i < c; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return acc;
    }

    private static JsonObject TextEntry(int i, string text) => new()
    {
        ["id"] = "id" + i,
        ["ts"] = $"2026-11-18 00:00:{i % 60:00}",
        ["status"] = "ok",
        ["text"] = text,
    };

    [Fact]
    public void J16_たたんだカードは答えの長さによらず同じ高さ_長い答えだけ続きを表示が出る()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = new LogStore(_paths);
            store.InsertAndDump(TextEntry(0, "短い答え"));
            store.InsertAndDump(TextEntry(1, string.Join("\n\n", Enumerable.Range(0, 30).Select(k => "長い答えの段落 " + k))));
            store.InsertAndDump(TextEntry(2, "二行の答え\n二行目"));
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Height = 1400;
            win.Show();
            Pump(win.Dispatcher);
            var lst = (ListBox)page.FindName("Lst");
            var items = Items(page);
            var heights = items.Select(it => ((FrameworkElement)lst.ItemContainerGenerator.ContainerFromItem(it)).ActualHeight).ToList();
            Assert.Equal(3, heights.Count);
            Assert.All(heights, h => Assert.Equal(heights[0], h, 1));
            var clips = Visuals<LogClipBox>(lst).ToDictionary(c => ((LogCardVm)c.DataContext).Id);
            Assert.False(clips["id0"].ShowMore);
            Assert.True(clips["id1"].ShowMore);
            Assert.False(clips["id2"].ShowMore);
            Assert.Equal(LogPage.BodyHeightFor(null), clips["id0"].ActualHeight, 1);
            win.Close();
        });
    }

    [Fact]
    public void J16_開くとそのカードだけ中身の高さまで伸び_たたむと戻る()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = new LogStore(_paths);
            store.InsertAndDump(TextEntry(0, string.Join("\n\n", Enumerable.Range(0, 30).Select(k => "長い答えの段落 " + k))));
            store.InsertAndDump(TextEntry(1, "短い答え"));
            var page = new LogPage(store, _ => { });
            var win = Host(page);
            win.Height = 1400;
            win.Show();
            Pump(win.Dispatcher);
            var lst = (ListBox)page.FindName("Lst");
            var items = Items(page);
            var longVm = items.OfType<LogCardVm>().Single(v => v.Id == "id0");
            var shortVm = items.OfType<LogCardVm>().Single(v => v.Id == "id1");
            double H(object it) => ((FrameworkElement)lst.ItemContainerGenerator.ContainerFromItem(it)).ActualHeight;
            double before = H(longVm), other = H(shortVm);
            var clip = Visuals<LogClipBox>(lst).Single(c => ReferenceEquals(c.DataContext, longVm));
            longVm.IsExpanded = true;
            Pump(win.Dispatcher);
            Assert.True(clip.IsExpanded);
            Assert.True(clip.ShowLess);
            Assert.False(clip.ShowMore);
            Assert.True(H(longVm) > before + 100);
            Assert.Equal(clip.FullHeight, clip.ActualHeight, 1);
            Assert.Equal(other, H(shortVm), 1);   // ほかのカードは変わらない
            longVm.IsExpanded = false;
            Pump(win.Dispatcher);
            Assert.Equal(before, H(longVm), 1);
            win.Close();
        });
    }
}

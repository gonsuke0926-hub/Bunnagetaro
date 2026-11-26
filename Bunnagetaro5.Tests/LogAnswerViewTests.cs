using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>H4a・H4b: ログの本文を解答欄と同じ AnswerView で出す</summary>
public class LogAnswerViewTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public LogAnswerViewTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_loga_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 90);

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    private static void Pump(Dispatcher d) => d.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        int n = root is T ? 1 : 0;
        for (int i = 0, c = VisualTreeHelper.GetChildrenCount(root); i < c; i++)
            n += Count<T>(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    private static void Collect<T>(DependencyObject root, System.Collections.Generic.List<T> acc) where T : DependencyObject
    {
        if (root is T t) acc.Add(t);
        for (int i = 0, c = VisualTreeHelper.GetChildrenCount(root); i < c; i++)
            Collect(VisualTreeHelper.GetChild(root, i), acc);
    }

    private static int InlineUiCount(AnswerView v)
    {
        int n = 0;
        foreach (var b in v.Body.Document.Blocks)
            if (b is Paragraph p) n += p.Inlines.OfType<InlineUIContainer>().Count();
        return n;
    }

    [Fact]
    public void 使い回しで本文が替わると前の式は残らない()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView { LogMode = true };
            v.LogText = "前の答え $$x^2$$ と $y$ です";
            Assert.True(InlineUiCount(v) > 0);
            v.LogText = "次の答え 式なし";
            Assert.Equal(0, InlineUiCount(v));
            Assert.Equal("次の答え 式なし", new TextRange(v.Body.Document.ContentStart, v.Body.Document.ContentEnd).Text.Trim());
            Assert.Equal("次の答え 式なし", v.ModelText);
        });
    }

    [Fact]
    public void ログ用は本文がスクロールバーを出さず高さが中身に伸びる()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView { LogMode = true };
            Assert.Equal(ScrollBarVisibility.Disabled, v.Body.VerticalScrollBarVisibility);
            var host = new Grid { Width = 300 };
            host.Children.Add(v);
            v.VerticalAlignment = VerticalAlignment.Top;
            v.LogText = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => "段落 " + i));
            host.Measure(new Size(300, double.PositiveInfinity));
            Assert.True(host.DesiredSize.Height > 300);   // 30段落ぶんに伸びる
        });
    }

    [Fact]
    public void ログの色は画面に合わせ大きさと間は解答欄の設定になる()
    {
        OnSta(() =>
        {
            EnsureApp();
            AnswerView.LogStyle = new PanelStyle("#ffffff", "#000000", 0.5, "", 20, "plain", 0.5, 1.5);
            try
            {
                var v = new AnswerView { LogMode = true };
                v.LogText = "a\n\nb";
                Assert.Equal(20, v.Body.FontSize);
                Assert.Equal(30, v.Body.Document.LineHeight);
                var p = (Paragraph)v.Body.Document.Blocks.FirstBlock;
                Assert.Equal(10, p.Margin.Bottom);   // 20 * 0.5
                // 解答欄の白い文字は使わない（ログの文字色 Text）
                Assert.Equal(Application.Current.FindResource("Text"), v.Body.Foreground);
            }
            finally { AnswerView.LogStyle = null; }
        });
    }

    [Fact]
    public void 式入りのログでスクロールしても各カードの本文はそのカードの答えと一致する()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = new LogStore(_paths);
            for (int i = 0; i < 60; i++)
                store.InsertAndDump(new JsonObject
                {
                    ["id"] = "id" + i,
                    ["ts"] = $"2026-11-12 01:{i:00}:00",
                    ["status"] = "ok",
                    ["text"] = $"答え{i} $$\\frac{{{i}}}{{2}}$$ 続き " + new string('長', 80),
                });
            var page = new LogPage(store, _ => { });
            var win = new Window { Width = 700, Height = 500, Content = page, ShowActivated = false,
                                   WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0 };
            win.Show();
            Pump(win.Dispatcher);
            var lst = (ListBox)page.FindName("Lst");
            var sc = (ScrollViewer)typeof(LogPage).GetProperty("Sc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
            foreach (double off in new[] { 0.0, 900.0, 2500.0, 0.0, 5000.0, 1200.0 })
            {
                sc.ScrollToVerticalOffset(off);
                Pump(win.Dispatcher);
                Pump(win.Dispatcher);
                var views = new System.Collections.Generic.List<AnswerView>();
                Collect(lst, views);
                Assert.NotEmpty(views);
                foreach (var v in views)
                {
                    if (v.DataContext is LogCardVm vm) Assert.True(vm.Body == v.ModelText, $"off={off} id={vm.Id} logtext=[{v.LogText.Length}] model=[{v.ModelText.Length}] vis={v.IsVisible} mode={v.LogMode}");
                }
                Assert.InRange(Count<ListBoxItem>(lst), 1, 30);   // 仮想化は効いたまま
            }
            win.Close();
        });
    }

    [Fact]
    public void H4b_選んだ所だけがコピーの対象になる()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView { LogMode = true };
            v.LogText = "abcdef";
            var sel = typeof(AnswerView).GetMethod("SelectedText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.Equal("", (string)sel.Invoke(v, null)!);
            var doc = v.Body.Document;
            var start = doc.ContentStart.GetPositionAtOffset(3);   // 段落の開始タグぶんを飛ばして b から
            v.Body.Selection.Select(start, start.GetPositionAtOffset(3));
            Assert.Equal("bcd", (string)sel.Invoke(v, null)!);
        });
    }

    [Fact]
    public void H4b_画像の右クリックはスクショをコピーだけでカードの余白は2つ()
    {
        OnSta(() =>
        {
            EnsureApp();
            var store = new LogStore(_paths);
            store.InsertAndDump(new JsonObject { ["id"] = "x1", ["ts"] = "2026-11-12 01:00:00", ["status"] = "ok", ["text"] = "答え" });
            var page = new LogPage(store, _ => { });
            var win = new Window { Width = 700, Height = 400, Content = page, ShowActivated = false,
                                   WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0 };
            win.Show();
            Pump(win.Dispatcher);
            var lst = (ListBox)page.FindName("Lst");
            var borders = new System.Collections.Generic.List<Border>();
            Collect(lst, borders);
            var withMenu = borders.Where(b => b.ContextMenu is not null).ToList();
            var thumb = withMenu.Single(b => b.Cursor == System.Windows.Input.Cursors.Hand);
            Assert.Equal(new[] { "スクショをコピー" }, thumb.ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray());
            var card = withMenu.Single(b => b != thumb);
            Assert.Equal(new[] { "答えをすべてコピー", "スクショをコピー" }, card.ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray());
            win.Close();
        });
    }

    [Fact]
    public void 本文の右クリックはコピーと答えをすべてコピーの2つ()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView { LogMode = true };
            v.LogText = "abcdef";
            var items = v.Body.ContextMenu!.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "コピー", "答えをすべてコピー" }, items.Select(i => (string)i.Header).ToArray());
        });
    }
}

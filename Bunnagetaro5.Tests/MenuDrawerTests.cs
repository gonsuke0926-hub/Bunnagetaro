using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// ☰メニューの動作確認。このPCの自動化シェルからはマウス・鍵盤入力が
/// 窓に届かないため、WPF自身のイベント（Click・Esc・スクリーンクリック）を
/// 起こして、開閉・スクリーン・今のページ印・ページ切り替えを確かめる。
/// </summary>
public class MenuDrawerTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public MenuDrawerTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_menu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers ----

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t && (string.IsNullOrEmpty(name) || t.Name == name)) return t;
            var deeper = Find<T>(ch, name);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    /// <summary>MainWindowを作ってSTAスレッドの上で動かし、結果を返す。</summary>
    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(TimeSpan.FromSeconds(30));
        if (err is not null) throw err;
        return result;
    }

    /// <summary>
    /// テストにはApp.xamlが無いため、テーマ（Colors→Controlsの順）を
    /// 自分のApplicationに読み込んでから窓を作る（本番と同じマージ順）。
    /// </summary>
    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private MainWindow MakeWindow(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        return new MainWindow(_paths, cfg);
    }

    // ---- tests ----

    [Fact]
    public void MenuButtonOpensDrawerAndItSlidesFullyOut()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);

            var layer = Find<Grid>(w, "DrawerLayer")!;
            var panel = Find<Border>(w, "DrawerPanel")!;
            Assert.Equal(Visibility.Collapsed, layer.Visibility);

            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, layer.Visibility);

            // 180msのアニメが終わって左端(X=0)まで出きるのを待つ
            for (int i = 0; i < 40; i++)
            {
                Pump(w.Dispatcher, 50);
                var tr = (TranslateTransform)panel.RenderTransform!;
                if (tr.X <= 0.5) break;
            }
            var t2 = (TranslateTransform)panel.RenderTransform!;
            Assert.True(t2.X <= 0.5, $"X={t2.X}");
            Assert.Equal(280.0, panel.Width);

            // スクリーン（黒0.2）が覆い、今のページ「ログ」に縦線
            var scrim = Find<System.Windows.Shapes.Rectangle>(w, "Scrim")!;
            Assert.Equal(Visibility.Visible, scrim.Visibility);
            var mark = Find<System.Windows.Shapes.Rectangle>(w, "MarkLog")!;
            Assert.Equal(Visibility.Visible, mark.Visibility);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void EscapeClosesDrawer()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);
            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(w.Dispatcher, 400);
            var layer = Find<Grid>(w, "DrawerLayer")!;
            Assert.Equal(Visibility.Visible, layer.Visibility);

            var esc = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(w), 0, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent };
            w.RaiseEvent(esc);
            for (int i = 0; i < 40 && layer.Visibility == Visibility.Visible; i++)
                Pump(w.Dispatcher, 50);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void ScrimClickClosesDrawer()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);
            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(w.Dispatcher, 400);
            var layer = Find<Grid>(w, "DrawerLayer")!;

            var scrim = Find<System.Windows.Shapes.Rectangle>(w, "Scrim")!;
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            scrim.RaiseEvent(args);
            for (int i = 0; i < 40 && layer.Visibility == Visibility.Visible; i++)
                Pump(w.Dispatcher, 50);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void ItemSwitchesPageAfterClosingAndMarksCurrentPage()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);
            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(w.Dispatcher, 400);

            var item = Find<Border>(w, "ItemCfg")!;
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            item.RaiseEvent(args);

            // 閉じ始めてから（Background優先で）ページが切り替わる
            for (int i = 0; i < 40; i++) Pump(w.Dispatcher, 50);
            var pageArea = Find<Grid>(w, "PageArea")!;
            Assert.Single(pageArea.Children);
            Assert.IsType<Bunnagetaro5.Pages.SettingsPage>(pageArea.Children[0]);

            // 閉み終わり→覆いは消えている。今のページ印は「設定」に移る
            var layer = Find<Grid>(w, "DrawerLayer")!;
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.Equal(Visibility.Visible, Find<System.Windows.Shapes.Rectangle>(w, "MarkCfg")!.Visibility);
            Assert.Equal(Visibility.Collapsed, Find<System.Windows.Shapes.Rectangle>(w, "MarkLog")!.Visibility);

            // 設定ページの見出しでログへ戻れる
            var back = Find<Button>(w, "BackButton")!;
            back.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(w.Dispatcher, 200);
            Assert.IsType<Bunnagetaro5.Pages.LogPage>(pageArea.Children[0]);
            Assert.Equal(Visibility.Visible, Find<System.Windows.Shapes.Rectangle>(w, "MarkLog")!.Visibility);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void ToggleClosesWhenAlreadyOpen()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);
            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(w.Dispatcher, 400);
            var layer = Find<Grid>(w, "DrawerLayer")!;
            Assert.Equal(Visibility.Visible, layer.Visibility);

            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (int i = 0; i < 40 && layer.Visibility == Visibility.Visible; i++)
                Pump(w.Dispatcher, 50);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);

            w.Close();
            return true;
        });
    }

    /// <summary>開く動きはレイアウト後の次フレームから始まり、項目の動作は閉じ終わってから走る。
    /// 動いている間だけビットマップキャッシュが載り、終わったら外れる</summary>
    [Fact]
    public void 開く動きはレイアウト後に始まり_項目の動作は閉じ終わってから走る()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 300);
            var layer = Find<Grid>(w, "DrawerLayer")!;
            var panel = Find<Border>(w, "DrawerPanel")!;
            var menu = Find<Button>(w, "MenuButton")!;
            menu.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // 押した直後: すでにレイアウト済み（幅が出ている）で、動きはまだ始まっていない（右の外）
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.True(panel.ActualWidth > 100, "レイアウト前に動き出している: " + panel.ActualWidth);
            var tr = (TranslateTransform)panel.RenderTransform;
            Assert.True(tr.X >= panel.ActualWidth - 0.5);
            Pump(w.Dispatcher, 100);
            Assert.True(tr.X < panel.ActualWidth - 0.5, "次フレーム以降に動き出していない");
            Assert.NotNull(panel.CacheMode);               // 動いている間だけキャッシュ
            Pump(w.Dispatcher, 400);
            Assert.Null(panel.CacheMode);

            var item = Find<Border>(w, "ItemCfg")!;
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            var pageArea = Find<Grid>(w, "PageArea")!;
            Pump(w.Dispatcher, 60);                         // 閉じる動きの途中
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.IsNotType<Bunnagetaro5.Pages.SettingsPage>(pageArea.Children.Count > 0 ? pageArea.Children[0] : null);
            for (int i = 0; i < 40; i++) Pump(w.Dispatcher, 50);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.IsType<Bunnagetaro5.Pages.SettingsPage>(pageArea.Children[0]);
            Assert.Null(panel.CacheMode);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void F32_DrawerFolderOpensRootAndIsNamedLogFolder()
    {
        OnSta(() =>
        {
            var w = MakeWindow(out _);
            w.Show();
            Pump(w.Dispatcher, 100);
            var item = Find<Border>(w, "ItemFolder")!;
            var grid = (Grid)item.Child;
            var label = grid.Children.OfType<TextBlock>().Last();
            Assert.Equal("ログのフォルダを開く", label.Text);
            Assert.Equal(_paths.Root, w.LogFolderTarget());
            w.Close();
            return true;
        });
    }
}

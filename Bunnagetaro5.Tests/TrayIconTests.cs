using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 修正J50: タスクトレイのアイコンと右クリックのメニュー。
/// メニューの項目は Click を呼び（PerformClick）、dispatcher を回して結果の設定を見る
/// </summary>
public class TrayIconTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public TrayIconTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_tray_" + Guid.NewGuid().ToString("N"));
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

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private (MainWindow w, ConfigStore cfg) Open()
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(200);
        return (w, cfg);
    }

    [Fact]
    public void J50_窓を出すとトレイのアイコンが見えて_窓を閉じると外れる()
    {
        OnSta(() =>
        {
            var (w, _) = Open();
            var tray = w.Tray!;
            Assert.NotNull(tray);
            Assert.True(tray.Icon.Visible);
            Assert.NotNull(tray.Icon.Icon);
            Assert.Equal(Bunnagetaro5.Pages.AboutWindow.AppTitle, tray.Icon.Text);
            Assert.True(tray.Icon.Text.Length <= 63);
            w.Close();
            Pump(100);
            Assert.True(tray.IsDisposed);
        });
    }

    [Fact]
    public void J50_メニューの項目は決めた順に並ぶ()
    {
        OnSta(() =>
        {
            var (w, _) = Open();
            try
            {
                var items = w.Tray!.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().ToList();
                Assert.Equal(6, items.Count);
                Assert.Equal("ログの表示", items[0].Text);
                Assert.Equal("解答欄の場所（このプリセット）", items[1].Text);
                Assert.Equal("クイックぶん投げ", items[2].Text);
                Assert.Equal("プリセット", items[3].Text);
                Assert.IsType<System.Windows.Forms.ToolStripSeparator>(items[4]);
                Assert.Equal("終了", items[5].Text);
                var sub = ((System.Windows.Forms.ToolStripMenuItem)items[1]).DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text);
                Assert.Equal(new[] { "枠の中", "枠の外" }, sub);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J50_プリセットを選ぶと切り替わり_チェックも動く()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            try
            {
                var tray = w.Tray!;
                tray.RefreshMenu();
                var items = tray.PresetItem.DropDownItems.Cast<System.Windows.Forms.ToolStripMenuItem>().ToList();
                Assert.True(items.Count >= 2);
                Assert.Single(items, i => i.Checked);
                var other = items.First(i => !i.Checked);
                other.PerformClick();
                Pump(100);
                Assert.Equal(other.Text, cfg.Settings.PromptActive);
                var after = tray.PresetItem.DropDownItems.Cast<System.Windows.Forms.ToolStripMenuItem>().ToList();
                Assert.Equal(other.Text, after.Single(i => i.Checked).Text);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J50_クイックを選ぶと入り切りが反転する()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            try
            {
                var tray = w.Tray!;
                bool before = cfg.Settings.QuickEnabled;
                tray.QuickItem.PerformClick();
                Pump(100);
                Assert.Equal(!before, cfg.Settings.QuickEnabled);
                Assert.Equal(!before, tray.QuickItem.Checked);
                tray.QuickItem.PerformClick();
                Pump(100);
                Assert.Equal(before, cfg.Settings.QuickEnabled);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J50_枠の外を選ぶと今のプリセットの解答欄の場所が外になり_枠の中で戻る()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            try
            {
                var tray = w.Tray!;
                tray.PlaceOutItem.PerformClick();
                Pump(100);
                Assert.Equal("outside", cfg.EffectiveResultPlace());
                Assert.True(tray.PlaceOutItem.Checked);
                Assert.False(tray.PlaceInItem.Checked);
                tray.PlaceInItem.PerformClick();
                Pump(100);
                Assert.Equal("inside", cfg.EffectiveResultPlace());
                Assert.True(tray.PlaceInItem.Checked);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J50_終了を選ぶと窓が閉じて_二重クリックは窓を出す()
    {
        OnSta(() =>
        {
            var (w, _) = Open();
            var tray = w.Tray!;
            w.WindowState = WindowState.Minimized;
            tray.OnDoubleClick();   // 修正J52: 左の1回押しは範囲ドラッグに変わった。窓を出すのは二重押し
            Assert.NotEqual(WindowState.Minimized, w.WindowState);
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Last().PerformClick();
            Pump(300);
            Assert.True(closed);
            Assert.True(w.Quitting);
            Assert.True(tray.IsDisposed);
        });
    }

    [Fact]
    public void J51_既定はOFFで_閉じるボタンを押すと今まで通り窓が閉じる()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            Assert.False(cfg.Settings.CloseToTray);
            var tray = w.Tray!;
            Assert.False(ConfigStore.DefaultConfig()["close_to_tray"]!.GetValue<bool>());
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            w.Close();
            Pump(300);
            Assert.True(closed);
            Assert.True(tray.IsDisposed);
        });
    }

    [Fact]
    public void J51_ONの時の閉じるボタンは窓を隠すだけで_トレイは残り_窓を出すで戻る()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            cfg.Settings.CloseToTray = true;
            var tray = w.Tray!;
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            try
            {
                w.Close();
                Pump(300);
                Assert.False(closed);
                Assert.False(w.IsVisible);
                Assert.False(tray.IsDisposed);
                Assert.True(tray.Icon.Visible);

                // 2回目の×も同じ（吹き出しは1回だけ。落ちない）
                tray.ShowWindow();
                Assert.True(w.IsVisible);
                w.Close();
                Pump(300);
                Assert.False(closed);
                Assert.False(w.IsVisible);

                // トレイの「ログの表示」
                tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().First().PerformClick();
                Pump(200);
                Assert.True(w.IsVisible);
                Assert.False(closed);
            }
            finally
            {
                w.QuitApp();
                Pump(300);
            }
        });
    }

    [Fact]
    public void J51_ONでもトレイの終了は本当に閉じてトレイも外れる()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            cfg.Settings.CloseToTray = true;
            var tray = w.Tray!;
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            w.Close();   // いったんトレイへ
            Pump(200);
            Assert.False(closed);
            tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Last().PerformClick();
            Pump(300);
            Assert.True(closed);
            Assert.True(tray.IsDisposed);
        });
    }

    [Fact]
    public void J55_しまった時の吹き出しは最初の1回だけで_設定へ残り_2回目以降と起動し直しでは出ない()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            Assert.False(cfg.Settings.TrayHintShown);
            Assert.False(ConfigStore.DefaultConfig()["tray_hint_shown"]!.GetValue<bool>());
            cfg.Settings.CloseToTray = true;
            var tray = w.Tray!;
            try
            {
                w.Close();
                Pump(300);
                Assert.Equal(1, tray.HintCount);
                Assert.True(cfg.Settings.TrayHintShown);
                var again = new ConfigStore(_paths);
                again.Load();
                Assert.True(again.Settings.TrayHintShown);   // 保存されている（起動し直しても出ない）

                tray.ShowWindow();
                w.Close();
                Pump(300);
                Assert.Equal(1, tray.HintCount);   // 2回目は出ない
            }
            finally { w.QuitApp(); Pump(300); }
        });
    }

    [Fact]
    public void J55_すでに出し終えていれば_しまっても吹き出しは出ない()
    {
        OnSta(() =>
        {
            var (w, cfg) = Open();
            cfg.Settings.CloseToTray = true;
            cfg.Settings.TrayHintShown = true;
            var tray = w.Tray!;
            try
            {
                w.Close();
                Pump(300);
                Assert.False(w.IsVisible);
                Assert.Equal(0, tray.HintCount);
            }
            finally { w.QuitApp(); Pump(300); }
        });
    }

    [Fact]
    public void J55_トレイ起動のあいだは出さず_設定も変えない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.CloseToTray = true;
            var w = new MainWindow(_paths, cfg);
            var tray = w.Tray!;
            try
            {
                w.StartHidden();
                w.ShowAndActivate();
                Pump(200);
                w.Close();
                Pump(300);
                Assert.False(w.IsVisible);
                Assert.Equal(0, tray.HintCount);
                Assert.False(cfg.Settings.TrayHintShown);
            }
            finally { w.QuitApp(); Pump(300); }
        });
    }
}

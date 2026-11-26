using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 設定ページでクイックぶん投げの設定を変えたとき、フックへその場で
/// 反映されること。OFFのあいだは巡回タイマー自体が止まっているので、
/// 「1秒ごとに設定を読み直す」だけではONにしても効かない（再起動まで効かなかった不具合）。
/// 待機は1秒より短くして、読み直しでは無くないことを確かめる。
/// </summary>
public class QuickInstantApplyTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public QuickInstantApplyTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_qinstant_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers（BindTests と同じ形） ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

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
        th.Join(TimeSpan.FromSeconds(60));
        if (err is not null) throw err;
        return result;
    }

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t && t.Name == name) return t;
            var deeper = Find<T>(ch, name);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static void PressEnter(UIElement box)
    {
        var src = PresentationSource.FromVisual(box);
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent };
        box.RaiseEvent(e);
    }

    private MainWindow NewWindow(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(w.Dispatcher, 250);
        w.ShowSettingsPage();
        Pump(w.Dispatcher, 150);
        return w;
    }

    private static QuickInputWatcher Watch(MainWindow w) => w.Quick.Input;

    /// <summary>フックが実際に握るボタンの集合（Applyで反映されたかを見る）</summary>
    private static IReadOnlySet<int> Grabbed(MainWindow w)
        => (IReadOnlySet<int>)Fld(Watch(w), "_mask")!;

    private static bool PollAlive(MainWindow w)
    {
        var t = w.Quick.PollTimer;
        return t is not null && t.IsEnabled;
    }

    // ---- tests ----

    [Fact]
    public void TurningQuickOnFromSettingsGrabsWheelRightAway()
    {
        OnSta(() =>
        {
            // OFFで起動（この状態だと巡回タイマーが止まっている）
            var w = NewWindow(out var cfg);
            cfg.SetBool("quick_enabled", false);
            cfg.SetStr("quick_button", "wheel");
            cfg.SetStr("quick_key", "");
            cfg.SetStr("hotkey", "");
            w.Quick.Apply(false);
            Assert.False(Watch(w).Enabled());
            Assert.False(PollAlive(w));
            Assert.Empty(Grabbed(w));

            // 設定ページのチェック欄をONにする（1秒も待たずに反映されなければいけない）
            // 既定はONなので、まず欄をOFFへ落としてからONへ戻す経路で見る
            var chk = Find<CheckBox>(w, "QuickOnChk")!;
            chk.IsChecked = false;
            Pump(w.Dispatcher, 60);
            Assert.False(cfg.GetBool("quick_enabled", true));
            Assert.False(Watch(w).Enabled());
            chk.IsChecked = true;
            Pump(w.Dispatcher, 120);          // 1秒の読み直しより短い待ち時間

            Assert.True(cfg.GetBool("quick_enabled", true));
            Assert.True(Watch(w).Enabled());              // フックを張り直した
            Assert.Contains(Binding.MButton, Grabbed(w)); // ホイールを握る割当になった
            Assert.True(PollAlive(w));                    // 巡回も生き返った
            Assert.Contains("クイックぶん投げON", Find<TextBlock>(w, "StatusText")!.Text);

            // OFFに戻せばフックを剥がし、巡回も止まる
            chk.IsChecked = false;
            Pump(w.Dispatcher, 120);
            Assert.False(cfg.GetBool("quick_enabled", true));
            Assert.False(Watch(w).Enabled());
            Assert.Empty(Grabbed(w));
            Assert.False(PollAlive(w));
            Assert.Contains("クイックぶん投げOFF", Find<TextBlock>(w, "StatusText")!.Text);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void QuickButtonComboChangeSwapsGrabbedButtonRightAway()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetBool("quick_enabled", true);
            cfg.SetStr("quick_button", "off");   // ONでもボタンなし → 巡回も止まる形
            cfg.SetStr("quick_key", "");
            cfg.SetStr("hotkey", "");
            w.Quick.Apply(false);
            Assert.False(Watch(w).Enabled());
            Assert.False(PollAlive(w));

            var cb = Find<ComboBox>(w, "QuickBtnCombo")!;
            cb.SelectedIndex = 2;               // マウス「進む」ボタン
            Pump(w.Dispatcher, 120);

            Assert.Equal("forward", cfg.GetStr("quick_button"));
            Assert.True(Watch(w).Enabled());
            Assert.Contains(Binding.XButton2, Grabbed(w));
            Assert.DoesNotContain(Binding.MButton, Grabbed(w));
            Assert.True(PollAlive(w));

            w.Close();
            return true;
        });
    }

    [Fact]
    public void QuickMinSizeEnterUpdatesTrackedMinimumRightAway()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetBool("quick_enabled", true);
            cfg.SetStr("quick_button", "wheel");
            w.Quick.Apply(false);
            Assert.Equal((30, 30), w.Quick.Geo);

            var wb = Find<TextBox>(w, "QuickWBox")!;
            wb.Text = "120";
            PressEnter(wb);
            var hb = Find<TextBox>(w, "QuickHBox")!;
            hb.Text = "80";
            PressEnter(hb);
            Pump(w.Dispatcher, 120);           // 1秒の読み直しに頼んでいないことを示す

            Assert.Equal(120, cfg.GetInt("quick_min_w", 30));
            Assert.Equal(80, cfg.GetInt("quick_min_h", 30));
            Assert.Equal((120, 80), w.Quick.Geo);

            w.Close();
            return true;
        });
    }

    private static void Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(o, args);
}

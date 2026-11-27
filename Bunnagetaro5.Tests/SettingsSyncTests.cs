using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bunnagetaro5.Input;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Status;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// AppSettings.PropertyChanged の購読による設定ページ↔バー・入力の同期と、
/// 相互書き込みによる再入ループが起きないことの検証。
/// </summary>
public class SettingsSyncTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public SettingsSyncTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var ch in LogicalTreeHelper.GetChildren(root))
        {
            if (ch is T t && t.Name == name) return t;
            if (ch is DependencyObject d)
            {
                var deeper = FindLogical<T>(d, name);
                if (deeper is not null) return deeper;
            }
        }
        return null;
    }

    private static void PressEnter(UIElement box)
    {
        var src = PresentationSource.FromVisual(box);
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent };
        box.RaiseEvent(e);
    }

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try
            {
                EnsureAppResources();
                result = body();
            }
            catch (Exception ex) { err = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (err is not null) throw new AggregateException(err);
        return result;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private CaptureSession MakeSession(ConfigStore cfg, Window owner)
    {
        var status = new StatusModel();
        var session = new CaptureSession(cfg, owner, status, _ => { }, () => "");
        var store = new LogStore(_paths);
        var logPage = new LogPage(store, _ => { }, () => PanelStyle.From(cfg));
        session.Throw = new ThrowController(cfg, store, logPage, status, session, _ => { }, owner.Dispatcher);
        return session;
    }

    private sealed class DummyQuickOps : IQuickOps
    {
        public (CaptureWindow Window, IntPtr Hwnd)? EnsureCaptureForQuick() => null;
        public bool CaptureAlive() => false;
        public void QuickHidePanels(int px, int py) { }
        public void QuickHideWindow(int px, int py) { }
        public bool QuickHidden => false;
        public void ForgetQuickHidden() { }
        public bool DiscardQuickOpened() => false;
        public void QuickShowWindow(bool fade = false) { }
        public void QuickPlace(int l, int t, int w, int h, bool fade = false) { }
        public void PositionChrome() { }
        public void AfterQuickShown(Action a) => a();
        public bool ThrowBusy => false;
        public void ThrowManual() { }
    }

    [Fact]
    public void 設定ページのMode変更はPropertyChanged経由でCaptureSessionへ伝わり再入ループしない()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var owner = new Window { Left = -10000, Width = 200, Height = 200, ShowInTaskbar = false };
            var session = new CaptureSession(cfg, owner, new StatusModel(), _ => { }, () => "");
            var page = new SettingsPage(cfg, () => { }, _ => { });
            session.SettingsView = page;

            int propChangedCount = 0;
            cfg.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.Mode)) propChangedCount++;
            };

            var modeChk = FindLogical<CheckBox>(page, "ModeChk")!;
            Assert.False(session.AutoMode);

            modeChk.IsChecked = true;
            Pump(20);
            Assert.True(session.AutoMode);
            Assert.Equal(1, propChangedCount);

            modeChk.IsChecked = false;
            Pump(20);
            Assert.False(session.AutoMode);
            Assert.Equal(2, propChangedCount);

            page.Detach();
            session.Dispose();
            owner.Close();
            return true;
        });
    }

    [Fact]
    public void CaptureSessionのバー操作はPropertyChanged経由で設定ページへ伝わり再入ループしない()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var owner = new Window { Left = -10000, Width = 200, Height = 200, ShowInTaskbar = false };
            var session = MakeSession(cfg, owner);
            var page = new SettingsPage(cfg, () => { }, _ => { });
            session.SettingsView = page;

            int modeEvents = 0;
            int presetEvents = 0;
            cfg.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.Mode)) modeEvents++;
                if (e.PropertyName == nameof(AppSettings.PromptActive)) presetEvents++;
            };

            var modeChk = FindLogical<CheckBox>(page, "ModeChk")!;
            var presetCb = FindLogical<ComboBox>(page, "PresetCombo")!;

            // バーから自動トリガを ON
            session.BarAutoTrig(true);
            Pump(20);
            Assert.True(modeChk.IsChecked);
            Assert.Equal(1, modeEvents);

            // バーからプリセット切り替え
            string? targetPreset = null;
            foreach (var item in presetCb.Items)
            {
                string s = item?.ToString() ?? "";
                if (s != cfg.Settings.PromptActive)
                {
                    targetPreset = s;
                    break;
                }
            }
            Assert.NotNull(targetPreset);
            Assert.True(session.BarPresetSelect(targetPreset!));
            Pump(20);
            Assert.Equal(targetPreset, presetCb.SelectedItem as string);
            Assert.Equal(1, presetEvents);

            page.Detach();
            session.Dispose();
            owner.Close();
            return true;
        });
    }

    [Fact]
    public void 設定ページに無効入力がある時のBarPresetSelectは切り替えず書きかけを消さない()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var owner = new Window { Left = -10000, Width = 200, Height = 200, ShowInTaskbar = false };
            var session = MakeSession(cfg, owner);
            var page = new SettingsPage(cfg, () => { }, _ => { });
            session.SettingsView = page;

            var win = new Window { Content = page, Width = 800, Height = 900, Left = -10000, ShowInTaskbar = false };
            win.Show();
            Pump(50);

            var presetCb = FindLogical<ComboBox>(page, "PresetCombo")!;
            presetCb.Text = "";   // 空（未コミット・拒否されるべき入力）
            string before = cfg.Settings.PromptActive;

            Assert.False(session.BarPresetSelect("OCR"));
            Pump(20);

            Assert.Equal(before, cfg.Settings.PromptActive);
            Assert.Equal("", presetCb.Text);   // 拒否時は RefreshFromBar で書きかけを潰さない

            page.Detach();
            session.Dispose();
            win.Close();
            owner.Close();
            return true;
        });
    }

    [Fact]
    public void NumCtlとQuickThrowControllerがPropertyChanged経由で同期する()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var page = new SettingsPage(cfg, () => { }, _ => { });
            var win = new Window { Content = page, Width = 800, Height = 900, Left = -10000, ShowInTaskbar = false };
            win.Show();
            Pump(50);

            var quick = new QuickThrowController(cfg, new DummyQuickOps(), _ => { });
            quick.Binds = new BindingRegistrar(cfg, quick.Input, _ => { }, quick);

            var props = new List<string?>();
            cfg.Settings.PropertyChanged += (_, e) => props.Add(e.PropertyName);

            var pxBox = FindLogical<TextBox>(page, "BorderPxBox")!;
            pxBox.Text = "8";
            PressEnter(pxBox);

            var qwBox = FindLogical<TextBox>(page, "QuickWBox")!;
            qwBox.Text = "120";
            PressEnter(qwBox);

            Pump(20);

            Assert.Contains(nameof(AppSettings.CaptureBorderPx), props);
            Assert.Contains(nameof(AppSettings.QuickMinW), props);
            Assert.Equal((120, 30), quick.Geo);

            quick.Stop();
            page.Detach();
            win.Close();
            return true;
        });
    }
}

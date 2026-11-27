using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Microsoft.Win32;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// Windows の起動時にトレイで起動する。レジストリは HKCU\Software\Bunnagetaro5Tests\... の使い捨てキーだけを使い、
/// 本物の Run は触らない（終わったら消す）
/// </summary>
public class StartupTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private readonly string _scratch = @"Software\Bunnagetaro5Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _oldKey = StartupRegistration.KeyPath;

    public StartupTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_start_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
        StartupRegistration.KeyPath = _scratch + @"\Run";
    }

    public void Dispose()
    {
        StartupRegistration.KeyPath = _oldKey;
        try { Registry.CurrentUser.DeleteSubKeyTree(_scratch, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Bunnagetaro5Tests", false); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private ConfigStore NewCfg()
    {
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetBool("quick_enabled", false);
        cfg.SetStr("base_url", "http://127.0.0.1:1");   // 届かない先
        return cfg;
    }

    [Fact]
    public void Runの登録は_値名Bunnagetaro_データは引用符つきexeと_dash_dash_tray_で_消せる()
    {
        Assert.False(StartupRegistration.IsRegistered());
        StartupRegistration.Register(@"C:\Apps\Bunnagetaro\Bunnagetaro.exe");
        Assert.True(StartupRegistration.IsRegistered());
        using (var k = Registry.CurrentUser.OpenSubKey(_scratch + @"\Run"))
            Assert.Equal("\"C:\\Apps\\Bunnagetaro\\Bunnagetaro.exe\" --tray", k!.GetValue("Bunnagetaro"));
        Assert.True(StartupRegistration.IsRegisteredFor(@"C:\Apps\Bunnagetaro\Bunnagetaro.exe"));
        Assert.False(StartupRegistration.IsRegisteredFor(@"D:\Other\Bunnagetaro.exe"));
        StartupRegistration.Unregister();
        Assert.False(StartupRegistration.IsRegistered());
        StartupRegistration.Unregister();   // 無くても落ちない
    }

    [Fact]
    public void 起動時の追従は_ONでパスが違えば入れ直し_同じなら何もせず_OFFなら触らない()
    {
        StartupRegistration.SyncOnStartup(false, @"C:\A\b.exe");
        Assert.False(StartupRegistration.IsRegistered());   // OFF は触らない

        StartupRegistration.Register(@"C:\Old\b.exe");
        StartupRegistration.SyncOnStartup(false, @"C:\New\b.exe");
        Assert.True(StartupRegistration.IsRegisteredFor(@"C:\Old\b.exe"));   // OFF なら古いままでも触らない

        StartupRegistration.SyncOnStartup(true, @"C:\New\b.exe");
        Assert.True(StartupRegistration.IsRegisteredFor(@"C:\New\b.exe"));   // 入れ直した
        StartupRegistration.SyncOnStartup(true, @"C:\New\b.exe");
        Assert.True(StartupRegistration.IsRegisteredFor(@"C:\New\b.exe"));
    }

    [Fact]
    public void 設定の既定はOFFで_型付きプロパティが読み書きできる()
    {
        Assert.False(ConfigStore.DefaultConfig()["start_with_windows"]!.GetValue<bool>());
        var cfg = NewCfg();
        Assert.False(cfg.Settings.StartWithWindows);
        cfg.Settings.StartWithWindows = true;
        Assert.True(cfg.Settings.StartWithWindows);
    }

    [Fact]
    public void 起動引数のdash_dash_trayを見分ける()
    {
        Assert.True(App.IsTrayStart(new[] { "--tray" }));
        Assert.True(App.IsTrayStart(new[] { "x", "--TRAY" }));
        Assert.False(App.IsTrayStart(Array.Empty<string>()));
        Assert.False(App.IsTrayStart(null));
    }

    private static Border? CardOf(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); p is not null;
             p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p))
            if (p is Border b && b.Parent is StackPanel sp && sp.Children.IndexOf(b) > 0 && sp.Children[sp.Children.IndexOf(b) - 1] is TextBlock) return b;
        return null;
    }

    [Fact]
    public void 起動と終了のカードに閉じた時と起動時の2つが並び_ONでRunへ入りOFFで消える()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = NewCfg();
            var w = new MainWindow(_paths, cfg);
            w.Show();
            Pump(200);
            try
            {
                var page = w.SettingsPageForTray!;
                var close = page.CloseToTrayCheck;
                var start = page.StartWithWindowsCheck;
                // 2つとも同じカード（見出しは「起動と終了」・いちばん下）。「答えの表示」のカードには無い
                var card = CardOf(start);
                Assert.NotNull(card);
                Assert.Same(card, CardOf(close));
                var body = (Panel)card!.Parent;
                int i = body.Children.IndexOf(card);
                Assert.Equal("起動と終了", ((TextBlock)body.Children[i - 1]).Text);
                Assert.Equal(body.Children.Count - 1, i);

                var startRow = LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(start)!) as Bunnagetaro5.Pages.SettingRow;
                var closeRow = LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(close)!) as Bunnagetaro5.Pages.SettingRow;
                Assert.Equal("Windows の起動時にトレイで起動する", startRow?.NameBox.Text);
                Assert.Equal("×で閉じた時はタスクトレイにしまう", closeRow?.NameBox.Text);
                start.IsChecked = true;
                Assert.True(cfg.Settings.StartWithWindows);
                Assert.True(StartupRegistration.IsRegisteredFor(Environment.ProcessPath!));
                start.IsChecked = false;
                Assert.False(cfg.Settings.StartWithWindows);
                Assert.False(StartupRegistration.IsRegistered());

                close.IsChecked = true;
                Assert.True(cfg.Settings.CloseToTray);
            }
            finally { w.Close(); Pump(200); }
        });
    }

    [Fact]
    public void トレイ起動は窓を出さずトレイと巡回が動き_ShowAndActivateや二重起動の知らせで窓が出る()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = NewCfg();
            cfg.Settings.MainGeometry = WindowGeometry.Format(120, 140, 1100, 800);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.StartHidden();
                Pump(1000);   // 500ms 後に状態巡回が始まる
                Assert.False(w.IsVisible);
                Assert.True(w.StartedHidden);
                Assert.NotNull(w.Tray);
                Assert.True(w.Tray!.Icon.Visible);
                Assert.True(w.StatusTimerEnabled);   // Loaded が来なくても動き出している

                // 二重起動の知らせ（隠れたままでも窓の題名で見つかり、知らせで出る）
                Assert.True(Native.FindWindow(null, MainWindow.WindowTitle) != IntPtr.Zero);
                var h = new WindowInteropHelper(w).Handle;
                int msg = Native.RegisterWindowMessage(SingleInstanceGuard.ActivateMessageName);
                Native.PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
                Pump(300);
                Assert.True(w.IsVisible);
                // 初めて出した時、前回の位置・大きさが効いている
                Native.GetWindowRect(h, out var r);
                Assert.Equal("1100x800+120+140", $"{r.Right - r.Left}x{r.Bottom - r.Top}+{r.Left}+{r.Top}");

                w.Hide();
                Assert.False(w.IsVisible);
                w.ShowAndActivate();
                Assert.True(w.IsVisible);
            }
            finally { w.QuitApp(); Pump(300); }
        });
    }
}

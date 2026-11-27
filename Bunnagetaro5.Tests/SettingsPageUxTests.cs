using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

public sealed class SettingsPageUxTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public SettingsPageUxTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_i12_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void OnSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                body();
            }
            catch (Exception ex) { err = ex; }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw new TargetInvocationException(err);
    }

    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;
        foreach (var ch in LogicalTreeHelper.GetChildren(root))
        {
            if (ch is DependencyObject d)
            {
                var hit = FindLogical<T>(d, name);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    private static bool IsEffectivelyVisible(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = LogicalTreeHelper.GetParent(cur))
        {
            if (cur is UIElement u && u.Visibility != Visibility.Visible) return false;
        }
        return true;
    }

    [Fact]
    public void ExpertMode_DefaultsFalse_TogglesExpertVisibility_AndPersists()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            Assert.False(cfg.Settings.ExpertMode);

            var page = new SettingsPage(cfg, () => { }, _ => { });
            try
            {
                var expChk = FindLogical<CheckBox>(page, "ExpertModeChk");
                Assert.NotNull(expChk);
                Assert.False(expChk!.IsChecked);

                // 左メニューの並び: 上段は AI接続 → プリセット → 表示・枠 → 操作・ホットキー → 記録・起動、下段に 使い方
                Assert.Equal(SettingsPage.CatAi, page.CurrentCategory);
                var topNav = FindLogical<StackPanel>(page, "NavTopPanel")!;
                Assert.Equal(
                    new[] { "Nav_ai", "Nav_presets", "Nav_display", "Nav_input", "Nav_system" },
                    topNav.Children.OfType<Border>().Select(b => b.Name).ToArray());
                var bottomNav = FindLogical<StackPanel>(page, "NavBottomPanel")!;
                Assert.Contains("Nav_help", bottomNav.Children.OfType<Border>().Select(b => b.Name));

                var normalByCat = new (string Cat, string[] Names)[]
                {
                    (SettingsPage.CatAi, new[] { "UrlBox", "ConnTestButton", "FetchModelsButton" }),
                    (SettingsPage.CatPresets, new[] { "PresetCombo", "ModelCombo" }),
                    (SettingsPage.CatDisplay, new[] { "BgBox", "FgBox", "FontCombo" }),
                    (SettingsPage.CatInput, new[] { "ModeChk", "QuickOnChk", "QuickBtnCombo", "HotkeyBox" }),
                    (SettingsPage.CatSystem, new[] { "SaveShotsChk", "CloseToTrayChk", "StartWithWindowsChk" }),
                };
                var expertByCat = new (string Cat, string[] Names)[]
                {
                    (SettingsPage.CatPresets, new[] { "ConvertOtslChk", "ConvertHtmlChk", "ConvertLatexChk", "ShowFollowChk", "CtxBox", "ClipChk" }),
                    (SettingsPage.CatDisplay, new[] { "BorderColorBox", "AlphaSlider", "FontSizeBox", "ParaSpacingSlider" }),
                    (SettingsPage.CatInput, new[] { "QuickKeyBox" }),
                };

                // 選んだカテゴリだけが見え、通常モードでは通常項目が見えて上級者項目は畳まれている
                foreach (var (cat, names) in normalByCat)
                {
                    page.SelectCategory(cat);
                    foreach (var name in names)
                    {
                        var el = FindLogical<FrameworkElement>(page, name);
                        Assert.NotNull(el);
                        Assert.True(IsEffectivelyVisible(el!), $"{name} should be visible in normal mode ({cat})");
                    }
                    // 別カテゴリの項目は非表示になっている
                    string other = cat == SettingsPage.CatAi ? "PresetCombo" : "UrlBox";
                    Assert.False(IsEffectivelyVisible(FindLogical<FrameworkElement>(page, other)!));
                }

                foreach (var (cat, names) in expertByCat)
                {
                    page.SelectCategory(cat);
                    foreach (var name in names)
                    {
                        var el = FindLogical<FrameworkElement>(page, name);
                        Assert.NotNull(el);
                        Assert.False(IsEffectivelyVisible(el!), $"{name} should be collapsed in normal mode ({cat})");
                    }
                }

                // Turn ExpertMode ON
                expChk.IsChecked = true;
                Assert.True(cfg.Settings.ExpertMode);
                cfg.Save();

                foreach (var (cat, names) in expertByCat)
                {
                    page.SelectCategory(cat);
                    foreach (var name in names)
                    {
                        var el = FindLogical<FrameworkElement>(page, name);
                        Assert.NotNull(el);
                        Assert.True(IsEffectivelyVisible(el!), $"{name} should be visible in expert mode ({cat})");
                    }
                }
            }
            finally
            {
                page.Detach();
            }

            var cfg2 = new ConfigStore(_paths);
            cfg2.Load();
            Assert.True(cfg2.Settings.ExpertMode);
        });
    }

    [Fact]
    public void NarrowWindow_WrapsWideSettingRowControlsWithoutClipping()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var page = new SettingsPage(cfg, () => { }, _ => { });
            // MainWindow の最小幅（640px）に置いても「使うプリセット」が切れない
            var win = new Window { Content = page, Width = 612, Height = 500, Left = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                win.Show();
                page.SelectCategory(SettingsPage.CatPresets);
                win.UpdateLayout();
                var row = FindLogical<SettingRow>(page, "PresetRow")!;
                var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
                var ctlPanel = (FrameworkElement)LogicalTreeHelper.GetParent(cb)!;
                Assert.Equal(1, Grid.GetRow(ctlPanel));
                Assert.True(ctlPanel.ActualWidth <= row.ActualWidth + 0.5, $"ctl={ctlPanel.ActualWidth} row={row.ActualWidth}");
            }
            finally
            {
                page.Detach();
                win.Close();
            }
        });
    }

    [Fact]
    public void ConversionCheckboxes_UpdateAppSettings()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.ExpertMode = true;
            var page = new SettingsPage(cfg, () => { }, _ => { });
            try
            {
                // グローバル設定は無くした。変換の欄は選んでいるプリセットの設定へ書く
                var otsl = FindLogical<CheckBox>(page, "ConvertOtslChk")!;
                var html = FindLogical<CheckBox>(page, "ConvertHtmlChk")!;
                var latex = FindLogical<CheckBox>(page, "ConvertLatexChk")!;
                var follow = FindLogical<CheckBox>(page, "ShowFollowChk")!;

                otsl.IsChecked = false;
                html.IsChecked = false;
                latex.IsChecked = false;
                follow.IsChecked = true;

                Assert.False(cfg.EffectiveBool("convert_otsl_table", true));
                Assert.False(cfg.EffectiveBool("convert_html_table", true));
                Assert.False(cfg.EffectiveBool("convert_latex_math", true));
                Assert.True(cfg.EffectiveBool("show_follow_row", false));

                otsl.IsChecked = true;
                Assert.True(cfg.EffectiveBool("convert_otsl_table", false));
            }
            finally
            {
                page.Detach();
            }
        });
    }

    [Fact]
    public void ColorPalettePopup_ClickSwatch_UpdatesHexAndSetting()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var page = new SettingsPage(cfg, () => { }, _ => { });
            try
            {
                var bgBox = FindLogical<TextBox>(page, "BgBox")!;
                var bgSwatch = FindLogical<Border>(page, "BgSwatch")!;
                bgSwatch.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
                var pop = FindLogical<Popup>(page, "BgSwatchPopup")!;
                pop.IsOpen = false;
                var targetBtn = FindPaletteButton(pop.Child, "#2563EB");
                Assert.NotNull(targetBtn);

                targetBtn!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("#2563EB", bgBox.Text);
                Assert.Equal("#2563EB", cfg.Settings.OverlayBg);
            }
            finally
            {
                page.Detach();
            }
        });
    }

    private static Button? FindPaletteButton(DependencyObject root, string hex)
    {
        if (root is Button b && Equals(b.Tag, hex)) return b;
        foreach (var ch in LogicalTreeHelper.GetChildren(root))
        {
            if (ch is DependencyObject d)
            {
                var hit = FindPaletteButton(d, hex);
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}

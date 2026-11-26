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

                // Normal items are visible when ExpertMode == false
                foreach (var name in new[]
                {
                    "UrlBox", "PresetCombo", "ModelCombo", "BgBox", "FgBox", "FontCombo",
                    "ModeChk", "QuickOnChk", "QuickBtnCombo", "HotkeyBox", "SaveShotsChk",
                })
                {
                    var el = FindLogical<FrameworkElement>(page, name);
                    Assert.NotNull(el);
                    Assert.True(IsEffectivelyVisible(el!), $"{name} should be visible in normal mode");
                }

                // Expert items are collapsed when ExpertMode == false
                foreach (var name in new[]
                {
                    "BorderColorBox", "AlphaSlider", "FontSizeBox", "ParaSpacingSlider",
                    "ConvertOtslChk", "ConvertHtmlChk", "ConvertLatexChk", "ShowFollowChk",
                    "QuickKeyBox", "CtxBox", "ClipChk",
                })
                {
                    var el = FindLogical<FrameworkElement>(page, name);
                    Assert.NotNull(el);
                    Assert.False(IsEffectivelyVisible(el!), $"{name} should be collapsed in normal mode");
                }

                // Turn ExpertMode ON
                expChk.IsChecked = true;
                Assert.True(cfg.Settings.ExpertMode);
                cfg.Save();

                foreach (var name in new[]
                {
                    "BorderColorBox", "AlphaSlider", "FontSizeBox", "ConvertOtslChk",
                    "ConvertHtmlChk", "ConvertLatexChk", "ShowFollowChk", "QuickKeyBox", "CtxBox", "ClipChk",
                })
                {
                    var el = FindLogical<FrameworkElement>(page, name);
                    Assert.NotNull(el);
                    Assert.True(IsEffectivelyVisible(el!), $"{name} should be visible in expert mode");
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
                // Select [グローバル設定] (index 0) to edit global conversion settings
                var presetCb = FindLogical<ComboBox>(page, "PresetCombo")!;
                presetCb.SelectedIndex = 0;

                var otsl = FindLogical<CheckBox>(page, "ConvertOtslChk")!;
                var html = FindLogical<CheckBox>(page, "ConvertHtmlChk")!;
                var latex = FindLogical<CheckBox>(page, "ConvertLatexChk")!;
                var follow = FindLogical<CheckBox>(page, "ShowFollowChk")!;

                otsl.IsChecked = false;
                html.IsChecked = false;
                latex.IsChecked = false;
                follow.IsChecked = false;

                Assert.False(cfg.Settings.ConvertOtslTable);
                Assert.False(cfg.Settings.ConvertHtmlTable);
                Assert.False(cfg.Settings.ConvertLatexMath);
                Assert.False(cfg.Settings.ShowFollowRow);

                // Switch back to first preset ("翻訳"), enable UseGlobal, then disable and override per-preset
                presetCb.SelectedItem = "翻訳";
                var useGlobal = FindLogical<CheckBox>(page, "UseGlobalChk")!;
                useGlobal.IsChecked = true;
                Assert.False(cfg.EffectiveBool("convert_otsl_table", true)); // still global while UseGlobal=true

                useGlobal.IsChecked = false;
                Assert.False(otsl.IsChecked);
                otsl.IsChecked = true;
                Assert.True(cfg.EffectiveBool("convert_otsl_table", false));
                Assert.False(cfg.Settings.ConvertOtslTable); // global remains false
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

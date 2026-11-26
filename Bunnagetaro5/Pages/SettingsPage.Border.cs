using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページのキャプチャ窓の枠（SettingsPage の partial class）。
/// 色・不透明度・太さ。変えるとその場でキャプチャ窓の縁（と撮る範囲の削り幅・掴める幅・クイックのダミー枠）へ効く。</summary>
public partial class SettingsPage
{
    private void BuildCaptureBorderSection()
    {
        var sec = new StackPanel();
        sec.Children.Add(Section("キャプチャ窓の枠"));

        _borderColorRow = new SettingRow("枠の色", "", MakeColorRow("capture_border_color", out _borderColorSwatch, out _borderColorBox));

        _borderAlphaLbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _borderAlphaSlider = new Slider
        {
            Minimum = 0.15, Maximum = 1.0,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.01,
            SmallChange = 0.01, LargeChange = 0.05, IsMoveToPointEnabled = true,
            Name = "BorderAlphaSlider",
        };
        _borderAlphaSlider.Value = _cfg.Settings.CaptureBorderAlpha;
        _borderAlphaLbl.Text = _borderAlphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
        _borderAlphaSlider.ValueChanged += (_, _) =>
        {
            _borderAlphaLbl.Text = _borderAlphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
            _cfg.Settings.CaptureBorderAlpha = _borderAlphaSlider.Value;
            SaveSoon();
        };
        var alphaCtl = new StackPanel { Orientation = Orientation.Horizontal };
        alphaCtl.Children.Add(_borderAlphaSlider);
        alphaCtl.Children.Add(_borderAlphaLbl);
        var alphaRow = new SettingRow("枠の不透明度", "", alphaCtl);

        _borderPxNum = AddNum(null, "枠の太さ", "capture_border_px", 1, 16, true, CaptureBorder.DefaultPx,
                              () => _cfg.Root, null, "BorderPxBox");
        _borderPxNum.Row.SetDesc("1〜16（物理px）");
        _borderPxNum.RangeText = "1〜16（物理px）";

        var reset = new Button { Content = "既定に戻す", Name = "BorderReset" };
        reset.Click += (_, _) => ResetCaptureBorder();
        var resetRow = new SettingRow("枠を既定に戻す", "既定: #0067C0・0.70・4px", reset);

        sec.Children.Add(MakeGroupCard(_borderColorRow, alphaRow, _borderPxNum.Row, resetRow));
        BodyPanel.Children.Add(MakeExpertWrap(sec));
    }

    /// <summary>3つだけ既定へ戻す（欄も書き換えて即反映）</summary>
    private void ResetCaptureBorder()
    {
        _cfg.Settings.CaptureBorderColor = CaptureBorder.DefaultColor;
        _cfg.Settings.CaptureBorderAlpha = CaptureBorder.DefaultAlpha;
        _cfg.Settings.CaptureBorderPx = CaptureBorder.DefaultPx;
        _borderColorBox.Text = CaptureBorder.DefaultColor;
        _borderColorBox.BorderBrush = SettingRow.NormalBorderBrush();
        _borderColorSwatch.Background = ColorUtil.ToBrush(CaptureBorder.DefaultColor);
        _borderColorRow.SetDesc("");
        _borderAlphaSlider.Value = CaptureBorder.DefaultAlpha;
        _borderPxNum.Box.Text = CaptureBorder.DefaultPx.ToString(CultureInfo.InvariantCulture);
        _borderPxNum.Box.BorderBrush = SettingRow.NormalBorderBrush();
        _borderPxNum.Invalid = false;
        SaveSoon();
    }
}

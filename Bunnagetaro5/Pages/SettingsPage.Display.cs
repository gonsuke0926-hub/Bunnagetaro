using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページの答えの表示（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ---------------- 答えの表示 ----------------

    private void BuildAnswerSection()
    {
        BodyPanel.Children.Add(Section("答えの表示"));

        // 背景色・文字色（四角を押すと色パレット、欄へ #rrggbb も直接書ける）
        _bgRow = new SettingRow("背景色", "", MakeColorRow("overlay_bg", out _bgSwatch, out _bgBox));
        _fgRow = new SettingRow("文字色", "", MakeColorRow("overlay_fg", out _fgSwatch, out _fgBox));
        // 修正J101: 外窓（枠の外に出す解答欄）の色は枠の中と別
        _stripBgRow = new SettingRow("背景色", "", MakeColorRow("strip_bg", out _stripBgSwatch, out _stripBgBox));
        _stripFgRow = new SettingRow("文字色", "", MakeColorRow("strip_fg", out _stripFgSwatch, out _stripFgBox));
        _stripAlphaLbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _stripAlphaSlider = new Slider
        {
            Minimum = 0.15, Maximum = 1.0,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.01,
            SmallChange = 0.01, LargeChange = 0.05, IsMoveToPointEnabled = true,
            Name = "StripAlphaSlider",
        };
        _stripAlphaSlider.Value = Math.Clamp(_cfg.Settings.StripAlpha, 0.15, 1.0);
        _stripAlphaLbl.Text = _stripAlphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
        _stripAlphaSlider.ValueChanged += (_, _) =>
        {
            _stripAlphaLbl.Text = _stripAlphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
            _cfg.Settings.StripAlpha = _stripAlphaSlider.Value;
            SaveSoon();
            ApplyPreview();
        };
        var stripAlphaCtl = new StackPanel { Orientation = Orientation.Horizontal };
        stripAlphaCtl.Children.Add(_stripAlphaSlider);
        stripAlphaCtl.Children.Add(_stripAlphaLbl);
        var stripAlphaRow = new SettingRow("背景の不透明度", "既定 1.00（透けない）", stripAlphaCtl);

        // 背景の不透明度
        _alphaLbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _alphaSlider = new Slider
        {
            Minimum = 0.15, Maximum = 1.0,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.01,
            SmallChange = 0.01, LargeChange = 0.05, IsMoveToPointEnabled = true,
            Name = "AlphaSlider",
        };
        double a = _cfg.Settings.OverlayAlpha;
        _alphaSlider.Value = Math.Clamp(a, 0.15, 1.0);
        _alphaLbl.Text = _alphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
        _alphaSlider.ValueChanged += Alpha_Changed;
        var alphaCtl = new StackPanel { Orientation = Orientation.Horizontal };
        alphaCtl.Children.Add(_alphaSlider);
        alphaCtl.Children.Add(_alphaLbl);
        var alphaRow = new SettingRow("背景の不透明度", "文字にもかかります", alphaCtl);

        // フォント
        _fontCb = new ComboBox { IsEditable = true, Width = 220, Name = "FontCombo" };
        var fams = FontFamilies();
        _fontCb.Items.Add("既定");
        foreach (var f in fams) _fontCb.Items.Add(f);
        var famNow = _cfg.Settings.OverlayFontFamily;
        _fontCb.Text = famNow.Length == 0 ? "既定" : famNow;
        _fontCb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitFont(); e.Handled = true; }
        };
        BindCommitOnFocusLoss(_fontCb, CommitFont);
        _fontRow = new SettingRow("フォント", "空・既定ならシステム既定", _fontCb);

        // 文字の大きさ
        _sizeBox = new TextBox
        {
            Text = _cfg.Settings.OverlayFontSize.ToString(CultureInfo.InvariantCulture),
            Width = 64, HorizontalAlignment = HorizontalAlignment.Right,
            Name = "FontSizeBox",
        };
        _sizeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitSize(); e.Handled = true; }
        };
        BindCommitOnFocusLoss(_sizeBox, CommitSize);
        _sizeRow = new SettingRow("文字の大きさ", "8〜72", _sizeBox);

        // プレビュー（通常設定でも色・フォントの確認ができるよう _fontRow の下段に置く）
        // 修正J101: 枠の中・枠の外（外窓）の見本を並べる
        (_previewBg, _previewText) = MakePreviewBox();
        (_previewStripBg, _previewStripText) = MakePreviewBox();
        _fontRow.SetBottom(MakePreviewHost());

        // まとまりの間（F35。文字の大きさに対する倍率 0〜1.5、0.1刻み、既定 0.8）
        _paraSpacingLbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _paraSpacingSlider = new Slider
        {
            Minimum = PanelStyle.MinParaSpacing, Maximum = PanelStyle.MaxParaSpacing,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.1,
            SmallChange = 0.1, LargeChange = 0.1, IsMoveToPointEnabled = true,
            Name = "ParaSpacingSlider",
        };
        _paraSpacingSlider.Value = _cfg.Settings.AnswerParaSpacing;
        _paraSpacingLbl.Text = _paraSpacingSlider.Value.ToString("F1", CultureInfo.InvariantCulture);
        _paraSpacingSlider.ValueChanged += ParaSpacing_Changed;
        var paraCtl = new StackPanel { Orientation = Orientation.Horizontal };
        paraCtl.Children.Add(_paraSpacingSlider);
        paraCtl.Children.Add(_paraSpacingLbl);
        var paraRow = new SettingRow("まとまりの間", "段落間の空き（文字サイズの倍率）", paraCtl);

        // 行間（F35。文字の大きさに対する倍率 1.0〜2.0、0.1刻み、既定は指定なし相当）
        _lineSpacingLbl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 32 };
        _lineSpacingSlider = new Slider
        {
            Minimum = PanelStyle.MinLineSpacing, Maximum = PanelStyle.MaxLineSpacing,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.1,
            SmallChange = 0.1, LargeChange = 0.1, IsMoveToPointEnabled = true,
            Name = "LineSpacingSlider",
        };
        _lineSpacingResetBtn = new Button
        {
            Content = "標準に戻す",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Name = "LineSpacingResetBtn",
        };
        _lineSpacingResetBtn.Click += LineSpacingReset_Click;
        RefreshLineSpacingUi();
        _lineSpacingSlider.ValueChanged += LineSpacing_Changed;
        var lineCtl = new StackPanel { Orientation = Orientation.Horizontal };
        lineCtl.Children.Add(_lineSpacingSlider);
        lineCtl.Children.Add(_lineSpacingLbl);
        lineCtl.Children.Add(_lineSpacingResetBtn);
        var lineRow = new SettingRow("行間", "行の高さの倍率（1.0〜2.0）", lineCtl);

        // LaTeX のコピーの形（F13）
        var copyPanel = new StackPanel { Orientation = Orientation.Horizontal };
        string copyNow = _cfg.Settings.LatexCopy;
        RadioButton CopyRadio(string text, string key, bool last)
        {
            var rb = new RadioButton
            {
                Content = text, GroupName = "latexcopy", IsChecked = copyNow == key,
                Margin = new Thickness(0, 0, last ? 0 : 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            rb.Checked += (_, _) => CommitLatexCopy(key);
            copyPanel.Children.Add(rb);
            return rb;
        }
        CopyRadio("中身だけ", "plain", false);
        CopyRadio("$ で囲む", "dollar", false);
        CopyRadio("\\( \\) で囲む", "paren", true);
        var copyRow = new SettingRow("LaTeX のコピーの形",
            "式コピー時の囲み方（文章と混ざる時は $ を付けます）", copyPanel);

        var allowCaptureChk = new CheckBox
        {
            Content = "OBS などの配信・録画に枠・バー・解答欄を映す",
            IsChecked = _cfg.Settings.AllowCaptureOverlay,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "AllowCaptureOverlayChk",
        };
        allowCaptureChk.Checked += (_, _) => { _cfg.Settings.AllowCaptureOverlay = true; SaveSoon(); };
        allowCaptureChk.Unchecked += (_, _) => { _cfg.Settings.AllowCaptureOverlay = false; SaveSoon(); };
        var allowCaptureRow = new SettingRow("配信・録画に映す",
            "ON で録画・配信に映ります（既定 OFF。※窓内表示＋自動トリガ併用時は誤検知注意）",
            allowCaptureChk);

        var expStack = new StackPanel();
        expStack.Children.Add(_sizeRow.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(paraRow.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(lineRow.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(copyRow.AsGrouped());

        // 修正J101: 枠の中・枠の外（外窓）を見出しで分ける（人の指示 2026-11-25: 中と外がはっきり分かる見た目に）
        BodyPanel.Children.Add(MakeGroupCard(
            GroupHeading("枠の中に出す時"), _bgRow, _fgRow, MakeExpertWrap(alphaRow.AsGrouped(), withTopDivider: true),
            GroupHeading("枠の外（外窓）に出す時"), _stripBgRow, _stripFgRow, MakeExpertWrap(stripAlphaRow.AsGrouped(), withTopDivider: true),
            GroupHeading("共通"), _fontRow, allowCaptureRow,
            MakeExpertWrap(expStack, withTopDivider: true)));
    }

    /// <summary>修正J101: 欄の中の小見出し（枠の中・枠の外などの区切り）。MakeGroupCard は見出しの直後に区切り線を入れない</summary>
    private Border GroupHeading(string text)
    {
        var tb = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13 };
        return new Border
        {
            Child = tb,
            Padding = new Thickness(14, 10, 14, 2),
            Background = new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)),
            Tag = GroupHeadingTag,
        };
    }

    internal const string GroupHeadingTag = "GroupHeading";

    private void CommitLatexCopy(string value)
    {
        if (_cfg.Settings.LatexCopy == value) return;
        _cfg.Settings.LatexCopy = value;
        SaveSoon();
    }

    private static (Border, TextBlock) MakePreviewBox()
    {
        var bg = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = SettingRow.NormalBorderBrush(),
            Padding = new Thickness(12, 8, 12, 8),
            Height = 88,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var text = new TextBlock
        {
            Text = "これは答えの見本です。ぶん投げた結果はこの色・大きさで表示されます。",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        bg.Child = text;
        return (bg, text);
    }

    private FrameworkElement MakePreviewHost()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Col(int c, string label, Border box)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = SettingRow.SubtleBrush(),
                Margin = new Thickness(0, 0, 0, 4),
            });
            sp.Children.Add(box);
            Grid.SetColumn(sp, c);
            grid.Children.Add(sp);
        }
        Col(0, "見本（枠の中）", _previewBg);
        Col(2, "見本（枠の外・外窓）", _previewStripBg);
        return grid;
    }

    private static List<string> FontFamilies()
    {
        var list = new List<string>();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var s = f.Source;
            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void CommitPlace(string value)
    {
        if (_cfg.Settings.ResultPlace == value) return;
        _cfg.Settings.ResultPlace = value;
        SaveSoon();
    }

    private static readonly string[] PaletteHexes =
    [
        "#1B1B1B", "#202020", "#0F172A", "#1E293B", "#27272A", "#000000",
        "#FFFFFF", "#F5F5F5", "#E2E8F0", "#FEF9C3", "#DCFCE7", "#DBEAFE",
        "#0067C0", "#2563EB", "#0EA5E9", "#10B981", "#22C55E", "#16A34A",
        "#F59E0B", "#EA580C", "#EF4444", "#EC4899", "#8B5CF6", "#64748B",
    ];

    private StackPanel MakeColorRow(string key, out Border swatch, out TextBox box)
    {
        var col = key switch
        {
            "overlay_bg" => _cfg.Settings.OverlayBg,
            "capture_border_color" => _cfg.Settings.CaptureBorderColor,
            "strip_bg" => _cfg.Settings.StripBg,
            "strip_fg" => _cfg.Settings.StripFg,
            _ => _cfg.Settings.OverlayFg,
        };
        swatch = new Border
        {
            Width = 26, Height = 26,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = SettingRow.NormalBorderBrush(),
            Background = ColorUtil.ToBrush(col) ?? Brushes.Transparent,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "クリックして色を選ぶ",
            Name = key switch { "overlay_bg" => "BgSwatch", "capture_border_color" => "BorderColorSwatch", "strip_bg" => "StripBgSwatch", "strip_fg" => "StripFgSwatch", _ => "FgSwatch" },
        };
        box = new TextBox
        {
            Text = col, Width = 84, VerticalAlignment = VerticalAlignment.Center,
            Name = key switch { "overlay_bg" => "BgBox", "capture_border_color" => "BorderColorBox", "strip_bg" => "StripBgBox", "strip_fg" => "StripFgBox", _ => "FgBox" },
        };
        // outパラメータはラムダ内で使えないので、同じ物を指すローカルに写す
        var sw = swatch;
        var bx = box;
        bx.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitColor(key, bx, sw); e.Handled = true; }
        };
        BindCommitOnFocusLoss(bx, () => CommitColor(key, bx, sw));

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(box);

        System.Windows.Controls.Primitives.Popup? popup = null;
        sw.MouseLeftButtonUp += (_, _) =>
        {
            if (popup is null)
            {
                popup = BuildColorPopup(key, sw, bx);
                panel.Children.Add(popup);
            }
            popup.IsOpen = !popup.IsOpen;
        };
        return panel;
    }

    private System.Windows.Controls.Primitives.Popup BuildColorPopup(string key, Border sw, TextBox bx)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = sw,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Name = sw.Name + "Popup",
        };
        var popGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, Margin = new Thickness(0, 4, 0, 6) };
        foreach (string hex in PaletteHexes)
        {
            var btn = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = ColorUtil.ToBrush(hex) ?? Brushes.Transparent,
                BorderBrush = SettingRow.NormalBorderBrush(), BorderThickness = new Thickness(1),
                ToolTip = hex, Tag = hex, Cursor = Cursors.Hand,
            };
            btn.Click += (_, _) =>
            {
                popup.IsOpen = false;
                bx.Text = hex;
                CommitColor(key, bx, sw);
            };
            popGrid.Children.Add(btn);
        }
        var customBtn = new Button
        {
            Content = "カスタム色...",
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        customBtn.Click += (_, _) =>
        {
            popup.IsOpen = false;
            var owner = Window.GetWindow(this);
            IntPtr hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var picked = ColorPicker.Pick(hwnd, bx.Text.Trim());
            if (picked is null) return;
            bx.Text = picked;
            CommitColor(key, bx, sw);
        };
        var popBody = new StackPanel();
        popBody.Children.Add(new TextBlock { Text = "色を選択", FontSize = 12, Foreground = SettingRow.SubtleBrush() });
        popBody.Children.Add(popGrid);
        popBody.Children.Add(customBtn);
        popup.Child = new Border
        {
            Background = Brushes.White,
            BorderBrush = SettingRow.NormalBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = popBody,
        };
        return popup;
    }

    private void CommitColor(string key, TextBox box, Border swatch)
    {
        var (row, label) = key switch
        {
            "overlay_bg" => (_bgRow, "背景色"),
            "capture_border_color" => (_borderColorRow, "枠の色"),
            "strip_bg" => (_stripBgRow, "外窓の背景色"),
            "strip_fg" => (_stripFgRow, "外窓の文字色"),
            _ => (_fgRow, "文字色"),
        };
        var val = box.Text.Trim();
        if (!ColorUtil.IsHexColor(val))
        {
            box.BorderBrush = SettingRow.DangerBrush();
            row.SetDesc("色は #rrggbb 形式で指定してください（不正: " + label + "）");
            return; // 前の値のまま
        }
        box.BorderBrush = SettingRow.NormalBorderBrush();
        row.SetDesc("");
        swatch.Background = ColorUtil.ToBrush(val);
        if (ConfigStore.NodeToStr(_cfg.Settings.ReadNode(key)) != val)
        {
            _cfg.Settings.WriteNode(key, val);
            SaveSoon();
        }
        ApplyPreview();
    }

    private void Alpha_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _alphaLbl.Text = _alphaSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
        _cfg.Settings.OverlayAlpha = _alphaSlider.Value;
        SaveSoon();
        ApplyPreview();
    }

    private void ParaSpacing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double v = Math.Round(_paraSpacingSlider.Value, 1, MidpointRounding.AwayFromZero);
        _paraSpacingLbl.Text = v.ToString("F1", CultureInfo.InvariantCulture);
        _cfg.Settings.AnswerParaSpacing = v;
        SaveSoon();
    }

    private void LineSpacing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLineSpacing) return;
        double v = Math.Round(_lineSpacingSlider.Value, 1, MidpointRounding.AwayFromZero);
        _cfg.Settings.AnswerLineSpacing = v;
        _lineSpacingLbl.Text = v.ToString("F1", CultureInfo.InvariantCulture);
        _lineSpacingResetBtn.IsEnabled = true;
        SaveSoon();
        ApplyPreview();
    }

    private void LineSpacingReset_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Settings.AnswerLineSpacing = PanelStyle.DefaultLineSpacing;
        RefreshLineSpacingUi();
        SaveSoon();
        ApplyPreview();
    }

    private void RefreshLineSpacingUi()
    {
        double ls = _cfg.Settings.AnswerLineSpacing;
        _suppressLineSpacing = true;
        try
        {
            if (ls <= 0)
            {
                _lineSpacingSlider.Value = 1.3;   // 標準時の見た目位置（保存はしない）
                _lineSpacingLbl.Text = "標準";
                _lineSpacingResetBtn.IsEnabled = false;
            }
            else
            {
                _lineSpacingSlider.Value = ls;
                _lineSpacingLbl.Text = ls.ToString("F1", CultureInfo.InvariantCulture);
                _lineSpacingResetBtn.IsEnabled = true;
            }
        }
        finally
        {
            _suppressLineSpacing = false;
        }
    }

    private void CommitFont()
    {
        string t = _fontCb.Text.Trim();
        string fam;
        string note = "";
        if (t.Length == 0 || t == "既定")
        {
            fam = "";
        }
        else
        {
            var fams = FontFamilies();
            var hit = fams.FirstOrDefault(f => string.Equals(f, t, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) fam = hit;
            else
            {
                fam = "";
                note = "フォント '" + t + "' は見つからず。システム既定で保存しました";
                _fontCb.Text = "既定";
            }
        }
        _fontRow.SetDesc(note);
        if (_cfg.Settings.OverlayFontFamily != fam)
        {
            _cfg.Settings.OverlayFontFamily = fam;
            SaveSoon();
        }
        ApplyPreview();
    }

    private void CommitSize()
    {
        string t = _sizeBox.Text.Trim();
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 8 || v > 72)
        {
            _sizeBox.BorderBrush = SettingRow.DangerBrush();
            _sizeRow.SetDesc("8〜72の数値");
            return; // 範囲外は保存しない（指示書6.4）
        }
        _sizeBox.BorderBrush = SettingRow.NormalBorderBrush();
        _sizeRow.SetDesc("8〜72");
        if (_cfg.Settings.OverlayFontSize != v)
        {
            _cfg.Settings.OverlayFontSize = v;
            SaveSoon();
        }
        ApplyPreview();
    }

    /// <summary>プレビューを今の設定で見せ直す（保存を待たず即反映）。</summary>
    private void ApplyPreview()
    {
        var bg = ColorUtil.ToBrush(_cfg.Settings.OverlayBg) ?? Brushes.Black;
        var fg = ColorUtil.ToBrush(_cfg.Settings.OverlayFg) ?? Brushes.White;
        double alpha = Math.Clamp(_cfg.Settings.OverlayAlpha, 0.15, 1.0);
        int fsize = _cfg.Settings.OverlayFontSize;
        string fam = _cfg.Settings.OverlayFontFamily;
        FontFamily ff;
        if (fam.Length > 0) ff = new FontFamily(fam);
        else ff = Application.Current?.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Yu Gothic UI");

        _previewBg.Background = bg;
        _previewBg.Opacity = alpha;
        _previewText.Foreground = fg;
        _previewText.FontFamily = ff;
        _previewText.FontSize = fsize;
        double ls = _cfg.Settings.AnswerLineSpacing;
        _previewText.LineHeight = ls > 0 ? fsize * ls : double.NaN;

        // 修正J101: 外窓の見本（色・不透明度は外窓の設定、字は共通）
        _previewStripBg.Background = ColorUtil.ToBrush(_cfg.Settings.StripBg) ?? Brushes.Black;
        _previewStripBg.Opacity = Math.Clamp(_cfg.Settings.StripAlpha, 0.15, 1.0);
        _previewStripText.Foreground = ColorUtil.ToBrush(_cfg.Settings.StripFg) ?? Brushes.White;
        _previewStripText.FontFamily = ff;
        _previewStripText.FontSize = fsize;
        _previewStripText.LineHeight = _previewText.LineHeight;
    }
}


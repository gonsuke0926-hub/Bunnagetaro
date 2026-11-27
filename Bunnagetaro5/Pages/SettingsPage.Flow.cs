using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 設定ページのプリセットの「処理の流れ」（SettingsPage の partial class）。
/// 上から 1 レイアウト判定 → 2 前処理 → 3 AI処理 の3つの独立ステップカードで選ぶ。
/// スイッチを OFF にした段は中身を畳んで短くし、保存値はそのまま保つ。
/// </summary>
public partial class SettingsPage
{
    private const double DimOpacity = 0.55;

    private CheckBox _layUse = null!, _preUse = null!, _aiUse = null!;
    private RadioButton _preBuiltin = null!, _preVlm = null!;
    private StepCardParts _layStep = null!, _preStep = null!, _aiStep = null!;
    private StackPanel _layBody = null!, _preBody = null!, _aiBody = null!;
    private TextBlock _aiNote = null!;
    private TextBlock? _flowSummaryText;
    private StackPanel _builtinSizePanel = null!, _ocrVlmPanel = null!;
    /// <summary>AI処理を OFF にしている時に効かない、流れの外の項目（「たずねる」欄）</summary>
    private readonly List<FrameworkElement> _aiOffUnused = new();
    /// <summary>レイアウト判定の細かい値（全体で1つ。cfg の root に書く）</summary>
    private readonly List<NumCtl> _layoutNums = new();
    private CheckBox _layoutTextBgChk = null!;

    private sealed record StepCardParts(
        Border Card,
        Border Badge,
        TextBlock Title,
        TextBlock Desc,
        Border BodyDivider,
        StackPanel Body);

    /// <summary>処理の流れの3つのステップカードと、その間の矢印を作る。</summary>
    private StackPanel BuildFlow(FrameworkElement instrHost, SettingRow resetRow, IEnumerable<UIElement> aiExpert)
    {
        var flow = new StackPanel { Name = "FlowPanel", Margin = new Thickness(0, 0, 0, 6) };

        // ---- 1 レイアウト判定 ----
        _layUse = MakeSwitch("LayoutUse");
        _layUse.Checked += (_, _) => { if (!_filling) { CommitGenBool("builtin_ocr_layout", true); UpdateFlowDim(); } };
        _layUse.Unchecked += (_, _) => { if (!_filling) { CommitGenBool("builtin_ocr_layout", false); UpdateFlowDim(); } };
        _layBody = new StackPanel { Name = "FlowLayoutBody" };
        _layBody.Children.Add(new SettingRow("モデル", "", ModelLabel("PP-DocLayoutV3（内蔵）", "LayoutModelLabel")).AsGrouped());

        var layExp = new StackPanel();
        _layoutTextBgChk = MakeSwitch("LayoutTextBgChk", _cfg.Settings.LayoutTextBg);
        _layoutTextBgChk.Checked += (_, _) => { _cfg.Settings.LayoutTextBg = true; SaveSoon(); };
        _layoutTextBgChk.Unchecked += (_, _) => { _cfg.Settings.LayoutTextBg = false; SaveSoon(); };
        layExp.Children.Add(new SettingRow("区画の所だけに背景を付ける", "OFF にすると解答欄全体に背景を敷きます", _layoutTextBgChk).AsGrouped());
        _layoutNums.Clear();
        for (int i = 0; i < LayoutParams.Specs.Length; i++)
        {
            var s = LayoutParams.Specs[i];
            _layoutNums.Add(AddNum(null, s.Name, s.Key, s.Lo, s.Hi, s.IsInt, s.Def, () => _cfg.Root, null, "Layout" + i + "Box"));
        }
        layExp.Children.Add(MakeDivider());
        layExp.Children.Add(_layoutNums[0].Row.AsGrouped());
        var layReset = new Button { Content = "既定に戻す", Name = "LayoutReset" };
        layReset.Click += (_, _) => ResetLayoutParams(firstBox: true);
        var d = LayoutParams.Default;
        layExp.Children.Add(MakeDivider());
        layExp.Children.Add(new SettingRow("既定に戻す", $"既定: 区画の所だけ / {d.ScoreThresh:0.##}", layReset).AsGrouped());
        _layBody.Children.Add(MakeExpertWrap(layExp, withTopDivider: true));
        _layStep = MakeStepCard("FlowLayoutBox", "1", "レイアウト判定", "画面の区画を探して、読んだ文を元の位置に並べます", _layUse, null, _layBody);

        // ---- 2 前処理 ----
        _preUse = MakeSwitch("PreUse");
        _preUse.Checked += (_, _) => FlowChanged();
        _preUse.Unchecked += (_, _) =>
        {
            if (_filling) return;
            if (_aiUse.IsChecked != true) SetChecked(_aiUse);   // 両方 OFF は選べない
            FlowChanged();
        };

        _preBody = new StackPanel { Name = "FlowPreBody" };
        _preBuiltin = MakeSegRadio("PreBuiltin", "内蔵OCR", "flowpreeng");
        _preVlm = MakeSegRadio("PreVlm", "VLM", "flowpreeng");
        _preBuiltin.Checked += (_, _) => FlowChanged();
        _preVlm.Checked += (_, _) => FlowChanged();
        _preBody.Children.Add(new SettingRow("読み方", "", MakeSegGroup(_preBuiltin, _preVlm)).AsGrouped());

        _builtinSizeCb = new ComboBox { Width = 230, Name = "BuiltinSizeCombo", VerticalAlignment = VerticalAlignment.Center };
        _builtinSizeCb.ItemsSource = new[] { "PP-OCRv6 small（内蔵）", "PP-OCRv6 medium（内蔵）" };
        _builtinSizeCb.SelectedIndex = 0;
        _builtinSizeCb.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            CommitBuiltinSize(_builtinSizeCb.SelectedIndex == 1 ? BuiltinOcr.SizeMedium : BuiltinOcr.SizeSmall);
        };
        _builtinSizePanel = new StackPanel { Name = "BuiltinSizePanel" };
        _builtinSizePanel.Children.Add(MakeDivider());
        _builtinSizePanel.Children.Add(new SettingRow("モデル", "small は速く、medium はよく読めます", _builtinSizeCb).AsGrouped());

        _ocrVlmModelCb = new ComboBox { IsEditable = true, Width = 250, Name = "OcrVlmModelCombo" };
        _ocrVlmModelCb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitOcrVlmModel(); _ocrVlmModelCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); } };
        BindCommitOnFocusLoss(_ocrVlmModelCb, CommitOcrVlmModel);
        _ocrVlmModelCb.SelectionChanged += (_, _) => { if (!_filling) CommitOcrVlmModel(); };
        _ocrVlmPromptBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 48,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Name = "OcrVlmPromptBox",
        };
        BindCommitOnFocusLoss(_ocrVlmPromptBox, CommitOcrVlmPrompt);
        _ocrVlmPromptBox.TextChanged += (_, _) =>
        {
            if (_filling) return;
            if (_ocrVlmPromptBox.Text.Trim().Length == 0) { _ocrVlmPromptTimer?.Stop(); return; }
            _ocrVlmPromptTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _ocrVlmPromptTimer.Stop();
            _ocrVlmPromptTimer.Tick -= OnOcrVlmPromptDebounceTick;
            _ocrVlmPromptTimer.Tick += OnOcrVlmPromptDebounceTick;
            _ocrVlmPromptTimer.Start();
        };
        var vlmModelRow = new SettingRow("モデル", "画像を読める VLM", _ocrVlmModelCb).AsGrouped();
        var vlmPromptHost = new StackPanel { Name = "OcrVlmPromptHost", Margin = new Thickness(14, 0, 14, 10) };
        vlmPromptHost.Children.Add(Subtle("指示"));
        vlmPromptHost.Children.Add(_ocrVlmPromptBox);

        var vlmExp = new StackPanel();
        var vCtx = AddNum(null, "コンテキスト長", "ocr_vlm_num_ctx", 512, 131072, true, 8192, GenScope, PresetAfter, "VlmCtxBox");
        var vPred = AddNum(null, "生成の上限", "ocr_vlm_num_predict", 256, 65536, true, 4096, GenScope, PresetAfter, "VlmPredictBox");
        vPred.Clamp = true;
        var vTemp = AddNum(null, "温度", "ocr_vlm_temperature", 0.0, 2.0, false, 0.0, GenScope, PresetAfter, "VlmTempBox");
        var vKeep = AddNum(null, "モデルを置いておく時間（分）", "ocr_vlm_keep_alive_min", 0, 1440, true, 30, GenScope, PresetAfter, "VlmKeepBox");
        foreach (var n in new[] { vCtx, vPred, vTemp, vKeep })
        {
            if (vlmExp.Children.Count > 0) vlmExp.Children.Add(MakeDivider());
            vlmExp.Children.Add(n.Row.AsGrouped());
            _genNums.Add(n);
        }

        _ocrVlmPanel = new StackPanel { Name = "OcrVlmPanel" };
        _ocrVlmPanel.Children.Add(MakeDivider());
        _ocrVlmPanel.Children.Add(vlmModelRow);
        _ocrVlmPanel.Children.Add(vlmPromptHost);
        _ocrVlmPanel.Children.Add(MakeExpertWrap(vlmExp, withTopDivider: true));

        _preBody.Children.Add(_builtinSizePanel);
        _preBody.Children.Add(_ocrVlmPanel);
        _preStep = MakeStepCard("FlowPreBox", "2", "前処理", "AI処理の前に、画像から文字を読みます", _preUse, null, _preBody);

        // ---- 3 AI処理 ----
        _aiUse = MakeSwitch("AiUse");
        _aiUse.Checked += (_, _) => FlowChanged();
        _aiUse.Unchecked += (_, _) =>
        {
            if (_filling) return;
            if (_preUse.IsChecked != true) SetChecked(_preUse);   // 両方 OFF は選べない
            FlowChanged();
        };
        _aiNote = FlowNote("↳ 前処理で読んだ文をそのまま出します", "AiNote");

        _aiBody = new StackPanel { Name = "FlowAiBody" };
        _aiBody.Children.Add(_modelRow);
        _aiBody.Children.Add(instrHost);
        _aiBody.Children.Add(MakeDivider());
        _aiBody.Children.Add(resetRow.AsGrouped());
        var aiExp = new StackPanel();
        foreach (var el in aiExpert)
        {
            if (aiExp.Children.Count > 0) aiExp.Children.Add(MakeDivider());
            aiExp.Children.Add(el);
        }
        for (int i = 1; i < _layoutNums.Count; i++)
        {
            aiExp.Children.Add(MakeDivider());
            aiExp.Children.Add(_layoutNums[i].Row.AsGrouped());
        }
        var batchReset = new Button { Content = "既定に戻す", Name = "LayoutBatchReset" };
        batchReset.Click += (_, _) => ResetLayoutParams(firstBox: false);
        aiExp.Children.Add(MakeDivider());
        aiExp.Children.Add(new SettingRow("区画の頼み方を既定に戻す",
            $"既定: {d.BatchChars} / {d.LabelWords} / {d.LabelChars}", batchReset).AsGrouped());
        _aiBody.Children.Add(MakeExpertWrap(aiExp, withTopDivider: true));
        _aiStep = MakeStepCard("FlowAiBox", "3", "AI処理", "読んだ文や画像を、指示に合わせて AI に頼みます", _aiUse, _aiNote, _aiBody);

        flow.Children.Add(_layStep.Card);
        flow.Children.Add(FlowArrow());
        flow.Children.Add(_preStep.Card);
        flow.Children.Add(FlowArrow());
        flow.Children.Add(_aiStep.Card);
        return flow;
    }

    private TextBlock FlowNote(string text, string name)
    {
        var tb = Subtle(text);
        tb.Name = name;
        tb.Margin = new Thickness(48, 0, 16, 10);
        return tb;
    }

    private static TextBlock ModelLabel(string text, string name) => new()
    {
        Name = name,
        Text = text,
        FontSize = 13,
        Foreground = SettingRow.SubtleBrush(),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private StepCardParts MakeStepCard(string name, string number, string title, string desc, CheckBox useSwitch, TextBlock? note, StackPanel body)
    {
        var headGrid = new Grid { Margin = new Thickness(14, 9, 14, 9) };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = SettingRow.AccentBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = number,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var descTb = new TextBlock
        {
            Text = desc,
            FontSize = 12,
            Foreground = SettingRow.SubtleBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        textCol.Children.Add(titleTb);
        textCol.Children.Add(descTb);
        Grid.SetColumn(textCol, 1);

        Grid.SetColumn(useSwitch, 2);
        headGrid.Children.Add(badge);
        headGrid.Children.Add(textCol);
        headGrid.Children.Add(useSwitch);

        var bodyDivider = MakeDivider();

        var sp = new StackPanel();
        sp.Children.Add(headGrid);
        if (note is not null) sp.Children.Add(note);
        sp.Children.Add(bodyDivider);
        sp.Children.Add(body);

        var card = new Border
        {
            Name = name,
            Child = sp,
            Background = Brushes.White,
            BorderBrush = SettingRow.CardBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            SnapsToDevicePixels = true,
        };
        return new StepCardParts(card, badge, titleTb, descTb, bodyDivider, body);
    }

    /// <summary>ステップカード同士をつなぐ縦線＋下向き矢印（Border 以外の要素にして FlowPanel 直下の Border が3箱だけになるようにする）</summary>
    private static FrameworkElement FlowArrow()
    {
        var sp = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 3),
            SnapsToDevicePixels = true,
        };
        sp.Children.Add(new Rectangle
        {
            Width = 2,
            Height = 6,
            Fill = SettingRow.NormalBorderBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new Path
        {
            Data = Geometry.Parse("M 0,0 L 14,0 L 7,7 Z"),
            Fill = SettingRow.SubtleBrush(),
            Width = 14,
            Height = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return sp;
    }

    private void SetChecked(CheckBox chk)
    {
        bool was = _filling;
        _filling = true;
        try { chk.IsChecked = true; }
        finally { _filling = was; }
    }

    private void FlowChanged()
    {
        if (_filling) return;
        CommitFlow();
        UpdateFlowDim();
    }

    /// <summary>保存の値から箱の選びを塗る（_filling の中で呼ぶ）</summary>
    private void FillFlow(JsonObject src)
    {
        string mode = TranslatePipeline.ThrowModeOf(src);
        bool vlm = mode == ConfigStore.ThrowModeVlmOcr
            || (mode != ConfigStore.ThrowModeBuiltinOcr && S(src["ocr_engine"]).Trim() == ConfigStore.OcrEngineVlm);
        bool pre = mode != ConfigStore.ThrowModeImage;
        bool ai = mode != ConfigStore.ThrowModeBuiltinOcr && mode != ConfigStore.ThrowModeVlmOcr;
        bool lay = src["builtin_ocr_layout"] is JsonValue lv && lv.GetValueKind() == JsonValueKind.True;
        _layUse.IsChecked = lay;
        _preUse.IsChecked = pre;
        _preBuiltin.IsChecked = !vlm;
        _preVlm.IsChecked = vlm;
        _aiUse.IsChecked = ai;
    }

    /// <summary>箱の選びから throw_mode を出す</summary>
    private string CurrentThrowMode()
    {
        bool pre = _preUse.IsChecked == true;
        bool ai = _aiUse.IsChecked == true;
        bool vlm = _preVlm.IsChecked == true;
        if (!pre) return ConfigStore.ThrowModeImage;
        if (ai) return ConfigStore.ThrowModeOcr;
        return vlm ? ConfigStore.ThrowModeVlmOcr : ConfigStore.ThrowModeBuiltinOcr;
    }

    private string CurrentOcrEngine() =>
        _preVlm.IsChecked == true ? ConfigStore.OcrEngineVlm : ConfigStore.OcrEngineBuiltin;

    /// <summary>箱の選びを throw_mode・ocr_split_mode・ocr_engine へ書く</summary>
    private void CommitFlow()
    {
        var scope = GenScope();
        if (scope is null) return;
        string mode = CurrentThrowMode();
        string eng = CurrentOcrEngine();
        bool split = mode == ConfigStore.ThrowModeOcr;
        bool changed = S(scope["throw_mode"]) != mode
            || S(scope["ocr_engine"]) != eng
            || scope["ocr_split_mode"] is not JsonValue jv || jv.GetValueKind() != (split ? JsonValueKind.True : JsonValueKind.False);
        if (!changed) return;
        scope["throw_mode"] = mode;
        scope["ocr_split_mode"] = split;
        scope["ocr_engine"] = eng;
        DoneGen();
    }

    /// <summary>OFF の段は中身を畳んで見出しを控えめにし、前処理の読み方に合わせて欄を出し分ける</summary>
    private void UpdateFlowDim()
    {
        bool lay = _layUse.IsChecked == true;
        bool pre = _preUse.IsChecked == true;
        bool ai = _aiUse.IsChecked == true;
        bool vlm = _preVlm.IsChecked == true;

        ApplyStepState(_layStep, !lay);
        ApplyStepState(_preStep, !pre);
        ApplyStepState(_aiStep, !ai);
        _aiNote.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        // 前処理とAI処理の両方 OFF は出す物が無いので、片方を使わないならもう片方のスイッチは切れない
        _aiUse.IsEnabled = pre;
        _preUse.IsEnabled = ai;

        _builtinSizePanel.Visibility = vlm ? Visibility.Collapsed : Visibility.Visible;
        _ocrVlmPanel.Visibility = vlm ? Visibility.Visible : Visibility.Collapsed;

        foreach (var el in _aiOffUnused)
        {
            el.IsEnabled = ai;
            el.Opacity = ai ? 1.0 : DimOpacity;
        }

        if (_flowSummaryText is not null)
        {
            var parts = new List<string>(3);
            if (lay) parts.Add("レイアウト判定");
            if (pre) parts.Add(vlm ? "前処理(VLM)" : "内蔵OCR");
            if (ai) parts.Add("AI処理");
            _flowSummaryText.Text = string.Join(" → ", parts);
        }
    }

    private static void ApplyStepState(StepCardParts step, bool off)
    {
        var activeCardBg = Application.Current?.TryFindResource("Surface") as Brush ?? Brushes.White;
        step.Card.Background = off ? SettingRow.SubtleSurfaceBrush() : activeCardBg;
        step.Badge.Background = off ? SettingRow.DisabledBrush() : SettingRow.AccentBrush();
        double op = off ? DimOpacity : 1.0;
        step.Title.Opacity = op;
        step.Desc.Opacity = op;
        step.BodyDivider.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        step.Body.IsEnabled = !off;
        step.Body.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>レイアウト判定の細かい値を既定へ（1 の箱はしきい値と背景、3 の箱は区画の頼み方）</summary>
    private void ResetLayoutParams(bool firstBox)
    {
        for (int i = 0; i < LayoutParams.Specs.Length; i++)
        {
            if ((i == 0) != firstBox) continue;
            var s = LayoutParams.Specs[i];
            _cfg.Root[s.Key] = s.IsInt ? JsonValue.Create((int)s.Def) : JsonValue.Create(s.Def);
            var n = _layoutNums[i];
            n.Load(_cfg.Root);
            n.Box.BorderBrush = SettingRow.NormalBorderBrush();
            n.Invalid = false;
        }
        if (firstBox) _layoutTextBgChk.IsChecked = true;
        SaveSoon();
    }
}

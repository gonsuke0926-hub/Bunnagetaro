using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 設定ページのプリセットの「処理の流れ」（SettingsPage の partial class）。
/// 上から 1 レイアウト判定 → 2 前処理 → 3 AI処理 の3つの箱で選ぶ。
/// 保存の形（throw_mode・ocr_engine・builtin_ocr_layout）は変えず、読み書きの時に箱と相互に変える:
///   前処理 内蔵OCR ＋ AI する → ocr ＋ builtin / VLM ＋ する → ocr ＋ vlm / しない ＋ する → image
///   内蔵OCR ＋ AI しない → builtin_ocr / VLM ＋ しない → vlm_ocr / しない ＋ しない → 選べない
/// 使わない段は箱ごと薄くする（する／しないのボタンだけは押せる）。
/// </summary>
public partial class SettingsPage
{
    private const double DimOpacity = 0.45;

    /// <summary>各段の「使用する」（する／しないの丸ボタンから1つのチェックにした）</summary>
    private CheckBox _layUse = null!, _preUse = null!, _aiUse = null!;
    private RadioButton _preBuiltin = null!, _preVlm = null!;
    private SettingRow _layHead = null!, _preHead = null!, _aiHead = null!;
    private StackPanel _layBody = null!, _preBody = null!, _aiBody = null!;
    private TextBlock _aiNote = null!;
    private StackPanel _builtinSizePanel = null!, _ocrVlmPanel = null!;
    /// <summary>AI処理を「しない」の時に効かない、流れの外の項目（「たずねる」欄）</summary>
    private readonly List<FrameworkElement> _aiOffUnused = new();
    /// <summary>レイアウト判定の細かい値（全体で1つ。cfg の root に書く）</summary>
    private readonly List<NumCtl> _layoutNums = new();
    private CheckBox _layoutTextBgChk = null!;

    /// <summary>処理の流れの3つの箱と、その間の矢印を作る。</summary>
    private StackPanel BuildFlow(FrameworkElement instrHost, SettingRow resetRow, IEnumerable<UIElement> aiExpert)
    {
        var flow = new StackPanel { Name = "FlowPanel", Margin = new Thickness(0, 0, 0, 8) };

        // ---- 1 レイアウト判定 ----
        _layUse = FlowUse("LayoutUse");
        _layUse.Checked += (_, _) => { if (!_filling) { CommitGenBool("builtin_ocr_layout", true); UpdateFlowDim(); } };
        _layUse.Unchecked += (_, _) => { if (!_filling) { CommitGenBool("builtin_ocr_layout", false); UpdateFlowDim(); } };
        _layHead = FlowHead("1 レイアウト判定", "画面の区画を探して、読んだ文を元の位置に並べます", _layUse);
        _layBody = new StackPanel { Name = "FlowLayoutBody" };
        _layBody.Children.Add(new SettingRow("モデル", "", ModelLabel("PP-DocLayoutV3（内蔵）", "LayoutModelLabel")).AsGrouped());

        var layExp = new StackPanel();
        _layoutTextBgChk = new CheckBox { Content = "区画の所だけに背景を付ける", VerticalAlignment = VerticalAlignment.Center, Name = "LayoutTextBgChk" };
        _layoutTextBgChk.IsChecked = _cfg.Settings.LayoutTextBg;
        _layoutTextBgChk.Checked += (_, _) => { _cfg.Settings.LayoutTextBg = true; SaveSoon(); };
        _layoutTextBgChk.Unchecked += (_, _) => { _cfg.Settings.LayoutTextBg = false; SaveSoon(); };
        layExp.Children.Add(new SettingRow("背景の範囲", "切ると解答欄全体に背景", _layoutTextBgChk).AsGrouped());
        _layoutNums.Clear();
        for (int i = 0; i < LayoutParams.Specs.Length; i++)
        {
            var s = LayoutParams.Specs[i];
            _layoutNums.Add(AddNum(null, s.Name, s.Key, s.Lo, s.Hi, s.IsInt, s.Def, () => _cfg.Root, null, "Layout" + i + "Box"));
        }
        // 区画を見つける時のしきい値は 1 の箱、区画をまとめて頼む値は AI に頼む時の物なので 3 の箱
        layExp.Children.Add(MakeDivider());
        layExp.Children.Add(_layoutNums[0].Row.AsGrouped());
        var layReset = new Button { Content = "既定に戻す", Name = "LayoutReset" };
        layReset.Click += (_, _) => ResetLayoutParams(firstBox: true);
        var d = LayoutParams.Default;
        layExp.Children.Add(MakeDivider());
        layExp.Children.Add(new SettingRow("既定に戻す", $"既定: 区画の所だけ / {d.ScoreThresh:0.##}", layReset).AsGrouped());
        _layBody.Children.Add(MakeExpertWrap(layExp, withTopDivider: true));

        // ---- 2 前処理 ----
        _preUse = FlowUse("PreUse");
        _preUse.Checked += (_, _) => FlowChanged();
        _preUse.Unchecked += (_, _) =>
        {
            if (_filling) return;
            if (_aiUse.IsChecked != true) SetChecked(_aiUse);   // 2 しない ＋ 3 しない は選べない
            FlowChanged();
        };
        _preHead = FlowHead("2 前処理", "AI処理の前に、画像から文字を読みます", _preUse);

        _preBody = new StackPanel { Name = "FlowPreBody" };
        _preBuiltin = FlowRadio("PreBuiltin", "内蔵OCR", "flowpreeng");
        _preVlm = FlowRadio("PreVlm", "VLM", "flowpreeng");
        _preBuiltin.Checked += (_, _) => FlowChanged();
        _preVlm.Checked += (_, _) => FlowChanged();
        var engPanel = new StackPanel { Orientation = Orientation.Horizontal };
        engPanel.Children.Add(_preBuiltin);
        engPanel.Children.Add(_preVlm);
        _preBody.Children.Add(new SettingRow("読み方", "", engPanel).AsGrouped());

        _builtinSizeCb = new ComboBox { Width = 240, Name = "BuiltinSizeCombo", VerticalAlignment = VerticalAlignment.Center };
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

        _ocrVlmModelCb = new ComboBox { IsEditable = true, Width = 260, Name = "OcrVlmModelCombo" };
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

        // ---- 3 AI処理 ----
        _aiUse = FlowUse("AiUse");
        _aiUse.Checked += (_, _) => FlowChanged();
        _aiUse.Unchecked += (_, _) =>
        {
            if (_filling) return;
            if (_preUse.IsChecked != true) SetChecked(_preUse);   // 2 しない ＋ 3 しない は選べない
            FlowChanged();
        };
        _aiHead = FlowHead("3 AI処理", "読んだ文や画像を、指示に合わせて AI に頼みます", _aiUse);
        _aiNote = FlowNote("前処理で読んだ文をそのまま出します", "AiNote");

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

        flow.Children.Add(FlowBox("FlowLayoutBox", _layHead, null, _layBody));
        flow.Children.Add(FlowArrow());
        flow.Children.Add(FlowBox("FlowPreBox", _preHead, null, _preBody));
        flow.Children.Add(FlowArrow());
        flow.Children.Add(FlowBox("FlowAiBox", _aiHead, _aiNote, _aiBody));
        return flow;
    }

    private RadioButton FlowRadio(string name, string label, string group) => new()
    {
        Name = name,
        Content = label,
        GroupName = group,
        Margin = new Thickness(0, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static CheckBox FlowUse(string name) => new()
    {
        Name = name,
        Content = "使用する",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static SettingRow FlowHead(string title, string desc, CheckBox use) =>
        new SettingRow(title, desc, use).AsGrouped();

    private TextBlock FlowNote(string text, string name)
    {
        var tb = Subtle(text);
        tb.Name = name;
        tb.Margin = new Thickness(14, 0, 14, 10);
        return tb;
    }

    private static TextBlock ModelLabel(string text, string name) => new()
    {
        Name = name,
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border FlowBox(string name, SettingRow head, TextBlock? note, StackPanel body)
    {
        var sp = new StackPanel();
        sp.Children.Add(head);
        if (note is not null) sp.Children.Add(note);
        sp.Children.Add(body);
        return new Border
        {
            Name = name,
            Child = sp,
            BorderBrush = SettingRow.NormalBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(14, 0, 14, 0),
        };
    }

    /// <summary>箱と箱の間の太めの下向き三角（真ん中に置く）</summary>
    private static FrameworkElement FlowArrow()
    {
        var brush = SettingRow.SubtleBrush();
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 36,0 L 18,16 Z"),
            Fill = brush,
            Width = 36,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6),
            Opacity = 1.0,
            SnapsToDevicePixels = true,
        };
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
        bool lay = src["builtin_ocr_layout"] is JsonValue lv && lv.GetValueKind() == JsonValueKind.True;   // 既定 OFF
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

    /// <summary>使わない段を箱ごと薄くし、前処理の読み方に合わせて欄を出し分ける</summary>
    private void UpdateFlowDim()
    {
        bool lay = _layUse.IsChecked == true;
        bool pre = _preUse.IsChecked == true;
        bool ai = _aiUse.IsChecked == true;
        bool vlm = _preVlm.IsChecked == true;

        DimBox(_layHead, _layBody, !lay);
        DimBox(_preHead, _preBody, !pre);
        DimBox(_aiHead, _aiBody, !ai);
        _aiNote.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        // 2 しない ＋ 3 しない は出す物が無いので、片方を使わないならもう片方のチェックは外せない
        _aiUse.IsEnabled = pre;
        _preUse.IsEnabled = ai;

        _builtinSizePanel.Visibility = vlm ? Visibility.Collapsed : Visibility.Visible;
        _ocrVlmPanel.Visibility = vlm ? Visibility.Visible : Visibility.Collapsed;

        foreach (var el in _aiOffUnused)
        {
            el.IsEnabled = ai;
            el.Opacity = ai ? 1.0 : DimOpacity;
        }
    }

    private static void DimBox(SettingRow head, FrameworkElement body, bool off)
    {
        double op = off ? DimOpacity : 1.0;
        head.NameBox.Opacity = op;
        head.DescBox.Opacity = op;
        body.IsEnabled = !off;
        body.Opacity = op;
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

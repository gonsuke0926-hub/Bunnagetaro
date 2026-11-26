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

/// <summary>設定ページのプリセット（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    private SettingRow _nameRow = null!;
    private SettingRow _modelRow = null!;
    private CheckBox _otslChk = null!;
    private CheckBox _htmlChk = null!;
    private CheckBox _latexChk = null!;
    private CheckBox _followChk = null!;
    private DispatcherTimer? _instrTimer;
    /// <summary>修正J22: プリセットのモデルが内蔵の OCR の時に使わない項目（薄くして触れなくする）</summary>
    private readonly List<FrameworkElement> _builtinUnused = new();
    // ================= プリセット＋生成 =================

    private void BuildPresetSection()
    {
        ReloadPresetsFromCfg();

        BodyPanel.Children.Add(Section("プリセット"));

        _presetCb = new ComboBox { Width = 210, Name = "PresetCombo", Focusable = true, IsTabStop = true };
        _presetCb.SelectionChanged += PresetSel_Changed;
        _addBtn = new Button { Content = "ふやす", Margin = new Thickness(6, 0, 0, 0), Name = "PresetAdd", Focusable = true, IsTabStop = true };
        _delBtn = new Button { Content = "けす", Margin = new Thickness(6, 0, 0, 0), Name = "PresetDel", Focusable = true, IsTabStop = true };
        _addBtn.Click += (_, _) => AddPreset_Click();
        _delBtn.Click += (_, _) => DelPreset_Click();
        var pctl = new StackPanel { Orientation = Orientation.Horizontal };
        pctl.Children.Add(_presetCb);
        pctl.Children.Add(_addBtn);
        pctl.Children.Add(_delBtn);
        _rowPreset = new SettingRow("使うプリセット", "「ふやす」で現在のプリセットを複製できます", pctl) { Name = "PresetRow" };

        _nameBox = new TextBox { Width = 220, Name = "PresetNameBox", Focusable = true, IsTabStop = true };
        _nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitNameText(); };
        BindCommitOnFocusLoss(_nameBox, CommitNameText);
        _instrBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            MinWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true,
            IsTabStop = true,
            Name = "PresetTextBox",
        };
        BindCommitOnFocusLoss(_instrBox, CommitNameText);
        _instrBox.TextChanged += (_, _) =>
        {
            if (_filling || _editingGlobal) return;
            if (_instrBox.Text.Trim().Length == 0) { _instrTimer?.Stop(); return; }
            _instrTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _instrTimer.Stop();
            _instrTimer.Tick -= OnInstrDebounceTick;
            _instrTimer.Tick += OnInstrDebounceTick;
            _instrTimer.Start();
        };
        var nameRow = new SettingRow("名前", "", _nameBox);
        _nameRow = nameRow;
        var instrHost = new StackPanel();
        instrHost.Children.Add(Subtle("指示（[system] / [user] 行で役割を分けられます）"));
        instrHost.Children.Add(_instrBox);

        _resetBtn = new Button { Content = "指示を既定に戻す", Name = "PresetReset", Focusable = true, IsTabStop = true };
        _resetBtn.Click += (_, _) => ResetText_Click();
        var resetRow = new SettingRow("指示を既定に戻す", "既定プリセットの指示だけ戻します", _resetBtn);

        _useGlobalChk = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Name = "UseGlobalChk", Focusable = true, IsTabStop = true };
        _useGlobalChk.Checked += (_, _) => UseGlobal_Changed();
        _useGlobalChk.Unchecked += (_, _) => UseGlobal_Changed();
        _rowUseGlobal = new SettingRow("グローバル設定を使う", "", _useGlobalChk);
        _rowUseGlobal.DescBox.MinHeight = 34;   // 状態文言が1〜2行に変わっても下の項目が上下にズレないよう高さを確保

        // 生成：プリセットカードの中にまとめる。「［グローバル設定］」なら共通の設定へ、
        // チェックを外せばそのプリセットの settings へ書く
        _genHeading = new TextBlock
        {
            Text = "生成",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingRow.SubtleBrush(),
            Margin = new Thickness(14, 10, 14, 0),
        };
        _genBox = new StackPanel();

        _modelCb = new ComboBox { IsEditable = true, Width = 260, Name = "ModelCombo" };
        _modelCb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitModel(); _modelCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); } };
        BindCommitOnFocusLoss(_modelCb, CommitModel);
        _modelCb.SelectionChanged += (_, _) => { if (!_filling) CommitModel(); };
        _modelRow = new SettingRow("モデル", "画像そのままは vision 対応モデル（文字起こし時はテキスト専用も可）", _modelCb);
        _modelRow.AsGrouped();
        _modelRow.Padding = new Thickness(0, 0, 0, 10);

        // 修正J28b: 名前・モデル・指示（プロンプト）を仕切り線なしの1ブロックにまとめる（上が名前、次がモデル、下が指示）
        var detailBox = new StackPanel();
        detailBox.Children.Add(_modelRow);
        detailBox.Children.Add(instrHost);
        nameRow.SetBottom(detailBox);

        _ocrOn = new RadioButton { Content = "文字起こし", GroupName = "genmode", IsChecked = true, Margin = new Thickness(0, 0, 10, 0), Name = "ThrowModeOcr", VerticalAlignment = VerticalAlignment.Center };
        _ocrOff = new RadioButton { Content = "画像そのまま", GroupName = "genmode", Margin = new Thickness(0, 0, 10, 0), Name = "ThrowModeImage", VerticalAlignment = VerticalAlignment.Center };
        _ocrBuiltinOnly = new RadioButton { Content = "内蔵OCRのみ", GroupName = "genmode", Name = "ThrowModeBuiltin", VerticalAlignment = VerticalAlignment.Center };
        _ocrOn.Checked += (_, _) => { if (!_filling) CommitThrowMode(ConfigStore.ThrowModeOcr); };
        _ocrOff.Checked += (_, _) => { if (!_filling) CommitThrowMode(ConfigStore.ThrowModeImage); };
        _ocrBuiltinOnly.Checked += (_, _) => { if (!_filling) CommitThrowMode(ConfigStore.ThrowModeBuiltinOcr); };
        var ocrPanel = new StackPanel { Orientation = Orientation.Horizontal };
        ocrPanel.Children.Add(_ocrOn);
        ocrPanel.Children.Add(_ocrOff);
        ocrPanel.Children.Add(_ocrBuiltinOnly);
        var ocrRow = new SettingRow("ぶん投げ方", "文字起こし後にAIへ渡す／画像をそのまま渡す／AI無しで内蔵OCRのみ", ocrPanel);

        // 修正J27: 「文字起こし」「内蔵OCRのみ」の下に出るサブ設定（内蔵OCR small/medium または VLM モデル＋プロンプト）
        _ocrSubBox = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        _ocrEngBuiltin = new RadioButton { Content = "内蔵OCR", GroupName = "ocreng", IsChecked = true, Margin = new Thickness(0, 0, 10, 0), Name = "OcrEngBuiltin", VerticalAlignment = VerticalAlignment.Center };
        _ocrEngVlm = new RadioButton { Content = "VLM", GroupName = "ocreng", Name = "OcrEngVlm", VerticalAlignment = VerticalAlignment.Center };
        _ocrEngBuiltin.Checked += (_, _) => { if (!_filling) CommitOcrEngine(ConfigStore.OcrEngineBuiltin); };
        _ocrEngVlm.Checked += (_, _) => { if (!_filling) CommitOcrEngine(ConfigStore.OcrEngineVlm); };
        _ocrEnginePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _ocrEnginePanel.Children.Add(new TextBlock { Text = "文字起こし方法:", Foreground = SettingRow.SubtleBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        _ocrEnginePanel.Children.Add(_ocrEngBuiltin);
        _ocrEnginePanel.Children.Add(_ocrEngVlm);

        _builtinSizeCb = new ComboBox { Width = 230, Name = "BuiltinSizeCombo", VerticalAlignment = VerticalAlignment.Center };
        _builtinSizeCb.ItemsSource = new[] { "PP-OCRv6 small（高速）", "PP-OCRv6 medium（高精度）" };
        _builtinSizeCb.SelectedIndex = 0;
        _builtinSizeCb.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            CommitBuiltinSize(_builtinSizeCb.SelectedIndex == 1 ? BuiltinOcr.SizeMedium : BuiltinOcr.SizeSmall);
        };
        _builtinSizePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        _builtinSizePanel.Children.Add(new TextBlock { Text = "内蔵OCRモデル:", Foreground = SettingRow.SubtleBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        _builtinSizePanel.Children.Add(_builtinSizeCb);
        // 修正J96・J110: 「読み取った位置に並べる」（既定 OFF）。J110 から、ぶん投げ方によらずいつも出す（VLM でも区画ごとに頼んで並べる）
        _layoutChk = MakePresetBoolChk("BuiltinLayoutChk", "読み取った位置に並べる（PP-DocLayoutV3）", "builtin_ocr_layout");
        _layoutChk.Margin = new Thickness(0, 6, 0, 2);
        _layoutChk.ToolTip = "PP-DocLayoutV3 で区画（段落・見出しなど）を見つけて、読んだ文・答えを元の画面での位置に並べます（区画ごとに頼むので遅くなります）";

        _ocrVlmModelCb = new ComboBox { IsEditable = true, Width = 240, Name = "OcrVlmModelCombo" };
        _ocrVlmModelCb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitOcrVlmModel(); _ocrVlmModelCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); } };
        BindCommitOnFocusLoss(_ocrVlmModelCb, CommitOcrVlmModel);
        _ocrVlmModelCb.SelectionChanged += (_, _) => { if (!_filling) CommitOcrVlmModel(); };
        var vlmModelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        vlmModelRow.Children.Add(new TextBlock { Text = "VLMモデル:", Foreground = SettingRow.SubtleBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        vlmModelRow.Children.Add(_ocrVlmModelCb);

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
        _ocrVlmPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        _ocrVlmPanel.Children.Add(vlmModelRow);
        _ocrVlmPanel.Children.Add(Subtle("VLMプロンプト"));
        _ocrVlmPanel.Children.Add(_ocrVlmPromptBox);

        _ocrSubBox.Children.Add(_ocrEnginePanel);
        _ocrSubBox.Children.Add(_builtinSizePanel);
        _ocrSubBox.Children.Add(_ocrVlmPanel);
        var ocrBottom = new StackPanel();
        ocrBottom.Children.Add(_layoutChk);   // 修正J110・J116: 区画が一番最初のレイヤーなので「ぶん投げ方」内の一番上に出す
        ocrBottom.Children.Add(_ocrSubBox);
        ocrRow.SetBottom(ocrBottom);

        // 表示する場所（案B: プリセット単位で持てる通常項目）
        var placePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _placeInside = new RadioButton
        {
            Content = "窓に重ねる", GroupName = "place", IsChecked = true,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _placeOutside = new RadioButton
        {
            Content = "窓の下", GroupName = "place", VerticalAlignment = VerticalAlignment.Center,
        };
        _placeInside.Checked += (_, _) => { if (!_filling) CommitGenStr("result_place", "inside"); };
        _placeOutside.Checked += (_, _) => { if (!_filling) CommitGenStr("result_place", "outside"); };
        placePanel.Children.Add(_placeInside);
        placePanel.Children.Add(_placeOutside);
        var placeRow = new SettingRow("表示する場所", "解答欄を出す位置", placePanel);

        _clipChk = new CheckBox { Content = "答えが出たらクリップボードへコピーする", VerticalAlignment = VerticalAlignment.Center, Name = "ClipChk" };
        _clipChk.Checked += (_, _) => { if (!_filling) CommitGenBool("clip_answer", true); };
        _clipChk.Unchecked += (_, _) => { if (!_filling) CommitGenBool("clip_answer", false); };
        var clipRow = new SettingRow("答えを自動でコピー", "", _clipChk);

        // I11: 変換・表示トグル（案B: プリセットごとの上級者項目）
        // 変換は OTSL → HTML → 画面の表 の順につながる（人の判断 2026-11-15）。
        // OTSL ON・HTML OFF なら OTSL を HTML の文字にして出す
        _otslChk = MakePresetBoolChk("ConvertOtslChk", "OTSL を HTML に変換する", "convert_otsl_table");
        _htmlChk = MakePresetBoolChk("ConvertHtmlChk", "HTML の表（<table>）を表として表示する", "convert_html_table");
        _latexChk = MakePresetBoolChk("ConvertLatexChk", "LaTeX 数式を数式として描画する", "convert_latex_math");
        _followChk = MakePresetBoolChk("ShowFollowChk", "外の解答欄に「たずねる」欄を出す", "show_follow_row");
        var otslRow = new SettingRow("OTSL の変換", "OTSL（<fcel> 等）を HTML の表へ変換します", _otslChk);
        var htmlRow = new SettingRow("HTML の表の変換", "HTML の <table> を表として描画します", _htmlChk);
        var latexRow = new SettingRow("LaTeX 数式の変換", "$...$ などを数式として描画します", _latexChk);
        var followRow = new SettingRow("「たずねる」欄", "", _followChk);

        _thinkCb = new ComboBox { Width = 140, Name = "ThinkCombo" };
        _thinkCb.ItemsSource = ThinkChoices.Select(c => c.Lab).ToList();
        _thinkCb.SelectionChanged += (_, _) => CommitThink();
        var thinkRow = new SettingRow("思考", "対応モデルのみ有効", _thinkCb);

        // 範囲は指示書6.4（Python版のクランプ範囲より狭い。指示書優先）
        _genNums.Clear();
        var gCtx = AddNum(null, "コンテキスト長", "ollama_num_ctx", 512, 131072, true, 8192, GenScope, PresetAfter, "CtxBox");
        var gPred = AddNum(null, "生成の上限", "ollama_num_predict", 256, 65536, true, 4096, GenScope, PresetAfter, "PredictBox");
        gPred.Clamp = true;   // 範囲外は入れた時点で寄せる（送る時の範囲と揃える。F17）
        var gTemp = AddNum(null, "温度", "ollama_temperature", 0.0, 2.0, false, 0.0, GenScope, PresetAfter, "TempBox");
        var gKeep = AddNum(null, "モデルを置いておく時間（分）", "ollama_keep_alive_min", 0, 1440, true, 30, GenScope, PresetAfter, "KeepBox");
        _genNums.AddRange([gCtx, gPred, gTemp, gKeep]);

        var genExp = new StackPanel();
        genExp.Children.Add(clipRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(otslRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(htmlRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(latexRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(followRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(thinkRow.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(gCtx.Row.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(gPred.Row.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(gTemp.Row.AsGrouped());
        genExp.Children.Add(MakeDivider());
        genExp.Children.Add(gKeep.Row.AsGrouped());

        _genBox.Children.Add(_genHeading);
        _genBox.Children.Add(ocrRow.AsGrouped());
        _genBox.Children.Add(MakeDivider());
        _genBox.Children.Add(placeRow.AsGrouped());
        _genBox.Children.Add(MakeExpertWrap(genExp, withTopDivider: true));

        // モデルはいつもプリセットごと（修正J7）なので、名前・指示の並びに置き、「グローバル設定を使う」はその下の生成の欄だけに効く
        // 修正J22・J27: 「内蔵OCRのみ」の時は AI のモデル・指示・思考・生成の数字を使わず、追加の問いもできない。
        // ぶん投げ方（モード切替・small/medium）・表示する場所・自動でコピー・表や式の変換は使う
        _builtinUnused.Clear();
        _builtinUnused.AddRange(new FrameworkElement[]
            { instrHost, _modelRow, resetRow, followRow, thinkRow, gCtx.Row, gPred.Row, gTemp.Row, gKeep.Row });

        BodyPanel.Children.Add(MakeGroupCard(_rowPreset, nameRow, resetRow, _rowUseGlobal, _genBox));
        BodyPanel.Children.Add(MakeExpertWrap(Subtle("既定: 8192 / 4096 / 0 / 30分 / モデル任せ")));

        ShowPrompt();
    }

    /// <summary>修正J22・J27: 「内蔵OCRのみ」の時は AI 用の項目を薄くして触れなくし、ぶん投げ方のサブ欄の表示も整える</summary>
    private void UpdateBuiltinDim()
    {
        bool builtin = !_editingGlobal && _ocrBuiltinOnly.IsChecked == true;
        foreach (var el in _builtinUnused)
        {
            el.IsEnabled = !builtin;
            el.Opacity = builtin ? 0.45 : 1.0;
        }
        UpdateOcrSubVisibility();
    }

    private void UpdateOcrSubVisibility()
    {
        bool isOcr = _ocrOn.IsChecked == true;
        bool isBuiltinOnly = _ocrBuiltinOnly.IsChecked == true;
        bool isVlm = isOcr && _ocrEngVlm.IsChecked == true;
        bool showBuiltinSize = isBuiltinOnly || (isOcr && !isVlm);
        _ocrEnginePanel.Visibility = isOcr ? Visibility.Visible : Visibility.Collapsed;
        _builtinSizePanel.Visibility = showBuiltinSize ? Visibility.Visible : Visibility.Collapsed;
        _ocrVlmPanel.Visibility = isVlm ? Visibility.Visible : Visibility.Collapsed;
        _ocrSubBox.Visibility = (isOcr || isBuiltinOnly) ? Visibility.Visible : Visibility.Collapsed;
    }

    private CheckBox MakePresetBoolChk(string name, string label, string key)
    {
        var chk = new CheckBox { Name = name, Content = label, VerticalAlignment = VerticalAlignment.Center };
        chk.Checked += (_, _) => { if (!_filling) CommitGenBool(key, true); };
        chk.Unchecked += (_, _) => { if (!_filling) CommitGenBool(key, false); };
        return chk;
    }

    private void ReloadPresetsFromCfg()
    {
        _pitems = new List<JsonObject>();
        if (_cfg.Settings.Prompts is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject p) _pitems.Add((JsonObject)p.DeepClone());
        if (_pitems.Count == 0)
            _pitems.Add(ConfigStore.DefaultTranslatePreset());
        string active = S(_cfg.Root.TryGetPropertyValue("prompt_active", out var a) ? a : null);
        _pcur = Math.Max(0, _pitems.FindIndex(p => S(p["name"]) == active));
    }

    private bool TryCurPreset(out JsonObject item)
    {
        item = null!;
        if (_pcur < 0 || _pcur >= _pitems.Count) return false;
        item = _pitems[_pcur];
        return true;
    }

    private string ActiveName() => S(_cfg.Root.TryGetPropertyValue("prompt_active", out var a) ? a : null);

    private void PresetSel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        string sel = _presetCb.SelectedItem as string ?? "";
        // 欄の書きかけを先に反映（直すところがあれば切り替えない）
        CommitAllVisible();
        if (_presetRejected)
        {
            _filling = true;
            _presetCb.SelectedIndex = _editingGlobal ? 0 : Math.Clamp(_pcur, 0, _pitems.Count - 1) + 1;
            _filling = false;
            return;
        }
        if (sel == GlobalEntry)
        {
            // 共通の設定を編集する画面へ。使っているプリセットは変えない
            if (_editingGlobal) return;
            _editingGlobal = true;
            ShowPrompt();
            return;
        }
        int idx = _pitems.FindIndex(p => S(p["name"]) == sel);
        if (idx < 0) { ShowPrompt(); return; }   // 一覧に無い名前にはしない
        if (_editingGlobal)
        {
            // 共通の設定の書きかけを反映してから、プリセットの画面へ戻す（まだ切り替えない）
            _editingGlobal = false;
            ShowPrompt();
            if (sel == ActiveName()) return;
        }
        if (_pcur == idx) return;
        _pcur = idx;
        WriteSelf(() =>
        {
            _cfg.Settings.PromptActive = sel;
            _cfg.Settings.Prompt = S(_pitems[_pcur]["text"]);
        });
        SaveSoon();
        ShowPrompt();
    }
}


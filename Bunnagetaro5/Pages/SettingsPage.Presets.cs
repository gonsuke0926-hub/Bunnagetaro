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
    private SettingRow _modelRow = null!;
    private CheckBox _otslChk = null!;
    private CheckBox _htmlChk = null!;
    private CheckBox _latexChk = null!;
    private CheckBox _mdChk = null!;
    private CheckBox _followChk = null!;
    private DispatcherTimer? _instrTimer;
    // ================= プリセット＋処理の流れ＋出力 =================

    private void BuildPresetSection()
    {
        ReloadPresetsFromCfg();

        var host = Pane(CatPresets);
        host.Children.Add(Section("プリセット", first: true));

        _presetCb = new ComboBox
        {
            Width = 220,
            Name = "PresetCombo",
            IsEditable = true,
            IsTextSearchEnabled = false,
            Focusable = true,
            IsTabStop = true,
        };
        _presetCb.SelectionChanged += PresetSel_Changed;
        _presetCb.DropDownOpened += (_, _) => CommitNameText();
        _presetCb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitNameText();
                if (!_presetRejected)
                    _presetCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (TryCurPreset(out var cur))
                {
                    _filling = true;
                    try
                    {
                        _presetCb.SelectedIndex = Math.Clamp(_pcur, 0, _pitems.Count - 1);
                        _presetCb.Text = S(cur["name"]);
                    }
                    finally { _filling = false; }
                    Note("");
                }
                e.Handled = true;
            }
        };
        BindCommitOnFocusLoss(_presetCb, CommitNameText);
        _addBtn = new Button { Content = "ふやす", Margin = new Thickness(6, 0, 0, 0), Name = "PresetAdd", Focusable = true, IsTabStop = true };
        _delBtn = new Button { Content = "けす", Margin = new Thickness(6, 0, 0, 0), Name = "PresetDel", Focusable = true, IsTabStop = true };
        _addBtn.Click += (_, _) => AddPreset_Click();
        _delBtn.Click += (_, _) => DelPreset_Click();
        var pctl = new StackPanel { Orientation = Orientation.Horizontal };
        pctl.Children.Add(_presetCb);
        pctl.Children.Add(_addBtn);
        pctl.Children.Add(_delBtn);
        _rowPreset = new SettingRow("使うプリセット", "欄を直接編集して名前変更・「ふやす」で複製できます", pctl) { Name = "PresetRow" };

        _instrBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            Height = 148,
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
            if (_filling) return;
            if (_instrBox.Text.Trim().Length == 0) { _instrTimer?.Stop(); return; }
            _instrTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _instrTimer.Stop();
            _instrTimer.Tick -= OnInstrDebounceTick;
            _instrTimer.Tick += OnInstrDebounceTick;
            _instrTimer.Start();
        };

        _resetBtn = new Button { Content = "指示を既定に戻す", Name = "PresetReset", Focusable = true, IsTabStop = true };
        _resetBtn.Click += (_, _) => ResetText_Click();
        var resetRow = new SettingRow("指示を既定に戻す", "既定プリセットの指示だけ戻します", _resetBtn);

        _genBox = new StackPanel();

        _modelCb = new ComboBox { IsEditable = true, Width = 250, Name = "ModelCombo" };
        _modelCb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitModel(); _modelCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); } };
        BindCommitOnFocusLoss(_modelCb, CommitModel);
        _modelCb.SelectionChanged += (_, _) => { if (!_filling) CommitModel(); };
        _modelRow = new SettingRow("モデル", "", _modelCb);
        _modelRow.AsGrouped();

        var instrHost = new StackPanel { Name = "PresetTextHost", Margin = new Thickness(14, 0, 14, 10) };
        instrHost.Children.Add(Subtle("指示　[system] / [user] 行で役割を分けられます"));
        instrHost.Children.Add(_instrBox);

        // 表示する場所（流れの外。セグメント選択）
        _placeInside = MakeSegRadio(null, "窓に重ねる", "place", true);
        _placeOutside = MakeSegRadio(null, "窓の下", "place", false);
        _placeInside.Checked += (_, _) => { if (!_filling) CommitGenStr("result_place", "inside"); };
        _placeOutside.Checked += (_, _) => { if (!_filling) CommitGenStr("result_place", "outside"); };
        var placeRow = new SettingRow("表示する場所", "解答欄を出す位置", MakeSegGroup(_placeInside, _placeOutside));

        _clipChk = MakeSwitch("ClipChk");
        _clipChk.Checked += (_, _) => { if (!_filling) CommitGenBool("clip_answer", true); };
        _clipChk.Unchecked += (_, _) => { if (!_filling) CommitGenBool("clip_answer", false); };
        var clipRow = new SettingRow("答えを自動でコピー", "答えが出たらクリップボードへコピーします", _clipChk);

        // 変換・表示トグル（プリセットごとの上級者項目）
        _otslChk = MakePresetBoolSwitch("ConvertOtslChk", "convert_otsl_table");
        _htmlChk = MakePresetBoolSwitch("ConvertHtmlChk", "convert_html_table");
        _latexChk = MakePresetBoolSwitch("ConvertLatexChk", "convert_latex_math");
        _mdChk = MakePresetBoolSwitch("ConvertMarkdownChk", "convert_markdown");
        _followChk = MakePresetBoolSwitch("ShowFollowChk", "show_follow_row");
        var otslRow = new SettingRow("OTSL の変換", "OTSL（<fcel> 等）を HTML の表へ変換します", _otslChk);
        var htmlRow = new SettingRow("HTML の表の変換", "HTML の <table> を表として描画します", _htmlChk);
        var latexRow = new SettingRow("LaTeX 数式の変換", "$...$ などを数式として描画します", _latexChk);
        var mdRow = new SettingRow("Markdown の変換", "**太字**・# 見出し・- 箇条書き・コード・| 表 | などを見た目にして表示します", _mdChk);
        var followRow = new SettingRow("「たずねる」欄", "外の解答欄に追加の質問欄を出します", _followChk);

        _thinkCb = new ComboBox { Width = 150, Name = "ThinkCombo" };
        _thinkCb.ItemsSource = ThinkChoices.Select(c => c.Lab).ToList();
        _thinkCb.SelectionChanged += (_, _) => CommitThink();
        var thinkRow = new SettingRow("思考", "対応モデルのみ有効", _thinkCb);

        _genNums.Clear();
        var gCtx = AddNum(null, "コンテキスト長", "ollama_num_ctx", 512, 131072, true, 8192, GenScope, PresetAfter, "CtxBox");
        var gPred = AddNum(null, "生成の上限", "ollama_num_predict", 256, 65536, true, 4096, GenScope, PresetAfter, "PredictBox");
        gPred.Clamp = true;
        var gTemp = AddNum(null, "温度", "ollama_temperature", 0.0, 2.0, false, 0.0, GenScope, PresetAfter, "TempBox");
        var gKeep = AddNum(null, "モデルを置いておく時間（分）", "ollama_keep_alive_min", 0, 1440, true, 30, GenScope, PresetAfter, "KeepBox");
        _genNums.AddRange([gCtx, gPred, gTemp, gKeep]);

        var flow = BuildFlow(instrHost, resetRow, new UIElement[]
            { thinkRow.AsGrouped(), gCtx.Row.AsGrouped(), gPred.Row.AsGrouped(), gTemp.Row.AsGrouped(), gKeep.Row.AsGrouped() });

        var outExp = new StackPanel();
        outExp.Children.Add(clipRow.AsGrouped());
        outExp.Children.Add(MakeDivider());
        outExp.Children.Add(otslRow.AsGrouped());
        outExp.Children.Add(MakeDivider());
        outExp.Children.Add(htmlRow.AsGrouped());
        outExp.Children.Add(MakeDivider());
        outExp.Children.Add(latexRow.AsGrouped());
        outExp.Children.Add(MakeDivider());
        outExp.Children.Add(mdRow.AsGrouped());
        outExp.Children.Add(MakeDivider());
        outExp.Children.Add(followRow.AsGrouped());

        // プリセット選択カードと処理の流れを独立させ、二重入れ子の枠線を解消する
        host.Children.Add(MakeGroupCard(_rowPreset));

        var flowHead = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 14, 0, 8) };
        var flowTitle = new TextBlock { Text = "処理の流れ", VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryFindResource("SectionHeading") is Style hst)
            flowTitle.Style = hst;
        else
        {
            flowTitle.FontSize = 16;
            flowTitle.FontWeight = FontWeights.SemiBold;
        }
        DockPanel.SetDock(flowTitle, Dock.Left);
        flowHead.Children.Add(flowTitle);

        _flowSummaryText = new TextBlock
        {
            Name = "FlowSummaryText",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Application.Current?.TryFindResource("AccentSoftText") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x00, 0x46, 0x7F)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var flowBadge = new Border
        {
            Background = Application.Current?.TryFindResource("AccentSoft") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0xD6, 0xE8, 0xF8)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _flowSummaryText,
        };
        DockPanel.SetDock(flowBadge, Dock.Right);
        flowHead.Children.Add(flowBadge);

        _genBox.Children.Add(flowHead);
        _genBox.Children.Add(flow);
        _genBox.Children.Add(Section("出力・表示位置"));
        _genBox.Children.Add(MakeGroupCard(placeRow, MakeExpertWrap(outExp, withTopDivider: true)));
        host.Children.Add(_genBox);
        host.Children.Add(MakeExpertWrap(Subtle("生成パラメータ既定: 8192 / 4096 / 0.0 / 30分 / モデル任せ")));

        // AI処理を OFF にしている時は「たずねる」欄も効かない
        _aiOffUnused.Clear();
        _aiOffUnused.Add(followRow);

        ShowPrompt();
    }

    private CheckBox MakePresetBoolSwitch(string name, string key)
    {
        var chk = MakeSwitch(name);
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
        // 編集可能 ComboBox で未一致の文字を入力中は SelectedItem が null になる（何もしない）
        if (sel.Length == 0) return;
        // テキスト入力で既存名と一致しただけの自動選択はプリセット切替にしない（Enter/フォーカス外れで重複検証へ回す）
        if (!_presetCb.IsDropDownOpen && _presetCb.Text == sel)
        {
            int curIdx = Math.Clamp(_pcur, 0, _pitems.Count - 1);
            if (_presetCb.SelectedIndex != curIdx)
            {
                _filling = true;
                try
                {
                    var tb = _presetCb.Template?.FindName("PART_EditableTextBox", _presetCb) as TextBox;
                    int caret = tb?.CaretIndex ?? sel.Length;
                    _presetCb.SelectedIndex = curIdx;
                    _presetCb.Text = sel;
                    if (tb is not null) tb.CaretIndex = Math.Min(caret, sel.Length);
                }
                finally { _filling = false; }
            }
            return;
        }
        CommitAllVisible();
        if (_presetRejected)
        {
            _filling = true;
            try
            {
                string typed = _presetCb.Text;
                _presetCb.SelectedIndex = Math.Clamp(_pcur, 0, _pitems.Count - 1);
                _presetCb.Text = typed;
            }
            finally { _filling = false; }
            return;
        }
        int idx = _pitems.FindIndex(p => S(p["name"]) == sel);
        if (idx < 0) { ShowPrompt(); return; }
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

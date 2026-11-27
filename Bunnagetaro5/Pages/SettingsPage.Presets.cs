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
            if (_filling) return;
            if (_instrBox.Text.Trim().Length == 0) { _instrTimer?.Stop(); return; }
            _instrTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _instrTimer.Stop();
            _instrTimer.Tick -= OnInstrDebounceTick;
            _instrTimer.Tick += OnInstrDebounceTick;
            _instrTimer.Start();
        };
        var nameRow = new SettingRow("名前", "", _nameBox);
        _nameRow = nameRow;

        _resetBtn = new Button { Content = "指示を既定に戻す", Name = "PresetReset", Focusable = true, IsTabStop = true };
        _resetBtn.Click += (_, _) => ResetText_Click();
        var resetRow = new SettingRow("指示を既定に戻す", "既定プリセットの指示だけ戻します", _resetBtn);

        // プリセットの中身は「処理の流れ」（1 レイアウト判定 → 2 前処理 → 3 AI処理）の箱で選ぶ
        _genHeading = new TextBlock
        {
            Text = "処理の流れ",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = SettingRow.SubtleBrush(),
            Margin = new Thickness(14, 10, 14, 6),
        };
        _genBox = new StackPanel();

        _modelCb = new ComboBox { IsEditable = true, Width = 260, Name = "ModelCombo" };
        _modelCb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitModel(); _modelCb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); } };
        BindCommitOnFocusLoss(_modelCb, CommitModel);
        _modelCb.SelectionChanged += (_, _) => { if (!_filling) CommitModel(); };
        _modelRow = new SettingRow("モデル", "", _modelCb);
        _modelRow.AsGrouped();

        var instrHost = new StackPanel { Name = "PresetTextHost", Margin = new Thickness(14, 0, 14, 10) };
        instrHost.Children.Add(Subtle("指示　[system] / [user] 行で役割を分けられます"));
        instrHost.Children.Add(_instrBox);

        // 表示する場所（流れの外。プリセットごと）
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

        // 変換・表示トグル（プリセットごとの上級者項目）
        // 変換は OTSL → HTML → 画面の表 の順につながる。
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

        _genNums.Clear();
        var gCtx = AddNum(null, "コンテキスト長", "ollama_num_ctx", 512, 131072, true, 8192, GenScope, PresetAfter, "CtxBox");
        var gPred = AddNum(null, "生成の上限", "ollama_num_predict", 256, 65536, true, 4096, GenScope, PresetAfter, "PredictBox");
        gPred.Clamp = true;   // 範囲外は入れた時点で寄せる（送る時の範囲と揃える）
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
        outExp.Children.Add(followRow.AsGrouped());

        _genBox.Children.Add(_genHeading);
        _genBox.Children.Add(flow);
        _genBox.Children.Add(MakeDivider());
        _genBox.Children.Add(placeRow.AsGrouped());
        _genBox.Children.Add(MakeExpertWrap(outExp, withTopDivider: true));

        // AI処理を「しない」時は「たずねる」欄も効かない（流れの外なので箱とは別に薄くする）
        _aiOffUnused.Clear();
        _aiOffUnused.Add(followRow);

        BodyPanel.Children.Add(MakeGroupCard(_rowPreset, nameRow, _genBox));   // 「グローバル設定を使う」は無くした
        BodyPanel.Children.Add(MakeExpertWrap(Subtle("既定: 8192 / 4096 / 0 / 30分 / モデル任せ")));

        ShowPrompt();
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
            _presetCb.SelectedIndex = Math.Clamp(_pcur, 0, _pitems.Count - 1);
            _filling = false;
            return;
        }
        int idx = _pitems.FindIndex(p => S(p["name"]) == sel);
        if (idx < 0) { ShowPrompt(); return; }   // 一覧に無い名前にはしない
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


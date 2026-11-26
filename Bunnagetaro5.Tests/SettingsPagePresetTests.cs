using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 設定ページ（手順7: プリセット・生成・自動トリガ・クイック・ホットキー・記録）の動き。
/// 指示書6.4のテスト項目：プリセットの追加・削除・名前重複・prompt の同期・
/// use_global の切り替え。ホットキー・キー起動の「登録」「解除」の配線もここ（手順24）。
/// </summary>
public class SettingsPagePresetTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public SettingsPagePresetTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_preset_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers（SettingsPageTests と同じ形） ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var ch in LogicalTreeHelper.GetChildren(root))
        {
            if (ch is T t && t.Name == name) return t;
            if (ch is DependencyObject d)
            {
                var deeper = FindLogical<T>(d, name);
                if (deeper is not null) return deeper;
            }
        }
        return null;
    }

    private static void PressEnter(UIElement box)
    {
        var src = PresentationSource.FromVisual(box);
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent };
        box.RaiseEvent(e);
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Pump(int ms)
    {
        var d = Application.Current!.Dispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(TimeSpan.FromSeconds(60));
        if (err is not null) throw err;
        return result;
    }

    private SettingsPage MakePage(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var page = new SettingsPage(cfg, () => { }, _ => { });
        var win = new Window { Content = page, Width = 900, Height = 1200, ShowActivated = false };
        win.Show();
        Pump(200);
        return page;
    }

    private static JsonArray PresetsOf(ConfigStore cfg) => (JsonArray)cfg.Root["prompts"]!;
    private static JsonObject? Preset(ConfigStore cfg, string name)
    {
        foreach (var n in PresetsOf(cfg))
            if (n is JsonObject p && p["name"]?.GetValue<string>() == name) return p;
        return null;
    }

    private static int IndexOfItem(ComboBox cb, string s)
    {
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] as string == s) return i;
        return -1;
    }

    // ---- tests ----

    [Fact]
    public void PresetComboStartsWithGlobalAndSwitchSyncsPrompt()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            Assert.Equal(SettingsPage.GlobalEntry, (string)cb.Items[0]!);
            Assert.True(IndexOfItem(cb, "OCR") >= 0);
            cb.SelectedIndex = IndexOfItem(cb, "OCR");
            Pump(50);
            Assert.Equal("OCR", cfg.GetStr("prompt_active"));
            Assert.Equal(Preset(cfg, "OCR")!["text"]!.GetValue<string>(), cfg.GetStr("prompt"));
            return true;
        });
    }

    [Fact]
    public void AddPresetMakesCopyNamedAfterCurrent()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var items = cb.Items;
            int before = PresetsOf(cfg).Count;
            cb.SelectedIndex = items.IndexOf("OCR");
            Pump(50);
            Click(FindLogical<Button>(page, "PresetAdd")!);
            Pump(50);
            Assert.Equal(before + 1, PresetsOf(cfg).Count);
            Assert.NotNull(Preset(cfg, "OCR のコピー"));
            Assert.Equal("OCR のコピー", cfg.GetStr("prompt_active"));
            return true;
        });
    }

    [Fact]
    public void DelPresetNeedsConfirmAndKeepsLastOne()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            page.ConfirmOverride = _ => false;
            int before = PresetsOf(cfg).Count;
            Click(FindLogical<Button>(page, "PresetDel")!);
            Pump(50);
            Assert.Equal(before, PresetsOf(cfg).Count);   // キャンセルなら消えない

            page.ConfirmOverride = _ => true;
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            for (int i = 0; i < before - 1; i++)
            {
                cb.SelectedIndex = 1;   // 今の選択を毎回消す
                Pump(20);
                Click(FindLogical<Button>(page, "PresetDel")!);
                Pump(30);
            }
            Assert.Single(PresetsOf(cfg));
            Click(FindLogical<Button>(page, "PresetDel")!);
            Pump(30);
            Assert.Single(PresetsOf(cfg));                // 最後の1件は消せない
            Assert.Contains("最後の1件は消せません", FindLogical<SettingRow>(page, "PresetRow")!.DescBox.Text);
            return true;
        });
    }

    [Fact]
    public void DuplicateNameIsRejectedAndNotSaved()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var name = FindLogical<TextBox>(page, "PresetNameBox")!;
            string active = cfg.GetStr("prompt_active");
            name.Text = "OCR";   // 既定プリセットの名前とかぶらせる
            PressEnter(name);
            Pump(50);
            Assert.NotEqual("OCR", Preset(cfg, active)!["name"]!.GetValue<string>());
            Assert.Contains("重複", FindLogical<SettingRow>(page, "PresetRow")!.DescBox.Text);
            return true;
        });
    }

    [Fact]
    public void EmptyInstructionIsRejected()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            instr.Text = "   ";
            PressEnter(FindLogical<TextBox>(page, "PresetNameBox")!);
            Pump(50);
            Assert.NotEqual("   ", cfg.GetStr("prompt"));
            Assert.Contains("指示が空です", FindLogical<SettingRow>(page, "PresetRow")!.DescBox.Text);
            return true;
        });
    }

    [Fact]
    public void UseGlobalToggleMovesSettingsInAndOut()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var chk = FindLogical<CheckBox>(page, "UseGlobalChk")!;
            string active = cfg.GetStr("prompt_active");
            Assert.False(chk.IsChecked);                    // 既定の翻訳は推奨モデル個別設定
            chk.IsChecked = true;
            Pump(50);
            chk.IsChecked = false;
            Pump(50);
            var p = Preset(cfg, active)!;
            Assert.False(p["use_global"]!.GetValue<bool>());
            Assert.NotNull(p["settings"] as JsonObject);    // 外した瞬間に欄の値が settings へ
            chk.IsChecked = true;
            Pump(50);
            p = Preset(cfg, active)!;
            Assert.True(p["use_global"]!.GetValue<bool>());
            Assert.Null(p["settings"]);                     // 使う形に戻せば settings は持たない
            return true;
        });
    }

    [Fact]
    public void GlobalEditWritesRootAndOutOfRangeNotSaved()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = 0;   // ［グローバル設定］
            Pump(50);
            var ctx = FindLogical<TextBox>(page, "CtxBox")!;
            ctx.Text = "1024";
            PressEnter(ctx);
            Pump(50);
            Assert.Equal(1024, cfg.GetInt("ollama_num_ctx", 0));

            ctx.Text = "100";       // 範囲外（512〜131072）
            PressEnter(ctx);
            Pump(50);
            Assert.Equal(1024, cfg.GetInt("ollama_num_ctx", 0));
            Assert.Contains("512〜131072の数値", FindLogical<SettingRow>(page, "CtxBoxRow")!.DescBox.Text);

            page.FlushPendingSave();
            Assert.Contains("\"ollama_num_ctx\":1024", File.ReadAllText(_paths.ConfigJson, System.Text.Encoding.UTF8).Replace(" ", ""));
            return true;
        });
    }

    [Fact]
    public void 生成の上限は256から65536で範囲外は入れた時点で寄せる()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = 0;
            Pump(50);
            var box = FindLogical<TextBox>(page, "PredictBox")!;
            box.Text = "100";
            PressEnter(box);
            Pump(50);
            Assert.Equal("256", box.Text);
            Assert.Equal(256, cfg.GetInt("ollama_num_predict", 0));
            box.Text = "99999";
            PressEnter(box);
            Pump(50);
            Assert.Equal("65536", box.Text);
            Assert.Equal(65536, cfg.GetInt("ollama_num_predict", 0));
            Assert.Contains("256〜65536", FindLogical<SettingRow>(page, "PredictBoxRow")!.DescBox.Text);
            return true;
        });
    }

    [Fact]
    public void PresetScopeNumWritesOnlyItsSettings()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var items = cb.Items;
            cb.SelectedIndex = items.IndexOf("OCR");        // use_global=false のプリセット
            Pump(50);
            var temp = FindLogical<TextBox>(page, "TempBox")!;
            temp.Text = "0.7";
            PressEnter(temp);
            Pump(50);
            var st = (JsonObject)Preset(cfg, "OCR")!["settings"]!;
            Assert.Equal(0.7, st["ollama_temperature"]!.GetValue<double>(), 3);
            Assert.Equal(0.0, cfg.GetDouble("ollama_temperature", -1), 3);   // グローバルは無傷
            return true;
        });
    }

    [Fact]
    public void ModelSwitchWritesIntoPresetSettings()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var items = cb.Items;
            cb.SelectedIndex = items.IndexOf("OCR");
            Pump(50);
            var model = FindLogical<ComboBox>(page, "ModelCombo")!;
            model.Text = "some-vision-model";
            PressEnter(model);
            Pump(50);
            Assert.Equal("some-vision-model", Preset(cfg, "OCR")!["model"]!.GetValue<string>());   // モデルはプリセットの "model"（修正J7）
            return true;
        });
    }

    [Fact]
    public void J7_グローバル設定を使うプリセットでもモデルは灰色にならずそのプリセットへ書く()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("OCR");
            Pump(50);
            var chk = FindLogical<CheckBox>(page, "UseGlobalChk")!;
            chk.IsChecked = true;                    // OCR をグローバル設定を使う形にする
            Pump(50);
            Assert.True(Preset(cfg, "OCR")!["use_global"]!.GetValue<bool>());
            var model = FindLogical<ComboBox>(page, "ModelCombo")!;
            Assert.True(model.IsEnabled);            // 生成の欄は灰色でも、モデルは触れる
            Assert.Equal(ConfigStore.OcrPresetDefaultModel, model.Text);   // チェックを入れてもプリセットのモデルのまま
            model.Text = "other-ocr";
            PressEnter(model);
            Pump(50);
            Assert.Equal("other-ocr", Preset(cfg, "OCR")!["model"]!.GetValue<string>());
            Assert.Equal("", cfg.GetStr("model"));   // グローバルの model は触らない
            cfg.SetStr("prompt_active", "OCR");
            Assert.Equal("other-ocr", cfg.EffectiveConfig()["model"]!.GetValue<string>());

            cb.SelectedIndex = 0;                    // ［グローバル設定］にはモデルの欄が無い
            Pump(50);
            DependencyObject? row = model;
            while (row is not null and not SettingRow) row = LogicalTreeHelper.GetParent(row);
            Assert.NotNull(row);
            Assert.Equal(Visibility.Collapsed, ((SettingRow)row!).Visibility);
            return true;
        });
    }

    [Fact]
    public void J22_内蔵OCRのみなら使わない項目だけ薄くして触れない()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("OCR");
            Pump(50);
            FindLogical<CheckBox>(page, "UseGlobalChk")!.IsChecked = false;   // 生成の欄を触れる形にして、内蔵の分だけを見る
            Pump(50);
            var builtinOnly = FindLogical<RadioButton>(page, "ThrowModeBuiltin")!;
            var imageMode = FindLogical<RadioButton>(page, "ThrowModeImage")!;
            Assert.True(builtinOnly.IsChecked);                                // 新しい設定の OCR は内蔵OCRのみ

            string[] unused = { "ModelCombo", "PresetTextBox", "PresetReset", "ThinkCombo", "ShowFollowChk", "CtxBox", "PredictBox", "TempBox", "KeepBox" };
            string[] used = { "PresetNameBox", "ThrowModeBuiltin", "BuiltinSizeCombo", "UseGlobalChk", "ClipChk", "ConvertOtslChk", "ConvertHtmlChk", "ConvertLatexChk" };
            void Expect(bool builtin)
            {
                foreach (var n in unused)
                    Assert.True(FindLogical<FrameworkElement>(page, n)!.IsEnabled == !builtin, n + " の IsEnabled");
                foreach (var n in used)
                    Assert.True(FindLogical<FrameworkElement>(page, n)!.IsEnabled, n + " は触れる");
            }
            Expect(builtin: true);

            imageMode.IsChecked = true;                                        // 画像そのままにすると戻る
            Pump(50);
            Expect(builtin: false);

            builtinOnly.IsChecked = true;                                      // 内蔵OCRのみに戻すと再び薄くなる
            Pump(50);
            Expect(builtin: true);

            cb.SelectedIndex = cb.Items.IndexOf("翻訳");                        // ほかのプリセットへ移ると戻る
            Pump(50);
            Assert.True(FindLogical<TextBox>(page, "PresetTextBox")!.IsEnabled);
            Assert.True(FindLogical<ComboBox>(page, "ModelCombo")!.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void J27_ぶん投げ方の切替で内蔵サイズとVLM欄が切り替わり設定へ保存される()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("翻訳");
            Pump(50);
            FindLogical<CheckBox>(page, "UseGlobalChk")!.IsChecked = false;
            Pump(50);

            var rOcr = FindLogical<RadioButton>(page, "ThrowModeOcr")!;
            var rImg = FindLogical<RadioButton>(page, "ThrowModeImage")!;
            var rBuilt = FindLogical<RadioButton>(page, "ThrowModeBuiltin")!;
            var engBuilt = FindLogical<RadioButton>(page, "OcrEngBuiltin")!;
            var engVlm = FindLogical<RadioButton>(page, "OcrEngVlm")!;
            var sizeCb = FindLogical<ComboBox>(page, "BuiltinSizeCombo")!;
            var vlmModel = FindLogical<ComboBox>(page, "OcrVlmModelCombo")!;
            var vlmPrompt = FindLogical<TextBox>(page, "OcrVlmPromptBox")!;

            // 初期: 翻訳は 文字起こし → 内蔵OCR → small
            Assert.True(rOcr.IsChecked);
            Assert.True(engBuilt.IsChecked);
            Assert.Equal(0, sizeCb.SelectedIndex);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)sizeCb.Parent).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)vlmPrompt.Parent).Visibility);

            // medium に変更
            sizeCb.SelectedIndex = 1;
            Pump(50);
            Assert.Equal(BuiltinOcr.SizeMedium, Preset(cfg, "翻訳")!["settings"]!["builtin_ocr_size"]!.GetValue<string>());

            // VLM に切替 → VLM欄が表示され、サイズ欄は閉じる
            engVlm.IsChecked = true;
            Pump(50);
            Assert.Equal(ConfigStore.OcrEngineVlm, Preset(cfg, "翻訳")!["settings"]!["ocr_engine"]!.GetValue<string>());
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)sizeCb.Parent).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)vlmPrompt.Parent).Visibility);

            vlmModel.Text = "glm-ocr:latest";
            PressEnter(vlmModel);
            vlmPrompt.Text = "Read text:";
            vlmPrompt.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, vlmPrompt, null)
            {
                RoutedEvent = UIElement.LostKeyboardFocusEvent,
            });
            Pump(50);
            Assert.Equal("glm-ocr:latest", Preset(cfg, "翻訳")!["settings"]!["ocr_vlm_model"]!.GetValue<string>());
            Assert.Equal("Read text:", Preset(cfg, "翻訳")!["settings"]!["ocr_vlm_prompt"]!.GetValue<string>());

            // 画像そのまま → サブ欄ごと隠れる
            rImg.IsChecked = true;
            Pump(50);
            Assert.Equal(ConfigStore.ThrowModeImage, Preset(cfg, "翻訳")!["settings"]!["throw_mode"]!.GetValue<string>());
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)engBuilt.Parent).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)sizeCb.Parent).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)vlmPrompt.Parent).Visibility);

            // 内蔵OCRのみ → サイズ欄だけ出る
            rBuilt.IsChecked = true;
            Pump(50);
            Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, Preset(cfg, "翻訳")!["settings"]!["throw_mode"]!.GetValue<string>());
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)engBuilt.Parent).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)sizeCb.Parent).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)vlmPrompt.Parent).Visibility);
            return true;
        });
    }

    [Fact]
    public void ModeCheckboxDrivesModeKey()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var chk = FindLogical<CheckBox>(page, "ModeChk")!;
            chk.IsChecked = true;
            Pump(20);
            Assert.Equal("auto", cfg.GetStr("mode"));
            chk.IsChecked = false;
            Pump(20);
            Assert.Equal("manual", cfg.GetStr("mode"));
            return true;
        });
    }

    [Fact]
    public void QuickMinSizeRangeEnforced()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var w = FindLogical<TextBox>(page, "QuickWBox")!;
            w.Text = "10";   // 範囲外（30〜4000）
            PressEnter(w);
            Pump(30);
            Assert.Equal(30, cfg.GetInt("quick_min_w", 0));
            w.Text = "60";
            PressEnter(w);
            Pump(30);
            Assert.Equal(60, cfg.GetInt("quick_min_w", 0));
            return true;
        });
    }

    [Fact]
    public void ShotsDirEnterSaves()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var box = FindLogical<TextBox>(page, "ShotsBox")!;
            box.Text = @"D:\shots\test";
            PressEnter(box);
            Pump(30);
            Assert.Equal(@"D:\shots\test", cfg.GetStr("shots_dir"));
            return true;
        });
    }

    [Fact]
    public void RegisterAndClearButtonsAreWiredToMainWindow()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            // 手順24で「登録」も生きた。ページは押下を本体へそのまま流す
            Assert.True(FindLogical<Button>(page, "HotkeyReg")!.IsEnabled);
            Assert.True(FindLogical<Button>(page, "QuickKeyReg")!.IsEnabled);
            var got = new List<string>();
            page.RegRequest = w => got.Add("reg:" + w);
            page.ClearRequest = w => got.Add("clear:" + w);
            Click(FindLogical<Button>(page, "QuickKeyReg")!);
            Click(FindLogical<Button>(page, "QuickKeyDel")!);
            Click(FindLogical<Button>(page, "HotkeyReg")!);
            Click(FindLogical<Button>(page, "HotkeyDel")!);
            Assert.Equal(new[] { "reg:kquick", "clear:kquick", "reg:hot", "clear:hot" }, got);
            return true;
        });
    }

    [Fact]
    public void BindFieldsShowLabelsAndWaitTextCanBeSet()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            var kq = FindLogical<TextBox>(page, "QuickKeyBox")!;
            var hk = FindLogical<TextBox>(page, "HotkeyBox")!;
            Assert.Equal("（未設定）", kq.Text);   // 初期表示はbinding_label表示（rawトークンを見せない）
            page.SetBindField("kquick", "登録するキーを押してください");
            Assert.Equal("登録するキーを押してください", kq.Text);
            page.SyncBindFields(Binding.Label("f8"), Binding.Label("ctrl+f9"), Binding.Label("f7"));
            Assert.Equal(Binding.Label("f8"), kq.Text);
            Assert.Equal(Binding.Label("ctrl+f9"), hk.Text);
            return true;
        });
    }

    [Fact]
    public void ResetInstructionRestoresDefaultText()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            instr.Text = "勝手に書き換えた";
            PressEnter(FindLogical<TextBox>(page, "PresetNameBox")!);
            Pump(30);
            Assert.Equal("勝手に書き換えた", cfg.GetStr("prompt"));
            var reset = FindLogical<Button>(page, "PresetReset")!;
            Assert.True(reset.IsEnabled);   // 翻訳用は既定プリセット
            Click(reset);
            Pump(50);
            Assert.Equal(ConfigStore.DefaultPrompt, cfg.GetStr("prompt"));
            return true;
        });
    }

    [Fact]
    public void TabNavigationReachesPresetResetFromPresetControls()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var add = FindLogical<Button>(page, "PresetAdd")!;
            var del = FindLogical<Button>(page, "PresetDel")!;
            var nameBox = FindLogical<TextBox>(page, "PresetNameBox")!;
            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            var reset = FindLogical<Button>(page, "PresetReset")!;
            var useGlobal = FindLogical<CheckBox>(page, "UseGlobalChk")!;

            // 各部品の Focusable / IsTabStop を確認
            Assert.True(cb.Focusable && cb.IsTabStop);
            Assert.True(add.Focusable && add.IsTabStop);
            Assert.True(del.Focusable && del.IsTabStop);
            Assert.True(nameBox.Focusable && nameBox.IsTabStop);
            Assert.True(instr.Focusable && instr.IsTabStop);
            Assert.True(reset.Focusable && reset.IsTabStop);
            Assert.True(useGlobal.Focusable && useGlobal.IsTabStop);

            // ComboBox から Tab (Next) で順に PresetReset・UseGlobalChk まで到達できること
            // （修正J7 でモデル欄が名前・指示の下へ、「グローバル設定を使う」が「指示を既定に戻す」の下へ移った）
            cb.Focus();
            Pump(20);
            var visited = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                var cur = Keyboard.FocusedElement as FrameworkElement;
                if (cur is not null && !string.IsNullOrEmpty(cur.Name))
                    visited.Add(cur.Name);
                else
                    visited.Add(cur?.GetType().Name ?? "(null)");
                if (ReferenceEquals(cur, useGlobal)) break;
                cur?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Pump(10);
            }
            w.Close();
            Assert.Contains("PresetReset", visited);
            Assert.Contains("UseGlobalChk", visited);
            Assert.True(visited.IndexOf("PresetReset") < visited.IndexOf("UseGlobalChk"), string.Join(" > ", visited));
            return true;
        });
    }

    [Fact]
    public void H3_無効のTextBoxはボタンと同じ無効ブラシになる()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var nameBox = FindLogical<TextBox>(page, "PresetNameBox")!;
            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;

            cb.SelectedIndex = 0;   // ［グローバル設定］
            Pump(30);

            Assert.False(nameBox.IsEnabled);
            Assert.False(instr.IsEnabled);

            var disabled = Application.Current!.Resources["Disabled"];
            var border = Application.Current.Resources["Border"];
            var surface = Application.Current.Resources["Surface"];

            foreach (var tb in new[] { nameBox, instr })
            {
                tb.ApplyTemplate();
                var bd = tb.Template.FindName("Bd", tb) as Border;
                Assert.NotNull(bd);
                Assert.Same(disabled, tb.Foreground);
                Assert.Same(border, bd!.BorderBrush);
                Assert.Same(surface, bd.Background);
            }

            w.Close();
            return true;
        });
    }

    [Fact]
    public void 指示欄はキーボードフォーカス喪失で即確定する()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            instr.Text = "[system]\n[plamo-translate]\ntarget_lang=en";
            instr.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, instr, null)
            {
                RoutedEvent = UIElement.LostKeyboardFocusEvent,
            });
            Pump(30);

            Assert.Equal("[system]\n[plamo-translate]\ntarget_lang=en", cfg.Settings.Prompt);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 指示欄は入力停止後の間引きタイマーでも確定する()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            instr.Text = "[system]\n[plamo-translate]\ntarget_lang=en";
            Pump(420);

            Assert.Equal("[system]\n[plamo-translate]\ntarget_lang=en", cfg.Settings.Prompt);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void J28_名前とモデルと指示が1ブロックにまとまり上から名前_モデル_指示の順になる()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            static SettingRow RowOf(DependencyObject? d)
            {
                while (d is not null and not SettingRow) d = LogicalTreeHelper.GetParent(d);
                return (SettingRow)d!;
            }
            var presetRow = RowOf(FindLogical<ComboBox>(page, "PresetCombo"));
            var nameRow = RowOf(FindLogical<TextBox>(page, "PresetNameBox"));
            var modelRow = RowOf(FindLogical<ComboBox>(page, "ModelCombo"));
            var instrBox = FindLogical<TextBox>(page, "PresetTextBox")!;
            var detailBox = (Panel)LogicalTreeHelper.GetParent(modelRow)!;
            var instrHost = (FrameworkElement)LogicalTreeHelper.GetParent(instrBox)!;
            // detailBox は nameRow の下段にあり、先頭がモデル・次が指示
            Assert.Same(nameRow, RowOf(detailBox));
            Assert.True(detailBox.Children.IndexOf(modelRow) < detailBox.Children.IndexOf(instrHost));
            // カード内では使うプリセットの直下に nameRow（名前＋モデル＋指示のまとまり）がある
            var card = (Panel)LogicalTreeHelper.GetParent(presetRow)!;
            Assert.True(card.Children.IndexOf(nameRow) > card.Children.IndexOf(presetRow));
            return true;
        });
    }

    [Fact]
    public void J119_位置の反映チェックに区画モデル名PP_DocLayoutV3が載っており翻訳とOCRは既定ON()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            var chk = FindLogical<CheckBox>(page, "BuiltinLayoutChk")!;
            Assert.Contains("PP-DocLayoutV3", chk.Content?.ToString() ?? "");
            Assert.True(chk.IsChecked);   // 初期選択「翻訳」は位置の反映 ON

            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedItem = "OCR";
            Pump(50);
            Assert.True(chk.IsChecked);   // 既定「OCR」も位置の反映 ON

            cb.SelectedItem = "数式";
            Pump(50);
            Assert.False(chk.IsChecked);  // 既定「数式」は OFF
            return true;
        });
    }
}

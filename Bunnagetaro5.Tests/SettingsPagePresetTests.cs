using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Linq;
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
/// 設定ページ（プリセット・生成・自動トリガ・クイック・ホットキー・記録）の動き。
/// プリセットの追加・削除・名前重複・prompt の同期・
/// use_global の切り替え。ホットキー・キー起動の「登録」「解除」の配線もここ。
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
    public void PresetComboHasNoGlobalAndSwitchSyncsPrompt()   // ［グローバル設定］は無くした
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            Assert.DoesNotContain("［グローバル設定］", cb.Items.Cast<object>().Select(o => o.ToString()));
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
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            string active = cfg.GetStr("prompt_active");
            cb.Text = "OCR";   // 既定プリセットの名前とかぶらせる（自動切替せず重複検証される）
            PressEnter(cb);
            Pump(50);
            Assert.NotEqual("OCR", Preset(cfg, active)!["name"]!.GetValue<string>());
            Assert.Equal(active, cfg.GetStr("prompt_active"));
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
            PressEnter(FindLogical<ComboBox>(page, "PresetCombo")!);
            Pump(50);
            Assert.NotEqual("   ", cfg.GetStr("prompt"));
            Assert.Contains("指示が空です", FindLogical<SettingRow>(page, "PresetRow")!.DescBox.Text);
            return true;
        });
    }

    [Fact]
    public void J121_グローバル設定の欄は無く_プリセットはいつも自分の設定を持つ()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            Assert.Null(FindLogical<CheckBox>(page, "UseGlobalChk"));
            foreach (var p in PresetsOf(cfg))
            {
                Assert.False(p["use_global"]!.GetValue<bool>());
                Assert.NotNull(p["settings"] as JsonObject);
            }
            return true;
        });
    }

    private static int Eff(ConfigStore cfg, string key) => cfg.EffectiveConfig()[key]!.GetValue<int>();

    [Fact]
    public void J121_生成の値は選んでいるプリセットへ書き_範囲外は保存しない()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            string active = cfg.GetStr("prompt_active");
            var ctx = FindLogical<TextBox>(page, "CtxBox")!;
            ctx.Text = "1024";
            PressEnter(ctx);
            Pump(50);
            Assert.Equal(1024, Eff(cfg, "ollama_num_ctx"));
            Assert.Equal(1024, Preset(cfg, active)!["settings"]!["ollama_num_ctx"]!.GetValue<int>());

            ctx.Text = "100";       // 範囲外（512〜131072）
            PressEnter(ctx);
            Pump(50);
            Assert.Equal(1024, Eff(cfg, "ollama_num_ctx"));
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
            Assert.Equal(256, Eff(cfg, "ollama_num_predict"));
            box.Text = "99999";
            PressEnter(box);
            Pump(50);
            Assert.Equal("65536", box.Text);
            Assert.Equal(65536, Eff(cfg, "ollama_num_predict"));
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
            Assert.Equal("some-vision-model", Preset(cfg, "OCR")!["model"]!.GetValue<string>());   // モデルはプリセットの "model"
            return true;
        });
    }

    [Fact]
    public void J7_モデルはそのプリセットへ書く()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("OCR");
            Pump(50);
            var model = FindLogical<ComboBox>(page, "ModelCombo")!;
            Assert.Equal(ConfigStore.OcrPresetDefaultModel, model.Text);
            model.Text = "other-ocr";
            PressEnter(model);
            Pump(50);
            Assert.Equal("other-ocr", Preset(cfg, "OCR")!["model"]!.GetValue<string>());
            Assert.Equal("", cfg.GetStr("model"));   // 前のグローバルの model は触らない
            cfg.SetStr("prompt_active", "OCR");
            Assert.Equal("other-ocr", cfg.EffectiveConfig()["model"]!.GetValue<string>());
            return true;
        });
    }

    private static string St(ConfigStore cfg, string preset, string key) =>
        Preset(cfg, preset)!["settings"]![key]!.GetValue<string>();

    [Fact]
    public void J123_AI処理をしないにすると3の箱ごと薄くなり触れない()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("OCR");
            Pump(50);
            var aiUse = FindLogical<CheckBox>(page, "AiUse")!;
            Assert.False(aiUse.IsChecked);                                        // 新しい設定の OCR は内蔵OCRのみ＝AI処理しない

            string[] unused = { "ModelCombo", "PresetTextBox", "PresetReset", "ThinkCombo", "ShowFollowChk", "CtxBox", "PredictBox", "TempBox", "KeepBox" };
            string[] used = { "PresetCombo", "AiUse", "BuiltinSizeCombo", "ClipChk", "ConvertOtslChk", "ConvertHtmlChk", "ConvertLatexChk" };
            void Expect(bool off)
            {
                foreach (var n in unused)
                    Assert.True(FindLogical<FrameworkElement>(page, n)!.IsEnabled == !off, n + " の IsEnabled");
                foreach (var n in used)
                    Assert.True(FindLogical<FrameworkElement>(page, n)!.IsEnabled, n + " は触れる");
                Assert.Equal(off ? Visibility.Collapsed : Visibility.Visible, FindLogical<FrameworkElement>(page, "FlowAiBody")!.Visibility);
                Assert.True(FindLogical<CheckBox>(page, "PreUse")!.IsEnabled == !off, "AI処理しないなら前処理のチェックは外せない");
            }
            Expect(off: true);

            aiUse.IsChecked = true;
            Pump(50);
            Expect(off: false);
            Assert.Equal(ConfigStore.ThrowModeOcr, St(cfg, "OCR", "throw_mode"));

            aiUse.IsChecked = false;
            Pump(50);
            Expect(off: true);
            Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, St(cfg, "OCR", "throw_mode"));

            cb.SelectedIndex = cb.Items.IndexOf("翻訳");                        // ほかのプリセットへ移ると戻る
            Pump(50);
            Assert.True(FindLogical<TextBox>(page, "PresetTextBox")!.IsEnabled);
            Assert.True(FindLogical<ComboBox>(page, "ModelCombo")!.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void J123_流れの箱の選びが保存の形に変わり読み方の欄が切り替わる()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedIndex = cb.Items.IndexOf("翻訳");
            Pump(50);

            var preUse = FindLogical<CheckBox>(page, "PreUse")!;
            var preBuilt = FindLogical<RadioButton>(page, "PreBuiltin")!;
            var preVlm = FindLogical<RadioButton>(page, "PreVlm")!;
            var aiUse = FindLogical<CheckBox>(page, "AiUse")!;
            var sizeCb = FindLogical<ComboBox>(page, "BuiltinSizeCombo")!;
            var sizePanel = FindLogical<FrameworkElement>(page, "BuiltinSizePanel")!;
            var vlmPanel = FindLogical<FrameworkElement>(page, "OcrVlmPanel")!;
            var vlmModel = FindLogical<ComboBox>(page, "OcrVlmModelCombo")!;
            var vlmPrompt = FindLogical<TextBox>(page, "OcrVlmPromptBox")!;
            Assert.Contains("PP-OCRv6 small（内蔵）", sizeCb.Items.Cast<string>());

            // 初期: 翻訳は 前処理する（内蔵OCR small）→ AI処理する
            Assert.True(preUse.IsChecked);
            Assert.True(preBuilt.IsChecked);
            Assert.True(aiUse.IsChecked);
            Assert.Equal("レイアウト判定 → 内蔵OCR → AI処理", FindLogical<TextBlock>(page, "FlowSummaryText")!.Text);
            Assert.Equal(0, sizeCb.SelectedIndex);
            Assert.Equal(Visibility.Visible, sizePanel.Visibility);
            Assert.Equal(Visibility.Collapsed, vlmPanel.Visibility);

            sizeCb.SelectedIndex = 1;
            Pump(50);
            Assert.Equal(BuiltinOcr.SizeMedium, St(cfg, "翻訳", "builtin_ocr_size"));

            // VLM → ocr ＋ vlm。VLM の欄が出て、内蔵のモデルの欄は閉じる
            preVlm.IsChecked = true;
            Pump(50);
            Assert.Equal(ConfigStore.ThrowModeOcr, St(cfg, "翻訳", "throw_mode"));
            Assert.Equal(ConfigStore.OcrEngineVlm, St(cfg, "翻訳", "ocr_engine"));
            Assert.Equal(Visibility.Collapsed, sizePanel.Visibility);
            Assert.Equal(Visibility.Visible, vlmPanel.Visibility);

            vlmModel.Text = "glm-ocr:latest";
            PressEnter(vlmModel);
            vlmPrompt.Text = "Read text:";
            vlmPrompt.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, vlmPrompt, null)
            {
                RoutedEvent = UIElement.LostKeyboardFocusEvent,
            });
            Pump(50);
            Assert.Equal("glm-ocr:latest", St(cfg, "翻訳", "ocr_vlm_model"));
            Assert.Equal("Read text:", St(cfg, "翻訳", "ocr_vlm_prompt"));

            // VLM ＋ AI処理しない → vlm_ocr
            aiUse.IsChecked = false;
            Pump(50);
            Assert.Equal(ConfigStore.ThrowModeVlmOcr, St(cfg, "翻訳", "throw_mode"));
            Assert.False(cfg.EffectiveBool("ocr_split_mode", true));

            // 内蔵OCR ＋ AI処理しない → builtin_ocr（まとめバッジにも「そのまま出力」は出さない）
            preBuilt.IsChecked = true;
            Pump(50);
            Assert.Equal("レイアウト判定 → 内蔵OCR", FindLogical<TextBlock>(page, "FlowSummaryText")!.Text);
            Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, St(cfg, "翻訳", "throw_mode"));
            Assert.Equal(ConfigStore.OcrEngineBuiltin, St(cfg, "翻訳", "ocr_engine"));

            // 前処理しない ＋ AI処理する → image。2 の箱ごと薄く、AI処理の「しない」は押せない
            aiUse.IsChecked = true;
            preUse.IsChecked = false;
            Pump(50);
            Assert.Equal("レイアウト判定 → AI処理", FindLogical<TextBlock>(page, "FlowSummaryText")!.Text);
            Assert.Equal(ConfigStore.ThrowModeImage, St(cfg, "翻訳", "throw_mode"));
            Assert.False(FindLogical<FrameworkElement>(page, "FlowPreBody")!.IsEnabled);
            Assert.False(sizeCb.IsEnabled);
            Assert.False(aiUse.IsEnabled);
            Assert.True(preUse.IsEnabled);
            Assert.Null(FindLogical<FrameworkElement>(page, "PreNote"));   // 前処理を使わない時の説明の行は出さない

            // 2 しない のまま 3 しない を入れられても、2 を「する」に戻して選べない組み合わせにしない
            aiUse.IsChecked = false;
            Pump(50);
            Assert.True(preUse.IsChecked);
            Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, St(cfg, "翻訳", "throw_mode"));

            // 読み込み直しても同じ選びに戻る
            cb.SelectedIndex = cb.Items.IndexOf("OCR");
            Pump(50);
            cb.SelectedIndex = cb.Items.IndexOf("翻訳");
            Pump(50);
            Assert.True(preUse.IsChecked);
            Assert.True(preBuilt.IsChecked);
            Assert.False(aiUse.IsChecked);
            return true;
        });
    }

    [Fact]
    public void J123_前処理のVLMの生成の値はocr_vlmのキーへ書く()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            string active = cfg.GetStr("prompt_active");
            var box = FindLogical<TextBox>(page, "VlmCtxBox")!;
            box.Text = "2048";
            PressEnter(box);
            Pump(50);
            var st = (JsonObject)Preset(cfg, active)!["settings"]!;
            Assert.Equal(2048, st["ocr_vlm_num_ctx"]!.GetValue<int>());
            Assert.NotEqual(2048, st["ollama_num_ctx"]!.GetValue<int>());   // AI処理の値は別
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
            // 「登録」は有効で、ページは押下を本体へそのまま流す
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
            PressEnter(FindLogical<ComboBox>(page, "PresetCombo")!);
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
            page.SelectCategory(SettingsPage.CatPresets);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            var add = FindLogical<Button>(page, "PresetAdd")!;
            var del = FindLogical<Button>(page, "PresetDel")!;
            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;
            var reset = FindLogical<Button>(page, "PresetReset")!;


            // 各部品の Focusable / IsTabStop を確認
            Assert.True(cb.Focusable && cb.IsTabStop);
            Assert.True(add.Focusable && add.IsTabStop);
            Assert.True(del.Focusable && del.IsTabStop);
            Assert.True(instr.Focusable && instr.IsTabStop);
            Assert.True(reset.Focusable && reset.IsTabStop);


            // ComboBox から Tab (Next) で順に PresetReset まで到達できること
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
                if (ReferenceEquals(cur, reset)) break;
                cur?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Pump(10);
            }
            w.Close();
            Assert.Contains("PresetReset", visited);

            int cbIdx = visited.FindIndex(n => n is "PresetCombo" or "PART_EditableTextBox");
            Assert.True(cbIdx >= 0 && cbIdx < visited.IndexOf("PresetReset"), string.Join(" > ", visited));
            return true;
        });
    }

    [Fact]
    public void H3_無効のTextBoxはボタンと同じ無効ブラシになる()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            page.SelectCategory(SettingsPage.CatPresets);
            var w = new Window { Content = page, Width = 900, Height = 700, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
            w.Show();
            Pump(50);

            var instr = FindLogical<TextBox>(page, "PresetTextBox")!;

            // 前は［グローバル設定］を選ぶと無効になった。今は欄を無効にして同じ見た目を確かめる
            instr.IsEnabled = false;
            Pump(30);

            var disabled = Application.Current!.Resources["Disabled"];
            var border = Application.Current.Resources["Border"];
            var surface = Application.Current.Resources["Surface"];

            foreach (var tb in new[] { instr })
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
    public void J123_流れは上から1レイアウト判定_2前処理_3AI処理でモデルと指示は3の箱にある()
    {
        OnSta(() =>
        {
            var page = MakePage(out _);
            static FrameworkElement Up(DependencyObject? d, string name)
            {
                while (d is not null && !(d is FrameworkElement fe && fe.Name == name)) d = LogicalTreeHelper.GetParent(d);
                return (FrameworkElement)d!;
            }
            var flow = FindLogical<StackPanel>(page, "FlowPanel")!;
            var boxes = flow.Children.OfType<Border>().Select(b => b.Name).ToList();
            Assert.Equal(new[] { "FlowLayoutBox", "FlowPreBox", "FlowAiBox" }, boxes);
            Assert.NotNull(Up(FindLogical<ComboBox>(page, "ModelCombo"), "FlowAiBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "PresetTextBox"), "FlowAiBox"));
            Assert.NotNull(Up(FindLogical<Button>(page, "PresetReset"), "FlowAiBox"));
            Assert.NotNull(Up(FindLogical<ComboBox>(page, "BuiltinSizeCombo"), "FlowPreBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "OcrVlmPromptBox"), "FlowPreBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "CtxBox"), "FlowAiBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "VlmCtxBox"), "FlowPreBox"));
            Assert.NotNull(Up(FindLogical<CheckBox>(page, "LayoutTextBgChk"), "FlowLayoutBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "Layout0Box"), "FlowLayoutBox"));
            Assert.NotNull(Up(FindLogical<TextBox>(page, "Layout1Box"), "FlowAiBox"));
            // 「表示する場所」などの出す設定は流れの外
            Assert.Null(Up(FindLogical<CheckBox>(page, "ClipChk"), "FlowPanel"));
            return true;
        });
    }

    [Fact]
    public void J123_レイアウト判定の箱にPP_DocLayoutV3内蔵が載り翻訳とOCRは既定でする()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            Assert.Equal("PP-DocLayoutV3（内蔵）", FindLogical<TextBlock>(page, "LayoutModelLabel")!.Text);
            var use = FindLogical<CheckBox>(page, "LayoutUse")!;
            var body = FindLogical<FrameworkElement>(page, "FlowLayoutBody")!;
            Assert.True(use.IsChecked);   // 初期選択「翻訳」はする
            Assert.True(body.IsEnabled);

            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            cb.SelectedItem = "OCR";
            Pump(50);
            Assert.True(use.IsChecked);   // 既定「OCR」もする

            cb.SelectedItem = "数式";
            Pump(50);
            Assert.False(use.IsChecked);  // 既定「数式」はしない
            Assert.False(body.IsEnabled);
            Assert.True(use.IsEnabled);   // 「使用する」のチェックは押せる

            use.IsChecked = true;
            Pump(50);
            Assert.True(Preset(cfg, "数式")!["settings"]!["builtin_ocr_layout"]!.GetValue<bool>());
            Assert.True(body.IsEnabled);
            return true;
        });
    }

    [Fact]
    public void J140_プリセット名はコンボボックスでインライン編集できEscで戻せる()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg);
            Assert.Null(FindLogical<TextBox>(page, "PresetNameBox"));
            var cb = FindLogical<ComboBox>(page, "PresetCombo")!;
            Assert.True(cb.IsEditable);
            Assert.False(cb.IsTextSearchEnabled);

            cb.Text = "英語翻訳";
            PressEnter(cb);
            Pump(50);
            Assert.Equal("英語翻訳", cfg.GetStr("prompt_active"));
            Assert.NotNull(Preset(cfg, "英語翻訳"));

            cb.Text = "途中入力";
            cb.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cb), 0, Key.Escape)
            {
                RoutedEvent = UIElement.KeyDownEvent
            });
            Pump(30);
            Assert.Equal("英語翻訳", cb.Text);
            Assert.Equal("英語翻訳", cfg.GetStr("prompt_active"));
            return true;
        });
    }
}

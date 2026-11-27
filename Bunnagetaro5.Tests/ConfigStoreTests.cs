using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>config.json の読み書き（未知キー保持／原子保存／
/// effective_config相当の重ね書き）。</summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public ConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_cfg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>config.jsonを書いてからLoad（旧フォルダは常に空）。</summary>
    private ConfigStore LoadWith(string json)
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.ConfigJson, json);
        var store = new ConfigStore(_paths);
        store.Load();
        return store;
    }

    // ------------------------------------------------------------ 初回・既定

    [Fact]
    public void LoadFresh_WritesDefaultsAndFillsPresetSettings()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        Assert.True(File.Exists(_paths.ConfigJson));
        Assert.Equal(8192, store.GetInt("ollama_num_ctx", 0));
        Assert.False(store.GetBool("clip_answer", true));
        var prompts = store.Root["prompts"]!.AsArray();
        Assert.Equal(4, prompts.Count);
        Assert.Equal(new[] { "翻訳", "OCR", "数式", "表" }, prompts.OfType<JsonObject>().Select(p => p["name"]!.GetValue<string>()));
        Assert.Equal("翻訳", store.GetStr("prompt_active"));
        Assert.True(store.GetBool("presets_v5", false));
        string M(string n) => prompts.OfType<JsonObject>().First(p => p["name"]!.GetValue<string>() == n)["model"]!.GetValue<string>();   // プリセットの "model"
        string T(string n) => prompts.OfType<JsonObject>().First(p => p["name"]!.GetValue<string>() == n)["text"]!.GetValue<string>();
        Assert.Equal("", M("OCR"));   // OCR プリセットは throw_mode=builtin_ocr でモデル欄は空
        Assert.Equal("glm-ocr", M("数式"));
        Assert.Equal("hf.co/PaddlePaddle/PaddleOCR-VL-1.6-GGUF", M("表"));
        Assert.Equal("Text Recognition:", T("OCR"));
        Assert.Equal("Formula Recognition:", T("数式"));
        Assert.Equal("Table Recognition:", T("表"));
        var ocr = prompts.OfType<JsonObject>().First(p => p["name"]!.GetValue<string>() == "OCR");
        var st = ocr["settings"]!.AsObject();
        Assert.Equal(ConfigStore.PresetKeys.Length, st.Count); // PRESET_KEYSは全部埋まる
        Assert.False(st["ocr_split_mode"]!.GetValue<bool>());
        Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, st["throw_mode"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeSmall, st["builtin_ocr_size"]!.GetValue<string>());
        Assert.Equal("", ocr["model"]!.GetValue<string>());   // 内蔵 OCR はモデル欄に置かない
        var tr = prompts.OfType<JsonObject>().First(p => p["name"]!.GetValue<string>() == "翻訳")["settings"]!.AsObject();
        Assert.Equal(ConfigStore.ThrowModeOcr, tr["throw_mode"]!.GetValue<string>());
        Assert.Equal(ConfigStore.OcrEngineBuiltin, tr["ocr_engine"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeSmall, tr["builtin_ocr_size"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ プリセットの入れ替え

    [Fact]
    public void PresetsV5_OldNamesAreRewrittenOnce_OwnPresetsKept_SelectionFollows()
    {
        var store = LoadWith("""
            {"prompt_active":"Excel","prompts":[
                {"name":"翻訳用","text":"my tr","use_global":true},
                {"name":"OCR","text":"old ocr","use_global":false,"settings":{"model":"x","ocr_split_mode":true,"ollama_num_ctx":4096}},
                {"name":"LaTeX","text":"old latex","use_global":false},
                {"name":"Excel","text":"old excel","use_global":false},
                {"name":"自作","text":"mine","use_global":true}],
             "presets_offered":["翻訳用","OCR","LaTeX","Excel"]}
            """);
        var ps = store.Root["prompts"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(new[] { "翻訳", "OCR", "数式", "表", "自作" }, ps.Select(p => p["name"]!.GetValue<string>()));
        Assert.Equal("my tr", ps[0]["text"]!.GetValue<string>());   // 翻訳は名前だけ
        Assert.Equal("Text Recognition:", ps[1]["text"]!.GetValue<string>());
        Assert.Equal("glm-ocr:latest", ps[1]["model"]!.GetValue<string>());   // プリセットの "model" へ
        Assert.False(ps[1]["settings"]!.AsObject().ContainsKey("model"));
        Assert.False(ps[1]["settings"]!["ocr_split_mode"]!.GetValue<bool>());
        Assert.Equal(4096, ps[1]["settings"]!["ollama_num_ctx"]!.GetValue<int>());   // 他の専用設定は残す
        Assert.Equal("Formula Recognition:", ps[2]["text"]!.GetValue<string>());
        Assert.Equal("glm-ocr", ps[2]["model"]!.GetValue<string>());
        Assert.Equal("Table Recognition:", ps[3]["text"]!.GetValue<string>());
        Assert.Equal("mine", ps[4]["text"]!.GetValue<string>());
        Assert.Equal("表", store.GetStr("prompt_active"));
        Assert.Equal("Table Recognition:", store.GetStr("prompt"));
        Assert.Equal(new[] { "翻訳", "OCR", "数式", "表" },
            store.Root["presets_offered"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(store.Migrations, m => m.Contains("入れ替えました"));
        Assert.True(store.GetBool("presets_v5", false));

        // 2回目の読み込みでは何も変わらない（お知らせも出ない）
        store.Save();
        string before = store.Root.ToJsonString();
        var again = new ConfigStore(_paths);
        again.Load();
        Assert.Empty(again.Migrations);
        Assert.Equal(before, again.Root.ToJsonString());
    }

    [Fact]
    public void PresetsV5_DeletedPresetDoesNotComeBack()
    {
        var store = LoadWith("""
            {"prompts":[{"name":"翻訳用","text":"t","use_global":true},{"name":"OCR","text":"o","use_global":false}],
             "presets_offered":["翻訳用","OCR","LaTeX","Excel"]}
            """);
        Assert.Equal(new[] { "翻訳", "OCR" },
            store.Root["prompts"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
    }

    // ------------------------------------------------------------ 未知キー保持

    [Fact]
    public void UnknownKeysSurviveSaveRoundTrip()
    {
        var store = LoadWith("""{"model":"m1","trig_extra":"e","zzz":{"a":[1,2]}}""");
        Assert.Equal("m1", store.GetStr("model"));
        store.SetStr("model", "m2");
        store.Save();
        string text = File.ReadAllText(_paths.ConfigJson);
        Assert.Contains("\"trig_extra\"", text);
        Assert.Contains("\"zzz\"", text);
        var again = new ConfigStore(_paths);
        again.Load();
        Assert.Equal("m2", again.GetStr("model"));
        Assert.Equal("e", again.Root["trig_extra"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ 原子保存・.bak

    [Fact]
    public void Save_BackupsPreviousFile()
    {
        var store = new ConfigStore(_paths);
        store.Load(); // 初回は config.json 作成のみ（.bakはまだ無し）
        Assert.False(File.Exists(_paths.ConfigBak));
        store.SetStr("model", "mX");
        store.Save();
        Assert.True(File.Exists(_paths.ConfigBak));
        var bak = JsonNode.Parse(File.ReadAllText(_paths.ConfigBak))!.AsObject();
        Assert.Equal(ConfigStore.DefaultModel, bak["model"]!.GetValue<string>());
        var now = JsonNode.Parse(File.ReadAllText(_paths.ConfigJson))!.AsObject();
        Assert.Equal("mX", now["model"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ プリセット重ね書き

    [Fact]
    public void EffectiveConfig_OverlaysOnlyPresetKeys()
    {
        var store = LoadWith("""
            {"prompt_active":"X","prompts":[{"name":"X","text":"t","use_global":false,
              "settings":{"model":"vision:7","base_url":"http://evil:9","ocr_split_mode":false}}]}
            """);
        var eff = store.EffectiveConfig();
        Assert.Equal("vision:7", eff["model"]!.GetValue<string>());
        Assert.False(eff["ocr_split_mode"]!.GetValue<bool>());
        // base_urlはPresetKeysに無いのでプリセットから漏れない（通信先はグローバル）
        Assert.Equal("http://localhost:11434", eff["base_url"]!.GetValue<string>());
        // グローバル側は微動だにしない（ EffectiveConfig は読むだけの写し）
        Assert.Equal(ConfigStore.DefaultModel, store.GetStr("model"));
        // 無いキーはグローバルの写しで充填されている
        var settings = store.ActivePreset()!["settings"]!.AsObject();
        Assert.Equal(8192, settings["ollama_num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public void EffectiveConfig_文字起こしVLMの設定を_ocr_stageへ写す()
    {
        var store = LoadWith("""
            {"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],"prompt_active":"翻訳","prompts":[
              {"name":"翻訳","text":"訳して","model":"translategemma:4b","use_global":false,
               "settings":{"throw_mode":"ocr","ocr_engine":"vlm","ocr_vlm_model":"paddle-ocr","ocr_vlm_prompt":"OCR:","ollama_num_predict":2000}}]}
            """);
        var eff = store.EffectiveConfig();
        Assert.Equal("translategemma:4b", eff["model"]!.GetValue<string>());
        Assert.Equal("訳して", eff["prompt"]!.GetValue<string>());
        var ocr = eff[ConfigStore.OcrStageKey]!.AsObject();
        Assert.Equal("paddle-ocr", ocr["model"]!.GetValue<string>());
        Assert.Equal("OCR:", ocr["prompt"]!.GetValue<string>());
        Assert.Equal(2000, ocr["ollama_num_predict"]!.GetValue<int>());
        // 写しの中だけ。保存する Root には入らない
        Assert.Null(store.Root[ConfigStore.OcrStageKey]);

        // 内蔵OCRのみ／画像そのまま／内蔵OCR文字起こし時は _ocr_stage を付けない
        var builtinOnly = LoadWith("""
            {"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],"prompt_active":"OCR","prompts":[
              {"name":"OCR","text":"OCR:","use_global":false,"settings":{"throw_mode":"builtin_ocr","builtin_ocr_size":"small"}}]}
            """);
        Assert.Null(builtinOnly.EffectiveConfig()[ConfigStore.OcrStageKey]);
        var builtinOcr = LoadWith("""
            {"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],"prompt_active":"A","prompts":[
              {"name":"A","text":"a","use_global":false,"settings":{"throw_mode":"ocr","ocr_engine":"builtin"}}]}
            """);
        Assert.Null(builtinOcr.EffectiveConfig()[ConfigStore.OcrStageKey]);
    }

    [Fact]
    public void PromptAlwaysSyncedToActive()
    {
        var store = LoadWith("""
            {"prompts":[{"name":"A","text":"text A","use_global":true},
                        {"name":"B","text":"text B","use_global":true}],
             "prompt_active":"B","prompt":"old"}
            """);
        Assert.Equal("B", store.GetStr("prompt_active"));
        Assert.Equal("text B", store.GetStr("prompt"));
    }

    // ------------------------------------------------------------ 古いconfigの引っ越し

    [Fact]
    public void OldPromptOnlyConfigUpgraded()
    {
        var store = LoadWith("""{"prompt":"my sentence"}""");
        var prompts = store.Root["prompts"]!.AsArray();
        Assert.Equal(4, prompts.Count);
        var first = prompts[0]!.AsObject();
        Assert.Equal("翻訳", first["name"]!.GetValue<string>());
        Assert.Equal("my sentence", first["text"]!.GetValue<string>());
        Assert.Equal("翻訳", store.GetStr("prompt_active"));
        Assert.Equal("my sentence", store.GetStr("prompt"));
        Assert.Contains(store.Migrations, m => m.Contains("「翻訳」"));
        Assert.Contains(store.Migrations, m => m.Contains("OCR"));
        Assert.Equal(4, store.Root["presets_offered"]!.AsArray().Count);
    }

    [Fact]
    public void OfferedPresetsAreNotReAdded()
    {
        var store = LoadWith("""
            {"prompts":[{"name":"翻訳","text":"keep","use_global":true}],
             "presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"]}
            """);
        Assert.Single(store.Root["prompts"]!.AsArray());
        Assert.Empty(store.Migrations);
    }

    [Fact]
    public void LegacyDefaultPromptReplaced()
    {
        string json = """{"prompt":""" + JsonSerializer.Serialize(ConfigStore.OldPromptEn) + "}";
        var store = LoadWith(json);
        // 古い config はグローバルのモデル（画像を読むモデル）で使うので、PLaMo 用の3行ではなく画像向けの文へ
        Assert.Equal(ConfigStore.DefaultImagePrompt, store.GetStr("prompt"));
        Assert.Contains(store.Migrations, m => m.Contains("あらゆる言語"));
        var first = store.Root["prompts"]!.AsArray()[0]!.AsObject();
        Assert.Equal(ConfigStore.DefaultImagePrompt, first["text"]!.GetValue<string>());
        Assert.False(first["use_global"]!.GetValue<bool>());   // グローバルの値を写して自分の設定にした
    }

    [Fact]
    public void 文の無い古いconfigと一覧が空のconfigは既定の翻訳を専用の設定ごと入れる()
    {
        foreach (var json in new[] { """{"model":"qwen3-vl:4b"}""", """{"model":"qwen3-vl:4b","prompts":[],"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"]}""" })
        {
            var store = LoadWith(json);
            var first = store.Root["prompts"]!.AsArray()[0]!.AsObject();
            Assert.Equal(ConfigStore.DefaultPromptName, first["name"]!.GetValue<string>());
            Assert.Equal(ConfigStore.DefaultPrompt, first["text"]!.GetValue<string>());
            Assert.False(first["use_global"]!.GetValue<bool>());   // PLaMo の3行を画像のモデルへ送らない
            Assert.Equal(ConfigStore.TranslatePresetModel, first["model"]!.GetValue<string>());
        }
    }

    [Fact]
    public void J7_古いconfigのモデルはプリセットへ写す_グローバルを使う物はグローバルのモデル()
    {
        var store = LoadWith("""
            {"model":"qwen3-vl:4b","presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],
             "prompt_active":"自作","prompts":[
                {"name":"自作","text":"mine","use_global":true},
                {"name":"専用","text":"own","use_global":false,"settings":{"model":"own:1b","ollama_num_ctx":4096}},
                {"name":"空","text":"empty","use_global":false,"settings":{"ollama_num_ctx":4096}}]}
            """);
        var ps = store.Root["prompts"]!.AsArray().OfType<JsonObject>().ToDictionary(p => p["name"]!.GetValue<string>());
        Assert.Equal("qwen3-vl:4b", ps["自作"]["model"]!.GetValue<string>());   // グローバルを使っていた → グローバルのモデル
        Assert.Equal("own:1b", ps["専用"]["model"]!.GetValue<string>());       // 外していた → 専用のモデル
        Assert.Equal("qwen3-vl:4b", ps["空"]["model"]!.GetValue<string>());    // 専用にモデルが無い → グローバルのモデル（今までもそれで動いていた）
        Assert.False(ps["専用"]["settings"]!.AsObject().ContainsKey("model"));
        Assert.Equal("qwen3-vl:4b", store.GetStr("model"));                   // グローバルの model は消さない
        Assert.Equal("qwen3-vl:4b", store.EffectiveConfig()["model"]!.GetValue<string>());
        store.Root["prompt_active"] = "専用";
        Assert.Equal("own:1b", store.EffectiveConfig()["model"]!.GetValue<string>());
        // 読み直しても変わらない（2回目は "model" を持っているのでそのまま）
        store.Save();
        var again = new ConfigStore(_paths);
        again.Load();
        var ps2 = again.Root["prompts"]!.AsArray().OfType<JsonObject>().ToDictionary(p => p["name"]!.GetValue<string>());
        Assert.Equal("qwen3-vl:4b", ps2["自作"]["model"]!.GetValue<string>());
        Assert.Equal("own:1b", ps2["専用"]["model"]!.GetValue<string>());
    }

    [Fact]
    public void QuickDefaultsMigrated()
    {
        var store = LoadWith("""{"quick_min_w":120,"quick_min_h":90,"quick_button":"back"}""");
        Assert.Equal(30, store.GetInt("quick_min_w", 0));
        Assert.Equal(30, store.GetInt("quick_min_h", 0));
        Assert.Equal("wheel", store.GetStr("quick_button"));
        Assert.Equal(2, store.Migrations.Count(m => m.Contains("クイック")));
    }

    [Fact]
    public void BaseUrlNormalizedOrFallback()
    {
        Assert.Equal("http://localhost:11434",
            LoadWith("""{"base_url":"localhost:11434"}""").GetStr("base_url"));
        Assert.Equal("http://localhost:11434",
            LoadWith("""{"base_url":":1234"}""").GetStr("base_url"));
        Assert.Equal("http://evil.example:8080/",
            LoadWith("""{"base_url":"http://evil.example:8080/"}""").GetStr("base_url"));
    }

    [Theory]
    [InlineData("http://evil.com\\@localhost", null)]
    [InlineData("http://[::1", null)]
    [InlineData("", null)]
    [InlineData("http://:1234", null)]
    [InlineData("HTTP://LOCALHOST:11434", "localhost")]
    [InlineData("http://localhost:11434/v1", "localhost")]
    public void SafeHostRejectsMalformed(string url, string? expected)
        => Assert.Equal(expected, ConfigStore.SafeHost(ConfigStore.NormBase(url)));

    // ------------------------------------------------------------ LaTeX プリセット既定文と引っ越し

    [Fact]
    public void LatexPresetDefaultText_MatchesSpecCharForChar()
    {
        const string expected = """Convert the mathematical expressions in the image into LaTeX. Reproduce them exactly as shown; do not solve, simplify, or explain them. Write each equation on its own line wrapped in $$ ... $$. Use LaTeX commands such as \nabla, \cdot, \times, \partial, \frac, \rho, \mathbf. Never use Unicode math symbols like ∇ · × ∂ ρ. Output nothing else.""";
        Assert.Equal(expected, ConfigStore.DefaultLatexPrompt);
    }

    [Fact]
    public void LatexPresetMigration_ThreeCases()
    {
        const string migMsg = "LaTeX プリセットの答えを数式として表示するようにしました";

        // 1) 旧既定文 → 新既定文へ置換
        string oldJson = JsonSerializer.Serialize(ConfigStore.OldLatexPrompt);
        var s1 = LoadWith($$"""
            {"prompt_active":"翻訳用","prompts":[
                {"name":"翻訳用","text":"t","use_global":true},
                {"name":"LaTeX","text":{{oldJson}},"use_global":false}],
             "presets_v5":true,"presets_offered":["翻訳用","OCR","LaTeX","Excel"]}
            """);
        var latex1 = s1.Root["prompts"]!.AsArray().OfType<JsonObject>()
            .First(p => p["name"]!.GetValue<string>() == "LaTeX");
        Assert.Equal(ConfigStore.DefaultLatexPrompt, latex1["text"]!.GetValue<string>());
        Assert.Contains(migMsg, s1.Migrations);

        // 2) 利用者が書き換え済み → 不変
        var s2 = LoadWith("""
            {"prompt_active":"LaTeX","prompt":"my custom latex","prompts":[
                {"name":"翻訳用","text":"t","use_global":true},
                {"name":"LaTeX","text":"my custom latex","use_global":false}],
             "presets_v5":true,"presets_offered":["翻訳用","OCR","LaTeX","Excel"]}
            """);
        var latex2 = s2.Root["prompts"]!.AsArray().OfType<JsonObject>()
            .First(p => p["name"]!.GetValue<string>() == "LaTeX");
        Assert.Equal("my custom latex", latex2["text"]!.GetValue<string>());
        Assert.Equal("my custom latex", s2.GetStr("prompt"));
        Assert.DoesNotContain(migMsg, s2.Migrations);

        // 3) prompt_active=="LaTeX" でトップの prompt も旧既定文 → 両方置換
        var s3 = LoadWith($$"""
            {"prompt_active":"LaTeX","prompt":{{oldJson}},"prompts":[
                {"name":"翻訳用","text":"t","use_global":true},
                {"name":"LaTeX","text":{{oldJson}},"use_global":false}],
             "presets_v5":true,"presets_offered":["翻訳用","OCR","LaTeX","Excel"]}
            """);
        var latex3 = s3.Root["prompts"]!.AsArray().OfType<JsonObject>()
            .First(p => p["name"]!.GetValue<string>() == "LaTeX");
        Assert.Equal(ConfigStore.DefaultLatexPrompt, latex3["text"]!.GetValue<string>());
        Assert.Equal(ConfigStore.DefaultLatexPrompt, s3.GetStr("prompt"));
        Assert.Equal(1, s3.Migrations.Count(m => m == migMsg));
    }

    [Fact]
    public void CorruptConfig_BackupsToBadFile_DoesNotSaveOnLoad_AndSurvivesSubsequentSaves()
    {
        const string corrupt = "{ \"model\": \"custom\", broken ";
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.ConfigJson, corrupt);

        var store = new ConfigStore(_paths);
        store.Load();

        Assert.False(store.HadSaved);
        Assert.NotNull(store.LoadWarning);
        Assert.Contains("設定ファイルが読めなかったので既定で起動しました。元は config.json.bad-", store.LoadWarning);

        // 起動直後の Load では Save() しないので元の config.json も上書きされない
        Assert.Equal(corrupt, File.ReadAllText(_paths.ConfigJson));

        var badFiles = Directory.GetFiles(_paths.Root, "config.json.bad-*");
        Assert.Single(badFiles);
        Assert.Equal(corrupt, File.ReadAllText(badFiles[0]));

        // AppLog に案内が出ていること
        string logText = File.ReadAllText(_paths.AppLogFile);
        Assert.Contains(store.LoadWarning, logText);

        // Save を2回呼んでも .bad-* の中身は変わらない
        store.SetStr("model", "m1");
        store.Save();
        store.SetStr("model", "m2");
        store.Save();

        var badFilesAfter = Directory.GetFiles(_paths.Root, "config.json.bad-*");
        Assert.Single(badFilesAfter);
        Assert.Equal(badFiles[0], badFilesAfter[0]);
        Assert.Equal(corrupt, File.ReadAllText(badFilesAfter[0]));
    }

    [Fact]
    public void CorruptConfig_KeepsAtMostThreeBadBackups()
    {
        Directory.CreateDirectory(_paths.Root);
        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(_paths.ConfigJson, $"{{ broken #{i}");
            var store = new ConfigStore(_paths);
            store.Load();
            Thread.Sleep(20);
        }

        var badFiles = Directory.GetFiles(_paths.Root, "config.json.bad-*")
            .OrderBy(p => new FileInfo(p).LastWriteTimeUtc)
            .ToArray();
        Assert.Equal(3, badFiles.Length);
        Assert.Equal("{ broken #3", File.ReadAllText(badFiles[0]));
        Assert.Equal("{ broken #4", File.ReadAllText(badFiles[1]));
        Assert.Equal("{ broken #5", File.ReadAllText(badFiles[2]));
    }

    [Fact]
    public void QuickEnabled_DefaultsToFalseForNewConfig_PreservesExistingTrue()
    {
        // config が無い状態から Load → quick_enabled = false
        var fresh = new ConfigStore(_paths);
        fresh.Load();
        Assert.False(fresh.GetBool("quick_enabled", true));
        Assert.False(fresh.Settings.QuickEnabled);

        // 既存の true は true のまま
        var existing = LoadWith("""{"quick_enabled":true}""");
        Assert.True(existing.GetBool("quick_enabled", false));
        Assert.True(existing.Settings.QuickEnabled);
    }

    [Fact]
    public void J27_旧設定の内蔵OCRモデル名と文字起こし設定を新しいぶん投げ方へ移行する()
    {
        var store = LoadWith("""
            {
              "presets_v5": true,
              "prompt_active": "翻訳",
              "prompts": [
                {"name": "翻訳", "text": "t", "model": "plamo", "use_global": false, "settings": {"ocr_split_mode": true}},
                {"name": "OCR", "text": "Text Recognition:", "model": "PP-OCRv6（内蔵）", "use_global": false, "settings": {"ocr_split_mode": false}},
                {"name": "高速OCR", "text": "Text Recognition:", "model": "PP-OCRv6 small（内蔵）", "use_global": false, "settings": {"ocr_split_mode": false}},
                {"name": "数式", "text": "Formula Recognition:", "model": "glm-ocr", "use_global": false, "settings": {"ocr_split_mode": false}}
              ]
            }
            """);
        var ps = store.Root["prompts"]!.AsArray().OfType<JsonObject>().ToDictionary(p => p["name"]!.GetValue<string>());
        // 旧モデル名が PP-OCRv6（内蔵）だった OCR は throw_mode=builtin_ocr + medium になり、model は空
        Assert.Equal("", ps["OCR"]["model"]!.GetValue<string>());
        Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, ps["OCR"]["settings"]!["throw_mode"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeMedium, ps["OCR"]["settings"]!["builtin_ocr_size"]!.GetValue<string>());
        // 旧モデル名が small だったプリセットは small を引き継ぐ
        Assert.Equal("", ps["高速OCR"]["model"]!.GetValue<string>());
        Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, ps["高速OCR"]["settings"]!["throw_mode"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeSmall, ps["高速OCR"]["settings"]!["builtin_ocr_size"]!.GetValue<string>());
        // 旧 ocr_split_mode=true の翻訳は旧 OCR プリセットが内蔵 medium だったことを引き継ぐ
        Assert.Equal(ConfigStore.ThrowModeOcr, ps["翻訳"]["settings"]!["throw_mode"]!.GetValue<string>());
        Assert.Equal(ConfigStore.OcrEngineBuiltin, ps["翻訳"]["settings"]!["ocr_engine"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeMedium, ps["翻訳"]["settings"]!["builtin_ocr_size"]!.GetValue<string>());
        // 画像そのままだった数式は image
        Assert.Equal(ConfigStore.ThrowModeImage, ps["数式"]["settings"]!["throw_mode"]!.GetValue<string>());
    }

    [Fact]
    public void J27_旧OCRプリセットがOllamaVLMだった場合は翻訳のocr_engineをvlmへ移行しEffectiveConfigに載せる()
    {
        var store = LoadWith("""
            {
              "presets_v5": true,
              "prompt_active": "翻訳",
              "prompts": [
                {"name": "翻訳", "text": "t", "model": "plamo", "use_global": false, "settings": {"ocr_split_mode": true}},
                {"name": "OCR", "text": "Read all:", "model": "glm-ocr:latest", "use_global": false, "settings": {"ocr_split_mode": false}}
              ]
            }
            """);
        var tr = store.Root["prompts"]!.AsArray().OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "翻訳")["settings"]!.AsObject();
        Assert.Equal(ConfigStore.ThrowModeOcr, tr["throw_mode"]!.GetValue<string>());
        Assert.Equal(ConfigStore.OcrEngineVlm, tr["ocr_engine"]!.GetValue<string>());
        Assert.Equal("glm-ocr:latest", tr["ocr_vlm_model"]!.GetValue<string>());
        Assert.Equal("Read all:", tr["ocr_vlm_prompt"]!.GetValue<string>());

        var eff = store.EffectiveConfig();
        var stage = eff[ConfigStore.OcrStageKey]!.AsObject();
        Assert.Equal("glm-ocr:latest", stage["model"]!.GetValue<string>());
        Assert.Equal("Read all:", stage["prompt"]!.GetValue<string>());
    }

    [Fact]
    public void J30_allow_capture_overlayは既定falseで保存復元できる()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        Assert.False(store.Settings.AllowCaptureOverlay);
        store.Settings.AllowCaptureOverlay = true;
        store.Save();

        var again = new ConfigStore(_paths);
        again.Load();
        Assert.True(again.Settings.AllowCaptureOverlay);
    }

    [Fact]
    public void J119_既定プリセットの翻訳とOCRは位置の反映ONで数式と表はOFF()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        var arr = store.Root["prompts"]!.AsArray().OfType<JsonObject>().ToList();
        JsonObject? SettingsOf(string name) => arr.Single(p => p["name"]!.GetValue<string>() == name)["settings"] as JsonObject;
        bool LayoutOf(string name) => SettingsOf(name)?["builtin_ocr_layout"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.True;

        Assert.True(LayoutOf("翻訳"));
        Assert.True(LayoutOf("OCR"));
        Assert.False(LayoutOf("数式"));
        Assert.False(LayoutOf("表"));
        Assert.False(store.Root["builtin_ocr_layout"]!.GetValue<bool>());
    }

    // ---- 前処理（VLM）の生成の値・VLM で読んでそのまま出すぶん投げ方 ----

    [Fact]
    public void J122_前の版の設定は前処理の生成の値にAI処理の値を写して始める()
    {
        var store = LoadWith("""
            {"ollama_num_ctx":2048,"ollama_temperature":0.3,"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],
             "prompt_active":"自作","prompts":[
                {"name":"自作","text":"mine","use_global":false,"model":"m:1","settings":{"ollama_num_ctx":4096,"ollama_keep_alive_min":5}}]}
            """);
        Assert.Equal(2048, store.GetInt("ocr_vlm_num_ctx", 0));
        var st = store.Root["prompts"]!.AsArray().OfType<JsonObject>().First(p => p["name"]!.GetValue<string>() == "自作")["settings"]!.AsObject();
        Assert.Equal(4096, st["ocr_vlm_num_ctx"]!.GetValue<int>());
        Assert.Equal(5, st["ocr_vlm_keep_alive_min"]!.GetValue<int>());
    }

    [Fact]
    public void J122_文字起こしVLMの1回目は前処理の生成の値を使い_AI処理は自分の値のまま()
    {
        var store = LoadWith("""
            {"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],"prompt_active":"自作","prompts":[
                {"name":"自作","text":"t","use_global":false,"model":"ai:1","settings":{"throw_mode":"ocr","ocr_engine":"vlm",
                 "ocr_vlm_model":"vlm:1","ollama_num_ctx":4096,"ocr_vlm_num_ctx":1024,"ocr_vlm_temperature":0.5}}]}
            """);
        var eff = store.EffectiveConfig();
        Assert.Equal(4096, eff["ollama_num_ctx"]!.GetValue<int>());
        var ocr = eff[ConfigStore.OcrStageKey]!.AsObject();
        Assert.Equal("vlm:1", ocr["model"]!.GetValue<string>());
        Assert.Equal(1024, ocr["ollama_num_ctx"]!.GetValue<int>());
        Assert.Equal(0.5, ocr["ollama_temperature"]!.GetValue<double>());
    }

    [Fact]
    public void J122_VLMで読んでそのまま出す時は_モデルと指示と生成の値がVLMの物になる()
    {
        var store = LoadWith("""
            {"presets_v5":true,"presets_offered":["翻訳","OCR","数式","表"],"prompt_active":"自作","prompts":[
                {"name":"自作","text":"t","use_global":false,"model":"ai:1","settings":{"throw_mode":"vlm_ocr",
                 "ocr_vlm_model":"vlm:1","ocr_vlm_prompt":"OCR:","ollama_num_ctx":4096,"ocr_vlm_num_ctx":1024}}]}
            """);
        var eff = store.EffectiveConfig();
        Assert.Equal(ConfigStore.ThrowModeVlmOcr, TranslatePipeline.ThrowModeOf(eff));
        Assert.Equal("vlm:1", eff["model"]!.GetValue<string>());
        Assert.Equal("OCR:", eff["prompt"]!.GetValue<string>());
        Assert.Equal(1024, eff["ollama_num_ctx"]!.GetValue<int>());
    }
}

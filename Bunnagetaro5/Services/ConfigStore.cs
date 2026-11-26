using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>
/// config.json の読み書きと、古い既定値の引っ越し（Python版 tw_config.py 相当）。
///
/// Python版は「既知キーだけメモリへ」という方針だったが、WPF版は指示書5.2の
/// 「知らないキーも消さずに残す」を全面採用し、ファイル全体を JsonObject として
/// 保持する（trig_* のマージ再読込も不要になる）。
/// prompts（ぶん投げ文の一覧）と prompt_active（選んでいる名前）と prompt は
/// 常に結び直す（Python版と同じく、ぶん投げる側は prompt だけ読めばよい）。
/// </summary>
public sealed partial class ConfigStore
{
    public Paths Paths { get; }

    /// <summary>config.json の全体（未知のキーも含む）。直接触るのはここ。</summary>
    public JsonObject Root { get; private set; }

    /// <summary>Loadが実施した「既定値の引っ越し」の お知らせ（設定窓に一度出す）。</summary>
    public List<string> Migrations { get; } = new();

    /// <summary>起動時にconfig.jsonを読むことができたか（初回で無ければ true）。</summary>
    public bool HadSaved { get; private set; }

    /// <summary>config.json が壊れていて既定で起動した時の状態欄向け案内（正常なら null）。</summary>
    public string? LoadWarning { get; private set; }

    public const string DefaultBaseUrl = "http://localhost:11434";
    /// <summary>グローバル設定の既定モデル。空（人の判断 2026-11-16: 既定のモデルはプリセットごとに持たせ、グローバルは空でよい）</summary>
    public const string DefaultModel = "";
    public const string DefaultPromptName = "翻訳";

    /// <summary>既定プリセットのモデル（翻訳・OCR・数式・表）</summary>
    public const string TranslatePresetModel = "hf.co/pfnet/plamo-3-translate-0.8b-GGUF:Q4_K_M";
    /// <summary>前の既定の OCR のモデル。旧名のプリセットの書き換え（presets_v5）や VLM 文字起こしの既定に使う</summary>
    public const string OcrPresetModel = "glm-ocr:latest";
    /// <summary>新しく作る設定の「OCR」プリセットのモデル（修正J27 で内蔵OCRはぶん投げ方へ移したため空）</summary>
    public const string OcrPresetDefaultModel = "";
    public const string FormulaPresetModel = "glm-ocr";
    public const string TablePresetModel = "hf.co/PaddlePaddle/PaddleOCR-VL-1.6-GGUF";
    public const string OcrPresetText = "Text Recognition:";
    public const string FormulaPresetText = "Formula Recognition:";
    public const string TablePresetText = "Table Recognition:";

    /// <summary>ぶん投げ方（修正J27: 文字起こし / 画像そのまま / 内蔵OCRのみ）</summary>
    public const string ThrowModeOcr = "ocr";
    public const string ThrowModeImage = "image";
    public const string ThrowModeBuiltinOcr = "builtin_ocr";
    /// <summary>文字起こしの方法（修正J27: 内蔵OCR / VLM）</summary>
    public const string OcrEngineBuiltin = "builtin";
    public const string OcrEngineVlm = "vlm";
    /// <summary>内蔵OCRモデルの既定（修正J27: 人の判断で small）</summary>
    public const string DefaultBuiltinOcrSize = BuiltinOcr.SizeSmall;

    /// <summary>プリセットが「グローバル設定を使う」を外した時に専用で持つキー。
    /// グローバルにも同じキーがある物だけ（tw_config.PRESET_KEYS）。
    /// モデルは入れない: モデルはいつもプリセットごとに持つ（修正J7。プリセットの "model"）</summary>
    public static readonly string[] PresetKeys =
    {
        "throw_mode", "ocr_split_mode", "ocr_engine", "builtin_ocr_size", "builtin_ocr_layout",
        "ocr_vlm_model", "ocr_vlm_prompt", "clip_answer",
        "ollama_num_ctx", "ollama_num_predict", "ollama_temperature",
        "ollama_keep_alive_min", "ollama_think",
        "result_place", "convert_otsl_table", "convert_html_table",
        "convert_latex_math", "show_follow_row",
    };

    /// <summary>v4.5の既定プリセット設定（表・数式・文字の形は画像そのまま＋コピー）。</summary>
    private static JsonObject ImagePresetSettings() => new()
    {
        ["throw_mode"] = ThrowModeImage,
        ["ocr_split_mode"] = false,
        ["clip_answer"] = true,
    };

    public const string DefaultPrompt =
        "[system]\n[plamo-translate]\ntarget_lang=ja";

    /// <summary>画像を読むモデル向けの多言語→日本語の翻訳文（v3.3〜v5.0 の既定）。
    /// 「翻訳」の既定は PLaMo（専用のモデルと文字起こし付き）になったが、古い config の
    /// グローバル設定のモデル（画像を読むモデル）へ送る文はこちら。PLaMo 用の3行を
    /// 画像のモデルへ送ると訳さないため、引っ越しではこちらを使う</summary>
    public const string DefaultImagePrompt =
        "Translate every readable line of text in the image into natural Japanese, " +
        "whatever language it is written in. Do not copy the original text into the " +
        "answer. Preserve proper names and short UI labels only when they should " +
        "remain unchanged. If the visible text is already Japanese, reproduce it " +
        "unchanged. Translate only text that is visibly present in " +
        "this image. Do not infer missing content, reconstruct an article, or add facts. Output " +
        "only the Japanese translation of the visible text; do not explain or summarize.";

    /// <summary>v3.3で既定文を「あらゆる言語→日本語」へ広げた。旧既定文がそのまま
    /// 入っている人だけを差し替えるための比較用（tw_ollama... でなく translator_window._OLD_PROMPT_EN）。</summary>
    public const string OldPromptEn =
        "Translate every readable English sentence in the image from English into natural Japanese. " +
        "Do not copy English sentences into the answer. Preserve proper names and short UI labels " +
        "only when they should remain unchanged. Translate only text that is visibly present in " +
        "this image. Do not infer missing content, reconstruct an article, or add facts. Output " +
        "only the Japanese translation of the visible text; do not explain or summarize.";

    /// <summary>LaTeX プリセットの既定文（E2: $$ ... $$ で囲ませて数式として表示する）</summary>
    public const string DefaultLatexPrompt =
        "Convert the mathematical expressions in the image into LaTeX. Reproduce them " +
        "exactly as shown; do not solve, simplify, or explain them. Write each equation " +
        "on its own line wrapped in $$ ... $$. Use LaTeX commands such as \\nabla, \\cdot, " +
        "\\times, \\partial, \\frac, \\rho, \\mathbf. Never use Unicode math symbols like " +
        "∇ · × ∂ ρ. Output nothing else.";

    /// <summary>LaTeX プリセットの旧既定文（E2 の引っ越し比較用）</summary>
    public const string OldLatexPrompt =
        "Convert the mathematical expressions in the image into LaTeX. Reproduce them " +
        "exactly as shown; do not solve, simplify, or explain them. Put each separate " +
        "equation on its own line. Output only the LaTeX code, without $ or \\[ \\] " +
        "delimiters and without code fences.";

    private static readonly Regex SchemeRe = new("^[A-Za-z][A-Za-z0-9+.\\-]*://", RegexOptions.Compiled);
    private static readonly object SaveGate = new();

    /// <summary>型のある設定の窓口（変更通知つき。REFACTOR_ROUND1 1.1）。使う側はこれを読み書きする</summary>
    public AppSettings Settings { get; }

    public ConfigStore(Paths paths)
    {
        Paths = paths;
        Root = DefaultConfig();
        Settings = new AppSettings(this);
    }

    // ---------------------------------------------------------------- 既定値

    /// <summary>DEFAULT_CONFIG（translator_window.py）。キーの並びもPython版と同じ順。</summary>
    public static JsonObject DefaultConfig()
    {
        var cfg = new JsonObject
        {
            ["base_url"] = DefaultBaseUrl,
            ["base_url_list"] = new JsonArray { DefaultBaseUrl },
            ["model"] = DefaultModel,
            ["model_list"] = new JsonArray(),
            ["mode"] = "manual",
            ["main_topmost"] = false,
            ["capture_width"] = 640,
            ["capture_height"] = 400,
            ["capture_x"] = null,
            ["capture_y"] = null,
            ["main_geometry"] = "",
            ["main_zoomed"] = false,
            ["strip_width"] = 520,
            ["strip_height"] = 140,
            ["chrome_offset_x"] = null,
            ["chrome_offset_y"] = null,
            ["chrome_linked"] = true,
            ["chrome_passthru"] = false,
            ["strip_offset_x"] = 0,
            ["prompt"] = DefaultPrompt,
            ["prompt_active"] = DefaultPromptName,
            ["prompts"] = DefaultPresets(),
            ["save_shots"] = false,
            ["shots_dir"] = "",
            ["overlay_bg"] = "#202020",
            ["overlay_fg"] = "#f5f5f5",
            ["overlay_alpha"] = PanelStyle.DefaultAlpha,
            // 修正J101: 外窓（枠の外に出す解答欄）の色と不透明度。枠の中とは別（人の判断 2026-11-25）
            ["strip_bg"] = PanelStyle.DefaultBg,
            ["strip_fg"] = PanelStyle.DefaultFg,
            ["strip_alpha"] = PanelStyle.DefaultStripAlpha,
            ["overlay_font_family"] = "",
            ["overlay_font_size"] = 12,
            ["result_place"] = "inside",
            ["convert_otsl_table"] = true,
            ["convert_html_table"] = true,
            ["convert_latex_math"] = true,
            ["show_follow_row"] = false,
            ["latex_copy"] = "plain",
            ["answer_para_spacing"] = PanelStyle.DefaultParaSpacing,
            ["answer_line_spacing"] = PanelStyle.DefaultLineSpacing,
            ["capture_border_color"] = "#0067C0",
            ["capture_border_alpha"] = 0.7,
            ["capture_border_px"] = 4,
            ["throw_mode"] = ThrowModeOcr,
            ["ocr_split_mode"] = true,
            ["ocr_engine"] = OcrEngineBuiltin,
            ["builtin_ocr_size"] = DefaultBuiltinOcrSize,
            ["builtin_ocr_layout"] = false,   // 修正J96: 内蔵 OCR の時、読み取った位置に並べる（OCR_LAYOUT_ROUND1）
            // 修正J100: 位置の反映の細かい値（全体で1つ。設定ページの「位置の反映（内蔵OCR）」）
            ["layout_text_bg"] = true,   // 修正J103・J107: 区画の所だけに背景（マスク）
            ["layout_score_thresh"] = LayoutParams.Default.ScoreThresh,   // 修正J116: 区画判定のしきい値
            ["layout_batch_chars"] = LayoutParams.Default.BatchChars,
            ["layout_label_words"] = LayoutParams.Default.LabelWords,
            ["layout_label_chars"] = LayoutParams.Default.LabelChars,
            ["ocr_vlm_model"] = OcrPresetModel,
            ["ocr_vlm_prompt"] = OcrPresetText,
            ["clip_answer"] = false,
            ["trig_poll_interval"] = TrigParams.DefaultInterval,
            ["trig_change_ratio"] = 0.01,
            ["trig_stable_ratio"] = 0.005,
            ["trig_stable_samples"] = 2,
            ["trig_cooldown"] = TrigParams.DefaultCooldown,
            ["quick_enabled"] = false,
            ["quick_button"] = "wheel",
            ["quick_key"] = "",
            ["quick_min_w"] = 30,
            ["quick_min_h"] = 30,
            ["quick_chatter_ms"] = 30,
            ["hotkey"] = "",
            ["snip_hotkey"] = "",
            ["presets_v5"] = true,
            ["presets_offered"] = new JsonArray("翻訳", "OCR", "数式", "表"),
            // OLLAMA_DEFAULTS（tw_ollama.py。DEFAULT_CONFIG.update で後ろに足される順）
            ["ollama_num_ctx"] = 8192,
            ["ollama_num_predict"] = 4096,
            ["ollama_temperature"] = 0.0,
            ["ollama_keep_alive_min"] = 30,
            ["ollama_think"] = "auto",
            ["allow_capture_overlay"] = false,
            ["close_to_tray"] = false,
            ["start_with_windows"] = false,
            ["tray_hint_shown"] = false,
        };
        return cfg;
    }

    /// <summary>既定の「翻訳」プリセット（PLaMo・文字起こし付き）の新しい写し。一覧が空の時の穴埋め用</summary>
    public static JsonObject DefaultTranslatePreset() => (JsonObject)DefaultPresets()[0]!.DeepClone();

    /// <summary>DEFAULT_PRESETS（翻訳・OCR・数式・表）。毎回新しい写しを返す。</summary>
    public static JsonArray DefaultPresets() => new()
    {
        new JsonObject
        {
            ["name"] = DefaultPromptName,
            ["use_global"] = false,
            ["model"] = TranslatePresetModel,
            ["settings"] = new JsonObject
            {
                ["throw_mode"] = ThrowModeOcr,
                ["ocr_split_mode"] = true,
                ["ocr_engine"] = OcrEngineBuiltin,
                ["builtin_ocr_size"] = DefaultBuiltinOcrSize,
                ["builtin_ocr_layout"] = true,   // 修正J119: 既定の「翻訳」は位置の反映 ON
                ["ocr_vlm_model"] = OcrPresetModel,
                ["ocr_vlm_prompt"] = OcrPresetText,
                ["clip_answer"] = false,
            },
            ["text"] = DefaultPrompt,
        },
        new JsonObject
        {
            ["name"] = "OCR", ["use_global"] = false, ["model"] = OcrPresetDefaultModel,
            ["settings"] = new JsonObject
            {
                ["throw_mode"] = ThrowModeBuiltinOcr,
                ["ocr_split_mode"] = false,
                ["builtin_ocr_size"] = DefaultBuiltinOcrSize,
                ["builtin_ocr_layout"] = true,   // 修正J119: 既定の「OCR」は位置の反映 ON
                ["clip_answer"] = false,
            },
            ["text"] = OcrPresetText,
        },
        new JsonObject
        {
            ["name"] = "数式", ["use_global"] = false, ["model"] = FormulaPresetModel, ["settings"] = ImagePresetSettings(),
            ["text"] = FormulaPresetText,
        },
        new JsonObject
        {
            ["name"] = "表", ["use_global"] = false, ["model"] = TablePresetModel, ["settings"] = ImagePresetSettings(),
            ["text"] = TablePresetText,
        },
    };

    // ---------------------------------------------------------------- 読み込み

    /// <summary>config.json を読む（無ければ既定値）。
    /// 引っ越しを実施したら、Python版のAppと同じくここで一度ディスクへ書き戻す。</summary>
    public void Load()
    {
        Migrations.Clear();
        LoadWarning = null;
        try { Directory.CreateDirectory(Paths.Root); } catch { }

        Root = DefaultConfig();
        HadSaved = false;
        bool loadFailed = false;
        if (File.Exists(Paths.ConfigJson))
        {
            try
            {
                // utf-8-sig相当: ReadAllTextがBOMを処理してくれる
                JsonNode? node;
                using (var fs = new FileStream(Paths.ConfigJson, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    node = JsonNode.Parse(fs);
                if (node is JsonObject saved)
                {
                    HadSaved = true;
                    Root = saved;
                    // 既定値の引っ越しとプリセットの追加は「読めたファイルの元の値」で
                    // 判断する（既定で埋めた値で判断しない）。埋めるのはその後
                    ApplyDefaultMigrations(saved);
                    OfferDefaultPresets(saved);
                    FillMissingKeysWithDefaults(saved);
                }
                else
                {
                    loadFailed = true;
                    AppLog.Error("config load failed: root is not JsonObject");
                }
            }
            catch (Exception e)
            {
                loadFailed = true;
                AppLog.Error("config load failed", e);
                Root = DefaultConfig();
            }

            if (loadFailed)
            {
                Root = DefaultConfig();
                string? badName = BackupCorruptConfig();
                string msg = badName is not null
                    ? $"設定ファイルが読めなかったので既定で起動しました。元は {badName} に残してあります"
                    : "設定ファイルが読めなかったので既定で起動しました";
                LoadWarning = msg;
                AppLog.Warning(msg);
            }
        }

        // 手編集configのbase_urlも読み込み時点で正規化する。解析不能ならDEFAULTへ退避
        // （不正値で走り続けて後で意味不明に失敗するよりマシ。tw_config.load_config）
        try
        {
            string nb = NormBase(GetStr("base_url"));
            if (SafeHost(nb) is null)
            {
                AppLog.Warning("configのbase_urlが解析不能のためDEFAULTに退避: " + GetStr("base_url"));
                nb = DefaultBaseUrl;
            }
            Root["base_url"] = nb;
        }
        catch
        {
            Root["base_url"] = DefaultBaseUrl;
        }

        NormalizeBaseUrlList();
        NormalizePrompts(HadSaved);

        // 引っ越しを実施したら一度ディスクへ書き戻す（Python版はApp側がやっていたが、
        // 忘れると直したつもりが次回また引っ越しになるのでここでやる）
        // ただし壊れた config.json を読んで既定起動した回は、起動直後の Save() をしない（G1）
        if ((!HadSaved && !loadFailed) || Migrations.Count > 0)
            Save();
        Settings.RaiseAll();
    }

    private const int MaxBadBackups = 3;

    /// <summary>接続先URL履歴を正規化・重複排除し、既定URLと現在のbase_urlを必ず含める。</summary>
    private void NormalizeBaseUrlList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new JsonArray();
        void AddIfValid(string raw)
        {
            string norm = NormBase(raw);
            if (SafeHost(norm) is null) return;
            if (seen.Add(norm)) list.Add(JsonValue.Create(norm));
        }

        if (Root["base_url_list"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                string s = NodeToStr(n);
                if (s.Length > 0) AddIfValid(s);
            }
        }
        AddIfValid(DefaultBaseUrl);
        AddIfValid(GetStr("base_url", DefaultBaseUrl));
        Root["base_url_list"] = list;
    }

    /// <summary>壊れた config.json を config.json.bad-&lt;日時&gt; へ写し、最新3つまで残す。</summary>
    private string? BackupCorruptConfig()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string basePath = Paths.ConfigJson + ".bad-" + stamp;
            string badPath = basePath;
            for (int n = 1; File.Exists(badPath); n++)
                badPath = $"{basePath}-{n}";
            File.Copy(Paths.ConfigJson, badPath, overwrite: false);

            var old = Directory.GetFiles(Paths.Root, "config.json.bad-*")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(MaxBadBackups);
            foreach (var fi in old)
            {
                try { fi.Delete(); } catch { }
            }
            return Path.GetFileName(badPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("config bad backup failed", ex);
            return null;
        }
    }

    /// <summary>知らないキーは残し、既定にあって無いキーだけ既定値で埋める。</summary>
    private static void FillMissingKeysWithDefaults(JsonObject saved)
    {
        foreach (var kv in DefaultConfig())
            if (!saved.ContainsKey(kv.Key))
                saved[kv.Key] = kv.Value?.DeepClone();
    }

    /// <summary>プリセットのモデル（修正J7: モデルはいつもプリセットごと）。</summary>
    public static string PresetModel(JsonObject? p) => p is null ? "" : NodeToStr(p["model"]).Trim();

    /// <summary>読み込み時のプリセットのモデル。持っていればそれ。無い古い config では、
    /// 今まで実際に使っていたモデルを写す:「グローバル設定を使う」を外したプリセットは
    /// settings の model、使うプリセットはグローバルの model（修正J7）</summary>
    private static string PresetModelFrom(JsonObject cfg, JsonObject p, bool useGlobal)
    {
        if (p.ContainsKey("model")) return NodeToStr(p["model"]).Trim();
        if (!useGlobal && p["settings"] is JsonObject st && NodeToStr(st["model"]).Trim() is { Length: > 0 } sm) return sm;
        return NodeToStr(cfg["model"]).Trim();
    }

    /// <summary>プリセット専用の設定を PresetKeys だけの形にそろえる。無いキーは
    /// グローバル（cfg）の今の値で埋める（tw_config.preset_settings_from）。</summary>
    public static JsonObject PresetSettingsFrom(JsonObject cfg, JsonNode? raw)
    {
        var defaults = DefaultConfig();
        var outObj = new JsonObject();
        foreach (string k in PresetKeys)
        {
            JsonNode? v = null;
            if (raw is JsonObject r && r.TryGetPropertyValue(k, out var rv) && rv is not null) v = rv;
            else if (cfg.TryGetPropertyValue(k, out var cv) && cv is not null) v = cv;
            else v = defaults[k];
            outObj[k] = v?.DeepClone();
        }
        return outObj;
    }

    // ---------------------------------------------------------------- 保存

    /// <summary>原子保存: config.&lt;pid&gt;.tmp へ全量書いて Flush(true) → 入れ替え。
    /// 入れ替え前は config.json.bak に残す（Python版の tmp.replace + 指示書の .bak）。</summary>
    public void Save()
    {
        lock (SaveGate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                string tmp = Paths.ConfigJson + "." + Environment.ProcessId + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }))
                {
                    Root.WriteTo(w);
                    w.Flush();
                    fs.Flush(true); // fsync相当
                }
                if (File.Exists(Paths.ConfigJson))
                    File.Replace(tmp, Paths.ConfigJson, Paths.ConfigBak);
                else
                    File.Move(tmp, Paths.ConfigJson);
            }
            catch (Exception e)
            {
                AppLog.Error("config save failed", e);
            }
        }
    }

    // ---------------------------------------------------------------- ぶん投げに使う設定

    /// <summary>今使っているプリセット（prompts の1件）。無ければ null。</summary>
    public JsonObject? ActivePreset()
    {
        string name = NodeToStr(Root["prompt_active"]);
        if (Root["prompts"] is not JsonArray arr) return null;
        foreach (var n in arr)
            if (n is JsonObject p && NodeToStr(p["name"]) == name)
                return p;
        return null;
    }

    /// <summary>ぶん投げに使う設定。使っているプリセットが「グローバル設定を使う」を
    /// 外していれば、そのプリセットの設定をグローバルの上へ重ねる。
    /// 読むだけの写しで、ディスクへ書く物ではない（tw_config.effective_config）。</summary>
    public JsonObject EffectiveConfig()
    {
        var p = ActivePreset();
        var outObj = ConfigFor(p, withPrompt: false);   // 使っている物のぶん投げ文は Root["prompt"] のまま
        // 修正J27: 文字起こし（VLM）の設定は、プリセット自身の ocr_vlm_model / ocr_vlm_prompt から組み立てる
        if (TranslatePipeline.ThrowModeOf(outObj) == ThrowModeOcr && !TranslatePipeline.UsesBuiltinOcrForTranscribe(outObj))
        {
            string vlmModel = NodeToStr(outObj["ocr_vlm_model"]).Trim();
            if (vlmModel.Length > 0)
            {
                var vlm = (JsonObject)outObj.DeepClone();
                vlm["model"] = vlmModel;
                string vp = NodeToStr(outObj["ocr_vlm_prompt"]).Trim();
                vlm["prompt"] = vp.Length > 0 ? vp : OcrPresetText;
                outObj[OcrStageKey] = vlm;
            }
        }
        return outObj;
    }

    /// <summary>文字起こしに使うプリセットの名前（修正I6）</summary>
    public const string OcrPresetName = "OCR";
    /// <summary>EffectiveConfig の写しの中で、文字起こし用の設定を入れるキー（ディスクへは書かない）</summary>
    public const string OcrStageKey = "_ocr_stage";

    private JsonObject? PresetNamed(string name)
    {
        if (Root["prompts"] is not JsonArray arr) return null;
        foreach (var n in arr)
            if (n is JsonObject p && NodeToStr(p["name"]) == name)
                return p;
        return null;
    }

    /// <summary>いま有効なプリセット（use_global=false のときは専用設定、それ以外はグローバル）から 1 キー読む</summary>
    public JsonNode? EffectiveNode(string key)
    {
        string active = NodeToStr(Root["prompt_active"]);
        var p = PresetNamed(active);
        if (p is not null && key == "model") return JsonValue.Create(PresetModel(p));
        if (p is not null && p["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False &&
            p["settings"] is JsonObject st && st.TryGetPropertyValue(key, out var pv) && pv is not null)
            return pv;
        return Root.TryGetPropertyValue(key, out var rv) ? rv : null;
    }

    /// <summary>いま有効なプリセットを反映した表示場所（"inside" / "outside"）</summary>
    public string EffectiveResultPlace() =>
        NodeToStr(EffectiveNode("result_place")) == "outside" ? "outside" : "inside";

    /// <summary>いま有効なプリセットを反映した真偽値</summary>
    public bool EffectiveBool(string key, bool def = true) =>
        EffectiveNode(key) is JsonValue jv && jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? jv.GetValueKind() == JsonValueKind.True
            : def;

    /// <summary>プリセット1件をぶん投げに使う設定にする（グローバルの写し＋専用の設定＋そのぶん投げ文）。</summary>
    private JsonObject ConfigFor(JsonObject? p, bool withPrompt)
    {
        var outObj = (JsonObject)Root.DeepClone();
        if (p is null) return outObj;
        if (p["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False &&
            p["settings"] is JsonObject st)
        {
            foreach (var kv in st)
                if (PresetKeys.Contains(kv.Key))
                    outObj[kv.Key] = kv.Value?.DeepClone();
        }
        // モデルは「グローバル設定を使う」に関係なく、いつもそのプリセットの物（修正J7）
        outObj["model"] = PresetModel(p);
        if (withPrompt) outObj["prompt"] = p["text"]?.DeepClone();
        return outObj;
    }

    /// <summary>prompts を C#のオブジェクトとして眺める（設定窓・履歴用）。</summary>
    public List<Models.Preset> Presets()
    {
        var list = new List<Models.Preset>();
        if (Root["prompts"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject p) continue;
            var pre = new Models.Preset
            {
                Name = NodeToStr(p["name"]),
                Text = NodeToStr(p["text"]),
                UseGlobal = !(p["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False),
                Model = PresetModel(p),
            };
            if (p["settings"] is JsonObject st)
                foreach (var kv in st) pre.Settings[kv.Key] = kv.Value?.DeepClone();
            list.Add(pre);
        }
        return list;
    }

    // ---------------------------------------------------------------- 型付きアクセサ

    public JsonNode? GetRaw(string key) => Root.TryGetPropertyValue(key, out var v) ? v : null;
    private static JsonNode? GetRaw(JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) ? v : null;

    public string GetStr(string key, string def = "")
    {
        if (!Root.TryGetPropertyValue(key, out var v) || v is null) return def;
        string s = NodeToStr(v);
        return s;
    }

    public bool GetBool(string key, bool def)
    {
        if (GetRaw(key) is JsonValue v && v.TryGetValue(out bool b)) return b;
        return def;
    }

    public int GetInt(string key, int def)
    {
        if (TryNum(Root, key, out double d)) return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return def;
    }

    public double GetDouble(string key, double def)
    {
        if (TryNum(Root, key, out double d)) return d;
        return def;
    }

    /// <summary>capture_x 等の「数かnull」。キー無し・nullなら null。</summary>
    public int? GetIntOrNull(string key)
    {
        var v = GetRaw(key);
        if (v is null) return null;
        if (TryNum(Root, key, out double d)) return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return null;
    }

    public void Set(string key, JsonNode? value) => Root[key] = value?.DeepClone();
    public void SetStr(string key, string value) => Root[key] = value;
    public void SetBool(string key, bool value) => Root[key] = value;
    public void SetInt(string key, int value) => Root[key] = value;
    public void SetDouble(string key, double value) => Root[key] = value;
    public void SetNull(string key) => Root[key] = null;

    private static bool TryNum(JsonObject o, string key, out double value)
    {
        value = 0;
        var v = GetRaw(o, key);
        // 解析直後のJsonValueと、C#から作ったJsonValueで内部表現が違うため、
        // 種類だけ見て文字列経由で数値にする（"120"・120・120.5・数字文字列を拾う）
        if (v is not JsonValue jv) return false;
        var kind = jv.GetValueKind();
        if (kind != JsonValueKind.Number && kind != JsonValueKind.String) return false;
        return double.TryParse(jv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>JsonNode → 文字列（Pythonの str() 相当）。nullは空文字。</summary>
    public static string NodeToStr(JsonNode? v)
    {
        if (v is null) return "";
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue(out string? s)) return s;
            return jv.ToString();
        }
        return v.ToJsonString();
    }

    private static string CollapseWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();


    // ---------------------------------------------------------------- URL補助（tw_ollama）

    /// <summary>"localhost:11434"等のスキーム無しURLへ http:// を補う。
    /// 部分一致でなく先頭から本物のスキーム接頭辞を見る（tw_ollama._norm_base）。</summary>
    public static string NormBase(string u)
    {
        u = (u ?? "").Trim();
        if (!SchemeRe.IsMatch(u)) u = "http://" + u;
        return u;
    }

    /// <summary>URLから接続先ホストを安全に取る。不正（バックスラッシュ・
    /// netloc空・不正ポート等）は全部 null に揃える（tw_ollama._safe_host）。</summary>
    public static string? SafeHost(string? url)
    {
        try
        {
            string u = (url ?? "").Trim();
            if (u.Contains('\\')) return null;
            var uri = new Uri(u);
            if (string.IsNullOrEmpty(uri.Host)) return null;
            return uri.Host.ToLowerInvariant();
        }
        catch (UriFormatException) { return null; }
        catch (Exception) { return null; }
    }
}

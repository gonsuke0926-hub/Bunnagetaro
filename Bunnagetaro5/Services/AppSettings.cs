using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bunnagetaro5.Services;

/// <summary>
/// 設定の「型のある・変更が自動で伝わる」窓口。
/// ConfigStore の JsonObject の上にかぶせる形なので、保存されるJSONのキー名・形・知らないキーは今のまま。
/// 値が変わった時だけ PropertyChanged（名前は PascalCase。quick_enabled → QuickEnabled）を出す。
/// 使う側は自分で購読して反映し、書く側は書くだけ。string.Empty は「全部が変わった」（Load 直後）。
/// 文字列キーでの読み書きはこのクラスの中（と ReadNode/WriteNode）だけに閉じ込める。
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private readonly ConfigStore _cfg;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AppSettings(ConfigStore cfg) { _cfg = cfg; }

    /// <summary>JSONのキー名 → プロパティ名（quick_min_w → QuickMinW）</summary>
    public static string PropName(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        bool up = true;
        foreach (char c in key)
        {
            if (c == '_') { up = true; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        return sb.ToString();
    }

    private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    /// <summary>Load し直した時など、全部が変わったかもしれない時に呼ぶ</summary>
    internal void RaiseAll() => Raise(string.Empty);

    // ---- キー指定の読み書き（設定ページの汎用の欄・プリセット編集用。通知は同じ） ----
    public JsonNode? ReadNode(string key) => _cfg.GetRaw(key);

    /// <summary>書く。JSONとして同じ値なら何もしない・通知しない</summary>
    public void WriteNode(string key, JsonNode? value)
    {
        string? old = _cfg.GetRaw(key)?.ToJsonString();
        _cfg.Set(key, value);
        if (old != _cfg.GetRaw(key)?.ToJsonString()) Raise(PropName(key));
    }

    private string S(string key, string def = "") => _cfg.GetStr(key, def);
    private bool B(string key, bool def) => _cfg.GetBool(key, def);
    private int I(string key, int def) => _cfg.GetInt(key, def);
    private double D(string key, double def) => _cfg.GetDouble(key, def);
    private void W(string key, JsonNode? v) => WriteNode(key, v);

    // ---- 接続・モデル ----
    public string BaseUrl { get => S("base_url", ConfigStore.DefaultBaseUrl); set => W("base_url", value); }
    public JsonNode? BaseUrlList { get => _cfg.GetRaw("base_url_list"); set => W("base_url_list", value); }
    public string Model { get => S("model"); set => W("model", value); }
    public JsonNode? ModelList { get => _cfg.GetRaw("model_list"); set => W("model_list", value); }
    public int OllamaNumCtx { get => I("ollama_num_ctx", 8192); set => W("ollama_num_ctx", value); }
    public int OllamaNumPredict { get => I("ollama_num_predict", 4096); set => W("ollama_num_predict", value); }
    public double OllamaTemperature { get => D("ollama_temperature", 0.0); set => W("ollama_temperature", value); }
    public int OllamaKeepAliveMin { get => I("ollama_keep_alive_min", 30); set => W("ollama_keep_alive_min", value); }
    // 前処理（VLM）の生成の値
    public int OcrVlmNumCtx { get => I("ocr_vlm_num_ctx", 8192); set => W("ocr_vlm_num_ctx", value); }
    public int OcrVlmNumPredict { get => I("ocr_vlm_num_predict", 4096); set => W("ocr_vlm_num_predict", value); }
    public double OcrVlmTemperature { get => D("ocr_vlm_temperature", 0.0); set => W("ocr_vlm_temperature", value); }
    public int OcrVlmKeepAliveMin { get => I("ocr_vlm_keep_alive_min", 30); set => W("ocr_vlm_keep_alive_min", value); }
    public string OllamaThink { get => S("ollama_think", "auto"); set => W("ollama_think", value); }

    // ---- ぶん投げ・プリセット ----
    public string Mode { get => S("mode", "manual"); set => W("mode", value); }
    public bool OcrSplitMode { get => B("ocr_split_mode", true); set => W("ocr_split_mode", value); }
    // 「ぶん投げ方」まわりの設定
    public string ThrowMode { get => S("throw_mode", ConfigStore.ThrowModeOcr); set => W("throw_mode", value); }
    public string OcrEngine { get => S("ocr_engine", ConfigStore.OcrEngineBuiltin); set => W("ocr_engine", value); }
    public string BuiltinOcrSize { get => S("builtin_ocr_size", ConfigStore.DefaultBuiltinOcrSize); set => W("builtin_ocr_size", value); }
    /// <summary>内蔵 OCR の時、読み取った文・訳を元の位置に並べる（プリセットごと。既定 OFF）</summary>
    public bool BuiltinOcrLayout { get => B("builtin_ocr_layout", false); set => W("builtin_ocr_layout", value); }
    // 位置の反映の細かい値（読むのは LayoutParams.From。ここは型付きの入口だけ）
    public double LayoutScoreThresh { get => D("layout_score_thresh", LayoutParams.Default.ScoreThresh); set => W("layout_score_thresh", value); }
    public int LayoutBatchChars { get => I("layout_batch_chars", LayoutParams.Default.BatchChars); set => W("layout_batch_chars", value); }
    public int LayoutLabelWords { get => I("layout_label_words", LayoutParams.Default.LabelWords); set => W("layout_label_words", value); }
    public int LayoutLabelChars { get => I("layout_label_chars", LayoutParams.Default.LabelChars); set => W("layout_label_chars", value); }
    public string OcrVlmModel { get => S("ocr_vlm_model", ConfigStore.OcrPresetModel); set => W("ocr_vlm_model", value); }
    public string OcrVlmPrompt { get => S("ocr_vlm_prompt", ConfigStore.OcrPresetText); set => W("ocr_vlm_prompt", value); }
    public bool ClipAnswer { get => B("clip_answer", false); set => W("clip_answer", value); }
    public string Prompt { get => S("prompt"); set => W("prompt", value); }
    public string PromptActive { get => S("prompt_active"); set => W("prompt_active", value); }
    public JsonNode? Prompts { get => _cfg.GetRaw("prompts"); set => W("prompts", value); }
    /// <summary>プリセット入れ替えの引っ越しを済ませた印（1回だけ走らせる）</summary>
    public bool PresetsV5 { get => B("presets_v5", false); set => W("presets_v5", value); }
    public JsonNode? PresetsOffered { get => _cfg.GetRaw("presets_offered"); set => W("presets_offered", value); }
    public bool SaveShots { get => B("save_shots", false); set => W("save_shots", value); }
    public string ShotsDir { get => S("shots_dir"); set => W("shots_dir", value); }

    // ---- メイン窓 ----
    public bool MainTopmost { get => B("main_topmost", false); set => W("main_topmost", value); }
    public string MainGeometry { get => S("main_geometry"); set => W("main_geometry", value); }
    public bool MainZoomed { get => B("main_zoomed", false); set => W("main_zoomed", value); }

    // ---- キャプチャ窓・バー・帯 ----
    public int CaptureWidth { get => I("capture_width", 640); set => W("capture_width", value); }
    public int CaptureHeight { get => I("capture_height", 400); set => W("capture_height", value); }
    public int? CaptureX { get => _cfg.GetIntOrNull("capture_x"); set => W("capture_x", value); }
    public int? CaptureY { get => _cfg.GetIntOrNull("capture_y"); set => W("capture_y", value); }
    public int StripWidth { get => I("strip_width", 520); set => W("strip_width", value); }
    public int StripHeight { get => I("strip_height", 140); set => W("strip_height", value); }
    public int StripOffsetX { get => I("strip_offset_x", 0); set => W("strip_offset_x", value); }
    public int? ChromeOffsetX { get => _cfg.GetIntOrNull("chrome_offset_x"); set => W("chrome_offset_x", value); }
    public int? ChromeOffsetY { get => _cfg.GetIntOrNull("chrome_offset_y"); set => W("chrome_offset_y", value); }
    public bool ChromeLinked { get => B("chrome_linked", true); set => W("chrome_linked", value); }
    public bool ChromePassthru { get => B("chrome_passthru", false); set => W("chrome_passthru", value); }

    // ---- 解答欄の見た目 ----
    public string OverlayBg { get => S("overlay_bg", "#202020"); set => W("overlay_bg", value); }
    public string OverlayFg { get => S("overlay_fg", "#f5f5f5"); set => W("overlay_fg", value); }
    public double OverlayAlpha { get => D("overlay_alpha", PanelStyle.DefaultAlpha); set => W("overlay_alpha", value); }
    // 外窓の色と不透明度
    public string StripBg { get => S("strip_bg", PanelStyle.DefaultBg); set => W("strip_bg", value); }
    public string StripFg { get => S("strip_fg", PanelStyle.DefaultFg); set => W("strip_fg", value); }
    public double StripAlpha { get => D("strip_alpha", PanelStyle.DefaultStripAlpha); set => W("strip_alpha", value); }
    public string OverlayFontFamily { get => S("overlay_font_family"); set => W("overlay_font_family", value); }
    public int OverlayFontSize { get => I("overlay_font_size", 12); set => W("overlay_font_size", value); }
    public string ResultPlace { get => S("result_place", "inside"); set => W("result_place", value); }
    /// <summary>OTSL 形式の表（PaddleOCR-VL）を HTML の表に変換するか。表にするかは ConvertHtmlTable</summary>
    public bool ConvertOtslTable { get => B("convert_otsl_table", true); set => W("convert_otsl_table", value); }
    /// <summary>HTML &lt;table&gt; を WPF 表へ変換するか</summary>
    public bool ConvertHtmlTable { get => B("convert_html_table", true); set => W("convert_html_table", value); }
    /// <summary>LaTeX 数式をレンダリングするか</summary>
    public bool ConvertLatexMath { get => B("convert_latex_math", true); set => W("convert_latex_math", value); }
    /// <summary>Markdown（見出し・太字・箇条書き・コード・表など）を見た目にして表示するか（プリセットごと。既定 ON、既定の OCR は OFF）</summary>
    public bool ConvertMarkdown { get => B("convert_markdown", true); set => W("convert_markdown", value); }
    /// <summary>外の解答欄の下に「たずねる」入力欄を表示するか</summary>
    public bool ShowFollowRow { get => B("show_follow_row", false); set => W("show_follow_row", value); }
    // 位置の反映の時、文字の所だけ地を敷く（上級者。全体で1つ）
    public bool LayoutTextBg { get => B("layout_text_bg", true); set => W("layout_text_bg", value); }
    // 位置の反映の時、大きい区画の中にほぼ入っている小さい区画を捨てる（上級者。全体で1つ）
    public bool LayoutDropInside { get => B("layout_drop_inside", true); set => W("layout_drop_inside", value); }
    /// <summary>設定画面で上級者モード（詳細設定）を表示するか</summary>
    public bool ExpertMode { get => B("expert_mode", false); set => W("expert_mode", value); }
    /// <summary>配信・録画（OBS 等）にキャプチャ枠・バー・解答欄を映すか（既定 false＝WDA_EXCLUDEFROMCAPTURE）</summary>
    public bool AllowCaptureOverlay { get => B("allow_capture_overlay", false); set => W("allow_capture_overlay", value); }
    /// <summary>×で閉じた時はタスクトレイにしまう（既定 OFF = ×で終わる）</summary>
    public bool CloseToTray { get => B("close_to_tray", false); set => W("close_to_tray", value); }
    /// <summary>Windows の起動時にトレイで起動する（既定 OFF）。ON の間、現ユーザーの Run に「exe --tray」を入れる</summary>
    public bool StartWithWindows { get => B("start_with_windows", false); set => W("start_with_windows", value); }
    /// <summary>「タスクトレイにしまいました」の吹き出しを出し終えたか（最初の1回だけ出す。既定 false）</summary>
    public bool TrayHintShown { get => B("tray_hint_shown", false); set => W("tray_hint_shown", value); }
    /// <summary>LaTeX のコピーの形: plain / dollar / paren。不正な値は plain</summary>
    public string LatexCopy
    {
        get { var v = S("latex_copy", "plain"); return v is "dollar" or "paren" ? v : "plain"; }
        set => W("latex_copy", value is "dollar" or "paren" ? value : "plain");
    }
    /// <summary>まとまりの間（文字の大きさに対する倍率 0〜1.5、既定 0.8）</summary>
    public double AnswerParaSpacing
    {
        get
        {
            double v = D("answer_para_spacing", PanelStyle.DefaultParaSpacing);
            return (double.IsNaN(v) || double.IsInfinity(v))
                ? PanelStyle.DefaultParaSpacing
                : Math.Clamp(v, PanelStyle.MinParaSpacing, PanelStyle.MaxParaSpacing);
        }
        set
        {
            double v = (double.IsNaN(value) || double.IsInfinity(value))
                ? PanelStyle.DefaultParaSpacing
                : Math.Clamp(value, PanelStyle.MinParaSpacing, PanelStyle.MaxParaSpacing);
            W("answer_para_spacing", Math.Round(v, 1, MidpointRounding.AwayFromZero));
        }
    }
    /// <summary>行間（文字の大きさに対する倍率 1.0〜2.0、既定 0=指定なし相当）</summary>
    public double AnswerLineSpacing
    {
        get
        {
            double v = D("answer_line_spacing", PanelStyle.DefaultLineSpacing);
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return PanelStyle.DefaultLineSpacing;
            return Math.Clamp(v, PanelStyle.MinLineSpacing, PanelStyle.MaxLineSpacing);
        }
        set
        {
            double v = (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                ? PanelStyle.DefaultLineSpacing
                : Math.Clamp(value, PanelStyle.MinLineSpacing, PanelStyle.MaxLineSpacing);
            W("answer_line_spacing", Math.Round(v, 1, MidpointRounding.AwayFromZero));
        }
    }

    // ---- キャプチャ窓の枠（範囲外・不正な値は読む時に直す） ----
    public const string CaptureBorderColorDefault = "#0067C0";
    public string CaptureBorderColor
    {
        get { var s = S("capture_border_color", CaptureBorderColorDefault); return ColorUtil.IsHexColor(s) ? s : CaptureBorderColorDefault; }
        set => W("capture_border_color", value);
    }
    public double CaptureBorderAlpha { get => Math.Clamp(D("capture_border_alpha", 0.70), 0.15, 1.0); set => W("capture_border_alpha", value); }
    public int CaptureBorderPx { get => Math.Clamp(I("capture_border_px", 4), 1, 16); set => W("capture_border_px", value); }

    // ---- 自動トリガ ----
    public double TrigPollInterval { get => D("trig_poll_interval", TrigParams.DefaultInterval); set => W("trig_poll_interval", value); }
    public double TrigChangeRatio { get => D("trig_change_ratio", 0.01); set => W("trig_change_ratio", value); }
    public double TrigStableRatio { get => D("trig_stable_ratio", 0.005); set => W("trig_stable_ratio", value); }
    public int TrigStableSamples { get => I("trig_stable_samples", 2); set => W("trig_stable_samples", value); }
    public double TrigCooldown { get => D("trig_cooldown", TrigParams.DefaultCooldown); set => W("trig_cooldown", value); }

    // ---- クイックぶん投げ・ホットキー ----
    public bool QuickEnabled { get => B("quick_enabled", false); set => W("quick_enabled", value); }
    public string QuickButton { get => S("quick_button", "wheel"); set => W("quick_button", value); }
    public string QuickKey { get => S("quick_key"); set => W("quick_key", value); }
    /// <summary>double のまま読む（読み方は今までの ReadQuickCfg と同じ。範囲の丸めは使う側）</summary>
    public double QuickMinW { get => D("quick_min_w", 30); set => W("quick_min_w", value); }
    public double QuickMinH { get => D("quick_min_h", 30); set => W("quick_min_h", value); }
    /// <summary>チャタリング対策で離しを預かる時間（ms。0 で預からない。範囲の丸めは使う側）</summary>
    public double QuickChatterMs { get => D("quick_chatter_ms", 30); set => W("quick_chatter_ms", value); }
    public string Hotkey { get => S("hotkey"); set => W("hotkey", value); }
    /// <summary>範囲ドラッグを始めるキー／ボタン（""=未設定。既定は何も握らない）</summary>
    public string SnipHotkey { get => S("snip_hotkey"); set => W("snip_hotkey", value); }

    // ---- 決まり（使う側がそれぞれ気にしなくていいように、ここに閉じ込める） ----

    /// <summary>プリセットを選ぶ: prompt_active と prompt（そのプリセットの text）をいっしょに変える。
    /// 一覧に無い名前なら何もしない（false）</summary>
    public bool SelectPreset(string name)
    {
        if (_cfg.GetRaw("prompts") is JsonArray arr)
        {
            foreach (var n in arr)
                if (n is JsonObject p && ConfigStore.NodeToStr(p["name"]) == name)
                {
                    PromptActive = name;
                    Prompt = ConfigStore.NodeToStr(p["text"]);
                    return true;
                }
        }
        return false;
    }

    /// <summary>使っているプリセットが「グローバル設定を使う」を外していれば、そのプリセット専用の設定へ、
    /// 無ければグローバルへ ocr_split_mode を書く（バーの「ぶん投げ方」）</summary>
    public void SetOcrSplitModeEffective(bool enabled)
    {
        var p = _cfg.ActivePreset();
        bool presetScoped = p is not null &&
            p["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False;
        if (presetScoped)
        {
            if (p!["settings"] is not JsonObject st)
            {
                st = new JsonObject();
                p["settings"] = st;
            }
            st["ocr_split_mode"] = enabled;
            Raise(nameof(Prompts));
        }
        else OcrSplitMode = enabled;
    }
}

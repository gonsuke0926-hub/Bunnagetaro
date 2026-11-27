using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>config.json の古い既定値・古い形からの引っ越し（ConfigStore を分けたもの）</summary>
public sealed partial class ConfigStore
{
    /// <summary>「既定値そのものが入っていた項目」だけを新しい既定へ寄せる
    /// <summary>前処理（VLM）の生成の値が無い（前の版の）設定は、今まで VLM にも使っていた AI処理の値を写して始める
    /// （動きを変えない）。グローバルとプリセットの専用の設定の両方</summary>
    private static void SeedVlmGen(JsonObject saved)
    {
        void Seed(JsonObject o)
        {
            foreach (var (v, a) in VlmGenKeys)
                if (!o.ContainsKey(v) && o.TryGetPropertyValue(a, out var n) && n is not null) o[v] = n.DeepClone();
        }
        Seed(saved);
        if (saved["prompts"] is JsonArray arr)
            foreach (var p in arr.OfType<JsonObject>())
                if (p["settings"] is JsonObject st) Seed(st);
    }

    /// （自分で選んだ値は触らない）。</summary>
    private void ApplyDefaultMigrations(JsonObject saved)
    {
        MigratePresetsV5(saved);
        SeedVlmGen(saved);
        // 外窓の色が無い（前の版の）設定は、今まで外窓に使っていた枠の中の色を引き継ぐ（見た目を変えない）
        foreach (var (sk, ok) in new[] { ("strip_bg", "overlay_bg"), ("strip_fg", "overlay_fg") })
            if (GetRaw(saved, sk) is null && NodeToStr(GetRaw(saved, ok)) is { Length: > 0 } ov && ColorUtil.IsHexColor(ov))
                Root[sk] = ov;
        int mw = TryNum(saved, "quick_min_w", out double w) ? (int)w : 0;
        int mh = TryNum(saved, "quick_min_h", out double h) ? (int)h : 0;
        if (mw == 120 && mh == 90)
        {
            Root["quick_min_w"] = 30;
            Root["quick_min_h"] = 30;
            Migrations.Add("クイックぶん投げの最小四角を 30x30（既定）へ更新しました");
            AppLog.Info("quick_min_* を古い既定 120x90 → 30x30 へ更新");
        }
        if (NodeToStr(GetRaw(saved, "quick_button")) == "back")
        {
            Root["quick_button"] = "wheel";
            Migrations.Add("クイックぶん投げの起動ボタンをホイールクリック（既定）へ更新しました");
            AppLog.Info("quick_button を古い既定 \"back\" → \"wheel\" へ更新");
        }
        if (NodeToStr(GetRaw(saved, "prompt")) == OldPromptEn)
        {
            Root["prompt"] = DefaultImagePrompt;
            Migrations.Add("ぶん投げ文を「あらゆる言語→日本語」の新しい既定へ更新しました");
            AppLog.Info("prompt: 英語限定の旧既定 → 多言語既定へ更新");
        }
        // LaTeX プリセットの旧既定文（デリミタ無し）→ 新既定文（$$ ... $$ 付き）への引っ越し
        bool latexMigrated = false;
        if (GetRaw(saved, "prompts") is JsonArray pArr)
        {
            foreach (var n in pArr)
            {
                if (n is JsonObject po && NodeToStr(po["name"]) == "LaTeX" && NodeToStr(po["text"]) == OldLatexPrompt)
                {
                    po["text"] = DefaultLatexPrompt;
                    latexMigrated = true;
                }
            }
        }
        if (NodeToStr(GetRaw(saved, "prompt_active")) == "LaTeX" && NodeToStr(GetRaw(saved, "prompt")) == OldLatexPrompt)
        {
            Root["prompt"] = DefaultLatexPrompt;
            latexMigrated = true;
        }
        if (latexMigrated)
        {
            Migrations.Add("LaTeX プリセットの答えを数式として表示するようにしました");
            AppLog.Info("LaTeX プリセットの既定文を数式表示向け（$$ ... $$）へ更新");
        }
        // v4.2で無くなった仕組みの名残（チェック欄・直近の参考）。読み捨てるが、
        // 知らないキーは残す方針なのでファイルからは消さない。お知らせだけ出す
        bool stale = saved.ContainsKey("prompt_raw") || saved.ContainsKey("context_n") ||
                     (GetRaw(saved, "prompts") is JsonArray ps &&
                      ps.Any(p => p is JsonObject po && po.ContainsKey("raw")));
        if (stale)
        {
            Migrations.Add("ぶん投げ文はそのまま送ります（チェック欄と直近の参考はやめました）");
            AppLog.Info("prompt_raw / context_n / prompts[].raw は読み捨て（v4.2）");
        }
    }

    /// <summary>旧名のプリセット（翻訳用・OCR・LaTeX・Excel）を新しい名前・指示・モデルへ1回だけ書き換える。
    /// 印は presets_v5。自分で足した名前のプリセットは触らない。選んでいる物が改名されたら新しい名前を選んだままにする</summary>
    private void MigratePresetsV5(JsonObject saved)
    {
        if (saved["presets_v5"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.True) return;
        var rename = new Dictionary<string, string>
        {
            ["翻訳用"] = "翻訳", ["OCR"] = "OCR", ["LaTeX"] = "数式", ["Excel"] = "表",
        };
        var changed = new List<string>();
        if (saved["prompts"] is JsonArray arr)
        {
            var names = new HashSet<string>(arr.OfType<JsonObject>().Select(p => NodeToStr(p["name"])));
            foreach (var n in arr)
            {
                if (n is not JsonObject p) continue;
                string old = NodeToStr(p["name"]);
                if (!rename.TryGetValue(old, out var neu)) continue;
                if (neu != old)
                {
                    if (names.Contains(neu)) continue;   // 同じ名前を自分で作っている: どちらも触らない
                    names.Remove(old); names.Add(neu);
                    p["name"] = neu;
                    if (NodeToStr(saved["prompt_active"]) == old) saved["prompt_active"] = neu;
                }
                if (old != "翻訳用")
                {
                    string text = neu switch { "OCR" => OcrPresetText, "数式" => FormulaPresetText, _ => TablePresetText };
                    string model = neu switch { "数式" => FormulaPresetModel, "表" => TablePresetModel, _ => OcrPresetModel };
                    p["text"] = text;
                    p["use_global"] = false;
                    var st = p["settings"] as JsonObject ?? new JsonObject();
                    st["model"] = model;
                    st["ocr_split_mode"] = false;
                    if (!st.ContainsKey("clip_answer")) st["clip_answer"] = true;
                    p["settings"] = st;
                }
                changed.Add(neu);
            }
        }
        if (saved["presets_offered"] is JsonArray off)
        {
            var list = new List<string>();
            foreach (var o in off)
            {
                string nm = NodeToStr(o);
                if (nm.Length == 0) continue;
                if (rename.TryGetValue(nm, out var nn)) nm = nn;
                if (!list.Contains(nm)) list.Add(nm);
            }
            saved["presets_offered"] = new JsonArray(list.Select(o => (JsonNode?)o).ToArray());
        }
        saved["presets_v5"] = true;
        if (changed.Count > 0)
        {
            Migrations.Add("プリセットを新しいモデル向けに入れ替えました（翻訳・OCR・数式・表）");
            AppLog.Info("プリセットを入れ替え（presets_v5）: " + string.Join("・", changed));
        }
    }

    /// <summary>既定のプリセット（OCR・数式・表）のうち、まだ一度も出していない物を
    /// 一覧の後ろへ足す。presets_offered に残すので、消した物は戻らない。
    /// 同じ名前を自分で作っていたらそちらを残して足さない。</summary>
    private void OfferDefaultPresets(JsonObject saved)
    {
        JsonArray items;
        if (GetRaw(saved, "prompts") is JsonArray arr)
            items = (JsonArray)arr.DeepClone();
        else
        {
            // v3.8以前（一覧が無く prompt 1文だけ）のconfig。その1文を「翻訳用」に
            // してから既定のプリセットを足す。旧configの生の値ではなく、
            // 引っ越し済みの値を読み、v3.3の旧既定文への巻き戻りや通知との食い違いを防ぐ
            string old = NodeToStr(GetRaw(saved, "prompt")).Trim();
            // 文が無ければ既定の「翻訳」（PLaMo・専用の設定付き）。あればグローバルのモデルで使う
            items = new JsonArray
            {
                old.Length == 0 ? DefaultTranslatePreset()
                    : new JsonObject { ["name"] = DefaultPromptName, ["text"] = old, ["use_global"] = true },
            };
            Migrations.Add("ぶん投げ文を「翻訳」として登録しました（設定タブで増やせます）");
        }

        // v4.4までのconfigには presets_offered が無い（出していたのは翻訳用だけ）
        List<string> offered = GetRaw(saved, "presets_offered") is JsonArray oa
            ? oa.Select(x => NodeToStr(x)).Where(s => s.Length > 0).ToList()
            : new List<string> { DefaultPromptName };
        var have = new HashSet<string>(items.OfType<JsonObject>().Select(p => NodeToStr(p["name"])));
        var added = new List<string>();
        foreach (var n in DefaultPresets())
        {
            if (n is not JsonObject p) continue;
            string name = NodeToStr(p["name"]);
            if (offered.Contains(name)) continue;
            offered.Add(name);
            if (!have.Contains(name))
            {
                // settings を必ず付ける
                // （後で NormalizePrompts がグローバルの写しに充填する）
                var item = (JsonObject)p.DeepClone();
                if (item["settings"] is not JsonObject) item["settings"] = new JsonObject();
                items.Add(item);
                added.Add(name);
            }
        }
        Root["prompts"] = items;
        Root["presets_offered"] = new JsonArray(offered.Select(o => (JsonNode?)o).ToArray());
        if (added.Count > 0)
        {
            Migrations.Add("プリセットを追加しました: " + string.Join("・", added));
            AppLog.Info("既定のプリセットを追加: " + string.Join("・", added));
        }
    }

    /// <summary>prompts / prompt_active / prompt を使える形へ整える。
    /// cfg["prompt"] はつねに選んでいる1文へ。</summary>
    private void NormalizePrompts(bool hadSaved)
    {
        var items = new JsonArray();
        var used = new HashSet<string>();
        // 旧 config（「文字起こし」が別プリセット「OCR」を参照していた形）からの引き継ぎ用に、
        // 変換前の「OCR」プリセットのモデルとプロンプトを控えておく
        bool hasLegacyOcr = false, legacyOcrBuiltin = false;
        string legacyOcrSize = DefaultBuiltinOcrSize, legacyOcrModel = "", legacyOcrPrompt = "";
        if (Root["prompts"] is JsonArray rawScan)
        {
            foreach (var n in rawScan)
            {
                if (n is not JsonObject p) continue;
                if (CollapseWs(NodeToStr(p["name"])) != OcrPresetName) continue;
                bool ug0 = !(p["use_global"] is JsonValue u0 && u0.GetValueKind() == JsonValueKind.False);
                string m0 = PresetModelFrom(Root, p, ug0);
                string t0 = NodeToStr(p["text"]).Trim();
                bool hasNewMode = p["settings"] is JsonObject s0 && s0.ContainsKey("throw_mode");
                if (!hasNewMode && m0.Length > 0)
                {
                    hasLegacyOcr = true;
                    legacyOcrBuiltin = BuiltinOcr.IsBuiltin(m0);
                    legacyOcrSize = legacyOcrBuiltin ? BuiltinOcr.SizeOfModel(m0) : DefaultBuiltinOcrSize;
                    legacyOcrModel = m0;
                    legacyOcrPrompt = t0;
                }
                break;
            }
        }
        if (Root["prompts"] is JsonArray raw)
        {
            foreach (var n in raw)
            {
                if (n is not JsonObject p) continue;
                string name = CollapseWs(NodeToStr(p["name"]));
                if (name.Length > 40) name = name[..40];
                string text = NodeToStr(p["text"]).Trim();
                if (name.Length == 0 || text.Length == 0 || used.Contains(name)) continue;
                used.Add(name);
                // v4.5: プリセット。古いconfig（use_global・settings無し）はグローバルを使う
                bool useGlobal = true;
                if (p["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False)
                    useGlobal = false;
                string model = PresetModelFrom(Root, p, useGlobal);
                bool rawHasThrowMode = p["settings"] is JsonObject rs1 && rs1.ContainsKey("throw_mode");
                bool rawHasOcrEngine = p["settings"] is JsonObject rs2 && rs2.ContainsKey("ocr_engine");
                JsonObject? st = (!useGlobal || p["settings"] is JsonObject) ? PresetSettingsFrom(Root, p["settings"]) : null;

                // モデル欄に内蔵 OCR が入っていた旧設定は「内蔵OCRのみ」へ移す
                if (BuiltinOcr.IsBuiltin(model))
                {
                    useGlobal = false;
                    st ??= PresetSettingsFrom(Root, p["settings"]);
                    st["throw_mode"] = ThrowModeBuiltinOcr;
                    st["ocr_split_mode"] = false;
                    st["builtin_ocr_size"] = BuiltinOcr.SizeOfModel(model);
                    model = "";
                }
                else if (st is not null && !rawHasThrowMode)
                {
                    bool split = st["ocr_split_mode"] is not JsonValue ov || ov.GetValueKind() != JsonValueKind.False;
                    st["throw_mode"] = split ? ThrowModeOcr : ThrowModeImage;
                    if (split && !rawHasOcrEngine && hasLegacyOcr)
                    {
                        if (legacyOcrBuiltin)
                        {
                            st["ocr_engine"] = OcrEngineBuiltin;
                            st["builtin_ocr_size"] = legacyOcrSize;
                        }
                        else
                        {
                            st["ocr_engine"] = OcrEngineVlm;
                            st["ocr_vlm_model"] = legacyOcrModel;
                            if (legacyOcrPrompt.Length > 0) st["ocr_vlm_prompt"] = legacyOcrPrompt;
                        }
                    }
                }

                var item = new JsonObject
                {
                    ["name"] = name,
                    ["text"] = text,
                    ["use_global"] = useGlobal,
                    ["model"] = model,
                };
                if (st is not null) item["settings"] = st;
                items.Add(item);
            }
        }
        if (items.Count == 0)
        {
            // 登録が無い＝古いconfig（prompt だけの形）か、一覧が壊れている状態。
            // 古いconfigのほうは「翻訳」として立ち上げる
            string old = NodeToStr(Root["prompt"]).Trim();
            // 既定で埋めただけの文（PLaMo 用）なら、既定の「翻訳」を専用の設定ごと入れる
            items.Add(old.Length == 0 || old == DefaultPrompt.Trim() ? DefaultTranslatePreset()
                : new JsonObject { ["name"] = DefaultPromptName, ["text"] = old, ["use_global"] = true });
            if (hadSaved)
                Migrations.Add("ぶん投げ文を「翻訳」として登録しました（設定タブで増やせます）");
        }
        Root["prompts"] = items;

        string active = CollapseWs(NodeToStr(Root["prompt_active"]));
        var names = items.OfType<JsonObject>().Select(p => NodeToStr(p["name"])).ToList();
        if (!names.Contains(active)) active = names[0];
        Root["prompt_active"] = active;
        Root["prompt"] = items.OfType<JsonObject>().First(p => NodeToStr(p["name"]) == active)["text"]?.DeepClone();
    }
}

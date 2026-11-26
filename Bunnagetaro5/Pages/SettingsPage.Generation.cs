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

/// <summary>設定ページの生成欄・バーとの連動（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ---------------- バー（ChromeBar）からの連動 ----------------

    /// <summary>
    /// バーからプリセットを選ぶ前に、設定ページの書きかけを cfg へ反映する。
    /// 通れば null、拒否したときはその理由の一行を返す
    /// </summary>
    public string? TryCommitForExternal()
    {
        FlushPendingSave();
        _presetRejected = false;
        CommitAllVisible();
        if (!_presetRejected) return null;
        return _lastNote.Length > 0 ? _lastNote : "欄を直してください";
    }

    /// <summary>バー側で cfg が変わった。作業一覧も欄も cfg から塗り直す（_prompt_reload_from_cfg 相当）</summary>
    public void RefreshFromBar()
    {
        ReloadPresetsFromCfg();
        ShowPrompt();   // FillGen も含む（欄全体を cfg の値へ戻す）
        SyncModeFromBar(_cfg.Settings.Mode == "auto");
    }

    /// <summary>自動トリガだけバーから変わった。チェック欄に反映（発火は止める）</summary>
    public void SyncModeFromBar(bool on)
    {
        _filling = true;
        _modeChk.IsChecked = on;
        _filling = false;
    }

    /// <summary>拒否が出たとき、ぶん投げ方の欄だけ新しい値に合わせる</summary>
    public void SyncOcrOnly(bool enabled)
    {
        _filling = true;
        _ocrOn.IsChecked = enabled;
        _ocrOff.IsChecked = !enabled;
        _filling = false;
    }

    private void ShowPrompt()
    {
        _filling = true;
        try
        {
            var names = _pitems.Select(p => S(p["name"])).ToList();
            _presetCb.ItemsSource = new List<string> { GlobalEntry }.Concat(names).ToList();
            _presetCb.SelectedIndex = _editingGlobal ? 0 : Math.Clamp(_pcur, 0, _pitems.Count - 1) + 1;

            string name = "";
            bool have = TryCurPreset(out var item);
            bool useGlobal = true;
            if (!_editingGlobal && have)
            {
                name = S(item["name"]);
                _nameBox.Text = name;
                _instrBox.Text = S(item["text"]);
                _modelCb.Text = ConfigStore.PresetModel(item);
                useGlobal = item["use_global"] is not JsonValue v || v.GetValueKind() != JsonValueKind.False;
                _useGlobalChk.IsChecked = useGlobal;
            }
            else
            {
                // 共通の設定を編集する画面。プリセットの名前・指示は空けて触れなくする
                _nameBox.Text = "";
                _instrBox.Text = "";
                _modelCb.Text = "";
                _useGlobalChk.IsChecked = true;
            }

            JsonObject src = _editingGlobal ? _cfg.Root
                : (useGlobal || !have || item["settings"] is not JsonObject st)
                    ? ConfigStore.PresetSettingsFrom(_cfg.Root, null)
                    : st;
            FillGen(src);

            bool editPreset = !_editingGlobal;
            _nameBox.IsEnabled = editPreset;
            _instrBox.IsEnabled = editPreset;
            _addBtn.IsEnabled = editPreset;
            _delBtn.IsEnabled = editPreset;
            _useGlobalChk.IsEnabled = editPreset;
            // モデルはプリセットごと。［グローバル設定］にはモデルが無いので欄ごと隠す
            _modelRow.Visibility = editPreset ? Visibility.Visible : Visibility.Collapsed;
            _nameRow.Opacity = editPreset ? 1.0 : 0.45;
            _rowUseGlobal.Opacity = editPreset ? 1.0 : 0.45;
            _resetBtn.IsEnabled = editPreset && ConfigStore.DefaultPresets()
                .OfType<JsonObject>().Any(p => S(p["name"]) == name);
            _resetBtn.Opacity = _resetBtn.IsEnabled ? 1.0 : 0.45;
        }
        finally { _filling = false; }
        UpdateScope();
    }

    private void UpdateScope()
    {
        string msg;
        bool genEditable, presetScope;
        if (_editingGlobal)
        {
            msg = "共通のグローバル設定を編集中（「グローバル設定を使う」プリセットに反映）";
            genEditable = true;
            presetScope = false;
        }
        else if (_useGlobalChk.IsChecked == true)
        {
            msg = "グローバル設定を使用中（変更は上の「" + GlobalEntry + "」またはチェックOFF）";
            genEditable = false;
            presetScope = false;
        }
        else
        {
            msg = "このプリセットだけの個別設定を編集しています";
            genEditable = true;
            presetScope = true;
        }
        _genHeading.Text = presetScope ? "生成（このプリセットだけ）" : "生成（グローバル設定を参照中）";
        if (_editingGlobal) _genHeading.Text = "生成（グローバル設定を編集中）";
        _genBox.IsEnabled = genEditable;
        _genBox.Opacity = genEditable ? 1.0 : 0.45;
        _rowUseGlobal.SetDesc(msg);
        UpdateBuiltinDim();
    }

    private void FillGen(JsonObject src)
    {
        var ml = ModelChoices();
        // 一覧を入れ替えると欄の文字が消えるので、プリセットのモデルを入れ直す（モデルは src でなくプリセットから）
        string keep = _modelCb.Text;
        _modelCb.ItemsSource = ml;
        _modelCb.Text = keep;
        _ocrVlmModelCb.ItemsSource = ml;
        string vlmModel = S(src["ocr_vlm_model"]).Trim();
        _ocrVlmModelCb.Text = vlmModel.Length > 0 ? vlmModel : ConfigStore.OcrPresetModel;
        string vlmPrompt = S(src["ocr_vlm_prompt"]);
        _ocrVlmPromptBox.Text = vlmPrompt.Trim().Length > 0 ? vlmPrompt : ConfigStore.OcrPresetText;

        string mode = TranslatePipeline.ThrowModeOf(src);
        _ocrOn.IsChecked = mode == ConfigStore.ThrowModeOcr;
        _ocrOff.IsChecked = mode == ConfigStore.ThrowModeImage;
        _ocrBuiltinOnly.IsChecked = mode == ConfigStore.ThrowModeBuiltinOcr;

        string eng = S(src["ocr_engine"]).Trim();
        bool isVlm = eng == ConfigStore.OcrEngineVlm;
        _ocrEngBuiltin.IsChecked = !isVlm;
        _ocrEngVlm.IsChecked = isVlm;

        string sz = S(src["builtin_ocr_size"]).Trim();
        _builtinSizeCb.SelectedIndex = sz == BuiltinOcr.SizeMedium ? 1 : 0;
        _layoutChk.IsChecked = src["builtin_ocr_layout"] is JsonValue lv && lv.GetValueKind() == JsonValueKind.True;   // 既定 OFF
        UpdateOcrSubVisibility();

        bool isOutside = S(src["result_place"]) == "outside";
        _placeInside.IsChecked = !isOutside;
        _placeOutside.IsChecked = isOutside;
        _clipChk.IsChecked = src["clip_answer"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.True;
        _otslChk.IsChecked = src["convert_otsl_table"] is not JsonValue c1 || c1.GetValueKind() != JsonValueKind.False;
        _htmlChk.IsChecked = src["convert_html_table"] is not JsonValue c2 || c2.GetValueKind() != JsonValueKind.False;
        _latexChk.IsChecked = src["convert_latex_math"] is not JsonValue c3 || c3.GetValueKind() != JsonValueKind.False;
        _followChk.IsChecked = src["show_follow_row"] is JsonValue c4 && c4.GetValueKind() == JsonValueKind.True;   // 既定 OFF
        string think = S(src["ollama_think"]);
        int ti = Array.FindIndex(ThinkChoices, c => c.Val == think);
        _thinkCb.SelectedIndex = ti < 0 ? 0 : ti;
        foreach (var n in _genNums) n.Load(src);
    }

    /// <summary>生成の欄が今どこへ書く場所か。Root（グローバル）か、プリセットの settings。
    /// グローバルを使うプリセットを選んでいる時は null（欄は触れない）。</summary>
    private JsonObject? GenScope()
    {
        if (_filling) return null;
        if (_editingGlobal) return _cfg.Root;
        if (!TryCurPreset(out var item)) return null;
        if (item["use_global"] is not JsonValue v || v.GetValueKind() != JsonValueKind.False) return null;
        if (item["settings"] is not JsonObject st)
        {
            st = new JsonObject();
            item["settings"] = st;
        }
        return st;
    }

    private void PresetAfter()
    {
        // プリセット専用の設定を変えた → 一覧ごと書き戻す
        if (!_editingGlobal) WritePresets();
    }

    private void DoneGen()
    {
        if (_editingGlobal) SaveSoon();
        else WritePresets();
    }

    /// <summary>モデルはいつもプリセットごと。「グローバル設定を使う」に関係なく、選んでいるプリセットへ書く</summary>
    private void CommitModel()
    {
        if (_filling || _editingGlobal) return;
        if (!TryCurPreset(out var item)) return;
        string m = _modelCb.Text.Trim();
        if (m == "") return;   // 空なら今のまま
        if (ConfigStore.PresetModel(item) == m) return;
        item["model"] = m;
        WritePresets();
        UpdateBuiltinDim();
    }

    private void CommitThink()
    {
        if (_filling) return;
        var scope = GenScope();
        if (scope is null) return;
        int i = _thinkCb.SelectedIndex;
        if (i < 0 || i >= ThinkChoices.Length) return;
        if (S(scope["ollama_think"]) == ThinkChoices[i].Val) return;
        scope["ollama_think"] = ThinkChoices[i].Val;
        DoneGen();
    }

    private void CommitGenBool(string key, bool val)
    {
        var scope = GenScope();
        if (scope is null) return;
        if (scope[key] is JsonValue jv && jv.GetValueKind() == (val ? JsonValueKind.True : JsonValueKind.False)) return;
        if (ReferenceEquals(scope, _cfg.Root))
            WriteSelf(() => _cfg.Settings.WriteNode(key, JsonValue.Create(val)));
        else
            scope[key] = val;
        DoneGen();
    }

    private void CommitGenStr(string key, string val)
    {
        var scope = GenScope();
        if (scope is null) return;
        if (S(scope[key]) == val) return;
        if (ReferenceEquals(scope, _cfg.Root))
            WriteSelf(() => _cfg.Settings.WriteNode(key, JsonValue.Create(val)));
        else
            scope[key] = val;
        DoneGen();
    }

    /// <summary>
    /// タスクトレイから「解答欄の場所」を今のプリセットへ書く。欄の radio を押した時と同じ
    /// 書き先（プリセット専用ならそのプリセットの settings、グローバルを使うならグローバル）へ書き、
    /// 解答欄・帯・設定ページが設定ページで変えた時と同じに追いつく。書けたら true
    /// </summary>
    internal bool SetResultPlaceForActive(string place)
    {
        place = place == "outside" ? "outside" : "inside";
        if (!TryCurPreset(out var item)) return false;
        bool own = item["use_global"] is JsonValue ug && ug.GetValueKind() == JsonValueKind.False;
        bool ok = true;
        if (own)
        {
            if (item["settings"] is not JsonObject st)
            {
                st = new JsonObject();
                item["settings"] = st;
            }
            if (S(st["result_place"]) != place)
            {
                st["result_place"] = place;
                ok = WritePresets();
            }
        }
        else if (_cfg.Settings.ResultPlace != place)
        {
            _cfg.Settings.ResultPlace = place;   // 自分では握らず、解答欄側の聞き手にも届ける
            SaveSoon();
        }
        ShowPrompt();   // 欄の radio を cfg の値へ合わせ直す
        return ok;
    }

    /// <summary>タスクトレイからクイックぶん投げの ON/OFF を変える。欄のチェックを押した時と同じ道を通る</summary>
    internal void SetQuickEnabled(bool on)
    {
        _quickOnChk.IsChecked = on;   // Checked/Unchecked の聞き手が設定へ書いて保存する
        if (_cfg.Settings.QuickEnabled != on)
        {
            _cfg.Settings.QuickEnabled = on;
            SaveSoon();
        }
    }

    private string CurrentThrowMode() =>
        _ocrBuiltinOnly.IsChecked == true ? ConfigStore.ThrowModeBuiltinOcr
        : _ocrOff.IsChecked == true ? ConfigStore.ThrowModeImage
        : ConfigStore.ThrowModeOcr;

    private void CommitThrowMode(string mode)
    {
        var scope = GenScope();
        if (scope is null) return;
        bool split = mode == ConfigStore.ThrowModeOcr;
        bool changed = S(scope["throw_mode"]) != mode
            || scope["ocr_split_mode"] is not JsonValue jv || jv.GetValueKind() != (split ? JsonValueKind.True : JsonValueKind.False);
        if (changed)
        {
            if (ReferenceEquals(scope, _cfg.Root))
            {
                WriteSelf(() =>
                {
                    _cfg.Settings.WriteNode("throw_mode", JsonValue.Create(mode));
                    _cfg.Settings.WriteNode("ocr_split_mode", JsonValue.Create(split));
                });
            }
            else
            {
                scope["throw_mode"] = mode;
                scope["ocr_split_mode"] = split;
            }
            DoneGen();
        }
        UpdateBuiltinDim();
    }

    private void CommitOcrEngine(string eng)
    {
        CommitGenStr("ocr_engine", eng);
        UpdateOcrSubVisibility();
    }

    private void CommitBuiltinSize(string sz) => CommitGenStr("builtin_ocr_size", sz);

    private void CommitOcrVlmModel()
    {
        if (_filling) return;
        string m = _ocrVlmModelCb.Text.Trim();
        if (m.Length == 0) return;
        CommitGenStr("ocr_vlm_model", m);
    }

    private void OnOcrVlmPromptDebounceTick(object? sender, EventArgs e)
    {
        _ocrVlmPromptTimer?.Stop();
        CommitOcrVlmPrompt();
    }

    private void CommitOcrVlmPrompt()
    {
        _ocrVlmPromptTimer?.Stop();
        if (_filling) return;
        string t = _ocrVlmPromptBox.Text.Trim();
        if (t.Length == 0) return;
        CommitGenStr("ocr_vlm_prompt", t);
    }

    private void CommitAllVisible()
    {
        _presetRejected = false;
        if (!_editingGlobal) CommitNameText();
        if (!_editingGlobal) CommitModel();
        if (_genBox.IsEnabled)
        {
            CommitThink();
            CommitThrowMode(CurrentThrowMode());
            CommitGenStr("ocr_engine", _ocrEngVlm.IsChecked == true ? ConfigStore.OcrEngineVlm : ConfigStore.OcrEngineBuiltin);
            CommitGenStr("builtin_ocr_size", _builtinSizeCb.SelectedIndex == 1 ? BuiltinOcr.SizeMedium : BuiltinOcr.SizeSmall);
            CommitGenBool("builtin_ocr_layout", _layoutChk.IsChecked == true);
            CommitOcrVlmModel();
            CommitOcrVlmPrompt();
            CommitGenStr("result_place", _placeOutside.IsChecked == true ? "outside" : "inside");
            CommitGenBool("clip_answer", _clipChk.IsChecked == true);
            CommitGenBool("convert_otsl_table", _otslChk.IsChecked == true);
            CommitGenBool("convert_html_table", _htmlChk.IsChecked == true);
            CommitGenBool("convert_latex_math", _latexChk.IsChecked == true);
            CommitGenBool("show_follow_row", _followChk.IsChecked == true);
            foreach (var n in _genNums) n.Commit(this);
        }
    }

    private void StoreCur()
    {
        if (_editingGlobal) return;
        if (!TryCurPreset(out var item)) return;
        item["name"] = _nameBox.Text;
        item["text"] = _instrBox.Text;
        item["use_global"] = _useGlobalChk.IsChecked == true;
    }

    private void OnInstrDebounceTick(object? sender, EventArgs e)
    {
        _instrTimer?.Stop();
        CommitNameText();
    }

    private void CommitNameText()
    {
        _instrTimer?.Stop();
        _presetRejected = false;
        if (_filling || _editingGlobal) return;
        if (!TryCurPreset(out var item)) return;
        if (S(item["name"]) == _nameBox.Text && S(item["text"]) == _instrBox.Text) return;
        item["name"] = _nameBox.Text;
        item["text"] = _instrBox.Text;
        _presetRejected = !WritePresets();
    }

    private void Note(string msg)
    {
        _lastNote = msg;
        _rowPreset.SetDesc(msg);
    }

    /// <summary>作業一覧を検証して prompts / prompt_active / prompt へ書き戻す。
    /// 名前が空・重複、指示が空なら何も書き戻さない。</summary>
    private bool WritePresets()
    {
        var items = new JsonArray();
        var used = new HashSet<string>();
        foreach (var p in _pitems)
        {
            string name = CollapseWs(S(p["name"]));
            if (name.Length > 40) name = name[..40].TrimEnd();
            string text = S(p["text"]).Trim();
            if (name == "") { Note("名前が空です。名前の欄を直してください"); return false; }
            if (text == "") { Note("「" + name + "」の指示が空です。空の指示ではぶん投げられません"); return false; }
            if (!used.Add(name)) { Note("名前が重複しています（" + name + "）"); return false; }
            if (name == GlobalEntry) { Note("「" + GlobalEntry + "」はプリセットの名前に使えません"); return false; }
            bool ug = p["use_global"] is not JsonValue v || v.GetValueKind() != JsonValueKind.False;
            var it = new JsonObject { ["name"] = name, ["text"] = text, ["use_global"] = ug, ["model"] = ConfigStore.PresetModel(p) };
            if (!ug && p["settings"] is JsonObject st) it["settings"] = (JsonObject)st.DeepClone();
            items.Add(it);
        }
        if (items.Count == 0) { Note("プリセットが1件もありません"); return false; }
        Note("");

        // 作業一覧も整えた形へ揃える（同じ物を共有せず、写しを持つ）
        _pitems = items.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
        if (_pcur < 0 || _pcur >= _pitems.Count) _pcur = 0;
        string active = S(_pitems[_pcur]["name"]);
        WriteSelf(() =>
        {
            _cfg.Settings.Prompts = (JsonArray)items.DeepClone();
            _cfg.Settings.PromptActive = active;
            _cfg.Settings.Prompt = S(_pitems[_pcur]["text"]);
        });
        SaveSoon();

        // 整えた名前だけを欄へ映す（設定の欄は描き直さない）
        if (!_editingGlobal)
        {
            _filling = true;
            try
            {
                _presetCb.ItemsSource = new List<string> { GlobalEntry }.Concat(_pitems.Select(p => S(p["name"]))).ToList();
                _presetCb.SelectedIndex = _pcur + 1;
                _nameBox.Text = active;
            }
            finally { _filling = false; }
        }
        return true;
    }

    private void AddPreset_Click()
    {
        if (_editingGlobal || !TryCurPreset(out var cur)) return;
        StoreCur();
        string base0 = S(cur["name"]) + " のコピー";
        string nm = base0;
        int n = 2;
        while (_pitems.Any(p => S(p["name"]) == nm)) nm = base0 + n.ToString(CultureInfo.InvariantCulture);
        var neu = (JsonObject)cur.DeepClone();
        neu["name"] = nm;
        _pitems.Add(neu);
        _pcur = _pitems.Count - 1;
        WritePresets();
        ShowPrompt();
        Note("「" + nm + "」を増やしました。名前と指示を直して使ってください");
        _nameBox.Focus();
    }

    private bool Confirm(string message)
    {
        if (ConfirmOverride is not null) return ConfirmOverride(message);
        if (Window.GetWindow(this) is Window w)
            return MessageBox.Show(w, message, "プリセットを消す", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        return true;
    }

    private void DelPreset_Click()
    {
        if (_editingGlobal || !TryCurPreset(out var cur)) return;
        StoreCur();
        if (_pitems.Count <= 1) { Note("最後の1件は消せません"); return; }
        string gone = S(cur["name"]);
        if (!Confirm("「" + gone + "」を消しますか？（消したプリセットは元に戻せません）")) { ShowPrompt(); return; }
        _pitems.RemoveAt(_pcur);
        if (_pcur >= _pitems.Count) _pcur = _pitems.Count - 1;
        WritePresets();
        ShowPrompt();
        Note("「" + gone + "」を消しました");
    }

    private void ResetText_Click()
    {
        if (_editingGlobal || !TryCurPreset(out var cur)) return;
        StoreCur();
        string name = S(cur["name"]);
        var def = ConfigStore.DefaultPresets().OfType<JsonObject>().FirstOrDefault(p => S(p["name"]) == name);
        if (def is null) return;
        cur["text"] = def["text"]?.DeepClone();
        WritePresets();
        ShowPrompt();
        Note("「" + name + "」の指示を既定に戻しました");
    }

    private void UseGlobal_Changed()
    {
        if (_filling || _editingGlobal) return;
        if (!TryCurPreset(out var item)) return;
        StoreCur();
        bool on = _useGlobalChk.IsChecked == true;
        item["use_global"] = on;
        if (on)
        {
            // 共通の値を使うプリセットは自分の値を持たない
            item.Remove("settings");
        }
        else
        {
            // 外した瞬間、欄に出ている共通の値がこのプリセットだけの値になる
            item["settings"] = ReadGenSettings(ConfigStore.PresetSettingsFrom(_cfg.Root, null));
        }
        WritePresets();
        ShowPrompt();
    }

    /// <summary>今欄に出ている生成設定の値を読む（数字が不正な欄は fallback の値で）。
    /// 「グローバル設定を使う」を外した瞬間に settings を作るために使う。</summary>
    private JsonObject ReadGenSettings(JsonObject fallback)
    {
        string tm = CurrentThrowMode();
        string vm = _ocrVlmModelCb.Text.Trim();
        string vp = _ocrVlmPromptBox.Text.Trim();
        var s = new JsonObject
        {
            ["throw_mode"] = tm,
            ["ocr_split_mode"] = tm == ConfigStore.ThrowModeOcr,
            ["ocr_engine"] = _ocrEngVlm.IsChecked == true ? ConfigStore.OcrEngineVlm : ConfigStore.OcrEngineBuiltin,
            ["builtin_ocr_size"] = _builtinSizeCb.SelectedIndex == 1 ? BuiltinOcr.SizeMedium : BuiltinOcr.SizeSmall,
            ["builtin_ocr_layout"] = _layoutChk.IsChecked == true,
            ["ocr_vlm_model"] = vm.Length > 0 ? vm : S(fallback["ocr_vlm_model"]),
            ["ocr_vlm_prompt"] = vp.Length > 0 ? vp : S(fallback["ocr_vlm_prompt"]),
            ["result_place"] = _placeOutside.IsChecked == true ? "outside" : "inside",
            ["clip_answer"] = _clipChk.IsChecked == true,
            ["convert_otsl_table"] = _otslChk.IsChecked == true,
            ["convert_html_table"] = _htmlChk.IsChecked == true,
            ["convert_latex_math"] = _latexChk.IsChecked == true,
            ["show_follow_row"] = _followChk.IsChecked == true,
        };
        int ti = _thinkCb.SelectedIndex;
        s["ollama_think"] = ti >= 0 && ti < ThinkChoices.Length ? ThinkChoices[ti].Val : S(fallback["ollama_think"]);
        foreach (var n in _genNums)
        {
            string t = n.Box.Text.Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= n.Lo && v <= n.Hi)
                s[n.Key] = n.IsInt ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : v;
            else if (fallback.TryGetPropertyValue(n.Key, out var f))
                s[n.Key] = f?.DeepClone();
        }
        return s;
    }
}


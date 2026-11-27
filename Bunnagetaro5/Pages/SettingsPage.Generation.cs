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
        // 文字起こし＝前処理する・AI処理する、画像そのまま＝前処理しない・AI処理する
        _filling = true;
        _preUse.IsChecked = enabled;
        _aiUse.IsChecked = true;
        _filling = false;
        UpdateFlowDim();
    }

    private void ShowPrompt()
    {
        _filling = true;
        try
        {
            var names = _pitems.Select(p => S(p["name"])).ToList();
            _presetCb.ItemsSource = names;   // ［グローバル設定］は無くした
            _presetCb.SelectedIndex = Math.Clamp(_pcur, 0, _pitems.Count - 1);

            string name = "";
            bool have = TryCurPreset(out var item);
            if (have)
            {
                name = S(item["name"]);
                _presetCb.Text = name;
                _instrBox.Text = S(item["text"]);
                _modelCb.Text = ConfigStore.PresetModel(item);
            }

            // プリセットはいつも自分の設定を持つ（足りないキーは今の値で埋める）
            FillGen(ConfigStore.PresetSettingsFrom(_cfg.Root, have ? item["settings"] : null));

            _resetBtn.IsEnabled = ConfigStore.DefaultPresets()
                .OfType<JsonObject>().Any(p => S(p["name"]) == name);
            _resetBtn.Opacity = _resetBtn.IsEnabled ? 1.0 : 0.45;
        }
        finally { _filling = false; }
        UpdateScope();
    }

    private void UpdateScope()
    {
        // グローバル設定を無くしたので、生成の欄はいつも選んでいるプリセットの設定
        _genBox.IsEnabled = true;
        _genBox.Opacity = 1.0;
        UpdateFlowDim();
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

        FillFlow(src);   // 1 レイアウト判定・2 前処理・3 AI処理 の箱

        string sz = S(src["builtin_ocr_size"]).Trim();
        _builtinSizeCb.SelectedIndex = sz == BuiltinOcr.SizeMedium ? 1 : 0;

        bool isOutside = S(src["result_place"]) == "outside";
        _placeInside.IsChecked = !isOutside;
        _placeOutside.IsChecked = isOutside;
        _clipChk.IsChecked = src["clip_answer"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.True;
        _otslChk.IsChecked = src["convert_otsl_table"] is not JsonValue c1 || c1.GetValueKind() != JsonValueKind.False;
        _htmlChk.IsChecked = src["convert_html_table"] is not JsonValue c2 || c2.GetValueKind() != JsonValueKind.False;
        _latexChk.IsChecked = src["convert_latex_math"] is not JsonValue c3 || c3.GetValueKind() != JsonValueKind.False;
        _mdChk.IsChecked = src["convert_markdown"] is not JsonValue c5 || c5.GetValueKind() != JsonValueKind.False;
        _followChk.IsChecked = src["show_follow_row"] is JsonValue c4 && c4.GetValueKind() == JsonValueKind.True;   // 既定 OFF
        string think = S(src["ollama_think"]);
        int ti = Array.FindIndex(ThinkChoices, c => c.Val == think);
        _thinkCb.SelectedIndex = ti < 0 ? 0 : ti;
        foreach (var n in _genNums) n.Load(src);
    }

    /// <summary>生成の欄が書く場所（選んでいるプリセットの settings。からいつもこれ）</summary>
    private JsonObject? GenScope()
    {
        if (_filling) return null;
        if (!TryCurPreset(out var item)) return null;
        item["use_global"] = false;
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
        WritePresets();
    }

    private void DoneGen()
    {
        WritePresets();
    }

    /// <summary>モデルはいつもプリセットごと。「グローバル設定を使う」に関係なく、選んでいるプリセットへ書く</summary>
    private void CommitModel()
    {
        if (_filling) return;
        if (!TryCurPreset(out var item)) return;
        string m = _modelCb.Text.Trim();
        if (m == "") return;   // 空なら今のまま
        if (ConfigStore.PresetModel(item) == m) return;
        item["model"] = m;
        WritePresets();
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
        bool ok = true;
        // プリセットはいつも自分の設定を持つ
        item["use_global"] = false;
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
        CommitNameText();
        CommitModel();
        if (_genBox.IsEnabled)
        {
            CommitThink();
            CommitFlow();
            CommitGenStr("builtin_ocr_size", _builtinSizeCb.SelectedIndex == 1 ? BuiltinOcr.SizeMedium : BuiltinOcr.SizeSmall);
            CommitGenBool("builtin_ocr_layout", _layUse.IsChecked == true);
            CommitOcrVlmModel();
            CommitOcrVlmPrompt();
            CommitGenStr("result_place", _placeOutside.IsChecked == true ? "outside" : "inside");
            CommitGenBool("clip_answer", _clipChk.IsChecked == true);
            CommitGenBool("convert_otsl_table", _otslChk.IsChecked == true);
            CommitGenBool("convert_html_table", _htmlChk.IsChecked == true);
            CommitGenBool("convert_latex_math", _latexChk.IsChecked == true);
            CommitGenBool("convert_markdown", _mdChk.IsChecked == true);
            CommitGenBool("show_follow_row", _followChk.IsChecked == true);
            foreach (var n in _genNums) n.Commit(this);
        }
    }

    private void StoreCur()
    {
        if (!TryCurPreset(out var item)) return;
        item["name"] = _presetCb.Text;
        item["text"] = _instrBox.Text;
        item["use_global"] = false;   // 
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
        if (_filling) return;
        if (!TryCurPreset(out var item)) return;
        if (S(item["name"]) == _presetCb.Text && S(item["text"]) == _instrBox.Text) return;
        item["name"] = _presetCb.Text;
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
            // プリセットはいつも自分の設定を持つ（use_global は前の版との行き来のため false で書く）
            var it = new JsonObject { ["name"] = name, ["text"] = text, ["use_global"] = false, ["model"] = ConfigStore.PresetModel(p) };
            it["settings"] = ConfigStore.PresetSettingsFrom(_cfg.Root, p["settings"]);
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
        // 前はグローバルの画面の時は映さなかった
        {
            _filling = true;
            try
            {
                _presetCb.ItemsSource = _pitems.Select(p => S(p["name"])).ToList();
                _presetCb.SelectedIndex = _pcur;
                _presetCb.Text = active;
            }
            finally { _filling = false; }
        }
        return true;
    }

    private void AddPreset_Click()
    {
        if (!TryCurPreset(out var cur)) return;
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
        _presetCb.Focus();
        if (_presetCb.Template?.FindName("PART_EditableTextBox", _presetCb) is TextBox tb)
            tb.SelectAll();
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
        if (!TryCurPreset(out var cur)) return;
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
        if (!TryCurPreset(out var cur)) return;
        StoreCur();
        string name = S(cur["name"]);
        var def = ConfigStore.DefaultPresets().OfType<JsonObject>().FirstOrDefault(p => S(p["name"]) == name);
        if (def is null) return;
        cur["text"] = def["text"]?.DeepClone();
        WritePresets();
        ShowPrompt();
        Note("「" + name + "」の指示を既定に戻しました");
    }

}

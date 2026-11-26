using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>既定の全47キーが「保存・復元」されることを一括で証明する。
/// 各キーを既定から非既定の値に変えて Save → 新しい ConfigStore で Load し、
/// 1キー残らず保存したままの JSON で戻ってくることを比べる。
/// （即反映は使う側の実装保証: trig_* は毎巡回読み直し MainWindow.ReadTrigParams、
/// quick 系と hotkey は1秒スロットルで登録し直し、ollama や model と base_url は毎回送信時に読む、
/// overlay_* は設定ページの欄が即 AnswerPanel へ効く。ここでは経路を増やさない。）</summary>
public sealed class ConfigKeysRoundTripTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public ConfigKeysRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_keys_rt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 既定の全キーは変えて保存しても読み直すとそのまま戻ってくる()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        var defaults = ConfigStore.DefaultConfig();
        var keys = new List<string>();
        foreach (var kv in defaults) keys.Add(kv.Key);
        Assert.True(keys.Count >= 47, "既定キー数が想定より少ない: " + keys.Count);
        foreach (var key in keys)
            Assert.True(store.Root.ContainsKey(key), "既定キーが無い: " + key);

        // 全キーを非既定値へ。base_url/quick_button は正規化で書き換えられない値を使う
        // （ConfigStore.Load の退避 L210・L252 に引っかからないため）。
        var mutated = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            // 派生値の2つは単独変更しない（prompt_active は実在のプリセット名に正規化、
            // prompt は選中の1文から常に作り直す）。
            if (key == "prompt" || key == "prompt_active" || key == "presets_v5") continue;   // presets_v5: false に戻すと読み直しで引っ越しが再走する
            store.Root[key] = Mutate(key, store.GetRaw(key));
            mutated[key] = store.GetRaw(key)!.ToJsonString();
        }
        store.Save();
        Assert.True(File.Exists(_paths.ConfigJson));

        var store2 = new ConfigStore(_paths);
        store2.Load();
        foreach (var key in keys)
        {
            if (key == "prompt" || key == "prompt_active" || key == "presets_v5") continue;   // presets_v5: false に戻すと読み直しで引っ越しが再走する
            Assert.Equal(mutated[key], store2.GetRaw(key)?.ToJsonString());
        }

        // prompt_active / prompt は「実在のプリセットを選ぶ」という本物の経路で保存・復元される。
        // （上のループで prompts に "追加" プリセットを足してある。）
        store2.Root["prompt_active"] = "追加";
        store2.Save();
        var store3 = new ConfigStore(_paths);
        store3.Load();
        Assert.Equal("追加", store3.GetStr("prompt_active"));
        Assert.Equal("てきすと", store3.GetStr("prompt"));
    }

    [Fact]
    public void 空フォルダから読むと全キーが既定のJSONそのままになる()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        var defaults = ConfigStore.DefaultConfig();
        foreach (var kv in defaults)
        {
            string key = kv.Key;
            var expected = kv.Value is null ? "null" : kv.Value.ToJsonString();
            var actual = store.GetRaw(key) is null ? "null" : store.GetRaw(key)!.ToJsonString();
            // プリセット設定の補完で prompts だけ中身が足されることがあるので等値はスキップ
            if (key == "prompts") continue;
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>型ごとの「変えたことに分かる」値。読み側のclampは Root に影響しない。</summary>
    private static JsonNode Mutate(string key, JsonNode? v)
    {
        if (key == "base_url") return JsonValue.Create("http://127.0.0.1:11434/v2")!;
        if (key == "base_url_list") return new JsonArray(JsonValue.Create("http://localhost:11434"), JsonValue.Create("http://127.0.0.1:11434/v2"));
        if (key == "quick_button") return JsonValue.Create("off")!;
        if (key == "prompts")
        {
            var na = (JsonArray)v!.DeepClone();
            // プリセットはいつも自分の "model" を持つ（無いと読み込みでグローバルの model を写す）
            na.Add(new JsonObject { ["name"] = "追加", ["text"] = "てきすと", ["use_global"] = true, ["model"] = "added:1b" });
            return na;
        }
        switch (v)
        {
            case JsonValue jv when jv.TryGetValue<bool>(out var b): return JsonValue.Create(!b)!;
            case JsonValue jv when jv.TryGetValue<double>(out var d): return JsonValue.Create(d + 0.5)!;
            case JsonValue jv when jv.TryGetValue<int>(out var i): return JsonValue.Create(i + 7)!;
            case JsonValue jv when jv.TryGetValue<string>(out var s): return JsonValue.Create(s + "-x")!;
            case JsonArray arr:
            {
                var na = (JsonArray)arr.DeepClone();
                na.Add(JsonValue.Create("extra"));
                return na;
            }
            case JsonObject: return JsonValue.Create("obj-x")!;
            default: return JsonValue.Create(7)!;   // capture_x / capture_y の既定 null
        }
    }
}

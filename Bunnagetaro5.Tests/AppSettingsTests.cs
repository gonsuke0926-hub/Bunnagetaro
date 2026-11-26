using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>整理1.5: AppSettings（型のある設定の窓口）。全キーの読み書き・知らないキーが残る・
/// 保存したJSONが今と同じ・値が変わった時だけ通知。</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_appset_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ConfigStore NewStore()
    {
        var s = new ConfigStore(_paths);
        s.Load();
        return s;
    }

    [Fact]
    public void 既定の全キーに型付きプロパティがある()
    {
        var props = typeof(AppSettings).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var kv in ConfigStore.DefaultConfig())
            Assert.True(props.Contains(AppSettings.PropName(kv.Key)), "プロパティが無い: " + kv.Key);
    }

    [Fact]
    public void 全プロパティの読み値は既定のJSONと一致する()
    {
        var s = NewStore();
        var def = ConfigStore.DefaultConfig();
        foreach (var kv in def)
        {
            if (kv.Key == "prompts") continue;   // 読み込み時に整えられる
            var p = typeof(AppSettings).GetProperty(AppSettings.PropName(kv.Key))!;
            object? v = p.GetValue(s.Settings);
            if (v is JsonNode jn) Assert.Equal(kv.Value!.ToJsonString(), jn.ToJsonString());
            else if (kv.Value is null) Assert.Null(v);
            else if (v is double d) Assert.Equal(double.Parse(kv.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture), d);
            else if (v is int or bool or string)
                Assert.Equal(kv.Value.ToString(), v is bool b ? (b ? "true" : "false") : v.ToString());
        }
    }

    [Fact]
    public void 書いて保存して読み直すと同じ値で戻る()
    {
        var s = NewStore();
        s.Settings.QuickEnabled = true;
        s.Settings.QuickMinW = 77;
        s.Settings.Hotkey = "f9";
        s.Settings.OverlayAlpha = 0.35;
        s.Settings.CaptureX = 123;
        s.Settings.ChromeOffsetX = null;
        s.Save();
        var t = NewStore();
        Assert.True(t.Settings.QuickEnabled);
        Assert.Equal(77, t.Settings.QuickMinW);
        Assert.Equal("f9", t.Settings.Hotkey);
        Assert.Equal(0.35, t.Settings.OverlayAlpha);
        Assert.Equal(123, t.Settings.CaptureX);
        Assert.Null(t.Settings.ChromeOffsetX);
    }

    [Fact]
    public void 知らないキーは残る()
    {
        var s = NewStore();
        s.Root["my_future_key"] = "x";
        s.Settings.QuickEnabled = false;
        s.Save();
        var t = NewStore();
        Assert.Equal("x", (string?)t.Root["my_future_key"]);
    }

    [Fact]
    public void 保存したJSONは今までのSetXと1バイトも違わない()
    {
        var a = NewStore();
        a.SetBool("quick_enabled", false);
        a.SetStr("hotkey", "f9");
        a.SetDouble("overlay_alpha", 0.35);
        a.SetInt("strip_width", 600);
        a.SetInt("capture_x", 10);
        a.SetNull("chrome_offset_x");
        a.Save();
        string before = File.ReadAllText(_paths.ConfigJson);

        File.Delete(_paths.ConfigJson);
        var b = NewStore();
        b.Settings.QuickEnabled = false;
        b.Settings.Hotkey = "f9";
        b.Settings.OverlayAlpha = 0.35;
        b.Settings.StripWidth = 600;
        b.Settings.CaptureX = 10;
        b.Settings.ChromeOffsetX = null;
        b.Save();
        Assert.Equal(before, File.ReadAllText(_paths.ConfigJson));
    }

    [Fact]
    public void 値が変わった時だけ通知が出る()
    {
        var s = NewStore();
        var got = new List<string?>();
        s.Settings.PropertyChanged += (_, e) => got.Add(e.PropertyName);
        s.Settings.QuickEnabled = false;    // 既定と同じ → 通知なし
        Assert.Empty(got);
        s.Settings.QuickEnabled = true;
        Assert.Equal(new[] { "QuickEnabled" }, got);
        s.Settings.QuickEnabled = true;     // 同じ → 増えない
        Assert.Single(got);
        s.Settings.QuickMinW = 40;
        s.Settings.WriteNode("hotkey", "f8");
        Assert.Equal(new[] { "QuickEnabled", "QuickMinW", "Hotkey" }, got);
    }

    [Fact]
    public void Loadし直すと全部が変わった通知が出る()
    {
        var s = NewStore();
        var got = new List<string?>();
        s.Settings.PropertyChanged += (_, e) => got.Add(e.PropertyName);
        s.Load();
        Assert.Contains(string.Empty, got);
    }

    [Fact]
    public void プリセットを選ぶとpromptがそのtextと同じになる()
    {
        var s = NewStore();
        var got = new List<string?>();
        s.Settings.PropertyChanged += (_, e) => got.Add(e.PropertyName);
        Assert.True(s.Settings.SelectPreset("OCR"));
        Assert.Equal("OCR", s.Settings.PromptActive);
        Assert.Equal(ConfigStore.NodeToStr(s.ActivePreset()!["text"]), s.Settings.Prompt);
        Assert.Contains("PromptActive", got);
        Assert.False(s.Settings.SelectPreset("存在しない"));
        Assert.Equal("OCR", s.Settings.PromptActive);
    }

    [Fact]
    public void ぶん投げ方はプリセット専用なら専用側へ書く()
    {
        var s = NewStore();
        s.Settings.SelectPreset("OCR");   // use_global=false
        s.Settings.SetOcrSplitModeEffective(true);
        Assert.True((bool)s.ActivePreset()!["settings"]!["ocr_split_mode"]!);
        Assert.True(s.Settings.OcrSplitMode);   // 既定のグローバルは true のまま（触っていない）
        // 既定のプリセットは全部 use_global=false になった（2026-11-14）ので、グローバルを使うものを足す
        s.Root["prompts"]!.AsArray().Add(new JsonObject { ["name"] = "自分の", ["text"] = "x", ["use_global"] = true });
        s.Settings.SelectPreset("自分の");      // use_global=true
        s.Settings.SetOcrSplitModeEffective(false);
        Assert.False(s.Settings.OcrSplitMode);
    }

    [Fact]
    public void F13_latex_copyの既定はplainで不正な値はplainに直り保存される()
    {
        var s = NewStore();
        Assert.Equal("plain", s.Settings.LatexCopy);
        s.Settings.LatexCopy = "dollar";
        Assert.Equal("dollar", s.Settings.LatexCopy);
        s.Settings.LatexCopy = "zzz";
        Assert.Equal("plain", s.Settings.LatexCopy);
        s.Settings.LatexCopy = "paren";
        s.Save();
        Assert.Equal("paren", NewStore().Settings.LatexCopy);
        Assert.Equal("paren", PanelStyle.From(NewStore()).LatexCopy);
    }
}

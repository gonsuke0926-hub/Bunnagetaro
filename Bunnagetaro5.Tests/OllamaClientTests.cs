using System.Collections.Generic;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// OllamaClientの解析部分と色の変換（ColorUtil）の確認。
/// 実際の通信はこのPCにOllamaが無い想定なので、解析関数だけを叩く。
/// </summary>
public class OllamaClientTests
{
    // ---- URL結合（_url） ----

    [Theory]
    [InlineData("http://localhost:11434", "/api/tags", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/", "/api/tags", "http://localhost:11434/api/tags")]
    [InlineData("localhost:11434", "/api/tags", "http://localhost:11434/api/tags")]
    [InlineData("https://ai.example.com//", "/api/tags", "https://ai.example.com/api/tags")]
    public void UrlNormalizesSchemeAndTrailingSlashes(string baseUrl, string path, string want)
    {
        Assert.Equal(want, OllamaClient.Url(baseUrl, path));
    }

    // ---- /api/tags の解析 ----

    private const string SampleJson = """
    {"models":[
      {"name":"gemma4:latest","size":8314873856},
      {"name":"","size":1},
      {"size":7},
      {"name":"qwen3:0.6b","size":"bad"}
    ]}
    """;

    [Fact]
    public void ParseTagNamesKeepsOrderAndEmptyNames()
    {
        // 名前が無い行も "" として並べる
        var names = OllamaClient.ParseTagNames(JsonNode.Parse(SampleJson));
        Assert.Equal(new[] { "gemma4:latest", "", "", "qwen3:0.6b" }, names);
    }

    [Fact]
    public void ParseTagInfoSkipsEmptyNameAndUsesZeroForBadSize()
    {
        var info = OllamaClient.ParseTagInfo(JsonNode.Parse(SampleJson));
        Assert.Equal(2, info.Count);
        Assert.Equal(8314873856L, info["gemma4:latest"]);
        Assert.Equal(0L, info["qwen3:0.6b"]); // 重さが読めなければ0（fmt_gbで空文字になる）
    }

    [Fact]
    public void ParseHandlesMissingModelsKey()
    {
        Assert.Empty(OllamaClient.ParseTagNames(JsonNode.Parse("{}")));
        Assert.Empty(OllamaClient.ParseTagInfo(JsonNode.Parse("{}")));
    }

    // ---- 導入済み判定（model_installed） ----

    [Theory]
    [InlineData("gemma4:latest", new[] { "gemma4:latest" }, true)]           // 完全一致
    [InlineData("gemma4", new[] { "gemma4:latest" }, true)]                   // tag省略
    [InlineData("gemma4", new[] { "gemma4" }, true)]                          // tag無しで入っている
    [InlineData("gemma4", new[] { "gemma4-mini:latest" }, false)]             // 別モデル
    [InlineData("gemma4:128k", new[] { "gemma4:latest" }, false)]             // tag指定は完全一致だけ
    [InlineData("", new[] { "gemma4:latest" }, false)]
    public void ModelInstalledCoversTagOmission(string model, string[] names, bool want)
    {
        Assert.Equal(want, OllamaClient.ModelInstalled(model, names));
    }

    // ---- GB見かけ（fmt_gb） ----

    [Theory]
    [InlineData(0L, "")]
    [InlineData(-1L, "")]
    [InlineData(1073741824L, "1.0GB")]  // 1GiB
    [InlineData(8314873856L, "7.7GB")]
    public void FmtGbMatchesOllamaDisplay(long nbytes, string want)
    {
        Assert.Equal(want, OllamaClient.FmtGb(nbytes));
    }

    [Fact]
    public void SizesLineShowsFirstSixOrNashi()
    {
        var info = new Dictionary<string, long> { ["a"] = 1073741824L, ["b"] = 0 };
        Assert.Equal("なし", OllamaClient.SizesLine(info, new List<string>()));
        Assert.Equal("a 1.0GB, b ", OllamaClient.SizesLine(info, new List<string> { "a", "b" }));
        var many = new List<string> { "1", "2", "3", "4", "5", "6", "7" };
        var line = OllamaClient.SizesLine(new Dictionary<string, long>(), many);
        Assert.Equal("1 , 2 , 3 , 4 , 5 , 6 ", line); // 7件目は見せない
    }
}

/// <summary>設定欄の色（#rrggbb 判定・Win32 COLORREF変換）。</summary>
public class ColorUtilTests
{
    [Theory]
    [InlineData("#202020", true)]
    [InlineData("#F5F5F5", true)]
    [InlineData("202020", false)]
    [InlineData("#202", false)]
    [InlineData("#12345g", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHexColorMatchesPythonRegex(string? s, bool want)
    {
        Assert.Equal(want, ColorUtil.IsHexColor(s));
    }

    [Fact]
    public void ColorRefRoundTrip()
    {
        // COLORREFは0x00BBGGRR（赤と緑が入れ替わる）
        Assert.Equal(0x0088FFu, ColorUtil.ToColorRef("#ff8800"));
        Assert.Equal("#ff8800", ColorUtil.FromColorRef(0x0088FFu));
        Assert.Null(ColorUtil.ToColorRef("zzz"));
    }

    [Fact]
    public void ToBrushOnlyForValidHex()
    {
        Assert.Null(ColorUtil.ToBrush("#12"));
        var b = ColorUtil.ToBrush("#c42b1c");
        Assert.NotNull(b);
        Assert.Equal((byte)0xC4, b!.Color.R);
        Assert.Equal((byte)0x1C, b.Color.B);
    }
}

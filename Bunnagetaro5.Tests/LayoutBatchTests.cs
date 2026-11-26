using System.Linq;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J96: 位置の反映で、段落を塊に分けて頼む決まり（OCR_LAYOUT_ROUND1 2.3。sonoba の README を参考）</summary>
public class LayoutBatchTests
{
    [Fact]
    public void J96_訳す物と訳さない物()
    {
        Assert.True(TranslatePipeline.NeedsTranslation("Hello world"));
        Assert.True(TranslatePipeline.NeedsTranslation("営業中"));
        Assert.False(TranslatePipeline.NeedsTranslation("1,284.6"));
        Assert.False(TranslatePipeline.NeedsTranslation(" -4.3% "));
        Assert.False(TranslatePipeline.NeedsTranslation("https://example.com/a?b=1"));
        Assert.False(TranslatePipeline.NeedsTranslation(""));
    }

    [Fact]
    public void J96_短いラベルは3語以下_日本語などは8文字以下()
    {
        Assert.True(TranslatePipeline.IsShortLabel("Open till dawn"));
        Assert.False(TranslatePipeline.IsShortLabel("Do not trust the ferryman"));
        Assert.True(TranslatePipeline.IsShortLabel("夜明けまで営業"));
        Assert.False(TranslatePipeline.IsShortLabel("夜明けまで営業しています。"));
    }

    [Fact]
    public void J96_塊は約240文字まで_短いラベルと訳さない物は1つずつ()
    {
        string longP = new string('a', 100) + " b c d";   // 4語・約106文字
        var src = new[] { longP, longP, longP, "Menu", "12:30", longP };
        var b = TranslatePipeline.LayoutBatches(src);
        Assert.Equal(new[]
        {
            new TranslatePipeline.LayoutBatch(0, 2, true),    // 106+106 ≦ 240、3つ目は超える
            new TranslatePipeline.LayoutBatch(2, 1, true),
            new TranslatePipeline.LayoutBatch(3, 1, true),    // 短いラベル
            new TranslatePipeline.LayoutBatch(4, 1, false),   // 訳さない
            new TranslatePipeline.LayoutBatch(5, 1, true),
        }, b.ToArray());
        // 1つで 240 文字を超える段落も、その1つで塊になる
        Assert.Equal(new[] { new TranslatePipeline.LayoutBatch(0, 1, true) },
                     TranslatePipeline.LayoutBatches(new[] { new string('x', 300) + " y z w" }).ToArray());
        Assert.Empty(TranslatePipeline.LayoutBatches(System.Array.Empty<string>()));
    }

    [Fact]
    public void J96_段落は1行にする()
    {
        Assert.Equal("a b c", TranslatePipeline.OneLine(" a\r\n  b\nc "));
        Assert.Equal("abc", TranslatePipeline.OneLine("abc"));
    }

    [Fact]
    public void J117_位置の反映のログからも長さの比率を学習する()
    {
        var entries = new[]
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["status"] = "ok",
                ["model"] = "m1",
                ["text"] = new string('あ', 50),
                ["diag"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["pipeline"] = "ocr_layout",
                    ["ocr_text"] = new string('a', 100),
                },
            },
        };
        Assert.Equal(2.0, TranslatePipeline.LearnedLengthRatio(entries, "m1"), 3);
    }
}

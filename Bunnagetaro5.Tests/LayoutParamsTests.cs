using System.Linq;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J100・J109: 位置の反映の細かい値（全体で1つ・上級者モード）。J109 から訳の頼み方の3つだけ</summary>
public sealed class LayoutParamsTests
{
    [Fact]
    public void J100_無ければ既定_範囲の外は範囲に収める()
    {
        Assert.Equal(LayoutParams.Default, LayoutParams.From(new JsonObject()));
        Assert.Equal(LayoutParams.Default, LayoutParams.From(null));
        var p = LayoutParams.From(new JsonObject { ["layout_score_thresh"] = 0.02, ["layout_batch_chars"] = -5, ["layout_label_words"] = 4.6, ["layout_label_chars"] = "x" });
        Assert.Equal(0.1, p.ScoreThresh, 3);
        Assert.Equal(0.95, LayoutParams.From(new JsonObject { ["layout_score_thresh"] = 1.5 }).ScoreThresh, 3);
        Assert.Equal(0, p.BatchChars);
        Assert.Equal(5, p.LabelWords);
        Assert.Equal(LayoutParams.Default.LabelChars, p.LabelChars);
        Assert.Equal(4, LayoutParams.Specs.Length);   // 修正J116: 区画判定のしきい値＋訳の頼み方の3つ
    }

    [Fact]
    public void J100_頼む文字数と短いラベルの長さが効く()
    {
        var src = new[] { "Do not trust the ferryman", "Keep your lantern lit at night", "Beware of the tide" };
        Assert.Single(TranslatePipeline.LayoutBatches(src).Where(b => b.Translate));
        var one = TranslatePipeline.LayoutBatches(src, LayoutParams.Default with { BatchChars = 0 });
        Assert.Equal(3, one.Count);
        Assert.All(one, b => Assert.Equal(1, b.Count));
        Assert.True(TranslatePipeline.IsShortLabel("Beware of the tide", LayoutParams.Default with { LabelWords = 4 }));
        Assert.False(TranslatePipeline.IsShortLabel("夜明けまで営業", LayoutParams.Default with { LabelChars = 3 }));
    }
}

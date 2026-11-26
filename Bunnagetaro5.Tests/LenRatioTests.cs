using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 修正J9: 書き起こし文を訳で上書きする速さを、過去の訳の長さから覚える。
/// </summary>
public class LenRatioTests
{
    [Fact]
    public void J9_比率1なら今までと同じ合成()
    {
        Assert.Equal("xyz\nqhij", TextUtil.MergeRetryStream("abcdef\nghij", "xyz\nq", 1.0));
        Assert.Equal("xyc\ndef\nghi", TextUtil.MergeRetryStream("abc\ndef\nghi", "xy", 1.0));
    }

    [Fact]
    public void J9_比率2なら訳1文字で旧を2文字消す()
    {
        // 「あい」2文字で "abcd" の4文字が消える
        Assert.Equal("あいefgh", TextUtil.MergeRetryStream("abcdefgh", "あい", 2.0));
        // 旧行より先まで進んだら残りは無し（はみ出さない）
        Assert.Equal("あいうえお", TextUtil.MergeRetryStream("abcdefgh", "あいうえお", 2.0));
        // 完了した行・まだの行の扱いは今まで通り
        Assert.Equal("訳1\nあdef\nghi", TextUtil.MergeRetryStream("abc\nabcdef\nghi", "訳1\nあ", 3.0));
    }

    [Fact]
    public void J9_おかしい比率は1として扱う()
    {
        Assert.Equal("xyc", TextUtil.MergeRetryStream("abc", "xy", 0));
        Assert.Equal("xyc", TextUtil.MergeRetryStream("abc", "xy", double.NaN));
        Assert.Equal("xyc", TextUtil.MergeRetryStream("abc", "xy", double.PositiveInfinity));
    }

    private static JsonObject Entry(string model, string src, string dst,
        string status = "ok", string pipeline = "ocr_split") => new()
    {
        ["model"] = model,
        ["status"] = status,
        ["diag"] = new JsonObject
        {
            ["pipeline"] = pipeline,
            ["chunks"] = new JsonArray(new JsonObject { ["src"] = src, ["dst"] = dst }),
        },
    };

    [Fact]
    public void J9_覚えた事が無ければ1()
    {
        Assert.Equal(1.0, TranslatePipeline.LearnedLengthRatio(new List<JsonObject>(), "plamo"));
        // 別のモデル・失敗・画像直接の経路は数えない
        var others = new List<JsonObject>
        {
            Entry("other", new string('a', 300), new string('あ', 100)),
            Entry("plamo", new string('a', 300), new string('あ', 100), status: "failed"),
            Entry("plamo", new string('a', 300), new string('あ', 100), pipeline: "direct_image"),
        };
        Assert.Equal(1.0, TranslatePipeline.LearnedLengthRatio(others, "plamo"));
    }

    [Fact]
    public void J9_同じモデルの訳の長さから比率を覚える()
    {
        var entries = new List<JsonObject>
        {
            Entry("plamo", new string('a', 285), new string('あ', 126)),
            Entry("plamo", new string('a', 279), new string('あ', 113)),
            Entry("plamo", new string('a', 189), new string('あ', 72)),
        };
        var r = TranslatePipeline.LearnedLengthRatio(entries, "plamo");
        Assert.InRange(r, 2.2, 2.5);
    }

    [Fact]
    public void J9_短い外れ1件には引きずられない()
    {
        var entries = new List<JsonObject>
        {
            Entry("plamo", "ok", "ok"),   // 短い文の比率1
            Entry("plamo", new string('a', 280), new string('あ', 120)),
            Entry("plamo", new string('a', 230), new string('あ', 100)),
        };
        Assert.InRange(TranslatePipeline.LearnedLengthRatio(entries, "plamo"), 2.2, 2.4);
    }

    [Fact]
    public void J9_直近の件だけを見るので言語を変えるとなじむ()
    {
        // 新→古の順。直近10件は日→英（0.4）、それより古い英→日（2.3）は見ない
        var entries = Enumerable.Range(0, 10)
            .Select(_ => Entry("plamo", new string('あ', 40), new string('a', 100)))
            .Concat(Enumerable.Range(0, 10)
                .Select(_ => Entry("plamo", new string('a', 230), new string('あ', 100))))
            .ToList();
        Assert.Equal(0.4, TranslatePipeline.LearnedLengthRatio(entries, "plamo"), 3);
    }

    [Fact]
    public void J9_cfgの比率が無い時は1()
    {
        Assert.Equal(1.0, TranslatePipeline.LengthRatioOf(new JsonObject()));
        Assert.Equal(2.5, TranslatePipeline.LengthRatioOf(new JsonObject { [TranslatePipeline.LengthRatioKey] = 2.5 }));
        Assert.Equal(1.0, TranslatePipeline.LengthRatioOf(new JsonObject { [TranslatePipeline.LengthRatioKey] = -1.0 }));
    }
}

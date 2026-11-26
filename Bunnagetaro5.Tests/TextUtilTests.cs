using System.Collections.Generic;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// TextUtil（手順9・Python版 tw_text.py と同じ入力で同じ結果）。
/// 期待値は Python版の関数（_line_is_untranslated / _merge_retry_stream /
/// _sentence_chunks / _chunk_chars_for_model）をそのまま走らせて採ったもの。
/// </summary>
public class TextUtilTests
{
    [Fact]
    public void LineIsUntranslated_Python版と同じ入力で同じ結果()
    {
        var inputs = new[]
        {
            "Hello world, this is a test.",
            "日本語だけの行です。",
            "『The Chinese Repository』などの新聞",
            "The Presbyterian Historical Society",
            "مرحبا بالعالم",                    // アラビア
            "Привет мир",                        // キリル
            "プライバシーポリシー：http://www.macrotappapps.com/privacy-policy-apps.html",
            "The Annual Report of the Commissioner of Education",
            "翻訳『The report says that the economy is improving』が続く",
            "この文章は this text is not translated で終わる",
            "α 1つだけ",
            "OK",
            "Status: 読み込み中...",
            "",
            "   ",
            "it's fine here",
            "ジョージ・スティーブンス",
            "（Buzan and Lawson 2015; Acharya and Buzan 2019）",
            "The Times",
            "hello",
            "Wait!!  Please",
            "שלום עולם",                         // ヘブライ
            "สวัสดีชาวโลก",                      // タイ
            "Посмотри на это предложение сейчас",
        };
        var expected = new[]
        {
            true, false, false, true, true, true, false, true, false, true,
            false, false, false, false, false, true, false, true, true, false,
            false, true, true, true,
        };
        Assert.Equal(expected, System.Array.ConvertAll(inputs, TextUtil.LineIsUntranslated));
    }

    [Fact]
    public void HasUntranslatedNonJapanese_1行でも未訳なら真()
    {
        Assert.True(TextUtil.HasUntranslatedNonJapanese("Hello world, this is a test.\n日本語"));
        Assert.False(TextUtil.HasUntranslatedNonJapanese("日本語の行だけ。\n『The Times』の話"));
    }

    [Fact]
    public void MergeRetryStream_Python版と同じ合成()
    {
        Assert.Equal("abcdef\nghij", TextUtil.MergeRetryStream("abcdef\nghij", ""));
        Assert.Equal("xyz\nqhij", TextUtil.MergeRetryStream("abcdef\nghij", "xyz\nq"));
        Assert.Equal("xyc\ndef\nghi", TextUtil.MergeRetryStream("abc\ndef\nghi", "xy"));
        // 新しい行が旧行より長い場合は残り末尾なしでそのまま置き換え
        Assert.Equal("abcdefgh\nnext", TextUtil.MergeRetryStream("abc\ndef", "abcdefgh\nnext"));
        // 新しい行数が旧より多い場合は旧の残り行は消える
        Assert.Equal("one\ntwo\nthree", TextUtil.MergeRetryStream("one", "one\ntwo\nthree"));
    }

    [Fact]
    public void SentenceChunks_Python版と同じ区切り()
    {
        Assert.Equal(new[] { "A. B. C." }, TextUtil.SentenceChunks("A. B. C.", 10));
        Assert.Equal(new[] { "A. B.", "C." }, TextUtil.SentenceChunks("A. B. C.", 5));
        Assert.Equal(new[] { "line1 line2", "para2 b" }, TextUtil.SentenceChunks("line1\nline2\n\npara2 b", 100));
        Assert.Empty(TextUtil.SentenceChunks("  \n\n  ", 100));
        Assert.Equal(
            new[] { "Hello world.", "This is a much longer sentence that keeps going and going." },
            TextUtil.SentenceChunks("Hello world. This is a much longer sentence that keeps going and going.", 30));
        Assert.Equal(
            new[] { "no punctuation here at all and it goes on and on" },
            TextUtil.SentenceChunks("no punctuation here at all and it goes on and on", 20));
        // 既定の上限は600（Python版 _TEXT_CHUNK_CHARS）
        Assert.Equal(600, TextUtil.TextChunkChars);
    }

    [Fact]
    public void ChunkCharsForModel_モデル名の数字で目安を変える()
    {
        Assert.Equal(600, TextUtil.ChunkCharsForModel("qwen2.5:7b"));
        Assert.Equal(600, TextUtil.ChunkCharsForModel("llama3:8b"));
        Assert.Equal(320, TextUtil.ChunkCharsForModel("x:4b"));
        Assert.Equal(1200, TextUtil.ChunkCharsForModel("foo:32b"));
        Assert.Equal(600, TextUtil.ChunkCharsForModel("mistral"));
        Assert.Equal(600, TextUtil.ChunkCharsForModel("my7b"));   // 数字の直前に : - _ でないなら拾わない
        Assert.Equal(600, TextUtil.ChunkCharsForModel("my-7b"));  // 7bは6.0超なので600
        Assert.Equal(320, TextUtil.ChunkCharsForModel("1.5b"));
        Assert.Equal(600, TextUtil.ChunkCharsForModel(""));
        Assert.Equal(600, TextUtil.ChunkCharsForModel("Gemma4:E2B"));
    }
}

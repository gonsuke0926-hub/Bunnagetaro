using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>
/// 文字の処理。未訳行の判定と、途中訳の合成。
/// </summary>
public static class TextUtil
{
    // 日本語の文字（ひらがな・カタカナ・漢字・々〆ヵヶ）
    private static readonly Regex JapaneseRe = new("[ぁ-んァ-ヶ一-龯々〆ヵヶ]", RegexOptions.Compiled);

    private static readonly Regex EnglishWordRe = new("[A-Za-z]+(?:['\u2019][A-Za-z]+)?", RegexOptions.Compiled);

    // よくある英語の語（これがある＝名札やURLではなく英語の文だと見る）
    private static readonly HashSet<string> EnglishCommonWords = new(
        ("a an and are as at be been but by can could did do does for from had has " +
         "have he her here him his how i if in is it its me my no not of on or our " +
         "she so than that the their them then there they this to up us was we were " +
         "what when where which who will with would you your").Split(' '));

    // v3.3: 翻訳対象は英語だけではない（ハングル・キリル・ギリシャ・アラビア・
    // ヘブライ・タイ・デヴァナーガリもそのまま残っていたら見つける）。
    // 3文字以上の連続を「その文字での単語」とみなす。漢字・カタカナは日本語と
    // 区別できないので対象外。
    private static readonly Regex ForeignRunRe = new(
        "[\u0400-\u04FF"                                  // キリル
        + "\u0370-\u03FF"                                // ギリシャ
        + "\uAC00-\uD7A3\u1100-\u11FF\u3130-\u318F"      // ハングル
        + "\u0590-\u05FF"                                // ヘブライ
        + "\u0600-\u06FF\u0750-\u077F"                   // アラビア
        + "\u0E00-\u0E7F"                                // タイ
        + "\u0900-\u097F"                                // デヴァナーガリ
        + "]{3,}", RegexOptions.Compiled);

    // v3.8: 書名・引用・出典・URLは日本語へ訳さなくていいので、
    // その外に英語の文が残っているかだけを見る。
    private static readonly Regex UrlRe =
        new(@"(?:https?://|ftp://|www\.)[^\s<>「」『』()（）]+", RegexOptions.Compiled);

    private static readonly Regex WrapRe = new(
        "[「『\u201C\"(（【\\[][^「」『』\u201C\u201D\"()（）【】\\[\\]]*[」』\u201D\")）】\\]]",
        RegexOptions.Compiled);

    // [ ], [ ] の間には半角空白とタブだけ
    private static readonly Regex EnglishRunRe =
        new("[A-Za-z][A-Za-z'\u2019]*(?:[ \t]+[A-Za-z][A-Za-z'\u2019]*)*", RegexOptions.Compiled);

    private const int EnglishRunMinWords = 4;
    private const int EnglishRunMinCommon = 2;

    /// <summary>日本語を含む行で、引用・括弧・URLの外に英語の文が残っているか。
    /// 4語以上かたまって続き、その塊に一般的な英語の語が2つ以上あるものを拾う
    /// （名前だけの「Presbyterian Historical Society」などは取りちがえない）。</summary>
    public static bool EnglishRunLeftUntranslated(string line)
    {
        var stripped = WrapRe.Replace(UrlRe.Replace(line, " "), " ");
        foreach (Match m in EnglishRunRe.Matches(stripped))
        {
            var words = m.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= EnglishRunMinWords &&
                words.Count(w => EnglishCommonWords.Contains(w.ToLowerInvariant())) >= EnglishRunMinCommon)
                return true;
        }
        return false;
    }

    /// <summary>1行が元の言語のままか（日本語行は名前やラベルを許す）。
    /// v3.8の形（日本語を含む行は引用・括弧・URLを落としてから4語＋常见2語）。</summary>
    public static bool LineIsUntranslated(string line)
    {
        if (ForeignRunRe.IsMatch(line))
            return true;
        var words = new List<string>();
        foreach (Match m in EnglishWordRe.Matches(line))
            words.Add(m.Value.ToLowerInvariant().Replace('\u2019', '\''));
        var letterCount = words.Sum(w => w.Length);
        if (words.Count < 2 || letterCount < 8)
            return false;
        var commonCount = words.Count(w => EnglishCommonWords.Contains(w));
        if (JapaneseRe.IsMatch(line))
            // 日本語が本体の行はv3.8の見方
            return EnglishRunLeftUntranslated(line);
        var rstrip = line.TrimEnd();
        return commonCount > 0
            || rstrip.EndsWith(".", StringComparison.Ordinal)
            || rstrip.EndsWith("!", StringComparison.Ordinal)
            || rstrip.EndsWith("?", StringComparison.Ordinal)
            || rstrip.EndsWith(";", StringComparison.Ordinal)
            || rstrip.EndsWith(":", StringComparison.Ordinal)
            || letterCount >= 24;
    }

    /// <summary>訳文に元の言語のままの行が残っているか。</summary>
    public static bool HasUntranslatedNonJapanese(string text)
    {
        foreach (var line in Regex.Split(text, @"\r\n|\r|\n"))
            if (LineIsUntranslated(line))
                return true;
        return false;
    }

    /// <summary>途中訳(accum)で初回テキスト(first)を上から上書き合成する。
    /// 完了行は新しい訳行で置換、生成中の行は先頭から新しい文字で上書きして
    /// 残り末尾だけ旧行を残し、未到達の行は旧行をそのまま残す。
    /// ratio は「新しい1文字で旧の何文字を消すか」。英文→和訳なら 2 前後で、
    /// 1 のままだと訳が旧行を押し出し、書き終わりに残りがまとめて消える。
    /// 呼び手が過去の訳の長さから覚えた値を渡す（TranslatePipeline.LearnedLengthRatio）。</summary>
    public static string MergeRetryStream(string firstText, string accum, double ratio = 1.0)
    {
        if (accum.Length == 0)
            return firstText;
        if (!(ratio > 0) || double.IsInfinity(ratio)) ratio = 1.0;
        var oldLines = firstText.Split('\n');
        var newLines = accum.Split('\n');
        var k = newLines.Length;
        var merged = new List<string>(newLines[..(k - 1)]);
        var cur = newLines[k - 1];
        if (k - 1 < oldLines.Length)
        {
            var old = oldLines[k - 1];
            var used = (int)Math.Min(old.Length, Math.Round(cur.Length * ratio));
            merged.Add(cur + old[used..]);
        }
        else
            merged.Add(cur);
        if (k < oldLines.Length)
            merged.AddRange(oldLines[k..]);
        return string.Join("\n", merged);
    }

    /// <summary>書き起こし文を文の切れ目で区切る（通常は分割しないが、
    /// 割らざるを得ない状況が来た時のために残している）。</summary>
    public const int TextChunkChars = 600;

    public static List<string> SentenceChunks(string text, int maxChars = TextChunkChars)
    {
        var chunks = new List<string>();
        foreach (var paragraphRaw in Regex.Split(text.Trim(), @"\n\s*\n"))
        {
            var paragraph = string.Join(" ",
                paragraphRaw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (paragraph.Length == 0)
                continue;
            var current = "";
            foreach (var sentence in Regex.Split(paragraph, @"(?<=[.!?])\s+"))
            {
                if (sentence.Length == 0)
                    continue;
                if (current.Length > 0 && current.Length + 1 + sentence.Length > maxChars)
                {
                    chunks.Add(current);
                    current = "";
                }
                if (current.Length > 0)
                    current += " ";
                current += sentence;
            }
            if (current.Length > 0)
                chunks.Add(current);
        }
        return chunks;
    }

    /// <summary>モデル名の数字（例 qwen2.5:7b）から1チャンクの目安文字数。</summary>
    public static int ChunkCharsForModel(string model)
    {
        var m = new Regex(@"(?:[:\-_]|^)(\d+(?:\.\d+)?)b\b").Match((model ?? "").Trim().ToLowerInvariant());
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var paramsB))
        {
            if (paramsB <= 6.0) return 320;
            if (paramsB <= 16.0) return 600;
            return 1200;
        }
        return TextChunkChars;
    }
}

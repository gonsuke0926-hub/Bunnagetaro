using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>解答欄の本文の区切れ目。
/// Text: そのまま出す文字（Texは空）。Inline: $...$ の式。
/// Display: $$...$$ または \[...\] の独立行の式。</summary>
public enum MathSegKind { Text, Inline, Display }

/// <summary>LaTeX のコピーの形（設定 latex_copy）</summary>
public enum LatexCopyForm { Plain, Dollar, Paren }

/// <summary>ひとつ分の区切れ目。Raw は元の文字そのまま（デリミタ込み・
/// 式が表示できないとき原文字へ戻すのに使う）。Tex はデリミタの中身。</summary>
public readonly record struct MathSeg(MathSegKind Kind, string Raw, string Tex);

/// <summary>本文を「そのままの文字」と「式」へ分ける（$...$ は文中、$$..$$・\[..\] は独立行）。
/// 閉じ記号が来ていない部分は式にせず文字のまま返す（ストリーミング中に文字で出る約束）。
/// 空の式（$$ の直後閉じ等）も文字のまま。文中の $...$ は行をまたがない
/// （値段の $ が2行またぎで式に見えるのを防ぐ）。</summary>
public static class MathText
{
    public static List<MathSeg> Split(string s)
    {
        var segs = new List<MathSeg>();
        var text = new StringBuilder();
        int i = 0;
        int n = s.Length;
        while (i < n)
        {
            if (s[i] == '\\' && i + 1 < n)
            {
                char nx = s[i + 1];
                if (nx == '[')
                {
                    int end = FindClose(s, i + 2, "\\]");
                    if (end >= 0)
                    {
                        string inner = s.Substring(i + 2, end - i - 2);
                        if (inner.Trim().Length > 0)
                        {
                            Flush(segs, text);
                            segs.Add(new MathSeg(MathSegKind.Display, s.Substring(i, end + 2 - i), inner));
                            i = end + 2;
                            continue;
                        }
                    }
                    text.Append("\\[");
                    i += 2;
                    continue;
                }
                if (nx == '$')
                {
                    text.Append("\\$");   // エスケープされた $ はただの文字
                    i += 2;
                    continue;
                }
                text.Append(s[i]);
                i++;
                continue;
            }
            if (s[i] == '$')
            {
                bool dbl = i + 1 < n && s[i + 1] == '$';
                int end = FindClose(s, i + (dbl ? 2 : 1), dbl ? "$$" : "$");
                if (dbl && end >= 0)
                {
                    string inner = s.Substring(i + 2, end - i - 2);
                    if (inner.Trim().Length > 0)
                    {
                        Flush(segs, text);
                        segs.Add(new MathSeg(MathSegKind.Display, s.Substring(i, end + 2 - i), inner));
                        i = end + 2;
                        continue;
                    }
                }
                if (!dbl && end >= 0)
                {
                    string inner = s.Substring(i + 1, end - i - 1);
                    if (ValidInline(inner))
                    {
                        Flush(segs, text);
                        segs.Add(new MathSeg(MathSegKind.Inline, s.Substring(i, end + 1 - i), inner));
                        i = end + 1;
                        continue;
                    }
                }
                // 閉じが無い・中身が空: このデリミタは文字。1個ぶんだけ足して
                // 先へ進む（後ろに本当の式の頭が控えていることがある）
                text.Append('$');
                if (dbl) text.Append('$');
                i += dbl ? 2 : 1;
                continue;
            }
            text.Append(s[i]);
            i++;
        }
        Flush(segs, text);
        return segs;
    }

    /// <summary>閉じデリミタを探す。from からtermまで。中のバックスケープ
    /// （\$ など）は閉じとして数えない。term=="$" のとき（文中の式）は
    /// 改行で打ち切り（行をまたがない）。</summary>
    private static int FindClose(string s, int from, string term)
    {
        int j = from;
        bool dollar = term == "$";
        bool ddollar = term == "$$";
        while (j < s.Length)
        {
            char c = s[j];
            if (c == '\\')
            {
                // エスケープされた1文字（\$ や \\）。閉じ候補の次へ進む
                if (ddollar || dollar) { j += 2; continue; }
                if (c == '\\' && j + 1 < s.Length && s[j + 1] == ']') return j;   // \[ の閉じ
                j += 2;
                continue;
            }
            if (dollar)
            {
                if (c == '\n') return -1;
                if (c == '$') return j;
                j++;
                continue;
            }
            if (ddollar)
            {
                if (c == '$' && j + 1 < s.Length && s[j + 1] == '$') return j;
                j++;
                continue;
            }
            // term == "\\]"
            if (c == '\\' && j + 1 < s.Length && s[j + 1] == ']') return j;
            j++;
        }
        return -1;
    }

    /// <summary>文中の $...$ として認める形か。両端が空白だと値段の $ などで
    /// 化ける（"cost $5 or $10" を式にしない）</summary>
    private static bool ValidInline(string inner)
        => inner.Length > 0 && !char.IsWhiteSpace(inner[0]) && !char.IsWhiteSpace(inner[^1]);

    // ---------------- LaTeX のコピーの形 ----------------

    /// <summary>設定 latex_copy の値。plain=中身だけ（既定）／dollar=$ $$ で囲む／paren=\( \) \[ \] で囲む</summary>
    public static LatexCopyForm ParseCopyForm(string? v) => v switch
    {
        "dollar" => LatexCopyForm.Dollar,
        "paren" => LatexCopyForm.Paren,
        _ => LatexCopyForm.Plain,
    };

    public static string CopyFormKey(LatexCopyForm f) => f switch
    {
        LatexCopyForm.Dollar => "dollar",
        LatexCopyForm.Paren => "paren",
        _ => "plain",
    };

    /// <summary>式1つを選んだ形にする（Plain は中身だけ。文中の式は $ / \( \)、独立行は $$ / \[ \]）</summary>
    public static string Wrap(string tex, bool display, LatexCopyForm form)
    {
        string t = tex.Trim();
        return form switch
        {
            LatexCopyForm.Dollar => display ? "$$" + t + "$$" : "$" + t + "$",
            LatexCopyForm.Paren => display ? "\\[" + t + "\\]" : "\\(" + t + "\\)",
            _ => t,
        };
    }

    /// <summary>全文コピー・選択コピーの文字列を選んだ形にする（text は $ 付きなどの原文）。
    /// 式だけ（間は空白だけ）の答えは、式を選んだ形にして改行でつなぐ。
    /// 文章と式が混ざっている時は、Plain でも式の区切りが分からなくなるので原文のまま（$ / $$ / \[ \] 付き）、
    /// それ以外の形は式をその形で囲み直す。式が無ければそのまま</summary>
    public static string ApplyCopyForm(string text, LatexCopyForm form)
    {
        var segs = Split(text);
        if (!segs.Exists(s => s.Kind != MathSegKind.Text)) return text;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        bool onlyMath = segs.TrueForAll(s => s.Kind != MathSegKind.Text || s.Raw.Trim().Length == 0);
        if (onlyMath)
        {
            var parts = new List<string>();
            foreach (var s in segs)
                if (s.Kind != MathSegKind.Text) parts.Add(Wrap(s.Tex, s.Kind == MathSegKind.Display, form));
            return string.Join(nl, parts);
        }
        if (form == LatexCopyForm.Plain) return text;
        var sb = new StringBuilder();
        foreach (var s in segs)
            sb.Append(s.Kind == MathSegKind.Text ? s.Raw : Wrap(s.Tex, s.Kind == MathSegKind.Display, form));
        return sb.ToString();
    }

    // ---------------- 式番号 ----------------

    private static readonly Regex TagRe = new(@"\\tag(\*?)\s*\{((?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.Compiled);

    /// <summary>描く用の式の式番号。\tag{X} は「\qquad\text{(X)}」、\tag*{X} は「\qquad\text{X}」にする。
    /// KaTeX は \tag を式の幅いっぱいの右端に置くので、式の幅ぴったりの箱で描くと式の右端に重なる
    /// （GLM-OCR が式番号を \tag で返す）。\tag が2つある（aligned の中など）時も全部置き換える。
    /// コピー用の原文字（MathSeg.Raw）には使わない（LaTeX の文書では \tag が正しい書き方）</summary>
    public static string TagForDisplay(string tex) =>
        tex.Contains("\\tag", StringComparison.Ordinal)
            ? TagRe.Replace(tex, m => m.Groups[1].Value == "*"
                ? "\\qquad\\text{" + m.Groups[2].Value + "}"
                : "\\qquad\\text{(" + m.Groups[2].Value + ")}")
            : tex;

    /// <summary>式の区切れ目の描く用の中身（Tex）だけ式番号を置き換えたもの（Raw はそのまま）</summary>
    public static List<MathSeg> WithDisplayTags(List<MathSeg> segs)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Kind == MathSegKind.Text) continue;
            string t = TagForDisplay(s.Tex);
            if (!ReferenceEquals(t, s.Tex)) segs[i] = s with { Tex = t };
        }
        return segs;
    }

    private static void Flush(List<MathSeg> segs, StringBuilder text)
    {
        if (text.Length == 0) return;
        segs.Add(new MathSeg(MathSegKind.Text, text.ToString(), ""));
        text.Clear();
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>表のセルの中の区切れ目。Text: そのままの文字。Sup・Sub: 単純な上付き・下付きの文字。
/// Math: そのほかの式（Text に中身の TeX、Raw に \( \) や $ 付きの原文字）</summary>
public enum CellSegKind { Text, Sup, Sub, Math }

public readonly record struct CellSeg(CellSegKind Kind, string Text, string Raw);

/// <summary>表のセルの中の数式（AI がセルの中に \(^{a}\) などの LaTeX を混ぜて返す。PaddleOCR-VL の脚注の印など）。
/// セルの中の \( … \) と $ … $（同じ行の中だけ・両端が空白でない）を式として扱い、
/// 文字・数字だけの上付き・下付き（^{a}・^2・_{12}・a^{2}）は Sup・Sub に、そのほかは Math にする</summary>
public static class CellMath
{
    // 上付き・下付きにしてよい中身（文字・数字・+ - * , . 空白）。\ や { } が残る物は式として描く
    private static readonly Regex ScriptArg = new(@"^[A-Za-z0-9+\-*,.′†‡ ]+$", RegexOptions.Compiled);
    private static readonly Regex ScriptForm = new(@"^(?<base>[A-Za-z0-9]*)\s*(?<op>[\^_])\s*(?<arg>\{.*\}|[A-Za-z0-9*]|\\[A-Za-z]+)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>セルに式の気配があるか（\( か $ が無ければ false。早い判定）</summary>
    public static bool HasMath(string s) => s.Contains("\\(", StringComparison.Ordinal) || s.Contains('$');

    /// <summary>セルの文字を区切れ目へ。式が無ければ Text 1つ</summary>
    public static List<CellSeg> Split(string s)
    {
        var segs = new List<CellSeg>();
        var text = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int end = -1, open = 0, close = 0;
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '(')
            {
                end = s.IndexOf("\\)", i + 2, StringComparison.Ordinal);
                open = 2; close = 2;
            }
            else if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '$')
            {
                text.Append("\\$");   // エスケープされた $ はただの文字
                i += 2;
                continue;
            }
            else if (s[i] == '$' && !(i + 1 < s.Length && s[i + 1] == '$'))
            {
                end = s.IndexOf('$', i + 1);
                if (end >= 0 && s.IndexOf('\n', i + 1, end - i - 1) >= 0) end = -1;   // 行をまたがない
                open = 1; close = 1;
            }
            if (end > i)
            {
                string inner = s.Substring(i + open, end - i - open);
                bool ok = inner.Trim().Length > 0 && (open == 2 || (!char.IsWhiteSpace(inner[0]) && !char.IsWhiteSpace(inner[^1])));
                if (ok)
                {
                    Flush(segs, text);
                    AddMath(segs, inner, s.Substring(i, end + close - i));
                    i = end + close;
                    continue;
                }
            }
            text.Append(s[i]);
            i++;
        }
        Flush(segs, text);
        return segs;
    }

    private static void AddMath(List<CellSeg> segs, string inner, string raw)
    {
        var m = ScriptForm.Match(inner.Trim());
        if (m.Success)
        {
            string arg = Unbrace(m.Groups["arg"].Value).Trim();
            arg = arg.Replace("\\ast", "*").Replace("\\dagger", "†").Replace("\\ddagger", "‡").Replace("\\prime", "′");
            if (arg.Length > 0 && ScriptArg.IsMatch(arg))
            {
                string b = m.Groups["base"].Value;
                if (b.Length > 0) segs.Add(new CellSeg(CellSegKind.Text, b, b));
                segs.Add(new CellSeg(m.Groups["op"].Value == "^" ? CellSegKind.Sup : CellSegKind.Sub, arg, raw));
                return;
            }
        }
        segs.Add(new CellSeg(CellSegKind.Math, inner.Trim(), raw));
    }

    /// <summary>{{a}} → a（外側の波かっこが対になっている間だけ外す）</summary>
    private static string Unbrace(string s)
    {
        s = s.Trim();
        while (s.Length >= 2 && s[0] == '{' && s[^1] == '}' && Balanced(s.Substring(1, s.Length - 2)))
            s = s.Substring(1, s.Length - 2).Trim();
        return s;
    }

    private static bool Balanced(string s)
    {
        int d = 0;
        foreach (char c in s)
        {
            if (c == '{') d++;
            else if (c == '}' && --d < 0) return false;
        }
        return d == 0;
    }

    private static void Flush(List<CellSeg> segs, StringBuilder text)
    {
        if (text.Length == 0) return;
        string t = text.ToString();
        segs.Add(new CellSeg(CellSegKind.Text, t, t));
        text.Clear();
    }

    // ---------------- コピー ----------------

    private const string SupFrom = "0123456789+-=()abcdefghijklmnoprstuvwxyzABDEGHIJKLMNOPRTUW";
    private const string SupTo = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁᵂ";
    private const string SubFrom = "0123456789+-=()aehijklmnoprstuvx";
    private const string SubTo = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";

    /// <summary>上付き・下付きの文字にする。1文字でも対応が無ければ ^a・^(ab)（下付きは _）</summary>
    public static string ScriptText(string arg, bool sup)
    {
        string from = sup ? SupFrom : SubFrom, to = sup ? SupTo : SubTo;
        var sb = new StringBuilder();
        foreach (char c in arg)
        {
            int k = from.IndexOf(c);
            if (k < 0)
            {
                string mark = sup ? "^" : "_";
                return arg.Length == 1 ? mark + arg : mark + "(" + arg + ")";
            }
            sb.Append(to[k]);
        }
        return sb.ToString();
    }

    /// <summary>文字のコピー用（TSV）。上付き・下付きは上付き文字など、そのほかの式は原文字のまま</summary>
    public static string ToPlain(string s)
    {
        if (!HasMath(s)) return s;
        var sb = new StringBuilder();
        foreach (var g in Split(s))
            sb.Append(g.Kind switch
            {
                CellSegKind.Sup => ScriptText(g.Text, true),
                CellSegKind.Sub => ScriptText(g.Text, false),
                CellSegKind.Math => g.Raw,
                _ => g.Text,
            });
        return sb.ToString();
    }

    /// <summary>HTML のコピー用（エンコード済み・改行は &lt;br&gt;）。上付き・下付きは &lt;sup&gt;・&lt;sub&gt;</summary>
    public static string ToHtml(string s)
    {
        if (!HasMath(s)) return TableText.HtmlText(s);
        var sb = new StringBuilder();
        foreach (var g in Split(s))
            sb.Append(g.Kind switch
            {
                CellSegKind.Sup => "<sup>" + WebUtility.HtmlEncode(g.Text) + "</sup>",
                CellSegKind.Sub => "<sub>" + WebUtility.HtmlEncode(g.Text) + "</sub>",
                CellSegKind.Math => TableText.HtmlText(g.Raw),
                _ => TableText.HtmlText(g.Text),
            });
        return sb.ToString();
    }
}

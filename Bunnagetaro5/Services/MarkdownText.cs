using System.Text;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>Markdown の塊の種類。表は TableText が読む（ここでは段落の文字のまま）</summary>
public enum MdBlockKind { Para, Heading, ListItem, Quote, Code, Rule, Math }

/// <summary>Markdown の塊1つ。Para・Heading・ListItem・Quote の Text は行の中の Markdown（\n は段落の中の改行）。
/// Code の Text はコードそのまま（Marker は言語名）。Math の Text は独立行の式の印。
/// Level は見出しの段（1〜6）・箇条書きの深さ（0〜）・引用の深さ（1〜）。
/// BlankBefore は前の塊との間に空行があったか（コピーの時の区切りに使う）</summary>
public sealed record MdBlock(MdBlockKind Kind, string Text, int Level = 0, string Marker = "", bool BlankBefore = false);

/// <summary>行の中の飾り</summary>
[Flags]
public enum MdStyle { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }

/// <summary>行の中の1かたまり。Math が 0 以上なら式（区切れ目の番号）で Text は空。
/// Text が "\n" なら段落の中の改行</summary>
public readonly record struct MdSpan(string Text, MdStyle Style, int Math = -1)
{
    public bool IsBreak => Math < 0 && Text == "\n";
}

/// <summary>
/// 答えの Markdown（見出し・太字・斜体・取り消し線・箇条書き・番号付き・引用・コード・区切り線・リンク）を読む。
/// 式（MathText の区切れ目）は先に印（私用領域の文字）へ置き換えてから読むので、式の中の * _ | は触らない。
/// 単語の中の * _（a*b*c・snake_case）は斜体にしない。**（太字）は日本語の文の中でも効く。
/// 表（| a | b |）は TableText が読む（解答欄では出し終わってから表にする）
/// </summary>
public static class MarkdownText
{
    // 文中の式・独立行の式の印。番号・番号
    private const char MathOpen = '';
    private const char DisplayOpen = '';
    private const char MathClose = '';
    private static readonly Regex TokenRe = new("[](\\d+)", RegexOptions.Compiled);
    private static readonly Regex DisplayLineRe = new("^\\d+$", RegexOptions.Compiled);

    private static readonly Regex FenceRe = new(@"^ {0,3}(`{3,}|~{3,})\s*([^`\s]*)", RegexOptions.Compiled);
    private static readonly Regex HeadingRe = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex RuleRe = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex SetextRe = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex ListRe = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteRe = new(@"^ {0,3}>", RegexOptions.Compiled);
    private static readonly Regex TaskRe = new(@"^\[([ xX])\][ \t]+", RegexOptions.Compiled);

    /// <summary>Markdown として読む価値がありそうか（記号が1つも無ければ false。速く済ませるため）</summary>
    public static bool MayContainMarkdown(string s) =>
        s.IndexOfAny(new[] { '*', '_', '`', '#', '-', '+', '>', '~', '[', '|', '=' }) >= 0 ||
        Regex.IsMatch(s, @"(^|\n)[ \t]*\d{1,9}[.)][ \t]");

    // ---------------------------------------------------------------- 式の印

    /// <summary>区切れ目の列を、式を印に置き換えた1本の文字にする。独立行の式は前後で行を分ける
    /// （displayOwnLine=false は文中の式と同じ印にして行を分けない。コピーの文字で元の行の並びを変えないため）</summary>
    public static string Protect(IReadOnlyList<MathSeg> segs, bool displayOwnLine = true)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            switch (s.Kind)
            {
                case MathSegKind.Text:
                    sb.Append(s.Raw);
                    break;
                case MathSegKind.Inline:
                    sb.Append(MathOpen).Append(i).Append(MathClose);
                    break;
                case MathSegKind.Display when !displayOwnLine:
                    sb.Append(MathOpen).Append(i).Append(MathClose);
                    break;
                default:
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                    sb.Append(DisplayOpen).Append(i).Append(MathClose).Append('\n');
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>印を元の式の文字（$ 付きなど）へ戻す</summary>
    public static string Restore(string s, IReadOnlyList<MathSeg> segs) =>
        s.IndexOf(MathClose) < 0 ? s : TokenRe.Replace(s, m =>
            int.TryParse(m.Groups[1].Value, out int i) && i >= 0 && i < segs.Count ? segs[i].Raw : m.Value);

    /// <summary>独立行の式の印なら、その区切れ目の番号</summary>
    public static int DisplayIndex(string blockText)
    {
        var m = TokenRe.Match(blockText);
        return m.Success && int.TryParse(m.Groups[1].Value, out int i) ? i : -1;
    }

    // ---------------------------------------------------------------- 塊

    /// <summary>文字（式は印に置き換え済み）を塊の並びへ</summary>
    public static List<MdBlock> ParseBlocks(string text)
    {
        var res = new List<MdBlock>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new List<string>();
        bool blank = false;            // 次の塊の前に空行があった
        bool paraBlank = false;        // 今ためている段落の前に空行があった
        var listIndents = new List<int>();
        bool lastWasList = false;      // 直前の塊が箇条書き（続きの行を足せる）

        void Add(MdBlock b)
        {
            res.Add(b);
            blank = false;
        }
        void FlushPara()
        {
            if (para.Count == 0) return;
            res.Add(new MdBlock(MdBlockKind.Para, string.Join("\n", para), BlankBefore: paraBlank));
            para.Clear();
            blank = false;
        }
        void EndList()
        {
            listIndents.Clear();
            lastWasList = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                FlushPara();
                if (res.Count > 0 || para.Count > 0) blank = true;
                continue;
            }

            var fm = FenceRe.Match(line);
            if (fm.Success)
            {
                FlushPara();
                EndList();
                string fence = fm.Groups[1].Value;
                var code = new List<string>();
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string t = lines[j].TrimStart(' ');
                    if (t.StartsWith(fence[0].ToString(), StringComparison.Ordinal) &&
                        t.TrimEnd().Length >= fence.Length && t.TrimEnd().All(ch => ch == fence[0]))
                        break;
                    code.Add(lines[j]);
                }
                Add(new MdBlock(MdBlockKind.Code, string.Join("\n", code), Marker: fm.Groups[2].Value, BlankBefore: blank));
                i = j;   // 閉じのフェンス（無ければ最後まで）
                continue;
            }

            string tl = line.Trim();
            if (DisplayLineRe.IsMatch(tl))
            {
                FlushPara();
                EndList();
                Add(new MdBlock(MdBlockKind.Math, tl, BlankBefore: blank));
                continue;
            }

            // 前の行の下線（=== / ---）は見出し。--- は段落の直後だけ（それ以外は区切り線）
            if (para.Count > 0 && SetextRe.Match(line) is { Success: true } sm)
            {
                string body = string.Join("\n", para);
                para.Clear();
                Add(new MdBlock(MdBlockKind.Heading, body, sm.Groups[1].Value[0] == '=' ? 1 : 2, BlankBefore: paraBlank));
                EndList();
                continue;
            }

            var hm = HeadingRe.Match(line);
            if (hm.Success)
            {
                FlushPara();
                EndList();
                Add(new MdBlock(MdBlockKind.Heading, hm.Groups[2].Value.Trim(), hm.Groups[1].Value.Length, BlankBefore: blank));
                continue;
            }

            if (RuleRe.IsMatch(line))
            {
                FlushPara();
                EndList();
                Add(new MdBlock(MdBlockKind.Rule, "", BlankBefore: blank));
                continue;
            }

            var lm = ListRe.Match(line);
            if (lm.Success)
            {
                FlushPara();
                int indent = Indent(lm.Groups[1].Value);
                if (!lastWasList) listIndents.Clear();
                while (listIndents.Count > 0 && listIndents[^1] > indent) listIndents.RemoveAt(listIndents.Count - 1);
                if (listIndents.Count == 0 || listIndents[^1] < indent) listIndents.Add(indent);
                int level = listIndents.Count - 1;
                string mk = lm.Groups[2].Value;
                string body = lm.Groups[3].Value;
                string marker;
                if (char.IsDigit(mk[0])) marker = mk.TrimEnd(')', '.') + ".";
                else marker = level == 0 ? "•" : level == 1 ? "◦" : "▪";
                var tm = TaskRe.Match(body);
                if (tm.Success)
                {
                    marker = tm.Groups[1].Value == " " ? "☐" : "☑";
                    body = body[tm.Length..];
                }
                Add(new MdBlock(MdBlockKind.ListItem, body.Trim(), level, marker, BlankBefore: blank));
                lastWasList = true;
                continue;
            }

            if (QuoteRe.IsMatch(line))
            {
                FlushPara();
                EndList();
                int depth = 0;
                var qlines = new List<string>();
                int j = i;
                for (; j < lines.Length && QuoteRe.IsMatch(lines[j]); j++)
                {
                    string q = lines[j].TrimStart(' ');
                    int d = 0;
                    while (q.StartsWith('>'))
                    {
                        d++;
                        q = q[1..];
                        if (q.StartsWith(' ')) q = q[1..];
                        q = q.TrimStart(' ');
                    }
                    depth = Math.Max(depth, d);
                    qlines.Add(q);
                }
                // 空の引用行（> だけ）は段落の切れ目。並べた行は1つの引用の塊の中の改行
                Add(new MdBlock(MdBlockKind.Quote, string.Join("\n", qlines).Trim('\n'), depth, BlankBefore: blank));
                i = j - 1;
                continue;
            }

            // 箇条書きの続きの行（字下げあり、または空行を挟まない行）は、その項目の中の改行
            if (lastWasList && para.Count == 0 && res.Count > 0 && res[^1].Kind == MdBlockKind.ListItem &&
                (!blank || Indent(line) >= 2))
            {
                var last = res[^1];
                res[^1] = last with { Text = last.Text + "\n" + line.Trim() };
                blank = false;
                continue;
            }

            if (para.Count == 0)
            {
                paraBlank = blank;
                EndList();
            }
            para.Add(line.TrimEnd());   // 行の頭の字下げは残す（Markdown でない文の見た目を変えない）
        }
        FlushPara();
        return res;
    }

    private static int Indent(string ws)
    {
        int n = 0;
        foreach (char c in ws)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    // ---------------------------------------------------------------- 行の中

    /// <summary>行の中の Markdown を、飾り付きのかたまりの並びへ</summary>
    public static List<MdSpan> ParseInline(string s)
    {
        var o = new List<MdSpan>();
        Inline(s, MdStyle.None, o);
        // 同じ飾りの文字が続いたら1つにまとめる
        var merged = new List<MdSpan>(o.Count);
        foreach (var sp in o)
        {
            if (merged.Count > 0 && sp.Math < 0 && !sp.IsBreak && merged[^1].Math < 0 && !merged[^1].IsBreak &&
                merged[^1].Style == sp.Style)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + sp.Text };
            else if (sp.Math >= 0 || sp.Text.Length > 0)
                merged.Add(sp);
        }
        return merged;
    }

    private static bool IsAsciiAlnum(char c) => c < 0x80 && char.IsLetterOrDigit(c);

    /// <summary>\ で記号のまま出すもの。LaTeX の \\ \( \[ などは触らない</summary>
    private static bool IsEscapable(char c) => c is '*' or '_' or '`' or '#' or '|' or '~' or '[' or ']' or '>' or '-' or '+' or '!';

    private static void Inline(string s, MdStyle st, List<MdSpan> o)
    {
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            o.Add(new MdSpan(sb.ToString(), st));
            sb.Clear();
        }
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsEscapable(s[i + 1]))
            {
                sb.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if ((c == MathOpen || c == DisplayOpen) && TryToken(s, i, out int idx, out int tokEnd))
            {
                Flush();
                o.Add(new MdSpan("", st, idx));
                i = tokEnd;
                continue;
            }
            if (c == '\n')
            {
                Flush();
                o.Add(new MdSpan("\n", st));
                i++;
                continue;
            }
            if (c == '`')
            {
                int n = Run(s, i, '`');
                int j = FindBacktickClose(s, i + n, n);
                if (j >= 0)
                {
                    Flush();
                    string code = s.Substring(i + n, j - i - n).Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                    o.Add(new MdSpan(code, st | MdStyle.Code));
                    i = j + n;
                    continue;
                }
                sb.Append(s, i, n);
                i += n;
                continue;
            }
            if ((c == '[' || (c == '!' && i + 1 < s.Length && s[i + 1] == '[')) && TryLink(s, c == '!' ? i + 1 : i, out string label, out int linkEnd))
            {
                Flush();
                Inline(label, st, o);   // リンクは文字だけ（押しても開かない）。画像は代わりの文字
                i = linkEnd;
                continue;
            }
            if (c is '*' or '_' or '~')
            {
                int n = Run(s, i, c);
                if (TryEmph(s, i, c, n, out int close, out MdStyle add))
                {
                    Flush();
                    Inline(s.Substring(i + n, close - i - n), st | add, o);
                    i = close + n;
                    continue;
                }
                sb.Append(s, i, n);
                i += n;
                continue;
            }
            sb.Append(c);
            i++;
        }
        Flush();
    }

    private static int Run(string s, int i, char c)
    {
        int j = i;
        while (j < s.Length && s[j] == c) j++;
        return j - i;
    }

    private static bool TryToken(string s, int i, out int idx, out int end)
    {
        idx = -1;
        end = i;
        int j = i + 1;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        if (j == i + 1 || j >= s.Length || s[j] != MathClose) return false;
        if (!int.TryParse(s.AsSpan(i + 1, j - i - 1), out idx)) return false;
        end = j + 1;
        return true;
    }

    /// <summary>同じ長さの ` の並びで閉じる所（無ければ -1）</summary>
    private static int FindBacktickClose(string s, int from, int n)
    {
        int j = from;
        while (j < s.Length)
        {
            if (s[j] == '`')
            {
                int m = Run(s, j, '`');
                if (m == n) return j;
                j += m;
                continue;
            }
            j++;
        }
        return -1;
    }

    /// <summary>[文字](URL) を読む。i は [ の位置。end は ) の次</summary>
    private static bool TryLink(string s, int i, out string label, out int end)
    {
        label = "";
        end = i;
        int depth = 0, j = i;
        for (; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '\\') { j++; continue; }
            if (c == '\n') return false;
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) break;
        }
        if (j >= s.Length || j + 1 >= s.Length || s[j + 1] != '(') return false;
        int k = s.IndexOf(')', j + 2);
        if (k < 0) return false;
        string url = s.Substring(j + 2, k - j - 2).Trim();
        if (url.Length == 0 || url.Contains('\n') || url.Contains(' ') && !url.Contains('"')) return false;
        label = s.Substring(i + 1, j - i - 1);
        end = k + 1;
        return label.Length > 0;
    }

    /// <summary>* _ ~ の並び（長さ n）が開きになって、閉じが見つかるか。
    /// 開きの直後・閉じの直前は空白でない。_ と1つの * は英数字の単語の中では飾りにしない</summary>
    private static bool TryEmph(string s, int i, char c, int n, out int close, out MdStyle add)
    {
        close = -1;
        add = MdStyle.None;
        if (c == '~') { if (n != 2) return false; add = MdStyle.Strike; }
        else if (n == 1) add = MdStyle.Italic;
        else if (n == 2) add = MdStyle.Bold;
        else if (n == 3) add = MdStyle.Bold | MdStyle.Italic;
        else return false;
        int after = i + n;
        if (after >= s.Length || char.IsWhiteSpace(s[after])) return false;
        char prev = i > 0 ? s[i - 1] : ' ';
        if (c == '_' && IsAsciiAlnum(prev)) return false;
        if (c == '*' && n == 1 && IsAsciiAlnum(prev) && IsAsciiAlnum(s[after])) return false;

        int j = after;
        while (j < s.Length)
        {
            char d = s[j];
            if (d == '\\' && j + 1 < s.Length) { j += 2; continue; }
            if (d == '`')
            {
                int m = Run(s, j, '`');
                int cl = FindBacktickClose(s, j + m, m);
                j = cl >= 0 ? cl + m : j + m;
                continue;
            }
            if ((d == MathOpen || d == DisplayOpen) && TryToken(s, j, out _, out int te)) { j = te; continue; }
            if (d == c)
            {
                int m = Run(s, j, c);
                if (m == n && j > after && !char.IsWhiteSpace(s[j - 1]))
                {
                    char next = j + m < s.Length ? s[j + m] : ' ';
                    bool ok = c switch
                    {
                        '_' => !IsAsciiAlnum(next),
                        '*' when n == 1 => !(IsAsciiAlnum(s[j - 1]) && IsAsciiAlnum(next)),
                        _ => true,
                    };
                    if (ok)
                    {
                        close = j;
                        return true;
                    }
                }
                j += m;
                continue;
            }
            j++;
        }
        return false;
    }

    // ---------------------------------------------------------------- 記号を外した文字

    /// <summary>Markdown の記号を外した文字（見えている形）。latex=true は $...$ などの中を触らない。
    /// 箇条書きは「• 」「1. 」、引用・見出しは文字だけ、コードは中身、区切り線は空行</summary>
    public static string ToPlain(string text, bool latex = true)
    {
        if (text.Length == 0 || !MayContainMarkdown(text)) return text;
        var segs = latex ? MathText.Split(text) : new List<MathSeg> { new(MathSegKind.Text, text, "") };
        var blocks = ParseBlocks(Protect(segs, displayOwnLine: false));
        var sb = new StringBuilder();
        bool first = true, ruleBefore = false;
        foreach (var b in blocks)
        {
            if (b.Kind == MdBlockKind.Rule) { ruleBefore = true; continue; }   // 区切り線は空行1つにする
            if (!first) sb.Append(b.BlankBefore || ruleBefore ? "\n\n" : "\n");
            first = false;
            ruleBefore = false;
            switch (b.Kind)
            {
                case MdBlockKind.ListItem:
                    sb.Append(' ', 2 * b.Level).Append(b.Marker).Append(' ').Append(InlinePlain(b.Text));
                    break;
                case MdBlockKind.Code:
                case MdBlockKind.Math:
                    sb.Append(b.Text);
                    break;
                default:
                    sb.Append(InlinePlain(b.Text));
                    break;
            }
        }
        return Restore(sb.ToString().TrimEnd('\n'), segs);
    }

    /// <summary>答え全体のコピーの中身（記号を外した文字）。Markdown の表があれば表はタブ区切りにし、HTML も返す
    /// （表が無ければ Html は null）。latex=true は $...$ などの中を触らない</summary>
    public static (string Text, string? Html) ForCopy(string text, bool latex = true)
    {
        if (TableText.MayContainTable(text, parseOtsl: false, parseHtml: false, parseMarkdown: true))
        {
            var parts = TableText.Parse(text, null, parseOtsl: false, parseHtml: false, parseMarkdown: true);
            if (TableText.HasTable(parts)) return TableText.ToClipboard(parts, t => ToPlain(t, latex));
        }
        return (ToPlain(text, latex), null);
    }

    /// <summary>行の中だけの記号を外す（表のマスの中など）。$...$ の中は触らない</summary>
    public static string InlineToPlain(string text)
    {
        if (text.Length == 0 || !MayContainMarkdown(text)) return text;
        var segs = MathText.Split(text);
        return Restore(InlinePlain(Protect(segs, displayOwnLine: false)), segs);
    }

    /// <summary>行の中の飾りを外した文字（式の印はそのまま残す）</summary>
    private static string InlinePlain(string s)
    {
        var sb = new StringBuilder();
        foreach (var sp in ParseInline(s))
        {
            if (sp.Math >= 0) sb.Append(MathOpen).Append(sp.Math).Append(MathClose);
            else sb.Append(sp.Text);
        }
        return sb.ToString();
    }
}

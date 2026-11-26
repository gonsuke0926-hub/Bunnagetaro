using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>表の1マス（左上の位置・結合の大きさ・中身）。結合で隠れたマスは持たない</summary>
public sealed record TableCellInfo(int Row, int Col, int RowSpan, int ColSpan, string Text, bool IsHeader);

/// <summary>HTML の表に揃えた中身。OTSL も HTML の &lt;table&gt; もここへ寄せる。
/// 結合で隠れたマスは Cells に出てこない（Rows×Cols の中で、どの Cells にも覆われない所は無い）</summary>
public sealed class TableGrid
{
    public int Rows { get; }
    public int Cols { get; }
    public IReadOnlyList<TableCellInfo> Cells { get; }

    public TableGrid(int rows, int cols, IEnumerable<TableCellInfo> cells)
    {
        Rows = rows;
        Cols = cols;
        Cells = cells.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
    }

    /// <summary>行 r0..r1・列 c0..c1（両端を含む）にかかるマスだけの表。端にかかる結合マスは切り詰める</summary>
    public TableGrid Slice(int r0, int r1, int c0, int c1)
    {
        r0 = Math.Max(0, r0); c0 = Math.Max(0, c0);
        r1 = Math.Min(Rows - 1, r1); c1 = Math.Min(Cols - 1, c1);
        var list = new List<TableCellInfo>();
        foreach (var c in Cells)
        {
            int cr1 = c.Row + c.RowSpan - 1, cc1 = c.Col + c.ColSpan - 1;
            if (cr1 < r0 || c.Row > r1 || cc1 < c0 || c.Col > c1) continue;
            int nr = Math.Max(c.Row, r0), nc = Math.Max(c.Col, c0);
            list.Add(c with
            {
                Row = nr - r0, Col = nc - c0,
                RowSpan = Math.Min(cr1, r1) - nr + 1,
                ColSpan = Math.Min(cc1, c1) - nc + 1,
            });
        }
        return new TableGrid(Math.Max(0, r1 - r0 + 1), Math.Max(0, c1 - c0 + 1), list);
    }

    /// <summary>Excel に貼って崩れないタブ区切り。1行ずつ、セルはタブで区切る。結合で隠れたマスは空。
    /// セルの中にタブ・改行・" があれば "…" で囲む（" は ""）。
    /// cellMath=true はセルの中の \(^{a}\) などを上付き文字などにする</summary>
    public string ToTsv(bool cellMath = false)
    {
        var g = new string?[Rows, Cols];
        foreach (var c in Cells)
            if (c.Row < Rows && c.Col < Cols) g[c.Row, c.Col] = cellMath ? CellMath.ToPlain(c.Text) : c.Text;
        var sb = new StringBuilder();
        for (int r = 0; r < Rows; r++)
        {
            if (r > 0) sb.Append('\n');
            for (int c = 0; c < Cols; c++)
            {
                if (c > 0) sb.Append('\t');
                sb.Append(TsvCell(g[r, c] ?? ""));
            }
        }
        return sb.ToString();
    }

    private static string TsvCell(string s)
    {
        if (s.IndexOfAny(new[] { '\t', '\n', '\r', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>HTML の表（結合は rowspan・colspan）。plain=false はコピー用（Excel で線が出る属性付き・1行）、
    /// plain=true は画面に文字のまま見せる用（属性なし・行ごとに改行）。
    /// cellMath=true はセルの中の上付き・下付きを &lt;sup&gt;・&lt;sub&gt; にする</summary>
    public string ToHtml(bool plain = false, bool cellMath = false)
    {
        var sb = new StringBuilder();
        sb.Append(plain ? "<table>" : "<table border=\"1\" cellspacing=\"0\" cellpadding=\"3\">");
        for (int r = 0; r < Rows; r++)
        {
            if (plain) sb.Append('\n');
            sb.Append("<tr>");
            foreach (var c in Cells.Where(x => x.Row == r))
            {
                string tag = c.IsHeader ? "th" : "td";
                sb.Append('<').Append(tag);
                if (c.RowSpan > 1) sb.Append(" rowspan=\"").Append(c.RowSpan).Append('"');
                if (c.ColSpan > 1) sb.Append(" colspan=\"").Append(c.ColSpan).Append('"');
                sb.Append('>').Append(cellMath ? CellMath.ToHtml(c.Text) : TableText.HtmlText(c.Text)).Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>");
        }
        if (plain) sb.Append('\n');
        sb.Append("</table>");
        return sb.ToString();
    }
}

/// <summary>答えを「文字」と「表」の並びへ分けたもの</summary>
public abstract record AnswerPart;
public sealed record TextPart(string Text) : AnswerPart;
public sealed record TablePart(TableGrid Grid, string Raw) : AnswerPart;

/// <summary>答えの中の表を見つける・HTML の表へ揃える。
/// 表として扱うのは OTSL（PaddleOCR-VL）と HTML の &lt;table&gt; だけ。Markdown の表・タブ区切り・
/// コードフェンスの中は表にしない。壊れた表は文字のまま（log に1行）</summary>
public static class TableText
{
    private static readonly Regex OtslTok = new(@"<(fcel|ecel|lcel|ucel|xcel|nl)>", RegexOptions.Compiled);
    private static readonly Regex TableOpen = new(@"<table(?=[\s>/])[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.Compiled);

    /// <summary>表の気配が無ければ（OTSL の印も &lt;table も無い）すぐ false。流している間の判定に使わない</summary>
    public static bool MayContainTable(string s, bool parseOtsl = true, bool parseHtml = true) =>
        (parseHtml && s.Contains("<table", StringComparison.OrdinalIgnoreCase)) ||
        (parseOtsl && OtslTok.IsMatch(s));

    /// <summary>text を 文字／表 の並びへ。表が無ければ全体で TextPart 1つ。
    /// 表の前後の文字は、表と接する側の改行を削る。壊れた表は文字のまま残し、log にその理由を渡す</summary>
    public static List<AnswerPart> Parse(
        string text, Action<string>? log = null, bool parseOtsl = true, bool parseHtml = true)
    {
        var parts = new List<AnswerPart>();
        if (!MayContainTable(text, parseOtsl, parseHtml))
        {
            parts.Add(new TextPart(text));
            return parts;
        }
        // コードフェンスの中は触らない: フェンスの外側の区間ごとに表を探す
        var spans = FenceFreeSpans(text);
        var found = new List<(int Start, int End, TableGrid Grid)>();
        foreach (var (a, b) in spans)
        {
            if (parseHtml) FindHtml(text, a, b, found, log);
            if (parseOtsl) FindOtsl(text, a, b, found, log);
        }
        found.Sort((x, y) => x.Start.CompareTo(y.Start));
        // 重なり（HTML の中の OTSL の印など）は先に始まったほうだけ
        int pos = 0;
        string pendingText = "";
        var kept = new List<(int Start, int End, TableGrid Grid)>();
        foreach (var f in found)
        {
            if (f.Start < pos) continue;
            kept.Add(f);
            pos = f.End;
        }
        pos = 0;
        foreach (var f in kept)
        {
            pendingText = text.Substring(pos, f.Start - pos);
            if (parts.Count > 0) pendingText = TrimLeadNewlines(pendingText);
            pendingText = TrimTailNewlines(pendingText);
            if (pendingText.Length > 0) parts.Add(new TextPart(pendingText));
            parts.Add(new TablePart(f.Grid, text.Substring(f.Start, f.End - f.Start)));
            pos = f.End;
        }
        string rest = TrimLeadNewlines(text.Substring(pos));
        if (rest.Length > 0 || parts.Count == 0) parts.Add(new TextPart(rest));
        return parts;
    }

    public static bool HasTable(IEnumerable<AnswerPart> parts) => parts.Any(p => p is TablePart);

    /// <summary>OTSL の表だけを HTML の表の文字に置き換える（ほかの文字・HTML の表はそのまま）。
    /// 変換は「OTSL → HTML → 画面の表」の順につながる。OTSL の変換 ON・HTML の表の変換 OFF の時は
    /// HTML の文字で止めて見せる。OTSL の表が無ければ text をそのまま返す</summary>
    public static string OtslToHtmlText(string text)
    {
        var parts = Parse(text, null, parseOtsl: true, parseHtml: false);
        if (!HasTable(parts)) return text;
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is TextPart tp) sb.Append(tp.Text);
            else if (p is TablePart tb)
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                sb.Append(tb.Grid.ToHtml(plain: true)).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string TrimLeadNewlines(string s) => s.TrimStart('\r', '\n');
    private static string TrimTailNewlines(string s) => s.TrimEnd('\r', '\n');

    /// <summary>``` で始まる行から次の ``` の行までを除いた区間（閉じが無ければ最後まで）</summary>
    private static List<(int A, int B)> FenceFreeSpans(string s)
    {
        var res = new List<(int, int)>();
        int segStart = 0, i = 0;
        bool inFence = false;
        while (i <= s.Length)
        {
            int eol = s.IndexOf('\n', i);
            int lineEnd = eol < 0 ? s.Length : eol;
            string line = s.Substring(i, lineEnd - i).TrimStart(' ', '\t');
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    if (i > segStart) res.Add((segStart, i));
                    inFence = true;
                }
                else
                {
                    inFence = false;
                    segStart = eol < 0 ? s.Length : eol + 1;
                }
            }
            if (eol < 0) break;
            i = eol + 1;
        }
        if (!inFence && segStart < s.Length) res.Add((segStart, s.Length));
        return res;
    }

    // ---------------------------------------------------------------- OTSL

    private static void FindOtsl(string s, int from, int to, List<(int, int, TableGrid)> found, Action<string>? log)
    {
        string seg = s.Substring(from, to - from);
        var toks = OtslTok.Matches(seg);
        if (toks.Count == 0) return;
        int first = toks[0].Index;
        // 最後の <nl> までが表。それより後ろに印が続いていたら（行が閉じていない）壊れた表
        int lastNl = -1;
        for (int k = toks.Count - 1; k >= 0; k--)
            if (toks[k].Groups[1].Value == "nl") { lastNl = k; break; }
        if (lastNl < 0)
        {
            log?.Invoke("OTSL の表の行が閉じていない（<nl> が無い）ので文字のまま出します");
            return;
        }
        if (lastNl != toks.Count - 1)
        {
            log?.Invoke("OTSL の表の最後の行が <nl> で閉じていないので文字のまま出します");
            return;
        }
        int end = toks[lastNl].Index + toks[lastNl].Length;
        var grid = OtslToGrid(seg.Substring(first, end - first), out string? err);
        if (grid is null)
        {
            log?.Invoke("OTSL の表を読めないので文字のまま出します: " + err);
            return;
        }
        found.Add((from + first, from + end, grid));
    }

    /// <summary>OTSL（先頭の印から最後の &lt;nl&gt; まで）→ 表。読めなければ null と理由</summary>
    public static TableGrid? OtslToGrid(string otsl, out string? error)
    {
        error = null;
        var toks = OtslTok.Matches(otsl);
        var rows = new List<List<(string Kind, string Text)>>();
        var cur = new List<(string, string)>();
        for (int k = 0; k < toks.Count; k++)
        {
            string kind = toks[k].Groups[1].Value;
            if (kind == "nl")
            {
                rows.Add(cur);
                cur = new List<(string, string)>();
                continue;
            }
            int a = toks[k].Index + toks[k].Length;
            int b = k + 1 < toks.Count ? toks[k + 1].Index : otsl.Length;
            string txt = kind == "fcel" ? CleanOtslText(otsl.Substring(a, b - a)) : "";
            cur.Add((kind, txt));
        }
        if (cur.Count > 0) { error = "最後の行が閉じていない"; return null; }
        if (rows.Count == 0) { error = "行が無い"; return null; }
        int cols = rows[0].Count;
        if (cols == 0) { error = "空の行"; return null; }
        for (int r = 0; r < rows.Count; r++)
            if (rows[r].Count != cols) { error = $"行ごとのセル数がそろわない（{r + 1}行目 {rows[r].Count} 個・1行目 {cols} 個）"; return null; }

        // 各マスの持ち主（結合の左上）を決める
        var owner = new (int R, int C)[rows.Count, cols];
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < cols; c++)
            {
                switch (rows[r][c].Kind)
                {
                    case "fcel":
                    case "ecel":
                        owner[r, c] = (r, c);
                        break;
                    case "lcel":
                        if (c == 0) { error = "左端に lcel がある"; return null; }
                        owner[r, c] = owner[r, c - 1];
                        break;
                    case "ucel":
                        if (r == 0) { error = "先頭行に ucel がある"; return null; }
                        owner[r, c] = owner[r - 1, c];
                        break;
                    default: // xcel
                        if (r == 0 || c == 0) { error = "端に xcel がある"; return null; }
                        owner[r, c] = owner[r, c - 1];
                        break;
                }
            }
        var rowSpan = new Dictionary<(int, int), int>();
        var colSpan = new Dictionary<(int, int), int>();
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < cols; c++)
            {
                var o = owner[r, c];
                rowSpan[o] = Math.Max(rowSpan.GetValueOrDefault(o, 1), r - o.R + 1);
                colSpan[o] = Math.Max(colSpan.GetValueOrDefault(o, 1), c - o.C + 1);
            }
        var cells = new List<TableCellInfo>();
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < cols; c++)
                if (owner[r, c] == (r, c))
                    cells.Add(new TableCellInfo(r, c, rowSpan.GetValueOrDefault((r, c), 1), colSpan.GetValueOrDefault((r, c), 1),
                        rows[r][c].Text, false));
        return new TableGrid(rows.Count, cols, cells);
    }

    /// <summary>OTSL のセルの文字: 前後の空白を削り、\n（2文字）と実際の改行はセル内の改行にそろえる</summary>
    private static string CleanOtslText(string s)
    {
        s = s.Replace("\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n');
        return s.Trim().Trim('\n').Trim();
    }

    // ---------------------------------------------------------------- HTML

    private static void FindHtml(string s, int from, int to, List<(int, int, TableGrid)> found, Action<string>? log)
    {
        int i = from;
        while (i < to)
        {
            var m = TableOpen.Match(s, i, to - i);
            if (!m.Success) return;
            int close = FindTableEnd(s, m.Index + m.Length, to);
            if (close < 0)
            {
                log?.Invoke("HTML の表が閉じていない（</table> が無い）ので文字のまま出します");
                return;
            }
            int end = close;
            var grid = HtmlToGrid(s.Substring(m.Index, end - m.Index), out string? err);
            if (grid is null)
            {
                log?.Invoke("HTML の表を読めないので文字のまま出します: " + err);
                i = end;
                continue;
            }
            found.Add((m.Index, end, grid));
            i = end;
        }
    }

    /// <summary>入れ子の表は数えて対応する &lt;/table&gt; の直後の位置を返す（無ければ -1）</summary>
    private static int FindTableEnd(string s, int from, int to)
    {
        int depth = 1;
        int i = from;
        while (i < to)
        {
            int lt = s.IndexOf('<', i, to - i);
            if (lt < 0) return -1;
            var m = Tag.Match(s, lt, to - lt);
            if (!m.Success || m.Index != lt) { i = lt + 1; continue; }
            if (m.Groups[2].Value.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Groups[1].Value == "/") { if (--depth == 0) return m.Index + m.Length; }
                else depth++;
            }
            i = m.Index + m.Length;
        }
        return -1;
    }

    /// <summary>&lt;table&gt;…&lt;/table&gt; → 表。tr・td・th・rowspan・colspan を読み、thead などは無視、
    /// セルの中のタグは文字だけ（&lt;br&gt; は改行）。入れ子の表・行の外のセルは読めない（null）</summary>
    public static TableGrid? HtmlToGrid(string html, out string? error)
    {
        error = null;
        var rows = new List<List<(string Text, bool Th, int Rs, int Cs)>>();
        List<(string, bool, int, int)>? curRow = null;
        StringBuilder? cell = null;
        bool cellTh = false;
        int cellRs = 1, cellCs = 1;
        int depth = 0;
        void EndCell()
        {
            if (cell is null || curRow is null) return;
            curRow.Add((CleanHtmlCell(cell.ToString()), cellTh, cellRs, cellCs));
            cell = null;
        }
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                cell?.Append(html, i, html.Length - i);
                break;
            }
            if (lt > i) cell?.Append(html, i, lt - i);
            var m = Tag.Match(html, lt);
            if (!m.Success || m.Index != lt)
            {
                cell?.Append('<');
                i = lt + 1;
                continue;
            }
            bool closing = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value.ToLowerInvariant();
            i = m.Index + m.Length;
            switch (name)
            {
                case "table":
                    if (!closing) { depth++; if (depth > 1) { error = "入れ子の表"; return null; } }
                    else { EndCell(); curRow = null; }
                    break;
                case "tr":
                    EndCell();
                    if (!closing) { curRow = new List<(string, bool, int, int)>(); rows.Add(curRow!); }
                    else curRow = null;
                    break;
                case "td":
                case "th":
                    EndCell();
                    if (closing) break;
                    if (curRow is null) { error = "行（tr）の外にセルがある"; return null; }
                    cell = new StringBuilder();
                    cellTh = name == "th";
                    cellRs = Math.Max(1, SpanAttr(m.Groups[3].Value, "rowspan"));
                    cellCs = Math.Max(1, SpanAttr(m.Groups[3].Value, "colspan"));
                    break;
                case "br":
                    cell?.Append('\n');
                    break;
                default:
                    break;   // thead・tbody・タグの飾りなどは無視（中の文字は残る）
            }
        }
        if (rows.Count == 0) { error = "行が無い"; return null; }

        // 結合を考えてマスに並べる
        var occupied = new List<bool[]>();
        bool[] RowOcc(int r)
        {
            while (occupied.Count <= r) occupied.Add(new bool[256]);
            return occupied[r];
        }
        var cells = new List<TableCellInfo>();
        int maxCols = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            int c = 0;
            foreach (var (text, th, rs, cs0) in rows[r])
            {
                while (c < 255 && RowOcc(r)[c]) c++;
                int cs = Math.Min(cs0, 256 - c);
                if (cs < 1) { error = "列が多すぎる"; return null; }
                int rsClamped = Math.Min(rs, rows.Count - r);   // 行を超える rowspan は表の端で切る
                for (int rr = r; rr < r + rsClamped; rr++)
                    for (int cc = c; cc < c + cs; cc++) RowOcc(rr)[cc] = true;
                cells.Add(new TableCellInfo(r, c, rsClamped, cs, text, th));
                c += cs;
                maxCols = Math.Max(maxCols, c);
            }
        }
        if (maxCols == 0) { error = "セルが無い"; return null; }
        // 穴（短い行の右側）は空のマスで埋める
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < maxCols; c++)
                if (!RowOcc(r)[c]) cells.Add(new TableCellInfo(r, c, 1, 1, "", false));
        return new TableGrid(rows.Count, maxCols, cells);
    }

    private static int SpanAttr(string attrs, string name)
    {
        foreach (Match m in Attr.Matches(attrs))
        {
            if (!m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            if (int.TryParse(v.Trim(), out int n)) return Math.Min(n, 200);
        }
        return 1;
    }

    private static string CleanHtmlCell(string raw)
    {
        string t = WebUtility.HtmlDecode(raw).Replace("\r\n", "\n").Replace('\r', '\n');
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @" ?\n ?", "\n");
        return t.Trim().Trim('\n').Trim();
    }

    // ---------------------------------------------------------------- コピー用

    /// <summary>コピーの中身。表が1つも無ければ Html は null。Text は表の外を xform（LaTeX のコピーの形）へ通し、
    /// 表は TSV にして、行・段の区切りは \r\n にそろえる。Html は表の外を &lt;p&gt;、表を &lt;table&gt; にした断片。
    /// cellMath=true はセルの中の数式も変換する</summary>
    public static (string Text, string? Html) ToClipboard(IReadOnlyList<AnswerPart> parts, Func<string, string> xform, bool cellMath = false)
    {
        if (!HasTable(parts)) return (xform(string.Concat(parts.OfType<TextPart>().Select(p => p.Text))), null);
        var text = new List<string>();
        var html = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is TextPart tp)
            {
                string t = xform(tp.Text).Trim('\r', '\n');
                if (t.Length == 0) continue;
                text.Add(t);
                foreach (var para in Regex.Split(t.Replace("\r\n", "\n"), @"\n{2,}"))
                    if (para.Length > 0) html.Append("<p>").Append(HtmlText(para)).Append("</p>");
            }
            else if (part is TablePart tb)
            {
                text.Add(tb.Grid.ToTsv(cellMath));
                html.Append(tb.Grid.ToHtml(cellMath: cellMath));
            }
        }
        string joined = string.Join("\n", text).Replace("\r\n", "\n").Replace("\n", "\r\n");
        return (joined, html.ToString());
    }

    /// <summary>CF_HTML（クリップボードの "HTML Format"）の形に包む。オフセットは UTF-8 のバイト数</summary>
    public static string CfHtml(string fragment)
    {
        const string head = "<html><body>\r\n<!--StartFragment-->";
        const string tail = "<!--EndFragment-->\r\n</body></html>";
        static string Hdr(int sh, int eh, int sf, int ef) =>
            $"Version:0.9\r\nStartHTML:{sh:D10}\r\nEndHTML:{eh:D10}\r\nStartFragment:{sf:D10}\r\nEndFragment:{ef:D10}\r\n";
        int hl = Encoding.UTF8.GetByteCount(Hdr(0, 0, 0, 0));
        int headB = Encoding.UTF8.GetByteCount(head);
        int fragB = Encoding.UTF8.GetByteCount(fragment);
        int tailB = Encoding.UTF8.GetByteCount(tail);
        int sf = hl + headB, ef = sf + fragB, eh = ef + tailB;
        return Hdr(hl, eh, sf, ef) + head + fragment + tail;
    }

    /// <summary>コピー用の HTML の文字（セルの中・段落の中）。改行は &lt;br&gt;</summary>
    public static string HtmlText(string s) =>
        WebUtility.HtmlEncode(s).Replace("\r\n", "\n").Replace("\n", "<br>");
}

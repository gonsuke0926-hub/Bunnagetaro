using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using WpfMath.Controls;

namespace Bunnagetaro5.Views;

/// <summary>解答欄のコピー（文・表・式の選択コピーと全文コピー）（AnswerView を分けたもの。修正J43）</summary>
public partial class AnswerView
{
    // ---------------- コピー ----------------

    /// <summary>LaTeX のコピーの形（F13。設定 latex_copy。ApplyStyle で更新）</summary>
    private LatexCopyForm _copyForm = LatexCopyForm.Plain;
    private bool _convertOtslTable = true;
    private bool _convertHtmlTable = true;
    /// <summary>OTSL を HTML の文字にして見せている（OTSL の変換 ON・HTML の表の変換 OFF）</summary>
    private bool _otslHtmlShown;
    private bool _convertLatexMath = true;

    /// <summary>本文を文字と式へ。描く用の式（Tex）の式番号 \tag は式の右に離す（修正J93。Raw はコピー用にそのまま）</summary>
    private List<MathSeg> SplitMath(string text) =>
        _convertLatexMath ? MathText.WithDisplayTags(MathText.Split(text)) : [new MathSeg(MathSegKind.Text, text, "")];

    private string ApplyCopyFormIfEnabled(string text) =>
        _convertLatexMath ? MathText.ApplyCopyForm(text, _copyForm) : text;

    /// <summary>式1つのコピー（右クリック「LaTeXでコピー」）。raw は $ 付きなどの原文</summary>
    private string FormulaCopyText(string raw) => MathText.ApplyCopyForm(raw, _copyForm);

    private string SelectedText() => SelectedCopy().Text;

    /// <summary>選んだ所のコピーの中身（文字と、表が含まれていれば HTML）。
    /// 表を一部だけ選んだ時は、選んだ範囲にかかるセルを含む行・列の長方形だけを表として入れる（I3）</summary>
    private (string Text, string? Html) SelectedCopy()
    {
        if (_body.Selection.IsEmpty) return ("", null);
        if (_tablesShown) return SelectedTableCopy();
        var tr = new TextRange(_body.Selection.Start, _body.Selection.End);
        return (_docMath ? MathText.ApplyCopyForm(MathAwareText(tr), _copyForm) : tr.Text, null);
    }

    private (string Text, string? Html) SelectedTableCopy()
    {
        var a = _body.Selection.Start;
        var b = _body.Selection.End;
        var parts = new List<AnswerPart>();
        var run = new List<Paragraph>();
        void Flush()
        {
            if (run.Count == 0) return;
            var st = run[0].ContentStart.CompareTo(a) < 0 ? a : run[0].ContentStart;
            var en = run[^1].ContentEnd.CompareTo(b) > 0 ? b : run[^1].ContentEnd;
            var tr = new TextRange(st, en);
            parts.Add(new TextPart(_docMath ? MathAwareText(tr) : tr.Text));
            run.Clear();
        }
        foreach (var blk in _body.Document.Blocks)
        {
            if (blk is Paragraph p)
            {
                if (p.ContentStart.CompareTo(b) < 0 && p.ContentEnd.CompareTo(a) > 0) run.Add(p);
            }
            else if (blk is Table t)
            {
                Flush();
                var part = SelectedTablePart(t, a, b);
                if (part is not null) parts.Add(part);
            }
        }
        Flush();
        var (text, html) = TableText.ToClipboard(parts, ApplyCopyFormIfEnabled, _convertLatexMath);   // 修正J94: セルの中の式も
        return (text, html);
    }

    /// <summary>選びが、このマスの文字にかかっているか。選びの端はマスの終わりを指しても次のマスの頭へ寄るので、
    /// 重なりが文字の無い所だけ（頭だけ触れている）なら外す。空のマスは丸ごと選びの中にある時だけ</summary>
    private static bool CellTouched(System.Windows.Documents.TableCell tc, TextPointer a, TextPointer b)
    {
        if (tc.ContentStart.CompareTo(b) >= 0 || tc.ContentEnd.CompareTo(a) <= 0) return false;
        var st = tc.ContentStart.CompareTo(a) < 0 ? a : tc.ContentStart;
        var en = tc.ContentEnd.CompareTo(b) > 0 ? b : tc.ContentEnd;
        if (st.CompareTo(en) >= 0) return false;
        if (new TextRange(st, en).Text.Trim().Length > 0) return true;
        return a.CompareTo(tc.ContentStart) <= 0 && b.CompareTo(tc.ContentEnd) >= 0;   // 空のマス
    }

    private static AnswerPart? SelectedTablePart(Table t, TextPointer a, TextPointer b)
    {
        if (t.Tag is not TableGrid g) return null;
        var hit = new List<(System.Windows.Documents.TableCell Cell, TableCellInfo Info)>();
        foreach (var grp in t.RowGroups)
            foreach (var row in grp.Rows)
                foreach (var tc in row.Cells)
                    if (tc.Tag is TableCellInfo info && CellTouched(tc, a, b))
                        hit.Add((tc, info));
        if (hit.Count == 0) return null;
        if (hit.Count == 1)
        {
            // 1マスの中だけ選んでいる: 表にせず、選んだ文字だけ
            var c = hit[0].Cell;
            var st = c.ContentStart.CompareTo(a) < 0 ? a : c.ContentStart;
            var en = c.ContentEnd.CompareTo(b) > 0 ? b : c.ContentEnd;
            return new TextPart(new TextRange(st, en).Text.Trim((char)13, (char)10));
        }
        int r0 = hit.Min(h => h.Info.Row), r1 = hit.Max(h => h.Info.Row + h.Info.RowSpan - 1);
        int c0 = hit.Min(h => h.Info.Col), c1 = hit.Max(h => h.Info.Col + h.Info.ColSpan - 1);
        return new TablePart(g.Slice(r0, r1, c0, c1), "");
    }

    /// <summary>式付き文書の選択範囲を LaTeX 文字列として拾う（手順26）。
    /// 通常の Text は InlineUIContainer の中を空文字にするので、式は
    /// $ 付きの原文字（Tag）へ置き換える。段落の区切りは通常の Text と
    /// 同じ \r\n。式は丸ごと選択（範囲が少し触れていれば原文字が入る）</summary>
    private static string MathAwareText(TextRange tr)
    {
        var sb = new System.Text.StringBuilder();
        var p0 = tr.Start.Paragraph;
        for (var p = p0; p is not null; p = p.NextBlock as Paragraph)
        {
            if (!ReferenceEquals(p, p0))
            {
                if (p.ContentStart.CompareTo(tr.End) > 0) break;
                if (p.ContentStart.CompareTo(tr.Start) > 0) sb.Append("\r\n");
            }
            foreach (var inl in p.Inlines)
            {
                if (inl is InlineUIContainer iuc)
                {
                    if (iuc.ContentStart.CompareTo(tr.End) < 0 &&
                        iuc.ContentEnd.CompareTo(tr.Start) > 0 &&
                        iuc.Child is FrameworkElement fc && fc.Tag is string raw)
                        sb.Append(raw);
                    continue;
                }
                if (inl is LineBreak lb)
                {
                    if (lb.ElementStart.CompareTo(tr.End) < 0 &&
                        lb.ElementEnd.CompareTo(tr.Start) > 0)
                        sb.Append("\r\n");
                    continue;
                }
                var s = inl.ContentStart;
                if (s.CompareTo(tr.Start) < 0) s = tr.Start;
                var e = inl.ContentEnd;
                if (e.CompareTo(tr.End) > 0) e = tr.End;
                if (s.CompareTo(e) < 0) sb.Append(new TextRange(s, e).Text);
            }
            if (p.ContentEnd.CompareTo(tr.End) >= 0) break;
        }
        return sb.ToString();
    }

    private string FullText() => FullCopy().Text;

    /// <summary>「答えをすべてコピー」の中身。答えに表（OTSL・HTML の table）があれば、文字（タブ区切り）と
    /// HTML の両方を返す。表が無ければ今まで通り文字だけ（I3）</summary>
    private (string Text, string? Html) FullCopy()
    {
        string all = _target.Trim();
        // 見えている形でコピーする: HTML の表の変換が OFF なら OTSL は HTML の文字にして（表は組まない）
        if (_convertOtslTable && !_convertHtmlTable)
            return (ApplyCopyFormIfEnabled(TableText.OtslToHtmlText(all).Trim()), null);
        if (TableText.MayContainTable(all, _convertOtslTable, _convertHtmlTable))
        {
            var parts = TableText.Parse(all, null, _convertOtslTable, _convertHtmlTable);
            if (TableText.HasTable(parts))
                return TableText.ToClipboard(parts, ApplyCopyFormIfEnabled, _convertLatexMath);   // 修正J94
        }
        return (FullTextPlain(), null);
    }

    private string FullTextPlain()
    {
        // 式は文書のTextにlatexの文字として出てこない（InlineUIContainerの中は
        // 空扱い）。全文コピーは $ 付きの元の文そのもの（手順26の「普通に
        // コピー=LaTeXの文字列」の約束もこれで満たす）
        return ApplyCopyFormIfEnabled(_target.Trim());
    }

    private void CopyAnswer((string Text, string? Html) c) => CopyText(c.Text, c.Html);

    private void CopyText(string t, string? html = null)
    {
        LastInteractUtc = DateTime.UtcNow;
        if (t.Length == 0) return;
        try
        {
            if (html is null) Clipboard.SetText(t);
            else
            {
                // 文字（タブ区切り）と HTML（CF_HTML）の両方。Excel は HTML、メモ帳は文字を使う
                var data = new DataObject();
                data.SetText(t, TextDataFormat.UnicodeText);
                data.SetData(DataFormats.Html, TableText.CfHtml(html));
                Clipboard.SetDataObject(data, true);
            }
            _host.SetStatus("答えをコピーしました");
        }
        catch (Exception ex)
        {
            AppLog.Error("クリップボードコピー失敗", ex);
            _host.SetStatus("コピーに失敗しました（app.log参照）");
        }
    }
}

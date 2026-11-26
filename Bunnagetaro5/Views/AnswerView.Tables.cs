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

/// <summary>解答欄の表（OTSL・HTML の表を WPF の表に組む）（AnswerView を分けたもの。修正J43）</summary>
public partial class AnswerView
{
    // ---------------- 表（修正I3） ----------------

    /// <summary>出し終わった答えに表（OTSL・HTML の table）があれば、表の部分だけ WPF の表に組み替える。
    /// 前後の文章・数式はそのまま。壊れた表は文字のまま（app.log に1行）。もう組み替え済みなら何もしない</summary>
    private void ApplyTables()
    {
        if (_tablesShown || _otslHtmlShown || _model.Length == 0) return;
        if (_convertOtslTable && !_convertHtmlTable)
        {
            // 変換は OTSL → HTML → 画面の表 の順。HTML の表の変換が OFF なら HTML の文字で止める
            if (!TableText.MayContainTable(_model, parseOtsl: true, parseHtml: false)) return;
            string h;
            try { h = TableText.OtslToHtmlText(_model); }
            catch (Exception ex) { AppLog.Error("OTSL を HTML の文字にできなかった", ex); return; }
            if (h == _model) return;
            bool atBottom = AtBottom();
            double keepOff = _body.VerticalOffset;
            ReplaceDoc(h);
            _otslHtmlShown = true;
            _body.ScrollToVerticalOffset(keepOff);
            if (atBottom) _body.ScrollToEnd();
            return;
        }
        if (!TableText.MayContainTable(_model, _convertOtslTable, _convertHtmlTable)) return;
        List<AnswerPart> parts;
        try { parts = TableText.Parse(_model, m => AppLog.Info("表: " + m), _convertOtslTable, _convertHtmlTable); }
        catch (Exception ex) { AppLog.Error("表の読み取り失敗", ex); return; }
        if (!TableText.HasTable(parts)) return;
        bool wasBottom = AtBottom();
        double keep = _body.VerticalOffset;
        try
        {
            NewDocGen();
            _body.Document.Blocks.Clear();
            _tableParts = parts;
            _docMath = parts.OfType<TextPart>().Any(t => SplitMath(t.Text).Exists(x => x.Kind != MathSegKind.Text));
            _body.IsDocumentEnabled = _docMath;
            BuildTableDoc();
            _tablesShown = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("表の組み替え失敗（文字のまま出します）", ex);
            ReplaceDoc(_model);
            return;
        }
        _body.ScrollToVerticalOffset(keep);
        if (wasBottom) _body.ScrollToEnd();
    }

    private void BuildTableDoc()
    {
        var doc = _body.Document;
        Paragraph? curPara = null;
        _needNewPara = false;
        _tailSingleNewline = false;
        foreach (var part in _tableParts!)
        {
            if (part is TextPart tp)
                AddSegs(SplitMath(tp.Text), ref curPara);
            else if (part is TablePart tb)
            {
                curPara = null;
                _needNewPara = false;
                _tailSingleNewline = false;
                doc.Blocks.Add(BuildTable(tb.Grid));
            }
        }
        ApplyParagraphMargins();
    }

    /// <summary>列の幅は中身の長さで配分（星。全角は2・最小4・最大40の目安）。線は文字色を薄くした細い線。
    /// 結合は RowSpan・ColumnSpan。見出し（th）だけ太字。セルの飾りは出さない</summary>
    private Table BuildTable(TableGrid g)
    {
        var table = new Table { CellSpacing = 0, Tag = g };
        Color fg = (_body.Foreground as SolidColorBrush)?.Color ?? Color.FromRgb(0xF5, 0xF5, 0xF5);
        var line = Frozen(Color.FromArgb(0x70, fg.R, fg.G, fg.B));
        var w = new double[g.Cols];
        Array.Fill(w, 4.0);
        foreach (var c in g.Cells)
            if (c.ColSpan == 1)
                w[c.Col] = Math.Max(w[c.Col], Math.Min(40.0, c.Text.Split('\n').Max(LineUnits)));
        foreach (double x in w) table.Columns.Add(new TableColumn { Width = new GridLength(x, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var rows = new TableRow[g.Rows];
        for (int r = 0; r < g.Rows; r++) { rows[r] = new TableRow(); group.Rows.Add(rows[r]); }
        foreach (var c in g.Cells)
        {
            var para = new Paragraph { Margin = new Thickness(0), TextAlignment = TextAlignment.Left };
            if (c.IsHeader) para.FontWeight = FontWeights.Bold;
            var lines = c.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) para.Inlines.Add(new LineBreak());
                if (_convertLatexMath && CellMath.HasMath(lines[i])) AddCellMath(para, lines[i]);
                else para.Inlines.Add(new Run(lines[i]));
            }
            var tc = new System.Windows.Documents.TableCell(para)
            {
                RowSpan = c.RowSpan,
                ColumnSpan = c.ColSpan,
                BorderBrush = line,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(4, 2, 4, 2),
                Tag = c,
            };
            rows[c.Row].Cells.Add(tc);
        }
        return table;
    }

    /// <summary>修正J94: セルの1行に式がある時。単純な上付き・下付きは小さい字を上下にずらした Run、
    /// そのほかの式は本文の文中の式と同じ描き方（KaTeX の画像・WpfMath。描けなければ原文字）</summary>
    private void AddCellMath(Paragraph para, string line)
    {
        double small = Math.Max(6, _body.FontSize * 0.7);
        foreach (var g in CellMath.Split(line))
        {
            switch (g.Kind)
            {
                case CellSegKind.Sup:
                    para.Inlines.Add(new Run(g.Text) { FontSize = small, BaselineAlignment = BaselineAlignment.Superscript });
                    break;
                case CellSegKind.Sub:
                    para.Inlines.Add(new Run(g.Text) { FontSize = small, BaselineAlignment = BaselineAlignment.Subscript });
                    break;
                case CellSegKind.Math:
                    var seg = new MathSeg(MathSegKind.Inline, g.Raw, MathText.TagForDisplay(g.Text));
                    if (MathRenderer.Active) para.Inlines.Add(MakeKatexInline(seg));
                    else if (MakeFormula(seg) is { } fc) para.Inlines.Add(new InlineUIContainer(fc));
                    else para.Inlines.Add(new Run(g.Raw));
                    break;
                default:
                    para.Inlines.Add(new Run(g.Text));
                    break;
            }
        }
    }

    private static double LineUnits(string s)
    {
        double u = 0;
        foreach (char ch in s) u += ch < 0x300 ? 1 : 2;
        return u;
    }
}

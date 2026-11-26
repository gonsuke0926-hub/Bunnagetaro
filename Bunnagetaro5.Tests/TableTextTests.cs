using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>表の見つけ方・OTSL と HTML の表の読み</summary>
public sealed class TableTextTests
{
    // 実物（Ollama で取った答え。表の画像は PIL で作った:
    // t1=単純 / t2=左上が空欄＋列結合 / t3=日本語 / t4=行結合）。GLM-OCR は t3 を表にせず文字で返した
    internal const string PaddleT1 = "<fcel>Name<fcel>Qty<fcel>Price<nl><fcel>Apple<fcel>3<fcel>120<nl><fcel>Banana<fcel>12<fcel>80<nl><fcel>Cherry<ecel><fcel>450<nl>";
    internal const string PaddleT2 = "<ecel><fcel>2024<lcel><fcel>2025<nl><ecel><fcel>Q1<fcel>Q2<fcel>Q1<nl><fcel>Sales<fcel>10<fcel>20<fcel>30<nl><fcel>Cost<fcel>5<fcel>6<fcel>7<nl>";
    internal const string PaddleT3 = "<fcel>項目<fcel>内容<nl><fcel>名前<fcel>山田 太郎<nl><fcel>住所<fcel>東京都千代田区1-1-1<nl><fcel>備考<fcel>特になし<nl>";
    internal const string PaddleT4 = "<fcel>区分<fcel>月<fcel>金額<nl><fcel>売上<fcel>1月<fcel>100<nl><ucel><fcel>2月<fcel>200<nl><fcel>経費<fcel>1月<fcel>50<nl>";
    internal const string GlmT1 = "<table border=\"1\"><tr><td>Name</td><td>Qty</td><td>Price</td></tr><tr><td>Apple</td><td>3</td><td>120</td></tr><tr><td>Banana</td><td>12</td><td>80</td></tr><tr><td>Cherry</td><td></td><td>450</td></tr></table>";
    internal const string GlmT2 = "<table border=\"1\"><tr><td></td><td colspan=\"2\">2024</td><td>2025</td></tr><tr><td></td><td>Q1</td><td>Q2</td><td>Q1</td></tr><tr><td>Sales</td><td>10</td><td>20</td><td>30</td></tr><tr><td>Cost</td><td>5</td><td>6</td><td>7</td></tr></table>";
    internal const string GlmT3 = "項目 内容\n名前 山田 太郎\n住所 東京都千代田区\n1-1-1\n備考 特になし";
    internal const string GlmT4 = "<table border=\"1\"><tr><td>区分</td><td>月</td><td>金额</td></tr><tr><td rowspan=\"2\">壳上</td><td>1月</td><td>100</td></tr><tr><td>2月</td><td>200</td></tr><tr><td>経費</td><td>1月</td><td>50</td></tr></table>";

    internal static TableGrid OneTable(string s)
    {
        var parts = TableText.Parse(s);
        var t = Assert.Single(parts.OfType<TablePart>());
        return t.Grid;
    }

    private static string Cell(TableGrid g, int r, int c) =>
        g.Cells.First(x => x.Row == r && x.Col == c).Text;

    // ---------------- OTSL

    [Fact]
    public void OTSL_実物_単純な表と空のセル()
    {
        var g = OneTable(PaddleT1);
        Assert.Equal((4, 3), (g.Rows, g.Cols));
        Assert.Equal("Name", Cell(g, 0, 0));
        Assert.Equal("", Cell(g, 3, 1));
        Assert.Equal("450", Cell(g, 3, 2));
        Assert.Equal(12, g.Cells.Count);
    }

    [Fact]
    public void OTSL_実物_左上の空欄と左結合()
    {
        var g = OneTable(PaddleT2);
        Assert.Equal((4, 4), (g.Rows, g.Cols));
        Assert.Equal("", Cell(g, 0, 0));
        var y = g.Cells.First(c => c.Text == "2024");
        Assert.Equal((0, 1, 1, 2), (y.Row, y.Col, y.RowSpan, y.ColSpan));
        Assert.DoesNotContain(g.Cells, c => c.Row == 0 && c.Col == 2);   // 結合で隠れたマス
        Assert.Equal("2025", Cell(g, 0, 3));
    }

    [Fact]
    public void OTSL_実物_日本語と上結合()
    {
        var g = OneTable(PaddleT4);
        var u = g.Cells.First(c => c.Text == "売上");
        Assert.Equal((1, 0, 2, 1), (u.Row, u.Col, u.RowSpan, u.ColSpan));
        Assert.Equal("2月", Cell(g, 2, 1));
        Assert.Equal("山田 太郎", Cell(OneTable(PaddleT3), 1, 1));
    }

    [Fact]
    public void OTSL_両方結合_xcel()
    {
        var g = OneTable("<fcel>A<lcel><fcel>B<nl><ucel><xcel><fcel>C<nl><fcel>D<fcel>E<fcel>F<nl>");
        var a = g.Cells.First(c => c.Text == "A");
        Assert.Equal((0, 0, 2, 2), (a.Row, a.Col, a.RowSpan, a.ColSpan));
        Assert.Equal("C", Cell(g, 1, 2));
        Assert.Equal(1 + 1 + 1 + 3, g.Cells.Count);
    }

    [Fact]
    public void OTSL_セルの中の改行は文字のバックスラッシュnも実際の改行も改行にする()
    {
        var g = OneTable("<fcel>a\\nb<fcel>c\nd<nl>");
        Assert.Equal("a\nb", Cell(g, 0, 0));
        Assert.Equal("c\nd", Cell(g, 0, 1));
    }

    [Fact]
    public void OTSL_行ごとのセル数がそろわなければ失敗して文字のまま()
    {
        var logs = new List<string>();
        var parts = TableText.Parse("<fcel>a<fcel>b<nl><fcel>c<nl>", logs.Add);
        Assert.False(TableText.HasTable(parts));
        Assert.Single(logs);
        Assert.Equal("<fcel>a<fcel>b<nl><fcel>c<nl>", ((TextPart)Assert.Single(parts)).Text);
    }

    [Fact]
    public void OTSL_壊れた並びは表にしない()
    {
        foreach (var bad in new[]
        {
            "<lcel><fcel>a<nl>",            // 左端の lcel
            "<ucel><fcel>a<nl>",            // 先頭行の ucel
            "<fcel>a<fcel>b",               // <nl> が無い
            "<fcel>a<nl><fcel>b",           // 最後の行が閉じていない
        })
            Assert.False(TableText.HasTable(TableText.Parse(bad)), bad);
    }

    [Fact]
    public void OTSL_前後の文章は残る()
    {
        var parts = TableText.Parse("前の文\n" + PaddleT1 + "\n後ろの文");
        Assert.Equal(3, parts.Count);
        Assert.Equal("前の文", ((TextPart)parts[0]).Text);
        Assert.IsType<TablePart>(parts[1]);
        Assert.Equal("後ろの文", ((TextPart)parts[2]).Text);
    }

    // ---------------- HTML

    [Fact]
    public void HTML_実物_GLMの表()
    {
        var g1 = OneTable(GlmT1);
        Assert.Equal((4, 3), (g1.Rows, g1.Cols));
        Assert.Equal("", Cell(g1, 3, 1));
        var g2 = OneTable(GlmT2);
        var y = g2.Cells.First(c => c.Text == "2024");
        Assert.Equal((0, 1, 1, 2), (y.Row, y.Col, y.RowSpan, y.ColSpan));
        Assert.Equal((4, 4), (g2.Rows, g2.Cols));
        var g4 = OneTable(GlmT4);
        var u = g4.Cells.First(c => c.Text == "壳上");
        Assert.Equal(2, u.RowSpan);
        Assert.Equal("2月", Cell(g4, 2, 1));   // 行結合で押し出された位置
        Assert.Equal(g4.Rows * g4.Cols, g4.Cells.Sum(c => c.RowSpan * c.ColSpan));
    }

    [Fact]
    public void HTML_GLMが表にしなかった答えは文字のまま()
    {
        Assert.False(TableText.HasTable(TableText.Parse(GlmT3)));
    }

    [Fact]
    public void HTML_th_rowspan_colspan_セル内のタグ_br_引用符なし_thead_tbody()
    {
        var g = OneTable("<TABLE class=x><thead><tr><th colspan=2>見出し</th><th>C</th></tr></thead>" +
                         "<tbody><tr><td rowspan='2'><b>太</b> <i>い</i></td><td>a<br>b<br/>c</td><td>&lt;x&gt; &amp;</td></tr>" +
                         "<tr><td>d</td><td>e</td></tr></tbody></table>");
        Assert.Equal((3, 3), (g.Rows, g.Cols));
        var h = g.Cells.First(c => c.Text == "見出し");
        Assert.True(h.IsHeader);
        Assert.Equal(2, h.ColSpan);
        Assert.True(g.Cells.First(c => c.Text == "C").IsHeader);
        var f = g.Cells.First(c => c.Text == "太 い");
        Assert.Equal((1, 0, 2, 1), (f.Row, f.Col, f.RowSpan, f.ColSpan));
        Assert.Equal("a\nb\nc", Cell(g, 1, 1));
        Assert.Equal("<x> &", Cell(g, 1, 2));
        Assert.Equal("d", Cell(g, 2, 1));
    }

    [Fact]
    public void HTML_td_の閉じタグが無くても読める_短い行は空で埋める()
    {
        var g = OneTable("<table><tr><td>a<td>b<td>c<tr><td>d</table>");
        Assert.Equal((2, 3), (g.Rows, g.Cols));
        Assert.Equal("", Cell(g, 1, 2));
    }

    [Fact]
    public void HTML_閉じていない表は文字のまま()
    {
        var logs = new List<string>();
        var parts = TableText.Parse("<table><tr><td>a</td></tr>", logs.Add);
        Assert.False(TableText.HasTable(parts));
        Assert.Single(logs);
    }

    [Fact]
    public void HTML_前後の文章と数式の文字は残る()
    {
        var parts = TableText.Parse("式 $x^2$ です。\n\n" + GlmT1 + "\n\n$$y$$");
        Assert.Equal(3, parts.Count);
        Assert.Equal("式 $x^2$ です。", ((TextPart)parts[0]).Text);
        Assert.Equal("$$y$$", ((TextPart)parts[2]).Text);
    }

    // ---------------- 誤検出しない

    [Fact]
    public void 誤検出しない_タブの字下げ_Markdownの表_コードフェンスの中()
    {
        Assert.False(TableText.HasTable(TableText.Parse("a\tb\tc\n\td\te\n")));
        Assert.False(TableText.HasTable(TableText.Parse("| a | b |\n|---|---|\n| 1 | 2 |\n")));
        string fenced = "```html\n" + GlmT1 + "\n```\n";
        var p = TableText.Parse(fenced);
        Assert.False(TableText.HasTable(p));
        Assert.Equal(fenced, ((TextPart)Assert.Single(p)).Text);
        Assert.False(TableText.HasTable(TableText.Parse("```\n" + PaddleT1 + "\n```")));
        // フェンスの外の表は読む
        Assert.True(TableText.HasTable(TableText.Parse("```\ncode\n```\n" + GlmT1)));
    }

    // ---------------- Slice・TSV・HTML

    [Fact]
    public void TSV_結合の隠れたマスは空_引用の規則()
    {
        var g = OneTable(PaddleT2);
        Assert.Equal("\t2024\t\t2025\n\tQ1\tQ2\tQ1\nSales\t10\t20\t30\nCost\t5\t6\t7", g.ToTsv());
        var q = OneTable("<fcel>a\tb<fcel>x\"y<nl><fcel>l1\\nl2<fcel>z<nl>");
        Assert.Equal("\"a\tb\"\t\"x\"\"y\"\n\"l1\nl2\"\tz", q.ToTsv());
    }

    [Fact]
    public void HTML出力_結合はrowspan_colspan_thはth()
    {
        var g = OneTable("<table><tr><th colspan=2>H</th></tr><tr><td rowspan=2>a&b</td><td>x<br>y</td></tr><tr><td>z</td></tr></table>");
        Assert.Equal("<table border=\"1\" cellspacing=\"0\" cellpadding=\"3\"><tr><th colspan=\"2\">H</th></tr>" +
                     "<tr><td rowspan=\"2\">a&amp;b</td><td>x<br>y</td></tr><tr><td>z</td></tr></table>", g.ToHtml());
    }

    [Fact]
    public void Slice_かかるマスだけ_端の結合は切り詰める()
    {
        var g = OneTable(PaddleT2);   // 4x4、2024 は (0,1) colspan 2
        var s = g.Slice(0, 1, 2, 3);   // 行0-1・列2-3
        Assert.Equal((2, 2), (s.Rows, s.Cols));
        var y = s.Cells.First(c => c.Text == "2024");
        Assert.Equal((0, 0, 1, 1), (y.Row, y.Col, y.RowSpan, y.ColSpan));
        Assert.Equal("2024\t2025\nQ2\tQ1", s.ToTsv());
    }
}

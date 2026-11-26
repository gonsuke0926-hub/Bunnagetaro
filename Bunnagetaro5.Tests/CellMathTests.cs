using System.Linq;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J94: 表のセルの中の数式（\(^{a}\) などの脚注の印・セルの中の式）の区切りとコピーの形</summary>
public class CellMathTests
{
    [Fact]
    public void J94_脚注の印は上付きになり二重の波かっこも外す()
    {
        var s = CellMath.Split(@"Components\(^{{a}}\)");
        Assert.Equal(2, s.Count);
        Assert.Equal(new CellSeg(CellSegKind.Text, "Components", "Components"), s[0]);
        Assert.Equal(CellSegKind.Sup, s[1].Kind);
        Assert.Equal("a", s[1].Text);
        Assert.Equal(@"\(^{{a}}\)", s[1].Raw);
    }

    [Fact]
    public void J94_ドルの式と下付きと式の中の土台の文字()
    {
        var s = CellMath.Split("H$_2$O and $x^{2}$");
        Assert.Equal(new[] { CellSegKind.Text, CellSegKind.Sub, CellSegKind.Text, CellSegKind.Text, CellSegKind.Sup },
                     s.Select(x => x.Kind).ToArray());
        Assert.Equal("2", s[1].Text);
        Assert.Equal("x", s[3].Text);
        Assert.Equal("2", s[4].Text);
        // \ast・\dagger は記号に
        Assert.Equal("*", CellMath.Split(@"\(^{\ast}\)").Single().Text);
        Assert.Equal("†", CellMath.Split(@"\(^\dagger\)").Single(x => x.Kind == CellSegKind.Sup).Text);
    }

    [Fact]
    public void J94_単純でない式はMathで中身と原文字を持つ()
    {
        var s = CellMath.Split(@"rate \(\alpha^2+\beta\) %");
        Assert.Equal(CellSegKind.Math, s[1].Kind);
        Assert.Equal(@"\alpha^2+\beta", s[1].Text);
        Assert.Equal(@"\(\alpha^2+\beta\)", s[1].Raw);
        Assert.Equal(" %", s[2].Text);
    }

    [Fact]
    public void J94_値段のドルや閉じの無い式は文字のまま()
    {
        Assert.Equal(CellSegKind.Text, CellMath.Split("cost $5 or $10").Single().Kind);
        Assert.Equal(CellSegKind.Text, CellMath.Split(@"open \(x").Single().Kind);
        Assert.Equal(CellSegKind.Text, CellMath.Split(@"\$5").Single().Kind);
        Assert.False(CellMath.HasMath("1,284.6"));
        Assert.Equal("1,284.6", CellMath.ToPlain("1,284.6"));
    }

    [Fact]
    public void J94_文字のコピーは上付き文字_無い字は記号付き()
    {
        Assert.Equal("Componentsᵃ", CellMath.ToPlain(@"Components\(^{a}\)"));
        Assert.Equal("H₂O", CellMath.ToPlain("H$_2$O"));
        Assert.Equal("x¹²", CellMath.ToPlain(@"x\(^{12}\)"));
        Assert.Equal("Q^q", CellMath.ToPlain(@"Q\(^{q}\)"));          // q の上付き文字は無い
        Assert.Equal("v^(*a)", CellMath.ToPlain(@"v\(^{*a}\)"));
        Assert.Equal(@"r \(\alpha\)", CellMath.ToPlain(@"r \(\alpha\)"));   // 単純でない式は原文字のまま
    }

    [Fact]
    public void J94_HTMLのコピーはsupとsub_文字はエンコード()
    {
        Assert.Equal("A&amp;B<sup>a</sup>", CellMath.ToHtml(@"A&B\(^{a}\)"));
        Assert.Equal("H<sub>2</sub>O<br>x", CellMath.ToHtml("H$_2$O\nx"));
        Assert.Equal("a&lt;b", CellMath.ToHtml("a<b"));
    }

    [Fact]
    public void J94_上付き下付きの対応表は文字数がそろう()
    {
        foreach (char c in "0123456789+-=()abcdefghijklmnoprstuvwxyzABDEGHIJKLMNOPRTUW")
            Assert.Equal(1, CellMath.ScriptText(c.ToString(), true).Length);
        foreach (char c in "0123456789+-=()aehijklmnoprstuvx")
            Assert.Equal(1, CellMath.ScriptText(c.ToString(), false).Length);
    }

    [Fact]
    public void J94_表のコピーはcellMathの時だけセルの中を変換する()
    {
        var g = TableText.HtmlToGrid(@"<table><tr><th>Seg</th><th>Rev</th></tr><tr><td>Comp\(^{a}\)</td><td>412.0</td></tr></table>", out _)!;
        Assert.Equal("Seg\tRev\nComp\\(^{a}\\)\t412.0", g.ToTsv());
        Assert.Equal("Seg\tRev\nCompᵃ\t412.0", g.ToTsv(cellMath: true));
        Assert.Contains("<td>Comp<sup>a</sup></td>", g.ToHtml(cellMath: true));
        Assert.Contains(@"<td>Comp\(^{a}\)</td>", g.ToHtml());
        var (text, html) = TableText.ToClipboard(new AnswerPart[] { new TablePart(g, "") }, x => x, cellMath: true);
        Assert.Contains("Compᵃ", text);
        Assert.Contains("<sup>a</sup>", html);
    }
}

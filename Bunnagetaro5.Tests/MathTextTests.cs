using System.Linq;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 本文を「そのままの文字」と「式」へ分ける分割器（MathText）。
/// $...$ 文中 / $$..$$・\[..\] 独立行 / 閉じ無しは文字 / 空の式は文字 /
/// 文中の $ は行をまたがない / \$ と \$ のエスケープ。
/// </summary>
public class MathTextTests
{
    private static (MathSegKind Kind, string Raw, string Tex)[] K(string s)
        => MathText.Split(s).Select(x => (x.Kind, x.Raw, x.Tex)).ToArray();

    [Fact]
    public void 式が無ければTextひとつ()
    {
        var segs = MathText.Split("ただの日本語訳");
        Assert.Single(segs);
        Assert.Equal(MathSegKind.Text, segs[0].Kind);
        Assert.Equal("ただの日本語訳", segs[0].Raw);
    }

    [Fact]
    public void 空文字は空列表()
    {
        Assert.Empty(MathText.Split(""));
    }

    [Fact]
    public void 文中式は三つに分かれる()
    {
        var segs = MathText.Split("a $x^2$ b");
        Assert.Collection(segs,
            s => { Assert.Equal(MathSegKind.Text, s.Kind); Assert.Equal("a ", s.Raw); },
            s => { Assert.Equal(MathSegKind.Inline, s.Kind); Assert.Equal("$x^2$", s.Raw); Assert.Equal("x^2", s.Tex); },
            s => { Assert.Equal(MathSegKind.Text, s.Kind); Assert.Equal(" b", s.Raw); });
    }

    [Fact]
    public void 二ドルは独立行式()
    {
        var segs = MathText.Split("式: $$E=mc^2$$ まで");
        Assert.Equal(3, segs.Count);
        Assert.Equal(MathSegKind.Display, segs[1].Kind);
        Assert.Equal("$$E=mc^2$$", segs[1].Raw);
        Assert.Equal("E=mc^2", segs[1].Tex);
    }

    [Fact]
    public void 角括弧も独立行式でRawにデリミタ込み()
    {
        var segs = MathText.Split(@"\[a+b\]");
        Assert.Single(segs);
        Assert.Equal(MathSegKind.Display, segs[0].Kind);
        Assert.Equal(@"\[a+b\]", segs[0].Raw);
        Assert.Equal("a+b", segs[0].Tex);
    }

    [Fact]
    public void 独立行式は行をまたげる()
    {
        var segs = MathText.Split("$$\n\\frac{1}{2}\n$$");
        Assert.Single(segs);
        Assert.Equal(MathSegKind.Display, segs[0].Kind);
    }

    [Fact]
    public void 文中式は行をまたがない()
    {
        var segs = MathText.Split("price $100\n and $200");
        Assert.All(segs, s => Assert.Equal(MathSegKind.Text, s.Kind));
        Assert.Equal("price $100\n and $200", string.Concat(segs.Select(s => s.Raw)));
    }

    [Fact]
    public void 閉じ無しの末尾は文字のまま()
    {
        var segs = MathText.Split("途中 E = $$x^2 + y");
        Assert.All(segs, s => Assert.Equal(MathSegKind.Text, s.Kind));
        Assert.Equal("途中 E = $$x^2 + y", string.Concat(segs.Select(s => s.Raw)));
    }

    [Fact]
    public void 閉じ無しの単独ドルも文字で後方の式は拾える()
    {
        var segs = MathText.Split("100$ です $x$ 式");
        Assert.Contains(segs, s => s.Kind == MathSegKind.Inline && s.Tex == "x");
        Assert.Equal("100$ です $x$ 式", string.Concat(segs.Select(s => s.Raw)));
    }

    [Fact]
    public void 値段のドル記号は式にしない()
    {
        var segs = MathText.Split("cost $5 or $10 total");
        Assert.All(segs, s => Assert.Equal(MathSegKind.Text, s.Kind));
        Assert.Equal("cost $5 or $10 total", string.Concat(segs.Select(s => s.Raw)));
    }

    [Fact]
    public void 両端が空白でもLaTeXの印があれば文中の式にする_修正J141()
    {
        const string src = "Given an input sequence $ x=(x_{1},...,x_{n}) $ , the scaled";
        var segs = MathText.Split(src);
        var m = Assert.Single(segs, s => s.Kind == MathSegKind.Inline);
        Assert.Equal(" x=(x_{1},...,x_{n}) ", m.Tex);
        Assert.Equal(src, string.Concat(segs.Select(s => s.Raw)));
        foreach (var t in new[] { @"a $ \alpha $ b", "a $ x^2 $ b", "a $ y_1 $ b" })
            Assert.Single(MathText.Split(t), s => s.Kind == MathSegKind.Inline);
        // 印の無い空白付きは今まで通り文字（値段など）
        Assert.All(MathText.Split("pay $ 5 and 10 $ now"), s => Assert.Equal(MathSegKind.Text, s.Kind));
        Assert.All(MathText.Split("a $   $ b"), s => Assert.Equal(MathSegKind.Text, s.Kind));
        // 表のセルでも同じ
        Assert.Contains(CellMath.Split("$ x_{1} $"), s => s.Kind != CellSegKind.Text);
    }

    [Fact]
    public void 空の式は文字のまま()
    {
        var segs = MathText.Split("a $$ b");
        Assert.All(segs, s => Assert.Equal(MathSegKind.Text, s.Kind));
        Assert.Equal("a $$ b", string.Concat(segs.Select(s => s.Raw)));
        var segs2 = MathText.Split("$ $");
        Assert.All(segs2, s => Assert.Equal(MathSegKind.Text, s.Kind));
    }

    [Fact]
    public void エスケープされたドルは文字()
    {
        var segs = MathText.Split(@"値は \$5 で \(x\)");
        Assert.All(segs, s => Assert.Equal(MathSegKind.Text, s.Kind));
    }

    [Fact]
    public void 式の中のエスケープドルは閉じにしない()
    {
        var segs = MathText.Split(@"a $x\$y$ b");
        var f = segs.Single(s => s.Kind == MathSegKind.Inline);
        Assert.Equal(@"x\$y", f.Tex);
    }

    [Fact]
    public void 式が連続しても分かれる()
    {
        var segs = MathText.Split("$a$$b$");
        Assert.Equal(2, segs.Count(s => s.Kind == MathSegKind.Inline));
        Assert.Equal("a", segs.First(s => s.Kind == MathSegKind.Inline).Tex);
    }

    [Fact]
    public void 文中式と独立行式の混在()
    {
        var segs = MathText.Split("本文 $x$ と\n$$y$$\n末尾");
        Assert.Equal(MathSegKind.Text, segs[0].Kind);
        Assert.Equal(MathSegKind.Inline, segs[1].Kind);
        Assert.Equal(MathSegKind.Text, segs[2].Kind);
        Assert.Equal(MathSegKind.Display, segs[3].Kind);
        Assert.Equal(MathSegKind.Text, segs[4].Kind);
        Assert.Equal("本文 $x$ と\n$$y$$\n末尾", string.Concat(segs.Select(s => s.Raw)));
    }

    [Fact]
    public void Rawのつながりは原文と完全に一致()
    {
        foreach (var src in new[]
                 {
                     "plain only", "a $b$ c", "$$x$$", @"\[y\]", "a$b$c",
                     "$$", "$$", "cost $5", "$a$$b$", "a\n\nb $c$\n",
                 })
        {
            Assert.Equal(src, string.Concat(MathText.Split(src).Select(s => s.Raw)));
        }
    }

    [Fact]
    public void マクスウェル方程式4本の複数行二ドルは式4つと平文0で切れ_ストリーミング途中でも崩れない()
    {
        const string eq1 = "$$\n\\nabla \\cdot \\mathbf{E} = \\frac{\\rho}{\\varepsilon_0}\n$$";
        const string eq2 = "$$\n\\nabla \\cdot \\mathbf{B} = 0\n$$";
        const string eq3 = "$$\n\\nabla \\times \\mathbf{E} = -\\frac{\\partial \\mathbf{B}}{\\partial t}\n$$";
        const string eq4 = "$$\n\\nabla \\times \\mathbf{B} = \\mu_0 \\mathbf{J} + \\mu_0 \\varepsilon_0 \\frac{\\partial \\mathbf{E}}{\\partial t}\n$$";

        // 4つ並んだ答え（連続でも改行区切りでも Display 式4つ・非空白の平文0）
        string joined = eq1 + eq2 + eq3 + eq4;
        var segs0 = MathText.Split(joined);
        Assert.Equal(4, segs0.Count(s => s.Kind == MathSegKind.Display));
        Assert.Equal(0, segs0.Count(s => s.Kind == MathSegKind.Text));

        string withNewlines = string.Join("\n", eq1, eq2, eq3, eq4);
        var segsN = MathText.Split(withNewlines);
        Assert.Equal(4, segsN.Count(s => s.Kind == MathSegKind.Display));
        Assert.Equal(0, segsN.Count(s => s.Kind == MathSegKind.Text && s.Raw.Trim().Length > 0));

        // ストリーミング途中（$$ が片方だけ等、1文字ずつ伸ばす全段階）でも例外にならず原文と一致する
        for (int len = 1; len <= withNewlines.Length; len++)
        {
            string partial = withNewlines[..len];
            var segs = MathText.Split(partial);
            Assert.Equal(partial, string.Concat(segs.Select(s => s.Raw)));
            Assert.InRange(segs.Count(s => s.Kind == MathSegKind.Display), 0, 4);
        }
    }

    // ---- LaTeX のコピーの形 ----

    [Theory]
    [InlineData(LatexCopyForm.Plain, "\\frac{a}{b}", "\\frac{a}{b}")]
    [InlineData(LatexCopyForm.Dollar, "$$\\frac{a}{b}$$", "$\\frac{a}{b}$")]
    [InlineData(LatexCopyForm.Paren, "\\[\\frac{a}{b}\\]", "\\(\\frac{a}{b}\\)")]
    public void F13_式1つは選んだ形で出る_文中と独立行で囲みが違う(LatexCopyForm form, string display, string inline)
    {
        Assert.Equal(display, MathText.Wrap(" \\frac{a}{b} ", true, form));
        Assert.Equal(inline, MathText.Wrap("\\frac{a}{b}", false, form));
    }

    [Fact]
    public void F13_式だけの答えは選んだ形で改行でつなぐ_元の囲みが何でも同じ()
    {
        string raw = "$$a=1$$\n\\[b=2\\]\n$c$";
        Assert.Equal("a=1\nb=2\nc", MathText.ApplyCopyForm(raw, LatexCopyForm.Plain));
        Assert.Equal("$$a=1$$\n$$b=2$$\n$c$", MathText.ApplyCopyForm(raw, LatexCopyForm.Dollar));
        Assert.Equal("\\[a=1\\]\n\\[b=2\\]\n\\(c\\)", MathText.ApplyCopyForm(raw, LatexCopyForm.Paren));
        // 選択コピーの改行（\r\n）はそのまま使う
        Assert.Equal("a=1\r\nb=2", MathText.ApplyCopyForm("$$a=1$$\r\n$$b=2$$", LatexCopyForm.Plain));
        // 式1つだけ
        Assert.Equal("\\frac{a}{b}", MathText.ApplyCopyForm("$$\\frac{a}{b}$$", LatexCopyForm.Plain));
    }

    [Fact]
    public void F13_文章と式が混ざる時は中身だけでも囲む_ほかの形はその形で囲み直す()
    {
        string raw = "答えは $x^2$ です。\n$$y=1$$\nおわり";
        Assert.Equal(raw, MathText.ApplyCopyForm(raw, LatexCopyForm.Plain));   // 安全側: 原文のまま
        Assert.Equal("答えは $x^2$ です。\n$$y=1$$\nおわり", MathText.ApplyCopyForm(raw, LatexCopyForm.Dollar));
        Assert.Equal("答えは \\(x^2\\) です。\n\\[y=1\\]\nおわり", MathText.ApplyCopyForm(raw, LatexCopyForm.Paren));
        // 式が無い答えは触らない
        Assert.Equal("ただの文章", MathText.ApplyCopyForm("ただの文章", LatexCopyForm.Paren));
    }

    [Fact]
    public void F13_設定値の読み書きは不正な値をplainに直す()
    {
        Assert.Equal(LatexCopyForm.Plain, MathText.ParseCopyForm(null));
        Assert.Equal(LatexCopyForm.Plain, MathText.ParseCopyForm("zzz"));
        Assert.Equal(LatexCopyForm.Dollar, MathText.ParseCopyForm("dollar"));
        Assert.Equal(LatexCopyForm.Paren, MathText.ParseCopyForm("paren"));
        Assert.Equal("paren", MathText.CopyFormKey(LatexCopyForm.Paren));
    }

    // ---------------- 式番号（\tag）は描く時だけ式の右に離す ----------------

    [Fact]
    public void J93_tagは描く用だけ式の右に離した文字にする()
    {
        Assert.Equal(@"x=1 \qquad\text{(4.8)}", MathText.TagForDisplay(@"x=1 \tag {4.8}"));
        Assert.Equal(@"x=1\qquad\text{(4.8)}", MathText.TagForDisplay(@"x=1\tag{4.8}"));
        Assert.Equal(@"x=1\qquad\text{★}", MathText.TagForDisplay(@"x=1\tag*{★}"));
        // 中に波かっこがあっても1段までは読む・2つあれば両方
        Assert.Equal(@"a\qquad\text{(A{1})}\\ b\qquad\text{(2)}", MathText.TagForDisplay(@"a\tag{A{1}}\\ b\tag{2}"));
        // \tag が無ければ同じ文字のまま
        const string plain = @"\frac{1}{2}";
        Assert.Same(plain, MathText.TagForDisplay(plain));
    }

    [Fact]
    public void J93_区切れ目はTexだけ置き換えRawはコピー用にそのまま()
    {
        string src = "前 $$x=1\\tag{4.8}$$ 後";
        var segs = MathText.WithDisplayTags(MathText.Split(src));
        var d = segs.Single(s => s.Kind == MathSegKind.Display);
        Assert.Equal("x=1\\qquad\\text{(4.8)}", d.Tex);
        Assert.Equal("$$x=1\\tag{4.8}$$", d.Raw);
        Assert.Equal("前 ", segs[0].Raw);
        // コピーの形は原文字から作るので \tag のまま
        Assert.Equal("x=1\\tag{4.8}", MathText.ApplyCopyForm("$$x=1\\tag{4.8}$$", LatexCopyForm.Plain));
    }
}

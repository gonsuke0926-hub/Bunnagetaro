using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>Markdown の変換（読み取り・記号を外したコピー・表・設定・解答欄の表示）</summary>
public sealed class MarkdownTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public MarkdownTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_md_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    // ---------------- 塊

    [Fact]
    public void 塊_見出し_箇条書きの深さ_番号_コード_引用_区切り線()
    {
        string md = "# 大見出し\n本文1行目\n本文2行目\n\n- a\n  - b\n- c\n\n1. 一\n2) 二\n\n```cs\nvar x = 1; // **太字ではない**\n```\n\n> 引用\n> 続き\n\n---\n最後";
        var b = MarkdownText.ParseBlocks(md);
        Assert.Equal(MdBlockKind.Heading, b[0].Kind);
        Assert.Equal(1, b[0].Level);
        Assert.Equal("大見出し", b[0].Text);
        Assert.Equal(MdBlockKind.Para, b[1].Kind);
        Assert.Equal("本文1行目\n本文2行目", b[1].Text);
        Assert.Equal((MdBlockKind.ListItem, 0, "•", "a"), (b[2].Kind, b[2].Level, b[2].Marker, b[2].Text));
        Assert.Equal((MdBlockKind.ListItem, 1, "◦", "b"), (b[3].Kind, b[3].Level, b[3].Marker, b[3].Text));
        Assert.Equal((MdBlockKind.ListItem, 0, "•", "c"), (b[4].Kind, b[4].Level, b[4].Marker, b[4].Text));
        Assert.Equal("1.", b[5].Marker);
        Assert.Equal("2.", b[6].Marker);
        Assert.Equal(MdBlockKind.Code, b[7].Kind);
        Assert.Equal("cs", b[7].Marker);
        Assert.Equal("var x = 1; // **太字ではない**", b[7].Text);
        Assert.Equal(MdBlockKind.Quote, b[8].Kind);
        Assert.Equal("引用\n続き", b[8].Text);
        Assert.Equal(MdBlockKind.Rule, b[9].Kind);
        Assert.Equal(MdBlockKind.Para, b[10].Kind);
        Assert.Equal("最後", b[10].Text);
    }

    [Fact]
    public void 塊_段落の直後の番号付きと箇条書き_下線の見出し_ハッシュタグは見出しにしない()
    {
        var b = MarkdownText.ParseBlocks("手順:\n1. 開く\n2. 押す\n\n題\n===\n#hashtag");
        Assert.Equal(MdBlockKind.Para, b[0].Kind);
        Assert.Equal(MdBlockKind.ListItem, b[1].Kind);
        Assert.Equal(MdBlockKind.ListItem, b[2].Kind);
        Assert.Equal((MdBlockKind.Heading, 1, "題"), (b[3].Kind, b[3].Level, b[3].Text));
        Assert.Equal((MdBlockKind.Para, "#hashtag"), (b[4].Kind, b[4].Text));
    }

    [Fact]
    public void 塊_閉じていないコードは最後までコード()
    {
        var b = MarkdownText.ParseBlocks("前\n```\nx = 1\n**y**");
        Assert.Equal(MdBlockKind.Code, b[1].Kind);
        Assert.Equal("x = 1\n**y**", b[1].Text);
    }

    // ---------------- 行の中

    private static string Show(string s) => string.Join("|", MarkdownText.ParseInline(s).Select(x =>
        x.IsBreak ? "BR" : (x.Style == MdStyle.None ? "" : x.Style + ":") + x.Text));

    [Theory]
    [InlineData("これは**重要**です", "これは|Bold:重要|です")]
    [InlineData("*斜体*と__太字__", "Italic:斜体|と|Bold:太字")]
    [InlineData("***両方***", "Bold, Italic:両方")]
    [InlineData("~~消し~~", "Strike:消し")]
    [InlineData("`a*b*c` は code", "Code:a*b*c| は code")]
    [InlineData("[文字](https://example.com) と ![絵](x.png)", "文字 と 絵")]
    [InlineData("a*b*c と snake_case_name", "a*b*c と snake_case_name")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("\\*記号\\*", "*記号*")]
    [InlineData("**閉じていない", "**閉じていない")]
    [InlineData("1行\n2行", "1行|BR|2行")]
    public void 行の中の飾り(string md, string expect) => Assert.Equal(expect, Show(md));

    [Fact]
    public void 式の中は触らない()
    {
        string text = "**太字 $a*b*c$** と $x_1 + y_2$ の_外_";
        var segs = MathText.Split(text);
        var spans = MarkdownText.ParseInline(MarkdownText.Protect(segs));
        // 太字の中に式が1つ、外に式が1つ
        Assert.Equal(2, spans.Count(s => s.Math >= 0));
        Assert.Contains(spans, s => s.Math >= 0 && s.Style == MdStyle.Bold);
        Assert.Contains(spans, s => s.Text == "外" && s.Style == MdStyle.Italic);
        Assert.Equal("太字 $a*b*c$ と $x_1 + y_2$ の外", MarkdownText.ToPlain(text));
    }

    // ---------------- 記号を外した文字

    [Fact]
    public void 記号を外した文字()
    {
        string md = "# 見出し\n\n**太字**と`コード`\n\n- a\n  - b\n1. c\n\n> 引用\n\n---\n\n```\n# そのまま\n```";
        Assert.Equal("見出し\n\n太字とコード\n\n• a\n  ◦ b\n1. c\n\n引用\n\n# そのまま", MarkdownText.ToPlain(md));
        Assert.Equal("記号の無い文\nそのまま", MarkdownText.ToPlain("記号の無い文\nそのまま"));
        // LaTeX の変換が OFF なら $ の中も Markdown として読む
        Assert.Equal("$a b$", MarkdownText.ToPlain("$a *b*$", latex: false));
    }

    // ---------------- 表

    [Fact]
    public void Markdownの表は_parseMarkdownの時だけ表になる()
    {
        string text = "前\n| 名前 | 値 |\n|:--|--:|\n| **a** | $|x|$ |\n| b\\|c |\n後";
        Assert.False(TableText.MayContainTable(text, parseOtsl: true, parseHtml: true, parseMarkdown: false));
        Assert.Empty(TableText.Parse(text).OfType<TablePart>());
        var parts = TableText.Parse(text, null, parseOtsl: false, parseHtml: false, parseMarkdown: true);
        var g = Assert.Single(parts.OfType<TablePart>()).Grid;
        Assert.Equal((3, 2), (g.Rows, g.Cols));
        Assert.True(g.Cells[0].IsHeader);
        Assert.Equal("名前", g.Cells[0].Text);
        Assert.Equal("a", g.Cells[2].Text);          // 飾りは外す
        Assert.Equal("$|x|$", g.Cells[3].Text);      // 式の中の | では分けない
        Assert.Equal("b|c", g.Cells[4].Text);        // \| は | の文字
        Assert.Equal("", g.Cells[5].Text);           // 足りないマスは空
        Assert.Equal("前", ((TextPart)parts[0]).Text);
        Assert.Equal("後", ((TextPart)parts[^1]).Text);
    }

    [Fact]
    public void 区切りの行が無い_列の数が違う_コードの中は表にしない()
    {
        foreach (var t in new[] { "| a | b |\n| 1 | 2 |", "| a | b |\n|---|\n| 1 | 2 |", "```\n| a | b |\n|---|---|\n```" })
            Assert.Empty(TableText.Parse(t, null, false, false, parseMarkdown: true).OfType<TablePart>());
    }

    [Fact]
    public void ForCopy_表はタブ区切りとHTML_表の外は記号を外す()
    {
        var (text, html) = MarkdownText.ForCopy("**結果**\n\n| A | B |\n|---|---|\n| 1 | 2 |");
        Assert.Equal("結果\r\nA\tB\r\n1\t2", text);
        Assert.NotNull(html);
        Assert.Contains("<th>A</th>", html);
        Assert.Equal(("太字", (string?)null), MarkdownText.ForCopy("**太字**"));
    }

    // ---------------- 設定

    [Fact]
    public void 設定_既定はON_既定のOCRだけOFF()
    {
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        Assert.True(cfg.Settings.ConvertMarkdown);
        Assert.True(PanelStyle.From(cfg).ConvertMarkdown);
        cfg.Settings.PromptActive = "OCR";
        Assert.False(cfg.EffectiveBool("convert_markdown", true));
        Assert.False(PanelStyle.From(cfg).ConvertMarkdown);
        cfg.Settings.PromptActive = "数式";
        Assert.True(PanelStyle.From(cfg).ConvertMarkdown);
    }

    [Fact]
    public void 引っ越し_前の版の設定は既定のOCRだけOFFにする_自分で決めた値は触らない()
    {
        // 前の版の設定（convert_markdown が無い）
        var old = ConfigStore.DefaultConfig();
        old.Remove("convert_markdown");
        foreach (var p in ((JsonArray)old["prompts"]!).OfType<JsonObject>())
            (p["settings"] as JsonObject)?.Remove("convert_markdown");
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.ConfigJson, old.ToJsonString());
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.Settings.PromptActive = "OCR";
        Assert.False(cfg.EffectiveBool("convert_markdown", true));
        cfg.Settings.PromptActive = "翻訳";
        Assert.True(cfg.EffectiveBool("convert_markdown", true));

        // もう convert_markdown がある設定（この版で保存した）は、OCR を ON にしていてもそのまま
        cfg.Settings.PromptActive = "OCR";
        var ocr = ((JsonArray)cfg.Root["prompts"]!).OfType<JsonObject>().First(p => (string?)p["name"] == "OCR");
        ocr["settings"]!["convert_markdown"] = true;
        cfg.Save();
        var cfg2 = new ConfigStore(_paths);
        cfg2.Load();
        cfg2.Settings.PromptActive = "OCR";
        Assert.True(cfg2.EffectiveBool("convert_markdown", true));
    }

    // ---------------- 解答欄

    private sealed class Host : IAnswerHost
    {
        public bool FollowupSend(string question, bool withImage) => false;
        public void TouchPanelGen() { }
        public void SetStatus(string msg) { }
        public Native.RECT? BarRectNow() => null;
        public void PlacePanel(Native.RECT? target = null) { }
        public void PlaceStrip(Native.RECT? barRect) { }
        public bool DeferPanelWhileDragging(Action again) => false;
        public void ResetPanelRun() { }
        public bool RequestPanelShow() => false;
        public bool IsPanelFadingOut() => false;
        public void HidePanelForCapture() { }
        public void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
        public void RequestStripShow() { }
        public void HideStripNow() { }
        public void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
    }

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    private static PanelStyle Style(bool md) =>
        new("#f5f5f5", "#202020", 0.7, "", 14, ConvertMarkdown: md);

    private static AnswerView NewView(bool md)
    {
        EnsureApp();
        var v = new AnswerView(new Host(), 6, 90) { Width = 400, Height = 400 };
        v.Measure(new Size(400, 400));
        v.Arrange(new Rect(0, 0, 400, 400));
        v.ApplyStyle(Style(md), 240, 120);
        return v;
    }

    private static string AllText(AnswerView v) =>
        new TextRange(v.Body.Document.ContentStart, v.Body.Document.ContentEnd).Text;

    private static (string Text, string? Html) FullCopy(AnswerView v)
        => ((string, string?))typeof(AnswerView).GetMethod("FullCopy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(v, null)!;

    private const string Sample = "# 見出し\n\n**太字** と *斜体* と `code`\n\n- 一つ目\n- 二つ目";

    [Fact]
    public void 解答欄_ONなら見た目にして記号を出さない_OFFなら記号のまま()
    {
        TestUtil.OnSta(() =>
        {
            var on = NewView(md: true);
            on.ShowFinal(Sample);
            string t = AllText(on);
            Assert.DoesNotContain("**", t);
            Assert.DoesNotContain("# ", t);
            Assert.Contains("• 一つ目", t.Replace(' ', ' '));
            var paras = on.Body.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(FontWeights.Bold, paras[0].FontWeight);   // 見出し
            Assert.True(paras[0].FontSize > 14);
            var runs = paras[1].Inlines.OfType<Run>().ToList();
            Assert.Contains(runs, r => r.Text == "太字" && r.FontWeight == FontWeights.Bold);
            Assert.Contains(runs, r => r.Text == "斜体" && r.FontStyle == FontStyles.Italic);
            Assert.Contains(runs, r => r.Text == "code" && r.FontFamily.Source.StartsWith("Consolas"));

            var off = NewView(md: false);
            off.ShowFinal(Sample);
            Assert.Contains("**太字**", AllText(off));
            Assert.Contains("# 見出し", AllText(off));
        }, 90);
    }

    [Fact]
    public void 解答欄_流している間は変わった塊から後ろだけ組み直す()
    {
        TestUtil.OnSta(() =>
        {
            var v = NewView(md: true);
            v.ResetRun();
            v.ApplyBody("# 見出し\n\n本文の**途");
            var first = v.Body.Document.Blocks.FirstBlock;
            Assert.Contains("**途", AllText(v));   // 閉じが来るまでは記号のまま
            v.ApplyBody("# 見出し\n\n本文の**途中**です");
            Assert.Same(first, v.Body.Document.Blocks.FirstBlock);   // 見出しは作り直さない
            Assert.DoesNotContain("**", AllText(v));
            v.ApplyBody("# 見出し\n\n本文の**途中**です\n\n| A | B |\n|---|---|\n| 1 | 2 |");
            Assert.Empty(v.Body.Document.Blocks.OfType<Table>());   // 表は出し終わってから
            v.ShowFinal("# 見出し\n\n本文の**途中**です\n\n| A | B |\n|---|---|\n| 1 | 2 |");
            Assert.Single(v.Body.Document.Blocks.OfType<Table>());
            Assert.DoesNotContain("**", AllText(v));
        }, 90);
    }

    [Fact]
    public void 解答欄_末尾に足すだけの近道は_読み方が変わる時は使わない()
    {
        TestUtil.OnSta(() =>
        {
            var v = NewView(md: true);
            v.ResetRun();
            v.ApplyBody("長い段落の1行目");
            var p = (Paragraph)v.Body.Document.Blocks.FirstBlock;
            v.ApplyBody("長い段落の1行目\n2行目");
            Assert.Same(p, v.Body.Document.Blocks.FirstBlock);   // 近道: 同じ段落に足す
            Assert.Equal("長い段落の1行目\r\n2行目\r\n", AllText(v));
            v.ApplyBody("長い段落の1行目\n2行目 _a_");
            Assert.Contains(((Paragraph)v.Body.Document.Blocks.FirstBlock).Inlines.OfType<Run>(), r => r.Text == "a" && r.FontStyle == FontStyles.Italic);
            v.ApplyBody("長い段落の1行目\n2行目 _a_b");   // 英字が続くと _ は飾りでなくなる
            Assert.Contains("_a_b", AllText(v));
        }, 90);
    }

    [Fact]
    public void 解答欄_全部コピーは記号を外した文字_式はそのまま_表はタブ区切り()
    {
        TestUtil.OnSta(() =>
        {
            var v = NewView(md: true);
            v.ShowFinal("**答え**は $a*b*c$ です\n\n| A | B |\n|---|---|\n| 1 | 2 |");
            var (text, html) = FullCopy(v);
            Assert.Equal("答えは $a*b*c$ です\r\nA\tB\r\n1\t2", text);
            Assert.NotNull(html);

            var off = NewView(md: false);
            off.ShowFinal("**答え**");
            Assert.Equal("**答え**", FullCopy(off).Text);
        }, 90);
    }

    [Fact]
    public void 解答欄_スイッチを切り替えるとその場で組み直す()
    {
        TestUtil.OnSta(() =>
        {
            var v = NewView(md: false);
            v.ShowFinal(Sample);
            Assert.Contains("**太字**", AllText(v));
            v.ApplyStyle(Style(true), 240, 120);
            Assert.DoesNotContain("**", AllText(v));
            v.ApplyStyle(Style(false), 240, 120);
            Assert.Contains("**太字**", AllText(v));
        }, 90);
    }
}

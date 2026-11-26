using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Bunnagetaro5.Services;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

public sealed class ConversionToggleTests : IDisposable
{
    private readonly string _dir;

    public ConversionToggleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Bunnagetaro5_I11_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void RunSta(Action action)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                err = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw new TargetInvocationException(err);
    }

    private static T GetField<T>(object obj, string name)
    {
        var f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (T)f!.GetValue(obj)!;
    }

    private Paths NewPaths(string name) =>
        new(Path.Combine(_dir, name));

    [Fact]
    public void AppSettings_I11Flags_変換は既定ON_たずねる欄は既定OFF_保存される()
    {
        var p = NewPaths("cfg");
        var cfg1 = new ConfigStore(p);
        cfg1.Load();
        Assert.True(cfg1.Settings.ConvertOtslTable);
        Assert.True(cfg1.Settings.ConvertHtmlTable);
        Assert.True(cfg1.Settings.ConvertLatexMath);
        Assert.False(cfg1.Settings.ShowFollowRow);   // 既定 OFF（人の判断 2026-11-14）

        cfg1.Settings.ConvertOtslTable = false;
        cfg1.Settings.ConvertHtmlTable = false;
        cfg1.Settings.ConvertLatexMath = false;
        cfg1.Settings.ShowFollowRow = true;
        cfg1.Save();

        var cfg2 = new ConfigStore(p);
        cfg2.Load();
        Assert.False(cfg2.Settings.ConvertOtslTable);
        Assert.False(cfg2.Settings.ConvertHtmlTable);
        Assert.False(cfg2.Settings.ConvertLatexMath);
        Assert.True(cfg2.Settings.ShowFollowRow);
    }

    [Fact]
    public void TableText_Parse_RespectsOtslAndHtmlFlags()
    {
        string text =
            "前\n" +
            "<fcel>A<fcel>B<nl><fcel>1<fcel>2<nl>\n" +
            "中\n" +
            "<table><tr><th>X</th><th>Y</th></tr><tr><td>3</td><td>4</td></tr></table>\n" +
            "後";

        var bothOn = TableText.Parse(text, null, parseOtsl: true, parseHtml: true);
        Assert.Equal(2, bothOn.OfType<TablePart>().Count());

        var otslOff = TableText.Parse(text, null, parseOtsl: false, parseHtml: true);
        Assert.Single(otslOff.OfType<TablePart>());
        Assert.Contains("<fcel>A", otslOff.OfType<TextPart>().First().Text);

        var htmlOff = TableText.Parse(text, null, parseOtsl: true, parseHtml: false);
        Assert.Single(htmlOff.OfType<TablePart>());
        Assert.Contains("<table>", htmlOff.OfType<TextPart>().Last().Text);

        var bothOff = TableText.Parse(text, null, parseOtsl: false, parseHtml: false);
        Assert.Empty(bothOff.OfType<TablePart>());
        Assert.Single(bothOff);
        Assert.False(TableText.MayContainTable(text, parseOtsl: false, parseHtml: false));
    }

    [Theory]
    // 変換は OTSL → HTML → 画面の表 の順につながる（人の判断 2026-11-15）
    [InlineData(true, true, "table", "")]                 // 両方 ON: 表
    [InlineData(true, false, "<table>", "<fcel>")]        // OTSL ON・HTML OFF: HTML の文字（OTSL の印は残らない）
    [InlineData(false, true, "<fcel>", "<table>")]        // OTSL OFF: OTSL のまま（HTML の表には変えない）
    [InlineData(false, false, "<fcel>", "<table>")]       // 両方 OFF: OTSL のまま
    public void AnswerView_OTSLはHTMLを経て表になる(bool otsl, bool html, string expect, string notExpect)
    {
        RunSta(() =>
        {
            var cfg = new ConfigStore(NewPaths("otsl_" + otsl + html));
            cfg.Settings.ConvertOtslTable = otsl;
            cfg.Settings.ConvertHtmlTable = html;
            var view = new AnswerView();
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);
            view.ResetRun();
            view.ShowFinal("前\n<fcel>A<fcel>B<nl><fcel>1<ecel><nl>\n後");

            var body = GetField<RichTextBox>(view, "_body");
            string plain = new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text;
            if (expect == "table")
            {
                Assert.Single(body.Document.Blocks.OfType<Table>());
                return;
            }
            Assert.Empty(body.Document.Blocks.OfType<Table>());
            Assert.Contains(expect, plain);
            Assert.DoesNotContain(notExpect, plain);
            Assert.Contains("前", plain);
            Assert.Contains("後", plain);
            if (otsl) Assert.Contains("<tr><td>A</td><td>B</td></tr>", plain);
        });
    }

    [Fact]
    public void OtslToHtmlText_OTSLだけをHTMLの表の文字にしてほかは残す()
    {
        string text = "前\n<fcel>A<lcel><nl><fcel>1<fcel>2<nl>\n中\n<table><tr><td>X</td></tr></table>";
        string h = TableText.OtslToHtmlText(text);
        Assert.Equal(
            "前\n<table>\n<tr><td colspan=\"2\">A</td></tr>\n<tr><td>1</td><td>2</td></tr>\n</table>\n中\n<table><tr><td>X</td></tr></table>",
            h);
        Assert.Equal("OTSL の無い文", TableText.OtslToHtmlText("OTSL の無い文"));
    }

    [Fact]
    public void AnswerView_RespectsMathAndFollowRowToggles()
    {
        RunSta(() =>
        {
            var cfg = new ConfigStore(NewPaths("view_cfg"));
            cfg.Settings.ConvertLatexMath = false;
            cfg.Settings.ShowFollowRow = false;

            var view = new AnswerView();
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);

            var followRow = GetField<DockPanel>(view, "_followRow");
            Assert.Equal(Visibility.Collapsed, followRow.Visibility);

            view.ResetRun();
            view.ShowFinal("$x^2 + y^2$");

            var body = GetField<RichTextBox>(view, "_body");
            Assert.False(body.IsDocumentEnabled);
            string plain = new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text;
            Assert.Contains("$x^2 + y^2$", plain);

            // Turning ON live rebuilds with math enabled and reveals follow row
            cfg.Settings.ConvertLatexMath = true;
            cfg.Settings.ShowFollowRow = true;
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);

            Assert.Equal(Visibility.Visible, followRow.Visibility);
            Assert.True(body.IsDocumentEnabled);
        });
    }

    [Fact]
    public void J24b_見た目の値が同じならApplyStyleで式の答えを組み直さず_変われば組み直す()
    {
        RunSta(() =>
        {
            var cfg = new ConfigStore(NewPaths("j24b_cfg"));
            var view = new AnswerView();
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);
            view.ResetRun();
            view.ShowFinal(string.Join("\n", Enumerable.Range(1, 10).Select(i => $"式{i}: $x^{i} + y^{i}$")));
            var body = GetField<RichTextBox>(view, "_body");
            Assert.True(body.IsDocumentEnabled);   // 式の答え

            for (int i = 0; i < 5; i++)
                view.ApplyStyle(PanelStyle.From(cfg), 240, 120);   // 何も変えずに何度も呼ばれる（プリセットの切り替えなど）
            Assert.Equal(0, view.StyleRebuilds);

            cfg.Settings.OverlayFontSize = cfg.Settings.OverlayFontSize + 2;   // 字の大きさが変われば組み直す
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);
            Assert.Equal(1, view.StyleRebuilds);
        });
    }

    [Fact]
    public void AnswerView_RespectsTableToggles()
    {
        RunSta(() =>
        {
            var cfg = new ConfigStore(NewPaths("table_cfg"));
            cfg.Settings.ConvertOtslTable = false;
            cfg.Settings.ConvertHtmlTable = false;

            var view = new AnswerView();
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);

            view.ResetRun();
            view.ShowFinal("<table><tr><th>A</th></tr><tr><td>1</td></tr></table>");

            var body = GetField<RichTextBox>(view, "_body");
            Assert.Empty(body.Document.Blocks.OfType<Table>());

            // Turning HTML table conversion ON live rebuilds into a Table block
            cfg.Settings.ConvertHtmlTable = true;
            view.ApplyStyle(PanelStyle.From(cfg), 240, 120);
            Assert.Single(body.Document.Blocks.OfType<Table>());
        });
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正I3b・I3c: 出し終わった答えの表を WPF の表に組み替える・表のコピー（文字＝タブ区切り と HTML）</summary>
public sealed class AnswerTableTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public AnswerTableTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_tbl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 90);

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    private sealed class Host : IAnswerHost
    {
        public string Status = "";
        public bool FollowupSend(string question, bool withImage) => false;
        public void TouchPanelGen() { }
        public void SetStatus(string msg) => Status = msg;
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

    private static AnswerView NewView(double width = 400)
    {
        EnsureApp();
        var v = new AnswerView(new Host(), 6, 90);
        v.Width = width;
        v.Height = 400;
        v.Measure(new Size(width, 400));
        v.Arrange(new Rect(0, 0, width, 400));
        v.UpdateLayout();
        return v;
    }

    private static void Settle(AnswerView v)
    {
        v.UpdateLayout();
        v.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        v.UpdateLayout();
    }

    private static Table[] Tables(AnswerView v) => v.Body.Document.Blocks.OfType<Table>().ToArray();

    private static (string Text, string? Html) Call(AnswerView v, string name)
        => ((string, string?))typeof(AnswerView).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(v, null)!;

    private static string AllText(AnswerView v) =>
        new TextRange(v.Body.Document.ContentStart, v.Body.Document.ContentEnd).Text;

    private const string Paddle = TableTextTests.PaddleT2;
    private const string Glm = TableTextTests.GlmT4;

    // ---------------- 組み替え（I3b）

    [Fact]
    public void 流している間は文字のまま_出し終わった後だけ表になる()
    {
        OnSta(() =>
        {
            var v = NewView();
            string full = "前の文です\n" + Paddle + "\n後ろの文です";
            for (int n = 10; n < full.Length; n += 17) v.ApplyBody(full[..n]);
            v.ApplyBody(full);
            Assert.Empty(Tables(v));
            Assert.Contains("<fcel>", AllText(v));   // OTSL の印が見えてよい
            v.ShowFinal(full);
            var t = Assert.Single(Tables(v));
            Assert.Equal(4, t.RowGroups[0].Rows.Count);
            Assert.DoesNotContain("<fcel>", AllText(v));
            Assert.Contains("前の文です", AllText(v));
            Assert.Contains("後ろの文です", AllText(v));
            Assert.Contains("Sales", AllText(v));
            // 結合（2024 は2列）と空の左上
            var cells = t.RowGroups[0].Rows[0].Cells;
            Assert.Equal(3, cells.Count);          // 空・2024（結合）・2025
            Assert.Equal(2, cells[1].ColumnSpan);
            Assert.Equal(full, v.ModelText);       // 答えそのものは変えない
        });
    }

    [Fact]
    public void HTMLの表と行結合_見出しだけ太字_前後の文章と数式が残る()
    {
        OnSta(() =>
        {
            var v = NewView();
            string full = "式 $x^2$ です\n\n<table><tr><th>見出し</th><th>B</th></tr><tr><td rowspan=2>売上</td><td>1</td></tr><tr><td>2</td></tr></table>\n\n$$y$$";
            v.ShowFinal(full);
            var t = Assert.Single(Tables(v));
            var rows = t.RowGroups[0].Rows;
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, rows[1].Cells[0].RowSpan);
            Assert.Equal(FontWeights.Bold, ((Paragraph)rows[0].Cells[0].Blocks.FirstBlock).FontWeight);
            Assert.NotEqual(FontWeights.Bold, ((Paragraph)rows[1].Cells[0].Blocks.FirstBlock).FontWeight);
            // 前後の文章・数式（式は画像か式の部品になって Paragraph に入る）
            var paras = v.Body.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(2, paras.Count);
            Assert.Same(paras[0], v.Body.Document.Blocks.FirstBlock);
            Assert.Same(paras[1], v.Body.Document.Blocks.LastBlock);
            Assert.Contains("です", new TextRange(paras[0].ContentStart, paras[0].ContentEnd).Text);
            Assert.True(paras[0].Inlines.OfType<InlineUIContainer>().Any() || AllText(v).Contains("$x^2$"));
            Assert.True(paras[1].Inlines.OfType<InlineUIContainer>().Any() || AllText(v).Contains("$$y$$"));
        });
    }

    [Fact]
    public void 壊れた表_Markdown_タブの字下げ_コードの中は文字のまま()
    {
        OnSta(() =>
        {
            foreach (var s in new[]
            {
                "<table><tr><td>a</td></tr>",
                "<fcel>a<fcel>b<nl><fcel>c<nl>",
                "| a | b |\n|---|---|\n| 1 | 2 |",
                "a\tb\n\tc\td",
                "```\n" + Glm + "\n```",
            })
            {
                var v = NewView();
                v.ShowFinal(s);
                Assert.Empty(Tables(v));
                Assert.Equal(s, v.ModelText);
            }
            Assert.Contains("表:", File.ReadAllText(_paths.AppLogFile));   // 壊れた表は app.log に1行
        });
    }

    [Fact]
    public void 表の後に続きが来たら文字の文書へ戻る()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ShowFinal(Glm);
            Assert.Single(Tables(v));
            v.ApplyBody(Glm + "\n続き");   // 新しい問いの途中など
            Assert.Empty(Tables(v));
            Assert.Contains("続き", AllText(v));
        });
    }

    [Fact]
    public void 欄の幅に収まる_細い窓でもはみ出さず_セルの中は折り返す()
    {
        OnSta(() =>
        {
            foreach (double w in new[] { 420.0, 240.0, 150.0 })
            {
                var v = NewView(w);
                v.ShowFinal("<table><tr><th>見出し</th><th>説明</th></tr>" +
                            "<tr><td>りんご</td><td>とてもとても長い説明の文章がここに入ります。ここは折り返されるはずです。さらにもっともっと長くして、どの幅でも必ず二行以上になるようにしておきます</td></tr>" +
                            "<tr><td>abc</td><td>x</td></tr></table>");
                Settle(v);
                var t = Assert.Single(Tables(v));
                double right = 0;
                foreach (var row in t.RowGroups[0].Rows)
                    foreach (var c in row.Cells)
                    {
                        var rc = c.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                        Assert.False(rc.IsEmpty, $"layout not valid width={w}");
                        right = Math.Max(right, rc.Right);
                    }
                Assert.True(right <= v.Body.ViewportWidth + 1, $"width={w} right={right} viewport={v.Body.ViewportWidth}");
                Assert.True(v.Body.ExtentWidth <= v.Body.ViewportWidth + 1);
                // 折り返した: 長い説明のセルは複数行（高さが1行より大きい）
                var longCell = t.RowGroups[0].Rows[1].Cells[1];
                var r0 = longCell.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var r1 = longCell.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                Assert.True(r1.Top > r0.Top + 2, $"width={w}");
            }
        });
    }

    [Fact]
    public void ログのカード_最初から表で出る_使い回しで前の表は残らない()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView { LogMode = true };
            v.LogText = "答え\n" + Paddle;
            Assert.Single(Tables(v));
            Assert.Contains("Sales", AllText(v));
            v.LogText = "次の答え 表なし";
            Assert.Empty(Tables(v));
            Assert.DoesNotContain("Sales", AllText(v));
            v.LogText = Glm;
            Assert.Single(Tables(v));
            v.LogText = "壊れた <table><tr><td>x</td></tr>";
            Assert.Empty(Tables(v));
        });
    }

    [Fact]
    public void 実物のGLMとPaddleの表はどちらも表になる()
    {
        OnSta(() =>
        {
            foreach (var s in new[]
            {
                TableTextTests.PaddleT1, TableTextTests.PaddleT2, TableTextTests.PaddleT3, TableTextTests.PaddleT4,
                TableTextTests.GlmT1, TableTextTests.GlmT2, TableTextTests.GlmT4,
            })
            {
                var v = NewView();
                v.ShowFinal(s);
                Assert.Single(Tables(v));
            }
            var none = NewView();
            none.ShowFinal(TableTextTests.GlmT3);   // GLM が表にしなかった答え
            Assert.Empty(Tables(none));
        });
    }

    [Fact]
    public void 色や大きさを変えても表は表のまま組み直される()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ShowFinal(Glm);
            v.ApplyStyle(new PanelStyle("#101010", "#eeeeee", 0.7, "", 18, "plain", 0.6, 1.4), 255, 255);
            var t = Assert.Single(Tables(v));
            Assert.Equal(4, t.RowGroups[0].Rows.Count);
            Assert.Equal(18, v.Body.FontSize);
        });
    }

    // ---------------- コピー（I3c）

    [Fact]
    public void 全部コピー_タブ区切りの文字とHTMLの両方_表の外は今まで通り()
    {
        OnSta(() =>
        {
            var v = NewView();
            string full = "前の文 $a$ です\n" + Paddle + "\n後ろ";
            v.ShowFinal(full);
            var (text, html) = Call(v, "FullCopy");
            Assert.Equal("前の文 $a$ です\r\n\t2024\t\t2025\r\n\tQ1\tQ2\tQ1\r\nSales\t10\t20\t30\r\nCost\t5\t6\t7\r\n後ろ", text);
            Assert.NotNull(html);
            Assert.Contains("<p>前の文 $a$ です</p>", html);
            Assert.Contains("<table", html);
            Assert.Contains("colspan=\"2\"", html);
            Assert.Contains("<td>Sales</td>", html);
            Assert.EndsWith("<p>後ろ</p>", html);
            // 表が無い答えは今まで通り文字だけ
            var plain = NewView();
            plain.ShowFinal("ただの文\nです");
            Assert.Null(Call(plain, "FullCopy").Html);
            Assert.Equal("ただの文\nです", Call(plain, "FullCopy").Text);
        });
    }

    [Fact]
    public void LaTeXのコピーの形が表の外の式に効く()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ApplyStyle(new PanelStyle("#101010", "#eeeeee", 0.7, "", 14, "dollar", 0.6, 1.4), 255, 255);
            v.ShowFinal("$$x^2$$\n" + Glm);
            var (text, html) = Call(v, "FullCopy");
            Assert.StartsWith("$$x^2$$", text);
            Assert.NotNull(html);
        });
    }

    [Fact]
    public void 選んだ所のコピー_全部選ぶと表全体_一部ならかかる行と列だけ_1マスなら文字だけ()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ShowFinal(Paddle);   // 4x4
            v.Body.SelectAll();
            var all = Call(v, "SelectedCopy");
            Assert.Equal("\t2024\t\t2025\r\n\tQ1\tQ2\tQ1\r\nSales\t10\t20\t30\r\nCost\t5\t6\t7", all.Text);
            Assert.Contains("<table", all.Html!);

            var t = Tables(v)[0];
            var rows = t.RowGroups[0].Rows;
            // 行2の「Sales」から「20」まで → 行2・列0-2
            v.Body.Selection.Select(rows[2].Cells[0].ContentStart, rows[2].Cells[2].ContentEnd);
            var part = Call(v, "SelectedCopy");
            Assert.Equal("Sales\t10\t20", part.Text);
            Assert.DoesNotContain("2025", part.Html!);
            // 行をまたぐ時は文章の並び順のとおり、間のマスは全部かかる（行2の頭〜行3の「6」なら行2-3の全列）
            v.Body.Selection.Select(rows[2].Cells[0].ContentStart, rows[3].Cells[2].ContentEnd);
            Assert.Equal("Sales\t10\t20\t30\r\nCost\t5\t6\t7", Call(v, "SelectedCopy").Text);

            // 1マスの中の一部だけ
            var c = rows[2].Cells[0];
            v.Body.Selection.Select(c.ContentStart.GetPositionAtOffset(3), c.ContentEnd.GetPositionAtOffset(-3));
            var one = Call(v, "SelectedCopy");
            Assert.Null(one.Html);
            Assert.Equal("ale", one.Text);
        });
    }

    [Fact]
    public void クリップボードへ文字とHTMLの両方が入る_元へ戻す()
    {
        OnSta(() =>
        {
            string? old = null;
            try { old = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }
            try
            {
                var v = NewView();
                v.ShowFinal("<fcel>日本語<fcel>a\"b<nl><fcel>x\ty<fcel>z<nl>");
                typeof(AnswerView).GetMethod("CopyAnswer", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(v, new object[] { Call(v, "FullCopy") });
                Assert.Equal("日本語\t\"a\"\"b\"\r\n\"x\ty\"\tz", RetryGet(() => Clipboard.GetText()));
                string h = RetryGet(() => (string)Clipboard.GetData(DataFormats.Html)!);
                Assert.StartsWith("Version:0.9", h);
                Assert.Contains("<td>日本語</td>", h);
                // CF_HTML のオフセットは UTF-8 のバイト位置
                var bytes = Encoding.UTF8.GetBytes(h);
                int Off(string key)
                {
                    int i = h.IndexOf(key + ":", StringComparison.Ordinal) + key.Length + 1;
                    return int.Parse(h.Substring(i, 10));
                }
                string frag = Encoding.UTF8.GetString(bytes, Off("StartFragment"), Off("EndFragment") - Off("StartFragment"));
                Assert.StartsWith("<table", frag);
                Assert.EndsWith("</table>", frag);
                Assert.Equal(bytes.Length, Off("EndHTML"));
            }
            finally
            {
                try { if (old is not null) Clipboard.SetText(old); else Clipboard.Clear(); } catch { }
            }
        });
    }

    // ---------------- 修正J94: セルの中の数式

    [Fact]
    public void J94_セルの中の脚注の印は上付きで出し_全文コピーも上付き文字とsup()
    {
        OnSta(() =>
        {
            var v = NewView();
            string full = @"<table><tr><th>Sub</th><th>Rev</th></tr><tr><td>Components\(^{{a}}\)</td><td>412.0</td></tr></table>";
            v.ShowFinal(full);
            var t = Assert.Single(Tables(v));
            var para = (Paragraph)t.RowGroups[0].Rows[1].Cells[0].Blocks.FirstBlock;
            var runs = para.Inlines.OfType<Run>().ToList();
            Assert.Equal("Components", runs[0].Text);
            Assert.Equal("a", runs[1].Text);
            Assert.Equal(BaselineAlignment.Superscript, runs[1].BaselineAlignment);
            Assert.True(runs[1].FontSize < v.Body.FontSize);
            Assert.DoesNotContain(@"\(", AllText(v));
            var (text, html) = Call(v, "FullCopy");
            Assert.Contains("Componentsᵃ\t412.0", text);
            Assert.Contains("Components<sup>a</sup>", html);
            Assert.Equal(full, v.ModelText);   // 答えそのものは変えない
        });
    }

    [Fact]
    public void J94_LaTeX数式の変換がOFFならセルの中も書かれた文字のまま()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ApplyStyle(new PanelStyle("#101010", "#eeeeee", 0.7, "", 14, "plain", 0.6, 1.4, ConvertLatexMath: false), 255, 255);
            string full = @"<table><tr><td>Components\(^{a}\)</td><td>1</td></tr></table>";
            v.ShowFinal(full);
            var t = Assert.Single(Tables(v));
            var para = (Paragraph)t.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock;
            Assert.Equal(@"Components\(^{a}\)", Assert.Single(para.Inlines.OfType<Run>()).Text);
            var (text, html) = Call(v, "FullCopy");
            Assert.Contains(@"Components\(^{a}\)", text);
            Assert.DoesNotContain("<sup>", html);
        });
    }

    private static T RetryGet<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch when (i < 10) { Thread.Sleep(50); }
        }
    }
}

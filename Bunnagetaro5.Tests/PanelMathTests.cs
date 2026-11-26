using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using WpfMath.Controls;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 手順25: 解答欄の本文で LaTeX 式を表示する（WpfMath FormulaControl）。
/// 文中式・独立行式・表示できない式は原文字・ストリーミング中は文字で出て
/// 閉じ記号で式に差し替わる・コピー（FullText）は $ 付きの元の文。
/// </summary>
public class PanelMathTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public PanelMathTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_math_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers（BindTests と同じ形） ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(TimeSpan.FromSeconds(60));
        if (err is not null) throw err;
        return result;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static object? CallPriv(object o, string name)
        => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(o, Array.Empty<object>());

    private MainWindow NewPanel(out AnswerPanel panel)
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(w.Dispatcher, 250);
        w.Session.OpenCapture();
        Pump(w.Dispatcher, 250);
        panel = (AnswerPanel)Fld(w.Session, "_panel")!;
        Assert.NotNull(panel);
        return w;
    }

    // ---- 文書の調べ物 ----

    private static RichTextBox Body(AnswerPanel p) => (RichTextBox)Fld(p.View, "_body")!;

    /// <summary>式として載っている FormulaControl を全部集める</summary>
    private static List<FormulaControl> Formulas(RichTextBox body)
    {
        var list = new List<FormulaControl>();
        foreach (Block b in body.Document.Blocks)
            Collect(b, list);
        return list;

        static void Collect(Block b, List<FormulaControl> list)
        {
            if (b is not Paragraph p) return;
            foreach (Inline inl in p.Inlines)
                if (inl is InlineUIContainer ui && ui.Child is FormulaControl fc)
                    list.Add(fc);
        }
    }

    /// <summary>式を除いた本文の文字（段落は \n でつなぐ）。空段落も \n で数える</summary>
    private static string PlainText(RichTextBox body)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (Block b in body.Document.Blocks)
        {
            if (!first) sb.Append('\n');
            first = false;
            if (b is Paragraph p)
                foreach (Inline inl in p.Inlines)
                {
                    if (inl is Run r) sb.Append(r.Text);
                    else if (inl is LineBreak) sb.Append('\n');
                }
        }
        return sb.ToString();
    }

    // ---- tests ----

    [Fact]
    public void 文中式はFormulaControlで載る()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("訳文 before $x^2$ after");
            Pump(w.Dispatcher, 50);
            var body = Body(panel);
            var fs = Formulas(body);
            Assert.Single(fs);
            Assert.Equal("x^2", fs[0].Formula);
            // 解答欄の色・字の大きさに合わせる（指示書25）
            Assert.Equal(Math.Max(1.0, body.FontSize), fs[0].Scale);
            Assert.Same(body.Foreground, fs[0].Foreground);
            // 式のまわりの文字はそのまま残る
            Assert.Equal("訳文 before  after", PlainText(body));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 独立行式は中央寄せの行になる()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("前置き\n$$E=mc^2$$\n後ろ");
            Pump(w.Dispatcher, 50);
            var body = Body(panel);
            var fs = Formulas(body);
            Assert.Single(fs);
            Assert.Equal("E=mc^2", fs[0].Formula);
            bool centered = false;
            foreach (Block b in body.Document.Blocks)
                if (b is Paragraph p && p.TextAlignment == TextAlignment.Center &&
                    p.Inlines.FirstInline is InlineUIContainer)
                    centered = true;
            Assert.True(centered, "独立行の式は中央寄せParagraphになる");
            Assert.Equal("前置き\n\n後ろ", PlainText(body));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 表示できない式は元の文字のまま()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody(@"壊れた $\qqqqq$ 式");
            Pump(w.Dispatcher, 50);
            var body = Body(panel);
            Assert.Empty(Formulas(body));
            Assert.Equal(@"壊れた $\qqqqq$ 式", PlainText(body));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void ストリーミングは文字で出て閉じ記号で式に差し替え()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            // 閉じが来るまでは文字のまま
            panel.ApplyBody("E = $$x^2");
            Pump(w.Dispatcher, 30);
            Assert.Empty(Formulas(Body(panel)));
            Assert.Equal("E = $$x^2", PlainText(Body(panel)));
            // 途中でさらに文字が伸びても文字のまま（fast path）
            panel.ApplyBody("E = $$x^2 + y^2");
            Pump(w.Dispatcher, 30);
            Assert.Empty(Formulas(Body(panel)));
            Assert.Equal("E = $$x^2 + y^2", PlainText(Body(panel)));
            // 閉じが来た段で式に差し替え
            panel.ApplyBody("E = $$x^2 + y^2$$");
            Pump(w.Dispatcher, 50);
            var fs = Formulas(Body(panel));
            Assert.Single(fs);
            Assert.Equal("x^2 + y^2", fs[0].Formula);
            // 文中式も同じ（$a → $a+b$）
            panel.ResetRun();
            panel.ApplyBody("値は $a");
            Assert.Equal("値は $a", PlainText(Body(panel)));
            panel.ApplyBody("値は $a+b$ まで");
            Pump(w.Dispatcher, 50);
            fs = Formulas(Body(panel));
            Assert.Single(fs);
            Assert.Equal("a+b", fs[0].Formula);
            Assert.Equal("値は  まで", PlainText(Body(panel)));
            w.Close();
            return true;
        });
    }

    /// <summary>文書の形（段落の種類・寄せ・余白と、中の文字・改行・式）を1つの文字列にする</summary>
    private static string DocShape(RichTextBox body)
    {
        var sb = new StringBuilder();
        foreach (Block b in body.Document.Blocks)
        {
            sb.Append('[').Append(b.GetType().Name).Append('|').Append(b.Tag).Append('|')
              .Append(b.TextAlignment).Append('|').Append(b.Margin).Append(']');
            if (b is Paragraph p)
                foreach (Inline inl in p.Inlines)
                    sb.Append(inl switch
                    {
                        // 書き足しは1回ごとに Run が分かれる（前からの作り。見た目は同じ）ので、続いた Run はまとめて比べる
                        Run r => (inl.PreviousInline is Run ? "" : "R(") + r.Text + (inl.NextInline is Run ? "" : ")"),
                        LineBreak => "LB",
                        InlineUIContainer { Child: FormulaControl fc } => "F(" + fc.Formula + ")",
                        InlineUIContainer { Child: FrameworkElement fe } => "U(" + fe.Tag + ")",
                        _ => inl.GetType().Name,
                    });
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void J39_閉じ記号で末尾だけ組み直しても_全部組み直した文書と同じ形()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            var replace = typeof(Bunnagetaro5.Views.AnswerView).GetMethod("ReplaceDoc", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string[] answers =
            {
                "前置き\n$$x^2$$\n後ろ $a$ と $b$ の文\n\n次の段落 $$y$$ 続き",
                "一行目\n$$E=mc^2$$\n\n$$F=ma$$\n二行目\n三行目 $c$\n\n\n$$z$$",
                "$x$ 先頭が式\n\n$$a+b$$$$c+d$$ くっついた式\n終わり",
                "文中 $p$\n$$q$$",
                "改行の前で閉じる $r$\n\n段落 $s$\n",
            };
            foreach (var ans in answers)
            {
                panel.ResetRun();
                for (int n = 1; n <= ans.Length; n++)
                    panel.ApplyBody(ans[..n]);   // ストリーミング（1字ずつ）
                Pump(w.Dispatcher, 30);
                var body = Body(panel);
                string streamed = DocShape(body);
                replace.Invoke(panel.View, new object[] { ans });   // 全部組み直す
                Pump(w.Dispatcher, 30);
                Assert.Equal(DocShape(body), streamed);
            }
            // 閉じ記号が来ても、前の段落と式は作り直さない（末尾だけ組み直す）
            panel.ResetRun();
            panel.ApplyBody("前置き $a$ の文\n\n$$b");
            Pump(w.Dispatcher, 30);
            var b0 = Body(panel);
            var first = b0.Document.Blocks.FirstBlock;
            var f0 = Formulas(b0)[0];
            panel.ApplyBody("前置き $a$ の文\n\n$$b$$");
            Pump(w.Dispatcher, 30);
            Assert.Same(first, b0.Document.Blocks.FirstBlock);
            Assert.Same(f0, Formulas(b0)[0]);
            Assert.Equal(new[] { "a", "b" }, Formulas(b0).Select(f => f.Formula).ToArray());
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 全文コピーはドル付きの元の文()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("E = $$x^2 + y^2$$");
            Pump(w.Dispatcher, 50);
            Assert.Single(Formulas(Body(panel)));
            // 文書のTextには式が消えるので、コピーは $ 付きの原文（手順26の約束）
            Assert.Equal("E = $$x^2 + y^2$$", (string)CallPriv(panel.View, "FullText")!);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 式なし本文は今まで通り()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("一行目\n二行目");
            Pump(w.Dispatcher, 30);
            Assert.Empty(Formulas(Body(panel)));
            Assert.Equal("一行目\n二行目", PlainText(Body(panel)));
            Assert.Equal("一行目\n二行目", (string)CallPriv(panel.View, "FullText")!);
            w.Close();
            return true;
        });
    }

    // ========= 手順26: コピーは $ 付き LaTeX・式は右クリックで2種のコピー =========

    private static string Sel(AnswerPanel p) => (string)CallPriv(p.View, "SelectedText")!;

    [Fact]
    public void 式のある文書だけ埋め込み式への入力を生やす()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            var body = Body(panel);
            panel.ApplyBody("文字だけ");
            Pump(w.Dispatcher, 50);
            Assert.False(body.IsDocumentEnabled);
            panel.ApplyBody("E = $x^2$");
            Pump(w.Dispatcher, 50);
            Assert.True(body.IsDocumentEnabled);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 全選択のコピーは段落を改行でつなぎ式はdollar付き文字で出る()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            string model = "前置き\n$$\\frac{d}{dx} x^2 = 2x$$\n文中 $a^2+b^2=c^2$";
            panel.ApplyBody(model);
            Pump(w.Dispatcher, 150);
            var body = Body(panel);
            body.SelectAll();
            Assert.Equal(model.Replace("\n", "\r\n"), Sel(panel));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 選択コピーは式を一つのかたまりとして扱う()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("前置き\n$$x^2$$\n文中 $a^2+b^2=c^2$");
            Pump(w.Dispatcher, 150);
            var body = Body(panel);
            var last = (Paragraph)body.Document.Blocks.LastBlock;
            // 最後の段落だけを選ぶと、式も $ 付きの文字で混ざって出る
            body.Selection.Select(last.ContentStart, last.ContentEnd);
            Assert.Equal("文中 $a^2+b^2=c^2$", Sel(panel));
            // 式のかたまりだけを選ぶと $ 付きのそれだけ
            var iuc = last.Inlines.OfType<InlineUIContainer>().First();
            body.Selection.Select(iuc.ContentStart, iuc.ContentEnd);
            Assert.Equal("a^2+b^2=c^2", Sel(panel));   // F13: 式だけの選択は既定の「中身だけ」
            // 文字の途中だけなら今まで通り文字だけ（式のかたまりは混ざらない）
            body.Selection.Select(last.ContentStart, last.ContentStart.GetPositionAtOffset(3));
            Assert.Equal("文中", Sel(panel));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 式なしの選択コピーは今まで通りの文字()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("一行目\n二行目");
            Pump(w.Dispatcher, 50);
            var body = Body(panel);
            body.SelectAll();
            // 式なしは従来通り TextRange そのまま（末尾の段落区切りもそのまま付く）
            Assert.Equal("一行目\r\n二行目\r\n", Sel(panel));
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 式の右クリックメニューは2つでLaTeXコピーはdollar文字列()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("前置き\n$$x^2 + y^2$$");
            Pump(w.Dispatcher, 150);
            var fc = Formulas(Body(panel))[0];
            Assert.Equal("$$x^2 + y^2$$", fc.Tag as string);
            var cm = fc.ContextMenu;
            Assert.NotNull(cm);
            Assert.Equal(2, cm!.Items.Count);
            Assert.Equal("LaTeXでコピー", ((MenuItem)cm.Items[0]).Header);
            Assert.Equal("画像でコピー", ((MenuItem)cm.Items[1]).Header);
            try { Clipboard.Clear(); } catch { }
            ((MenuItem)cm.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Clipboard.ContainsText());
            Assert.Equal("x^2 + y^2", Clipboard.GetText());   // F13: 既定は中身だけ
            // 「$ で囲む」「\( \) で囲む」にすると、その形で出る
            var view = (Bunnagetaro5.Views.AnswerView)panel.View;
            var fi = typeof(Bunnagetaro5.Views.AnswerView).GetField("_copyForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            fi.SetValue(view, Bunnagetaro5.Services.LatexCopyForm.Dollar);
            ((MenuItem)cm.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("$$x^2 + y^2$$", Clipboard.GetText());
            fi.SetValue(view, Bunnagetaro5.Services.LatexCopyForm.Paren);
            ((MenuItem)cm.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(@"\[x^2 + y^2\]", Clipboard.GetText());
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 画像でコピーは拡大した式のビットマップをクリップボードへ()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ApplyBody("$$\\frac{d}{dx} x^2 = 2x$$");
            Pump(w.Dispatcher, 250);
            var fc = Formulas(Body(panel))[0];
            Assert.True(fc.ActualWidth > 5);
            var cm = fc.ContextMenu!;
            try { Clipboard.Clear(); } catch { }
            ((MenuItem)cm.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Clipboard.ContainsImage());
            BitmapSource img = Clipboard.GetImage();
            // 3倍狙い（下地パッド分だけ少し大きい）: 2倍より大きく4倍未満
            Assert.InRange(img.PixelWidth, (int)(fc.ActualWidth * 2), (int)(fc.ActualWidth * 4 + 40));
            Assert.True(img.PixelHeight > fc.ActualHeight * 2);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 余白の左押下はOSの移動ループWM_NCLBUTTONDOWNへ渡しキャプチャ窓とバーと解答欄が一緒に動く()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            panel.ShowFinal("短い1行");
            Pump(w.Dispatcher, 150);

            var cap = (CaptureWindow)Fld(w.Session, "_capture")!;
            var bar = (ChromeBar)Fld(w.Session, "_bar")!;
            var capH = new System.Windows.Interop.WindowInteropHelper(cap).Handle;
            var barH = new System.Windows.Interop.WindowInteropHelper(bar).Handle;
            var panelH = new System.Windows.Interop.WindowInteropHelper(panel).Handle;

            Native.GetWindowRect(capH, out var c0);
            Native.GetWindowRect(barH, out var b0);
            Native.GetWindowRect(panelH, out var p0);

            int nclCount = 0;
            System.Windows.Interop.HwndSource.FromHwnd(capH)!.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == Native.WM_NCLBUTTONDOWN && wParam == (IntPtr)Native.HTCAPTION)
                {
                    nclCount++;
                    Native.SetWindowPos(capH, IntPtr.Zero, c0.Left + 60, c0.Top + 60, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    handled = true;
                }
                return IntPtr.Zero;
            });

            var body = Body(panel);
            var tBefore = panel.LastInteractUtc;
            var downMethod = typeof(AnswerPanel).GetMethod("Body_Down", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ev = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                Source = body
            };
            downMethod.Invoke(panel, new object[] { body, ev });
            Assert.True(ev.Handled);
            Assert.True(panel.LastInteractUtc >= tBefore);
            Assert.Equal(1, nclCount);

            Native.GetWindowRect(capH, out var c1);
            Native.GetWindowRect(barH, out var b1);
            Native.GetWindowRect(panelH, out var p1);
            Assert.Equal(60, c1.Left - c0.Left);
            Assert.Equal(60, c1.Top - c0.Top);
            Assert.Equal(60, b1.Left - b0.Left);
            Assert.Equal(60, b1.Top - b0.Top);
            Assert.Equal(60, p1.Left - p0.Left);
            Assert.Equal(60, p1.Top - p0.Top);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void 置き場所を切り替えても出ていた答えを新しい側へ移す()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            var cfg = (ConfigStore)Fld(w, "_cfg")!;
            panel.ApplyThink("考え中のメモ");
            panel.ApplyBody("答えの本文");
            Pump(w.Dispatcher, 300);
            Assert.True(panel.IsVisible);
            var fe = (TextBox)Fld(panel.View, "_followEnt")!;
            fe.Text = "書きかけの問い";

            CfgTestUtil.SetPlace(cfg, "outside");
            w.Session.SyncBarFromCfg();
            Pump(w.Dispatcher, 600);
            var strip = (AnswerStrip)Fld(w.Session, "_strip")!;
            Assert.Equal("答えの本文", strip.View.ModelText);
            Assert.Equal("考え中のメモ", strip.View.ThinkText);
            Assert.Equal("書きかけの問い", ((TextBox)Fld(strip.View, "_followEnt")!).Text);
            Assert.False(panel.IsVisible);

            // 外の解答欄で問いを書き直し、窓に重ねる側へ戻す
            ((TextBox)Fld(strip.View, "_followEnt")!).Text = "別の問い";
            if (!strip.IsVisible) { w.Close(); return true; }   // 収まる場所が無い環境では出ない。移しの確認はここまで
            CfgTestUtil.SetPlace(cfg, "inside");
            w.Session.SyncBarFromCfg();
            Pump(w.Dispatcher, 600);
            Assert.True(panel.IsVisible);
            Assert.Equal("答えの本文", panel.View.ModelText);
            Assert.Equal("別の問い", fe.Text);
            w.Close();
            return true;
        });
    }
}

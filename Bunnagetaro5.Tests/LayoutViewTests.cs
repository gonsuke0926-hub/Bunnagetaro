using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>内蔵 OCR の位置の反映（答えの n 行目を n 番目の段落の位置へ並べる）</summary>
public sealed class LayoutViewTests : IDisposable
{
    private readonly string _temp;

    public LayoutViewTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_lay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        AppLog.Init(new Paths(Path.Combine(_temp, "Bunnagetaro5")));
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 90);

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

    private static AnswerView NewView(double width = 400, double height = 300, bool log = false)
    {
        TestUtil.EnsureAppResources();
        var v = new AnswerView(new Host(), 6, 90);
        if (log) v.LogMode = true;
        Layout(v, width, height);
        return v;
    }

    private static void Layout(AnswerView v, double width, double height)
    {
        v.Width = width;
        v.Height = height;
        v.Measure(new Size(width, height));
        v.Arrange(new Rect(0, 0, width, height));
        v.UpdateLayout();
        v.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        v.UpdateLayout();
    }

    private static readonly OcrBlock[] Two =
    {
        new("STAFF ONLY", 0.10, 0.10, 0.30, 0.10),
        new("Open till dawn", 0.50, 0.60, 0.40, 0.12),
    };

    [Fact]
    public void J97_n行目をn番目の位置に置き_本文は見えなくする()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("関係者以外立入禁止\n夜明けまで営業");
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            Assert.Equal(0, v.Body.Opacity);
            var c = v.LayoutCanvas;
            var tbs = v.LayoutBlocks;
            Assert.Equal("関係者以外立入禁止", v.LayoutTexts[0]);
            Assert.Equal("夜明けまで営業", v.LayoutTexts[1]);
            Assert.Equal(0.10 * c.ActualWidth, Canvas.GetLeft(tbs[0]), 3);
            Assert.Equal(0.10 * c.ActualHeight, Canvas.GetTop(tbs[0]), 3);
            Assert.Equal(0.50 * c.ActualWidth, Canvas.GetLeft(tbs[1]), 3);
            Assert.Equal(0.60 * c.ActualHeight, Canvas.GetTop(tbs[1]), 3);
            Assert.All(tbs, t => Assert.InRange(t.FontSize, AnswerView.LayoutMinFont, AnswerView.LayoutMaxFont));
            Assert.Equal("関係者以外立入禁止\n夜明けまで営業", v.ModelText);   // 答えの文は1行＝1段落のまま
        });
    }

    [Fact]
    public void J142_欄の大きさを変えても区画は撮った時の位置と大きさのまま()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("a\nb");
            Layout(v, 400, 300);
            double cw = v.LayoutCanvas.ActualWidth, ch = v.LayoutCanvas.ActualHeight;
            double x1 = Canvas.GetLeft(v.LayoutBlocks[1]), y1 = Canvas.GetTop(v.LayoutBlocks[1]);
            double w1 = v.LayoutBlocks[1].Width;
            Assert.Equal(0.50 * cw, x1, 3);

            Layout(v, 800, 600);   // 広げても枠だけ広がる
            Assert.Equal(x1, Canvas.GetLeft(v.LayoutBlocks[1]), 3);
            Assert.Equal(y1, Canvas.GetTop(v.LayoutBlocks[1]), 3);
            Assert.Equal(w1, v.LayoutBlocks[1].Width, 3);

            Layout(v, 250, 200);   // 縮めても動かさない（外は描き板で切れる）
            Assert.Equal(x1, Canvas.GetLeft(v.LayoutBlocks[1]), 3);
            Assert.True(v.LayoutCanvas.ClipToBounds);

            v.SetLayout(Two);      // 新しい区画は、その時の広さで決め直す
            v.ApplyBody("a\nb");
            Layout(v, 250, 201);
            Assert.Equal(0.50 * v.LayoutCanvas.ActualWidth, Canvas.GetLeft(v.LayoutBlocks[1]), 3);
        });
    }

    [Fact]
    public void J142_左上の縁で大きさを変えた分だけ区画を逆向きにずらして画面の同じ場所に残す()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("a\nb");
            Layout(v, 400, 300);
            double x0 = Canvas.GetLeft(v.LayoutBlocks[0]), y0 = Canvas.GetTop(v.LayoutBlocks[0]);
            var dpi = VisualTreeHelper.GetDpi(v);

            // 左の縁を 40px・上の縁を 20px 外へ広げた（窓の左上が左上へ動いた）
            v.ShiftLayoutOrigin(-40, -20);
            Layout(v, 400 + 40 / dpi.DpiScaleX, 300 + 20 / dpi.DpiScaleY);
            Assert.Equal(x0 + 40 / dpi.DpiScaleX, Canvas.GetLeft(v.LayoutBlocks[0]), 3);
            Assert.Equal(y0 + 20 / dpi.DpiScaleY, Canvas.GetTop(v.LayoutBlocks[0]), 3);

            // 左の縁を内へ 60px 縮めた
            v.ShiftLayoutOrigin(60, 0);
            Assert.Equal(x0 - 20 / dpi.DpiScaleX, Canvas.GetLeft(v.LayoutBlocks[0]), 3);
        });
    }
    /// <summary>上下に近く並んだ2つの段落（上の段落の下の空きは、下の段落の上端まで）</summary>
    private static readonly OcrBlock[] Stacked =
    {
        new("Line one", 0.05, 0.10, 0.40, 0.06),
        new("Line two", 0.05, 0.18, 0.40, 0.06),
    };

    private const string Long = "とても長い訳の文がここに入ります。とても長い訳の文がここに入ります。とても長い訳の文がここに入ります。";

    [Fact]
    public void J98_離れた段落は解答欄の文字サイズのまま()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("短い\nもう少しだけ長い訳の文");
            Layout(v, 400, 300);
            Assert.All(v.LayoutBlocks, t => Assert.Equal(v.Body.FontSize, t.FontSize));
            Layout(v, 600, 450);   // 欄の大きさを変えても字の大きさは変わらない
            Assert.All(v.LayoutBlocks, t => Assert.Equal(v.Body.FontSize, t.FontSize));
        });
    }

    [Fact]
    public void J98_下の段落にぶつかる時だけ小さくする()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Stacked);
            v.ApplyBody(Long + "\n短い");
            Layout(v, 400, 300);
            var tbs = v.LayoutBlocks;
            Assert.True(tbs[0].FontSize < v.Body.FontSize);
            Assert.Equal(v.Body.FontSize, tbs[1].FontSize);
            tbs[0].Measure(new Size(tbs[0].Width, double.PositiveInfinity));
            if (tbs[0].FontSize > AnswerView.LayoutMinFont)
                Assert.True(Canvas.GetTop(tbs[0]) + tbs[0].DesiredSize.Height <= Canvas.GetTop(tbs[1]) + 0.5);
        });
    }

    [Fact]
    public void J98_訳の途中は大きく戻さず_欄の大きさを変えたら測り直す()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Stacked);
            v.ApplyBody(Long + "\nb");
            Layout(v, 400, 300);
            double small = v.LayoutBlocks[0].FontSize;
            Assert.True(small < v.Body.FontSize);
            v.ApplyBody("短\nb");
            Assert.Equal(small, v.LayoutBlocks[0].FontSize);
            Layout(v, 420, 310);
            Assert.Equal(v.Body.FontSize, v.LayoutBlocks[0].FontSize);
        });
    }

    [Fact]
    public void J97_行の数が合わない時と新しいぶん投げでは本文をそのまま見せる()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("1行だけ");
            Layout(v, 400, 300);
            Assert.False(v.LayoutShown);
            Assert.Equal(1, v.Body.Opacity);
            v.ApplyBody("1行だけ\n2行目");
            Assert.True(v.LayoutShown);
            v.ResetRun();
            Assert.False(v.LayoutShown);
            v.ApplyBody("x\ny");
            Assert.False(v.LayoutShown);   // 位置は外れたまま
        });
    }

    [Fact]
    public void J97_ログ画面では位置の反映をしない()
    {
        OnSta(() =>
        {
            var v = NewView(log: true);
            v.SetLayout(Two);
            v.ApplyBody("a\nb");
            Assert.False(v.LayoutShown);
        });
    }

    [Fact]
    public void J97_全文コピーはただの文章()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ShowFinal("関係者以外立入禁止\n夜明けまで営業");
            var (text, html) = ((string, string?))typeof(AnswerView)
                .GetMethod("FullCopy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(v, null)!;
            Assert.Equal("関係者以外立入禁止\n夜明けまで営業", text.Replace("\r\n", "\n"));
            Assert.Null(html);
        });
    }

    private static System.Windows.Media.Brush RootBg(AnswerView v) =>
        ((Border)typeof(AnswerView).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(v)!).Background;


    private static byte AlphaOf(System.Windows.Media.Brush? b) => b is System.Windows.Media.SolidColorBrush s ? s.Color.A : (byte)0;

    [Fact]
    public void J103_文字の所だけ地を敷き_全体の地は透明()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ApplyStyle(new PanelStyle("#ffffff", "#202020", 0.65, "", 12), 166, 83);
            Assert.Equal(166, AlphaOf(RootBg(v)));
            v.SetLayout(Two);
            v.ApplyBody("関係者以外立入禁止\n夜明けまで営業");
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            Assert.Equal(1, AlphaOf(RootBg(v)));   // 見えないがクリックは受ける（アルファ 0 だと後ろの窓へ抜ける）
            Assert.All(v.LayoutMasks, m => Assert.Equal(166, AlphaOf(m.Background)));   // 背景は区画のマスク
            // 地は文字の幅だけ（矩形の幅いっぱいにしない）
            Assert.All(v.LayoutBlocks, t => Assert.Null(t.Background));   // 四角い地は付けず、行ごとに文字の後ろだけ（Run の地）
            // 位置の反映をやめたら全体の地に戻る
            v.ResetRun();
            Assert.Equal(166, AlphaOf(RootBg(v)));
        });
    }

    [Fact]
    public void J103_切なら今まで通り全体に地()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ApplyStyle(new PanelStyle("#ffffff", "#202020", 0.65, "", 12, LayoutTextBg: false), 166, 83);
            v.SetLayout(Two);
            v.ApplyBody("a\nb");
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            Assert.Equal(166, AlphaOf(RootBg(v)));
            Assert.All(v.LayoutMasks, m => Assert.Null(m.Background));
            // 入に切り替えるとその場で文字の所だけに
            v.ApplyStyle(new PanelStyle("#ffffff", "#202020", 0.65, "", 12), 166, 83);
            Assert.Equal(1, AlphaOf(RootBg(v)));
            Assert.All(v.LayoutMasks, m => Assert.Equal(166, AlphaOf(m.Background)));   // 背景は区画のマスク
        });
    }

    [Fact]
    public void J107_位置が分かった時点で区画のマスクを出し_文はその上に出る()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.ApplyStyle(new PanelStyle("#ffffff", "#202020", 0.65, "", 12), 166, 83);
            v.SetLayout(Two);   // 文はまだ無い
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            Assert.Equal(2, v.LayoutMasks.Count);
            var c = v.LayoutCanvas;
            var m0 = v.LayoutMasks[0];
            Assert.Equal(0.10 * c.ActualWidth, Canvas.GetLeft(m0), 3);
            Assert.Equal(0.10 * c.ActualHeight, Canvas.GetTop(m0), 3);
            Assert.Equal(0.30 * c.ActualWidth, m0.Width, 3);
            Assert.Equal(0.10 * c.ActualHeight, m0.Height, 3);
            Assert.Equal(166, AlphaOf(m0.Background));
            Assert.All(v.LayoutTexts, t => Assert.Equal("", t));
            // 文が来たらマスクの上に出る（マスクは文より奥＝描き板の前の方）
            v.ApplyBody("関係者以外立入禁止\n夜明けまで営業");
            Layout(v, 400, 300);
            Assert.Equal("関係者以外立入禁止", v.LayoutTexts[0]);
            Assert.True(c.Children.IndexOf(v.LayoutMasks[0]) < c.Children.IndexOf(v.LayoutBlocks[0]));
            // 長い文が区画からはみ出したら、マスクも文の所まで広がる
            v.ApplyBody("関係者以外立入禁止の札がここに長々と書かれていて区画には入りきりません\n夜明けまで営業");
            Layout(v, 400, 300);
            var tb = v.LayoutBlocks[0];
            Assert.True(v.LayoutMasks[0].Width >= tb.ActualWidth - 0.5 || v.LayoutMasks[0].Height >= tb.ActualHeight - 0.5);
        });
    }

    [Fact]
    public void J113_位置の反映中は見えない本文を組み直さず_外したら本文が同期される()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ApplyBody("いち\nに");
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            // 位置の反映中は見えない _body の組み直しを省いている
            string bodyBefore = new System.Windows.Documents.TextRange(v.Body.Document.ContentStart, v.Body.Document.ContentEnd).Text.Trim();
            Assert.Equal("", bodyBefore);
            // 位置の反映を外すと _body が同期される
            v.SetLayout(null);
            string bodyAfter = new System.Windows.Documents.TextRange(v.Body.Document.ContentStart, v.Body.Document.ContentEnd).Text.Trim();
            Assert.Contains("いち", bodyAfter);
            Assert.Contains("に", bodyAfter);
        });
    }

    [Fact]
    public void J115_位置の反映中でも数式と表が変換され_文字選択できるRichTextBoxになる()
    {
        OnSta(() =>
        {
            var v = NewView();
            v.SetLayout(Two);
            v.ShowFinal("式は $x^2 + 1$ です\n<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>");
            Layout(v, 500, 400);
            Assert.True(v.LayoutShown);
            var b0 = v.LayoutBlocks[0];
            var b1 = v.LayoutBlocks[1];
            Assert.False(b0.Body.IsReadOnly == false);   // 読み取り専用の RichTextBox
            // 1つ目の区画に数式（InlineUIContainer）が入っている
            bool hasMath = b0.Body.Document.Blocks
                .OfType<System.Windows.Documents.Paragraph>()
                .SelectMany(p => p.Inlines)
                .OfType<System.Windows.Documents.InlineUIContainer>()
                .Any();
            Assert.True(hasMath);
            // 2つ目の区画が WPF の Table に組み替わっている
            Assert.Contains(b1.Body.Document.Blocks, blk => blk is System.Windows.Documents.Table);
        });
    }

    [Fact]
    public void J115_位置の反映中は裏の本文にヒットせず_透けた余白は文字外で区画の文字上だけ文字扱い()
    {
        OnSta(() =>
        {
            var v = NewView();
            // 左上 (0,0) は空け、下の方に2つ置く（裏の _body なら左上に文字がある場所）
            var lower = new OcrBlock[]
            {
                new("one", 0.10, 0.50, 0.40, 0.12),
                new("two", 0.10, 0.75, 0.40, 0.12),
            };
            v.SetLayout(lower);
            v.ApplyBody("見えている文字\n下の文字");
            Layout(v, 400, 300);
            Assert.True(v.LayoutShown);
            Assert.False(v.Body.IsHitTestVisible);
            // 左上の透けた余白は文字上ではない（窓を掴める）
            Assert.False(v.IsOverLayoutTextAt(new Point(10, 10)));
            // 区画の文字の上は文字上（選択コピーできる）
            var b0 = v.LayoutBlocks[0];
            double x = Canvas.GetLeft(b0) + 8;
            double y = Canvas.GetTop(b0) + 8;
            Assert.True(v.IsOverLayoutTextAt(new Point(x, y)));
        });
    }
}

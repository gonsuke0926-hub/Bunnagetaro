using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>WebView2 ランタイムが無い環境では飛ばす印</summary>
public sealed class WebView2FactAttribute : FactAttribute
{
    public WebView2FactAttribute()
    {
        if (!MathRenderer.RuntimeAvailable()) Skip = "WebView2 ランタイムが無い";
    }
}

/// <summary>整理8.6: KaTeX での数式描画（WebView2 が要るものは WebView2Fact）</summary>
public class KatexTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _p;

    public KatexTests()
    {
        // 固定の場所（消さない）: WebView2 のブラウザプロセスが Dispose 後もしばらく生きるため、
        // 一時フォルダを消すと Edge の「データ ディレクトリを作成できませんでした」ダイアログが人の画面に出る
        _temp = Path.Combine(Path.GetTempPath(), "b5_katex_shared");
        Directory.CreateDirectory(_temp);
        _p = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_p);
    }

    public void Dispose() { }

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    /// <summary>STA スレッドでディスパッチャを回しながら非同期本体を待つ
    /// （WebView2 は「イベントループが回っている中」でないと立ち上げられない）</summary>
    private static void RunSta(Func<Task> body, int timeoutSec = 120)
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            try
            {
                EnsureApp();
                var d = Dispatcher.CurrentDispatcher;
                var t = d.InvokeAsync(body).Task.Unwrap();
                var frame = new DispatcherFrame();
                _ = t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSec) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
                if (!t.IsCompleted) throw new TimeoutException("katex test timeout");
                t.GetAwaiter().GetResult();
            }
            catch (Exception e) { err = e; }
            finally
            {
                // 見えない窓と WebView2 は、作ったこのスレッドが生きている間に片付ける。
                // 次のテスト（別のスレッド）からでは Dispose が効かず、ブラウザが残る（2026-11-16）
                try { MathRenderer.ResetForTests(); } catch { }
            }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (err is not null) throw err;
    }

    private static readonly Color Fg = Color.FromRgb(0xf5, 0xf5, 0xf5);

    /// <summary>実ログ由来の式と、WpfMath で落ちた命令の例（2026-11-12 の実測で通ったものから）</summary>
    private static readonly string[] Formulas =
    {
        @"\int_{a}^{b} \left( u \frac{dv}{dx} + v \frac{du}{dx} \right) dx = vu \Big|_{a}^{b}",
        @"\int_{0}^{\frac{\pi}{2}} \frac{\sin x}{\sqrt{1 - k^{2} \sin^{2} x}} \, dx = \frac{1}{2k} \log \frac{1 + k}{1 - k},",
        @"\frac{\partial A}{\partial t} = d_{A} \Delta A - K_{a} A + \sum_{i=1}^{N} I_{A}(c_{i}, x_{i}, r_{i})",
        @"\nabla \cdot \mathbf{B}(t, \mathbf{x}) = 0",
        @"\nabla \times \mathbf{E}(t, \mathbf{x}) + \frac{\partial \mathbf{B}(t, \mathbf{x})}{\partial t} = 0",
        @"\nabla \cdot \mathbf{D}(t, \mathbf{x}) = \rho(t, \mathbf{x})",
        @"\nabla \times \mathbf{H}(t, \mathbf{x}) - \frac{\partial \mathbf{D}(t, \mathbf{x})}{\partial t} = \mathbf{j}(t, \mathbf{x})",
        @"\lim_{n\to\infty}\left(1+\frac{1}{n}\right)^{n}=e",
        @"\frac{3}{10} + \frac{2}{5} = \frac{7}{10}",
        @"\nabla = \left( \frac{\partial}{\partial x}, \frac{\partial}{\partial y}, \frac{\partial}{\partial z} \right)",
        @"= ( a ^ { 8 } + a ^ { 6 } b ^ { 2 } + a ^ { 4 } b ^ { 4 } + a ^ { 2 } b ^ { 6 } + b ^ { 8 } ) + ( a ^ { 5 } + b ^ { 5 } )",
        @"= \left\{ ( a ^ { 4 } + a ^ { 2 } b ^ { 2 } + b ^ { 4 } ) ^ { 2 } - ( a ^ { 3 } b + a b ^ { 3 } ) ^ { 2 } \right\} + ( a ^ { 5 } + b ^ { 5 } )",
        @"a^8 + a^5 + a^6 b^2 + a^4 b^4 + a^2 b^6 + b^5 + b^8",
        @"\text{合計} \quad 727",
        @"\text{税率 8\%対象}",
        @"\begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}",
        @"\begin{bmatrix} 1 & 2 \\ 3 & 4 \end{bmatrix}",
        @"\begin{aligned} x &= 1 \\ y &= \text{日本語} \end{aligned}",
        @"f(x)=\begin{cases} x & x>0 \\ -x & x \le 0 \end{cases}",
        @"\mathbb{R} \mathbf{u} \boldsymbol{\alpha} \operatorname{sgn}(x) \mathcal{R}",
        @"\overset{a}{=} \underbrace{x+y}_{n} \xrightarrow{f} \dfrac{a}{b} \tag{1}",
        @"\Bigl( x \Bigr) \big( y \big) \iint \oint \nabla",
        @"\ce{H2O + CO2 -> H2CO3}",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);

    private static byte[] Alpha(System.Windows.Media.Imaging.BitmapSource s, out int w, out int h)
    {
        var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        w = c.PixelWidth; h = c.PixelHeight;
        var px = new byte[w * h * 4];
        c.CopyPixels(px, w * 4, 0);
        var a = new byte[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = px[i * 4 + 3];
        return a;
    }

    /// <summary>文字（alpha&gt;128）の画素の外枠を、画像の幅・高さに対する割合で</summary>
    private static (double L, double T, double R, double B) Ink(System.Windows.Media.Imaging.BitmapSource s)
    {
        var a = Alpha(s, out int w, out int h);
        int l = w, t = h, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (a[y * w + x] > 128) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
        return (Math.Round(l / (double)w, 3), Math.Round(t / (double)h, 3), Math.Round((r + 1) / (double)w, 3), Math.Round((b + 1) / (double)h, 3));
    }

    /// <summary>重なる範囲の alpha の差の平均（0〜255）</summary>
    private static double AlphaDiff(System.Windows.Media.Imaging.BitmapSource s1, System.Windows.Media.Imaging.BitmapSource s2)
    {
        var a = Alpha(s1, out int w1, out int h1);
        var b = Alpha(s2, out int w2, out int h2);
        int w = Math.Min(w1, w2), h = Math.Min(h1, h2);
        double sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                sum += Math.Abs(a[y * w1 + x] - b[y * w2 + x]);
        return sum / Math.Max(1, w * h);
    }

    /// <summary>修正H2(続き): 起動中に画面の倍率を変えた直後は、見えない窓の WPF の倍率と WebView2 の倍率がずれる
    /// （ノート実測: WPF 1.5・WebView2 2.5 で、式が大きく描かれて右が切れた／逆だと小さく左上に寄った）。
    /// WebView2 の ZoomFactor で devicePixelRatio だけをずらしても、式の大きさ（DIP）と画素数は変わらない</summary>
    [WebView2Fact]
    public void WebView2の倍率がWPFとずれても式の大きさと画素は同じ()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var wv = (Microsoft.Web.WebView2.Wpf.WebView2)typeof(MathRenderer).GetField("_wv", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            try
            {
                const string tex = @"\int_{0}^{\frac{\pi}{2}} \frac{\sin x}{\sqrt{1 - k^{2} \sin^{2} x}} \, dx = \frac{1}{2k} \log \frac{1 + k}{1 - k}";
                var a = await MathRenderer.RenderAsync(tex, true, Fg, null, 14, 2.0);
                Assert.NotNull(a);
                foreach (var zoom in new[] { 1.6, 0.6 })
                {
                    ((System.Collections.IDictionary)typeof(MathRenderer).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Clear();
                    wv.ZoomFactor = zoom;   // 拡大（WebView2 が WPF より大きい倍率）・縮小（小さい倍率）
                    await Task.Delay(300);
                    var b = await MathRenderer.RenderAsync(tex, true, Fg, null, 14, 2.0);
                    Assert.NotNull(b);
                    Assert.InRange(b!.W / a!.W, 0.97, 1.03);   // 大きさは同じ
                    Assert.InRange(b.H / a.H, 0.97, 1.03);
                    Assert.InRange(b.Bmp.PixelWidth / (b.W * 2.0), 0.97, 1.03);   // 1 DIP あたり 2 画素のまま
                    Assert.True(Math.Abs(b.Base - a.Base) < 1.0, $"zoom {zoom}: base {b.Base:0.###} vs {a.Base:0.###}");
                    // 中身も同じ（大きく描かれて右が切れる・小さく描かれて左上に寄る、が無い）: 文字の画素の外枠と、画素の違いの平均
                    var (ia, ib) = (Ink(a.Bmp), Ink(b.Bmp));
                    Assert.True(Math.Abs(ia.R - ib.R) < 0.03 && Math.Abs(ia.B - ib.B) < 0.05, $"zoom {zoom}: 文字の外枠 {ib} vs {ia}");
                    double diff = AlphaDiff(a.Bmp, b.Bmp);
                    Assert.True(diff < 12, $"zoom {zoom}: 画素の違い {diff:0.#}");
                }
            }
            finally { wv.ZoomFactor = 1.0; MathRenderer.ResetForTests(); }
        });
    }

    [WebView2Fact]
    public void Shutdownで見えない窓とWebView2が解放されウィンドウが残らない()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            var h = MathRenderer.WindowHandle;
            Assert.NotEqual(IntPtr.Zero, h);
            Assert.True(IsWindow(h));
            Assert.True(MathRenderer.Shutdown(20000), "ブラウザのプロセスが終わらない");   // PC が忙しいと5秒では終わらないことがある（2026-11-16）
            Assert.False(IsWindow(h));
            Assert.Equal(IntPtr.Zero, MathRenderer.WindowHandle);
            Assert.False(MathRenderer.Ready);
            Assert.False(MathRenderer.Active);
            Assert.Null(await MathRenderer.RenderAsync(@"x^2", false, Fg, null, 16, 1));
            MathRenderer.ResetForTests();
        });
    }

    [Fact]
    public void 立ち上げ途中でShutdownしても窓を作り直さず落ちない()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            if (!MathRenderer.RuntimeAvailable()) return;
            var t = MathRenderer.Start(_p);
            Assert.True(MathRenderer.Shutdown(20000), "ブラウザのプロセスが終わらない");   // PC が忙しいと5秒では終わらないことがある（2026-11-16）
            await t;
            Assert.Equal(IntPtr.Zero, MathRenderer.WindowHandle);
            MathRenderer.ResetForTests();
        });
    }

    [WebView2Fact]
    public void 実ログ由来の式とWpfMathで落ちた命令を全部描ける()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            Assert.Equal("katex", MathRenderer.Mode);
            Assert.True(Formulas.Length >= 20);
            var failed = new List<string>();
            var swAll = Stopwatch.StartNew();
            foreach (var tex in Formulas)
            {
                var img = await MathRenderer.RenderAsync(tex, true, Fg, null, 14, 1.0);
                if (img is null || img.W < 5 || img.H < 5) failed.Add(tex);
            }
            Assert.Empty(failed);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "katex_time.txt"), $"startup_ms={MathRenderer.StartupMs} avg_render_ms={swAll.Elapsed.TotalMilliseconds / Formulas.Length:F1}");
            // キャッシュは同じ画像を返す
            var a = await MathRenderer.RenderAsync(@"x^2", false, Fg, null, 14, 1.0);
            var b = await MathRenderer.RenderAsync(@"x^2", false, Fg, null, 14, 1.0);
            Assert.Same(a, b);
            Assert.True(a!.Base > 0 && a.Base < a.H);
            // 画面の倍率を上げると画素は増え、DIP の大きさはほぼ同じ
            var c = await MathRenderer.RenderAsync(@"x^2", false, Fg, null, 14, 2.0);
            Assert.True(c!.Bmp.PixelWidth > a.Bmp.PixelWidth * 1.7);
            Assert.InRange(c.W / a.W, 0.9, 1.1);
            MathRenderer.ResetForTests();
        });
    }

    [WebView2Fact]
    public void 危ない命令は何も開かず描けない式として扱う()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            // trust:false: \href / \url / \includegraphics はリンクにも画像にもならず赤い文字で出るだけ
            // （KaTeX は throwOnError でも落とさない）。何も開かず、描画部品は生きたまま
            foreach (var bad in new[] { @"\href{https://example.com}{x}", @"\url{https://example.com}", @"\includegraphics{a.png}" })
                await MathRenderer.RenderAsync(bad, false, Fg, null, 14, 1.0);
            Assert.True(MathRenderer.Ready);
            // 未知の命令は throwOnError で落ちて null（呼び側は原文字のまま）
            Assert.Null(await MathRenderer.RenderAsync(@"\qqqqq", false, Fg, null, 14, 1.0));
            MathRenderer.ResetForTests();
        });
    }

    // ---- 解答欄（AnswerPanel）と組み合わせる ----

    private static async Task WaitUntil(Func<bool> cond, int ms = 30000)   // 条件がそろうまで待つだけ。PC が忙しいと8秒では足りないことがある（2026-11-16）
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private (MainWindow W, AnswerPanel Panel) NewPanel()
    {
        var cfg = new ConfigStore(_p);
        cfg.Load();
        var w = new MainWindow(_p, cfg);
        w.Show();
        w.Session.OpenCapture();
        var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
        Assert.NotNull(panel);
        return (w, panel);
    }

    private static RichTextBox Body(AnswerPanel p) => (RichTextBox)Fld(p.View, "_body")!;

    private static List<Image> Images(RichTextBox body)
    {
        var list = new List<Image>();
        foreach (Block b in body.Document.Blocks)
            if (b is Paragraph p)
                foreach (Inline inl in p.Inlines)
                    if (inl is InlineUIContainer ui && ui.Child is Image im) list.Add(im);
        return list;
    }

    private static string FullText(AnswerPanel p)
        => (string)p.View.GetType().GetMethod("FullText", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p.View, null)!;

    private static string SelectedText(AnswerPanel p)
        => (string)p.View.GetType().GetMethod("SelectedText", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p.View, null)!;

    private const string Maxwell =
        "$$\\nabla \\cdot \\mathbf{B}(t, \\mathbf{x}) = 0$$\n" +
        "$$\\nabla \\times \\mathbf{E}(t, \\mathbf{x}) + \\frac{\\partial \\mathbf{B}(t, \\mathbf{x})}{\\partial t} = 0$$\n" +
        "$$\\nabla \\cdot \\mathbf{D}(t, \\mathbf{x}) = \\rho(t, \\mathbf{x})$$\n" +
        "$$\\nabla \\times \\mathbf{H}(t, \\mathbf{x}) - \\frac{\\partial \\mathbf{D}(t, \\mathbf{x})}{\\partial t} = \\mathbf{j}(t, \\mathbf{x})$$";

    [WebView2Fact]
    public void マクスウェル方程式4本が解答欄で画像になりコピーは原文のまま()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var (w, panel) = NewPanel();
            try
            {
                panel.ApplyBody(Maxwell);
                var body = Body(panel);
                await WaitUntil(() => Images(body).Count == 4);
                var ims = Images(body);
                Assert.Equal(4, ims.Count);
                Assert.All(ims, im => Assert.StartsWith("$$", (string)im.Tag));
                // 描けた後も、全文コピー・選択コピー・LaTeXでコピーは原文の式（F13: 既定の「中身だけ」。式だけの答えは中身を改行でつなぐ）
                static string Inner(string raw) => raw[2..^2].Trim();
                string plainAll = string.Join("\n", Maxwell.Split('\n').Select(Inner));
                Assert.Equal(plainAll, FullText(panel));
                body.SelectAll();
                Assert.Equal(plainAll.Replace("\n", "\r\n"), SelectedText(panel));
                var tex = (MenuItem)ims[1].ContextMenu!.Items[0]!;
                tex.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(Inner((string)ims[1].Tag), Clipboard.GetText());
                // 「$ で囲む」にすると、全文も式も $$ 付きの原文と同じ
                typeof(Bunnagetaro5.Views.AnswerView).GetField("_copyForm", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(panel.View, LatexCopyForm.Dollar);
                Assert.Equal(Maxwell, FullText(panel));
                tex.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal((string)ims[1].Tag, Clipboard.GetText());
                // 画像でコピー（3倍・黒い文字・透明な地。E12）: "PNG"（透明付き）と Bitmap（白地）の2形式
                var pic = (MenuItem)ims[1].ContextMenu!.Items[1]!;
                Clipboard.Clear();
                pic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitUntil(() => Clipboard.ContainsData("PNG"));
                Assert.True(Clipboard.ContainsData("PNG"));
                Assert.True(Clipboard.ContainsImage());
                var pngStream = (System.IO.Stream)Clipboard.GetData("PNG")!;
                var png = System.Windows.Media.Imaging.BitmapDecoder.Create(pngStream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                Assert.True(png.PixelWidth >= ims[1].Width * 2.5, $"{png.PixelWidth} vs {ims[1].Width}");
                // F13b: 画素は3倍のまま、PNG に 288dpi の解像度情報（貼ると解答欄で見えている大きさ）
                Assert.InRange(png.DpiX, 287.5, 288.5);
                Assert.InRange(png.DpiY, 287.5, 288.5);
                Assert.InRange(png.PixelWidth / (png.DpiX / 96.0), ims[1].Width * 0.97, ims[1].Width * 1.03);
                var px = new byte[png.PixelWidth * png.PixelHeight * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(png, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, png.PixelWidth * 4, 0);
                Assert.Equal(0, px[3]);                                    // 角（文字以外）は alpha 0
                int dark = 0;
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i + 3] > 200) { Assert.True(px[i] < 40 && px[i + 1] < 40 && px[i + 2] < 40, "文字は黒"); dark++; }
                Assert.True(dark > 50, "文字の画素がある");
                var bmp = Clipboard.GetImage()!;
                var bp = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(bp, bmp.PixelWidth * 4, 0);
                Assert.Equal((255, 255, 255), (bp[0], bp[1], bp[2]));      // Bitmap 版の地は白
                // 表示用の画像は解答欄の色のまま（コピー用の黒とは別の画像）
                Assert.All(Images(body), im => Assert.NotNull(im.Source));
                var fgNow = (Color)(((SolidColorBrush)((RichTextBox)Fld(panel.View, "_body")!).Foreground).Color);
                Assert.True(MathRenderer.TryGetCached(((string)ims[1].Tag)[2..^2], true, fgNow, null, ((RichTextBox)Fld(panel.View, "_body")!).FontSize, 2.0 * System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX, out var disp) && disp is not null);
                // F29: 表示用は画面の倍率の2倍の画素で撮り、DIP の大きさに縮めて出す
                Assert.All(ims, im => Assert.InRange(((System.Windows.Media.Imaging.BitmapSource)im.Source).PixelWidth / (im.Width * System.Windows.Media.VisualTreeHelper.GetDpi(im).DpiScaleX), 1.9, 2.1));
                Assert.All(ims, im => Assert.Equal(System.Windows.Media.BitmapScalingMode.HighQuality, System.Windows.Media.RenderOptions.GetBitmapScalingMode(im)));
                Assert.True(MathRenderer.TryGetCached(((string)ims[1].Tag)[2..^2], true, Colors.Black, null, ((RichTextBox)Fld(panel.View, "_body")!).FontSize, 3.0, out var copyImg) && copyImg is not null);
                Assert.NotSame(disp, copyImg);
            }
            finally { w.Close(); MathRenderer.ResetForTests(); }
        });
    }

    /// <summary>式の画像の右端が、本文の見える範囲（ScrollViewer の見える幅 − PagePadding の右）に入っているか</summary>
    private static void AssertImagesInside(RichTextBox body, string when)
    {
        var sv = (ScrollViewer)typeof(Bunnagetaro5.Views.AnswerView).GetMethod("BodyScroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(FindView(body), null)!;
        double edge = sv.Padding.Left + sv.ViewportWidth - body.Document.PagePadding.Right;
        foreach (var im in Images(body))
        {
            double right = im.TransformToAncestor(sv).Transform(new Point(im.ActualWidth, 0)).X;
            Assert.True(right <= edge + 0.5, $"{when}: 式の右端 {right:0.##} が見える範囲 {edge:0.##} を越えた（幅 {im.Width:0.##}）");
        }
    }

    private static Bunnagetaro5.Views.AnswerView FindView(DependencyObject d)
    {
        while (d is not Bunnagetaro5.Views.AnswerView) d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return (Bunnagetaro5.Views.AnswerView)d;
    }

    /// <summary>H2（続き）: 狭い欄で、本文が伸びて縦スクロールバーが後から出ても・欄の幅を変えても、長い式の右が切れない
    /// （250% のノートで、スクロールバーが後から出ると欄の ActualWidth が変わらず付け直されなかった・PagePadding を見ていなかった）</summary>
    [WebView2Fact]
    public void 狭い欄で長い式はスクロールバーが後から出ても欄の幅を変えても右が切れない()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var (w, panel) = NewPanel();
            try
            {
                panel.Width = 320; panel.Height = 300;
                await Task.Delay(300);
                var body = Body(panel);
                const string longDisp = @"$$= \left\{ ( a ^ { 4 } + a ^ { 2 } b ^ { 2 } + b ^ { 4 } ) ^ { 2 } - ( a ^ { 3 } b + a b ^ { 3 } ) ^ { 2 } \right\} + ( a ^ { 5 } + b ^ { 5 } ) + ( a ^ { 3 } + b ^ { 3 } )$$";
                const string longInl = @"文中の式は $a^8 + a^5 + a^6 b^2 + a^4 b^4 + a^2 b^6 + b^5 + b^8 + a^3 b^3 + a^7 b + a b^7$ です。";
                // 短い本文（スクロールバー無し）
                panel.ApplyBody("前置き\n" + longDisp);
                await WaitUntil(() => Images(body).Count == 1);
                await Task.Delay(500);
                AssertImagesInside(body, "短い本文");
                // 本文が伸びて、式を置いた後に縦スクロールバーが出る
                string many = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"行{i} ながい文章ながい文章ながい文章"));
                panel.ApplyBody("前置き\n" + longDisp + "\n" + longInl + "\n" + many);
                await WaitUntil(() => Images(body).Count == 2);
                await Task.Delay(800);
                AssertImagesInside(body, "スクロールバーが後から出た");
                // 欄を細く・広く
                panel.Width = 280; await Task.Delay(800);
                AssertImagesInside(body, "細くした");
                panel.Width = 500; await Task.Delay(800);
                AssertImagesInside(body, "広くした");
                Assert.Contains(Images(body), im => im.Width > 290);   // 広げたら元の大きさへ戻る
            }
            finally { w.Close(); MathRenderer.ResetForTests(); }
        }, 180);
    }

    /// <summary>修正J47: 行ごとに並んだ文中の分数（OCR の答えの形）が、下の行と重ならない。小さな式は今まで通り基準線に揃う</summary>
    [WebView2Fact]
    public void J47_文中の分数が並んでも下の行と重ならず_小さな式は基準線に揃う()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var (w, panel) = NewPanel();
            try
            {
                panel.Width = 600; panel.Height = 500;
                await Task.Delay(300);
                var body = Body(panel);
                panel.ApplyBody("$\\frac{y'}{y} = \\frac{1}{x+1} + \\frac{1}{x+2}$\n" +
                                "$= \\frac{(x+2)(x+3)+(x+1)(x+3)}{(x+1)(x+2)(x+3)}$\n" +
                                "$= \\frac{3x^2+12x+11}{(x+1)(x+2)(x+3)}$\n" +
                                "小さな式 $x^2$ の行\n" +
                                "解 (1) $\\log_{10}116 = \\log_{10}(1.16 \\times 10^2)$\n" +
                                "$= \\log_{10}1.16 + \\log_{10}10^2$");
                await WaitUntil(() => Images(body).Count == 6);
                await Task.Delay(500);
                var sv = (ScrollViewer)typeof(Bunnagetaro5.Views.AnswerView).GetMethod("BodyScroll", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(FindView(body), null)!;
                var ims = Images(body);
                var boxes = ims.Select(im => im.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, im.ActualWidth, im.ActualHeight))).ToList();
                for (int i = 0; i + 1 < 3; i++)
                    Assert.True(boxes[i].Bottom <= boxes[i + 1].Top + 0.5,
                        $"{i + 1}行目の式の下 {boxes[i].Bottom:0.##} が {i + 2}行目の式の上 {boxes[i + 1].Top:0.##} に重なった");
                Assert.True(boxes[4].Bottom <= boxes[5].Top + 0.5,
                    $"「解 (1)」の式の下 {boxes[4].Bottom:0.##} が次の行の式の上 {boxes[5].Top:0.##} に重なった");
                // 修正J48: 式は持ち上げない。「解 (1)」の文字の基準線と、同じ行の式の基準線（画像の下 − 基準線より下の分）が揃う
                var rtbBox = ims[4].TransformToAncestor(body).TransformBounds(new Rect(0, 0, ims[4].ActualWidth, ims[4].ActualHeight));
                // 式の本当の基準線は、描いた式の情報（画像の上からの基準線の位置 Base）から取る（余白の値には頼らない）
                var view = FindView(body);
                var (fg, font, scale) = ((System.Windows.Media.Color, double, double))typeof(Bunnagetaro5.Views.AnswerView)
                    .GetMethod("MathParams", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, null)!;
                Assert.True(MathRenderer.TryGetCached(@"\log_{10}116 = \log_{10}(1.16 \times 10^2)", false, fg, null, font, scale, out var mimg) && mimg is not null);
                double imgBase = rtbBox.Top + mimg!.Base * (rtbBox.Height / mimg.H);
                var run = body.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>())
                    .First(r => r.Text.StartsWith("解"));   // 改行1つは同じ段落の中（F35）
                var caret = run.ContentStart.GetPositionAtOffset(1)!.GetCharacterRect(LogicalDirection.Forward);
                var ff = body.FontFamily;
                double textBase = caret.Bottom - (ff.LineSpacing - ff.Baseline) * body.FontSize;
                Assert.True(Math.Abs(imgBase - textBase) <= 2.0,
                    $"「解 (1)」の文字の基準線 {textBase:0.##} と式の基準線 {imgBase:0.##} がずれた");
                // 小さな式も今まで通り基準線に揃う（基準線より下を行の高さに数えない）
                Assert.True(ims[3].Margin.Bottom < 0, $"小さな式の下の余白 {ims[3].Margin.Bottom}");
            }
            finally { w.Close(); MathRenderer.ResetForTests(); }
        }, 180);
    }

    [WebView2Fact]
    public void ストリーミング中は原文字で出て描き終わると画像に替わる()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var (w, panel) = NewPanel();
            try
            {
                var body = Body(panel);
                panel.ApplyBody("前置き $$x^2");           // 閉じ記号待ち: 文字のまま
                Assert.Empty(Images(body));
                panel.ApplyBody("前置き $$x^2$$ 後ろ");     // 閉じた: まず原文字、描けたら画像
                await WaitUntil(() => Images(body).Count == 1);
                Assert.Single(Images(body));
                // 同じ式をもう一度出すとキャッシュで即、画像
                panel.ApplyBody("別の文 $$x^2$$");
                Assert.Single(Images(body));
                Assert.Single((System.Collections.IEnumerable)Fld(panel.View, "_mathImgs")!);
                // 描けない式は原文字のまま
                panel.ApplyBody("壊れた $$\\qqqqq$$ 式");
                await Task.Delay(500);
                Assert.Empty(Images(body));
                Assert.Empty((System.Collections.IEnumerable)Fld(panel.View, "_mathImgs")!);
                Assert.Equal("壊れた $$\\qqqqq$$ 式", FullText(panel));
            }
            finally { w.Close(); MathRenderer.ResetForTests(); }
        });
    }

    [WebView2Fact]
    [Trait("Category", "Perf")]   // 整理（2026-11-22）: 時間を測って合否を決める。普段の全体の流しでは外す
    public void 式30個の長い答えでもストリーミング1操作は150ms未満()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var (w, panel) = NewPanel();
            try
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 30; i++)
                {
                    sb.Append("第").Append(i).Append("項の説明です。".PadRight(300, 'あ')).Append('\n');
                    sb.Append("$$").Append(Formulas[i % 15]).Append(" + ").Append(i).Append("$$\n");
                }
                string full = sb.ToString();
                Assert.True(full.Length > 10000, full.Length.ToString());
                double worst = 0;
                int step = 120;
                for (int n = step; n < full.Length + step; n += step)
                {
                    string part = full[..Math.Min(n, full.Length)];
                    var sw = Stopwatch.StartNew();
                    panel.ApplyBody(part);
                    worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                    await Task.Delay(1);
                }
                Assert.True(worst < 150, $"最悪 {worst:F1}ms");
                await WaitUntil(() => Images(Body(panel)).Count >= 30, 20000);
                Assert.Equal(30, Images(Body(panel)).Count);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "katex_perf.txt"), $"worst_apply_ms={worst:F1}");
            }
            finally { w.Close(); MathRenderer.ResetForTests(); }
        }, 180);
    }

    [Fact]
    public void WpfMathの予備経路でも画像でコピーは黒い透明PNGと白地Bitmapの2形式()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            var (w, panel) = NewPanel();
            try
            {
                panel.ApplyBody("文 $x^2+y^2$ 文");
                var body = Body(panel);
                WpfMath.Controls.FormulaControl? fc = null;
                foreach (Block b in body.Document.Blocks)
                    if (b is Paragraph p)
                        foreach (Inline inl in p.Inlines)
                            if (inl is InlineUIContainer ui && ui.Child is WpfMath.Controls.FormulaControl f) fc = f;
                Assert.NotNull(fc);
                var pic = (MenuItem)fc!.ContextMenu!.Items[1]!;
                Clipboard.Clear();
                pic.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(300);
                Assert.True(Clipboard.ContainsData("PNG"));
                var png = System.Windows.Media.Imaging.BitmapDecoder.Create((System.IO.Stream)Clipboard.GetData("PNG")!, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                var px = new byte[png.PixelWidth * png.PixelHeight * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(png, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, png.PixelWidth * 4, 0);
                Assert.Equal(0, px[3]);
                int dark = 0;
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i + 3] > 200) { Assert.True(px[i] < 40 && px[i + 1] < 40 && px[i + 2] < 40); dark++; }
                Assert.True(dark > 20);
                var bmp = Clipboard.GetImage()!;
                var bp = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(bp, bmp.PixelWidth * 4, 0);
                Assert.Equal((255, 255, 255), (bp[0], bp[1], bp[2]));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 立ち上げ前やランタイムが無い時はWpfMathへ戻る()
    {
        // WebView2 が要らないテスト。Start していない（Active=false）解答欄は FormulaControl で式を出す
        RunSta(() =>
        {
            MathRenderer.ResetForTests();
            Assert.False(MathRenderer.Active);
            Assert.Equal("wpfmath", MathRenderer.Mode);
            var (w, panel) = NewPanel();
            try
            {
                panel.ApplyBody("文 $x^2$ 文");
                var body = Body(panel);
                bool hasFormula = false;
                foreach (Block b in body.Document.Blocks)
                    if (b is Paragraph p)
                        foreach (Inline inl in p.Inlines)
                            if (inl is InlineUIContainer ui && ui.Child is WpfMath.Controls.FormulaControl) hasFormula = true;
                Assert.True(hasFormula);
                Assert.Empty(Images(body));
                Assert.Equal("文 $x^2$ 文", FullText(panel));
                body.SelectAll();
                Assert.Equal("文 $x^2$ 文", SelectedText(panel));
            }
            finally { w.Close(); }
            return Task.CompletedTask;
        });
    }

    private const string SumTex = @"\sum_{i=1}^{k} c_{i} \, \vec{v}_{i} = \vec{0}";

    [WebView2Fact]
    public void F19_文字の大きさと倍率の表で切り出しが式の矩形と一致する()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            var texs = new[] { SumTex, @"\nabla \times \mathbf{H}(t, \mathbf{x}) - \frac{\partial \mathbf{D}(t, \mathbf{x})}{\partial t} = \mathbf{j}(t, \mathbf{x})" };
            var bad = new List<string>();
            foreach (var tex in texs)
                foreach (double font in new[] { 12.0, 36, 72 })
                    foreach (double sc in new[] { 1.0, 1.25, 1.5, 3, 3.75, 4.5 })
                    {
                        var img = await MathRenderer.RenderAsync(tex, true, Fg, null, font, sc);
                        var d = MathRenderer.LastDiag!;
                        // 72・倍率 4.5 のマクスウェルは 5600 画素超: Windows の既定の上限（約5140）を超えても欠けない
                        if (img is null || d.CropW < d.WantW - 2 || d.CropH < d.WantH - 2 || Math.Abs(img.Scale - sc) > 1e-9)
                            bad.Add($"{font}x{sc} {tex[..10]}");
                    }
            Assert.Empty(bad);
            MathRenderer.ResetForTests();
        }, 600);
    }

    [WebView2Fact]
    public void F19_窓に収まらない式は欠けた画像を返さずAppLogに出てキャッシュに入らない()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            MathRenderer.MaxTrackOverridePx = 700;   // 窓の上限をわざと小さくして、収まらない時の動きを見る
            try
            {
                await MathRenderer.Start(_p);
                var img = await MathRenderer.RenderAsync(SumTex, true, Fg, null, 72, 3.0);
                Assert.Null(img);   // 欠けた画像は返さない（呼び側は原文字のまま）
                Assert.False(MathRenderer.TryGetCached(SumTex, true, Fg, null, 72, 3.0, out _));   // 欠けた結果は覚えない
                Assert.Contains("欠けた", File.ReadAllText(_p.AppLogFile));
                // 倍率を下げてよい呼び方（画像でコピー・F29）なら、届く倍率まで下げて欠けずに撮れる
                var small = await MathRenderer.RenderAsync(SumTex, true, Fg, null, 72, 2.9, 1.0);
                Assert.NotNull(small);
                Assert.InRange(small!.Scale, 1.0, 2.89);
                var d = MathRenderer.LastDiag!;
                Assert.True(d.CropW >= d.WantW - 2 && d.CropH >= d.WantH - 2);
                // 下げた結果は元の倍率の問い合わせにも返る（解像度が低いだけで欠けてはいない）
                Assert.True(MathRenderer.TryGetCached(SumTex, true, Fg, null, 72, 2.9, out var again));
                Assert.Same(small, again);
            }
            finally { MathRenderer.MaxTrackOverridePx = 0; MathRenderer.ResetForTests(); }
        }, 300);
    }

    [Fact]
    public void F13b_画像でコピーのPNGは画素そのままで解像度情報だけ付く()
    {
        RunSta(() =>
        {
            var px = new byte[30 * 20 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = (byte)(i % 8 == 0 ? 255 : 0); }
            var src = System.Windows.Media.Imaging.BitmapSource.Create(30, 20, 96, 96, PixelFormats.Bgra32, null, px, 30 * 4);
            foreach (var (ppd, dpi) in new[] { (3.0, 288.0), (1.5, 144.0) })
            {
                var data = Views.AnswerView.BuildImageData(src, ppd);
                var ms = (System.IO.Stream)data.GetData("PNG")!;
                var png = System.Windows.Media.Imaging.BitmapDecoder.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal(30, png.PixelWidth);   // 画素数は変えない
                Assert.Equal(20, png.PixelHeight);
                Assert.InRange(png.DpiX, dpi - 0.5, dpi + 0.5);
                var back = new byte[px.Length];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0).CopyPixels(back, 30 * 4, 0);
                Assert.Equal(px[3], back[3]);        // 透明は透明のまま
                Assert.Equal(px[7], back[7]);
            }
            return Task.CompletedTask;
        });
    }

    [WebView2Fact]
    public void 描いている途中で終わらせても_立ち上げ直した後の式は順番待ちで止まらない()
    {
        // 1つ目のスレッド: 描いている途中（順番札を借りたまま）でスレッドごと終わる。
        // 描きかけの式の続きはこのスレッドで動く予定だったので、終わらないまま放り出される（50ms のテストが落ちた時と同じ形）
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            _ = MathRenderer.RenderAsync(@"\sum_{i=1}^{n} i^{2} = \frac{n(n+1)(2n+1)}{6}", true, Fg, null, 16, 2);
            await Task.Delay(1);
        });
        // 2つ目のスレッド: 立ち上げ直した後の式は、順番待ちで止まらずに描ける
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            var img = await MathRenderer.RenderAsync(@"x^{2} + y^{2}", false, Fg, null, 16, 1).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(img);
        });
    }

    [WebView2Fact]
    public void H4a_取り消した式は順番が来ても描かずキャッシュにも入れない()
    {
        RunSta(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(_p);
            Assert.True(MathRenderer.Ready);
            string a = @"h4a_first + \frac{1}{2}", b = @"h4a_cancelled + \frac{3}{4}", c = @"h4a_alive + \frac{5}{6}";
            int before = MathRenderer.SkippedCancelled;
            using var cts = new System.Threading.CancellationTokenSource();
            var ta = MathRenderer.RenderAsync(a, true, Fg, null, 14, 1.0);                       // 先に順番を取る
            var tb = MathRenderer.RenderAsync(b, true, Fg, null, 14, 1.0, 0, cts.Token);         // 順番待ちの間に取り消す
            var tc = MathRenderer.RenderAsync(c, true, Fg, null, 14, 1.0);
            cts.Cancel();
            Assert.NotNull(await ta);
            Assert.Null(await tb);
            Assert.NotNull(await tc);
            Assert.Equal(before + 1, MathRenderer.SkippedCancelled);
            Assert.False(MathRenderer.TryGetCached(b, true, Fg, null, 14, 1.0, out _));         // 取り消した式はキャッシュに入らない
            Assert.True(MathRenderer.TryGetCached(a, true, Fg, null, 14, 1.0, out _));
            // 同じ式を生きた札で待っている呼び出しがあれば、片方が取り消しても描く
            using var cts2 = new System.Threading.CancellationTokenSource();
            var t1 = MathRenderer.RenderAsync(@"h4a_x + 1", true, Fg, null, 14, 1.0);
            var t2 = MathRenderer.RenderAsync(@"h4a_shared + \frac{7}{8}", true, Fg, null, 14, 1.0, 0, cts2.Token);
            var t3 = MathRenderer.RenderAsync(@"h4a_shared + \frac{7}{8}", true, Fg, null, 14, 1.0);
            cts2.Cancel();
            await t1;
            Assert.NotNull(await t2);
            Assert.NotNull(await t3);
        });
    }
}

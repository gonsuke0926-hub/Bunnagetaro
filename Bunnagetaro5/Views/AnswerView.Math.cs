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

/// <summary>解答欄の数式（KaTeX・WpfMath の描画、幅合わせ、式の画像コピー）（AnswerView を分けたもの。修正J43）</summary>
public partial class AnswerView
{
    // ---------------- KaTeX（整理8.6） ----------------

    /// <summary>表示用の式は画面の倍率の2倍の画素で撮り、DIP の大きさに縮めて出す（にじみ対策 F29）。
    /// 2倍で見えない窓に収まらない式は、画面の倍率（等倍）まで下げて撮る（F19）</summary>
    internal const double DisplaySuperSample = 2.0;

    private (Color Fg, double Font, double Scale) MathParams()
    {
        Color fg = (_body.Foreground as SolidColorBrush)?.Color ?? Color.FromRgb(0xF5, 0xF5, 0xF5);
        double dpi = 1.0;
        try { dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX); } catch { }
        return (fg, Math.Max(1.0, _body.FontSize), dpi * DisplaySuperSample);
    }

    /// <summary>KaTeX の式1つ分。キャッシュに画像があればその画像、無ければ原文字の Run を返し、
    /// 描き終わったら Run を画像へ差し替える（描けなかった式は原文字のまま）</summary>
    private Inline MakeKatexInline(MathSeg seg)
    {
        bool display = seg.Kind == MathSegKind.Display;
        var (fg, font, scale) = MathParams();
        if (MathRenderer.TryGetCached(seg.Tex, display, fg, null, font, scale, out var img))
            return img is null ? new Run(seg.Raw) : MakeImageInline(img, seg);
        var run = new Run(seg.Raw);
        _ = RenderAndSwap(run, seg, display, fg, font, scale, _docCts.Token);
        return run;
    }

    private async Task RenderAndSwap(Run run, MathSeg seg, bool display, Color fg, double font, double scale, CancellationToken ct)
    {
        MathImage? img;
        try { img = await MathRenderer.RenderAsync(seg.Tex, display, fg, null, font, scale, scale / DisplaySuperSample, ct); }
        catch (Exception ex) { AppLog.Warning("math: 式の差し替えに失敗: " + ex.Message); return; }
        if (ct.IsCancellationRequested) return;   // 組み直された・使い回された
        if (img is null || run.Parent is not Paragraph p) return;   // 描けない／もう文書から外れた
        bool wasBottom = AtBottom();
        var ui = MakeImageInline(img, seg);
        p.Inlines.InsertBefore(run, ui);
        p.Inlines.Remove(run);
        if (ui.Child is SnapImage sim && _natSizes.TryGetValue(sim, out var n)) ApplyFit(sim, n);   // 修正J48: 直後のつっかえ棒を合わせる
        if (wasBottom) _body.ScrollToEnd();
    }

    private ScrollViewer? _bodySv;

    /// <summary>本文の RichTextBox の中の ScrollViewer（テンプレートが付く前は null）</summary>
    private ScrollViewer? BodyScroll()
    {
        if (_bodySv is not null) return _bodySv;
        var q = new Queue<DependencyObject>();
        q.Enqueue(_body);
        while (q.Count > 0)
        {
            var d = q.Dequeue();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ScrollViewer sv) return _bodySv = sv;
                q.Enqueue(c);
            }
        }
        return null;
    }

    /// <summary>H2: 本文を流せる幅。ScrollViewer の見える幅（縦スクロールバーの分は既に引かれている）
    /// − 文書の PagePadding。固定の数字は引かない（250% の実測: PagePadding 5,0,5,0・細バー 8）。
    /// 見える幅がまだ決まっていなければ欄の幅 − Padding − PagePadding、それも未定なら 0（縮めない）</summary>
    internal double AvailableBodyWidth()
    {
        var sv = BodyScroll();
        double w;
        if (sv is not null && sv.ViewportWidth > 0) w = sv.ViewportWidth;
        else
        {
            w = _body.ActualWidth;
            if (w <= 0) return 0;
            w -= _body.Padding.Left + _body.Padding.Right;
        }
        var pp = _body.Document.PagePadding;
        w -= (double.IsNaN(pp.Left) ? 0 : pp.Left) + (double.IsNaN(pp.Right) ? 0 : pp.Right);
        return Math.Max(1, w);
    }

    private DateTime _lastNarrowUtc;
    private double _lastNarrow;

    /// <summary>H2: 見える幅が変わったら式を付け直す（欄のサイズ変更も、本文が伸びて縦スクロールバーが後から出た時も）。
    /// 縮めた結果スクロールバーが消え、広げるとまた出る…の行ったり来たりを避けるため、
    /// 直前（300ms 以内）に狭まったのと同じ分だけ広がった時は付け直さない（狭い方のまま）</summary>
    private void OnBodyScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0 || !ReferenceEquals(e.OriginalSource, BodyScroll())) return;
        var now = DateTime.UtcNow;
        if (e.ViewportWidthChange < 0) { _lastNarrowUtc = now; _lastNarrow = -e.ViewportWidthChange; }
        else if ((now - _lastNarrowUtc).TotalMilliseconds < 300 && Math.Abs(e.ViewportWidthChange - _lastNarrow) < 0.5) return;
        RefitMathImages();
    }

    /// <summary>H2: 幅 avail に収まらない時だけ縦横比を保って縮める（収まる・avail 未定なら元のまま）</summary>
    internal static (double W, double H) FitToWidth(double w, double h, double avail)
    {
        if (avail <= 0 || w <= avail || w <= 0) return (w, h);
        return (avail, h * avail / w);
    }

    private sealed class NatSize { public double W, H, MarginBottom; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SnapImage, NatSize> _natSizes = new();
    private readonly List<WeakReference<SnapImage>> _mathImgs = new();

    private void ApplyFit(SnapImage im, NatSize n)
    {
        var (w, h) = FitToWidth(n.W, n.H, AvailableBodyWidth());
        im.Width = w;
        im.Height = h;
        double s = n.W > 0 ? w / n.W : 1;
        if (n.MarginBottom != 0) im.Margin = new Thickness(0, 0, 0, n.MarginBottom * s);
        SyncSpacer(im, -n.MarginBottom * s);
    }

    // ---------------- 修正J48: 文中の式の下の「つっかえ棒」 ----------------
    // 文中の式は基準線に揃えるため、基準線より下（分母など）を行の高さに数えない（下の余白をマイナス）。
    // そのままだと、下が大きい式は次の行に重なる（修正J47 で直そうとして式を持ち上げたら、左の文字と並びがずれた）。
    // 式の直後に幅0の見えない部品を置き、文字の上端から「字の上の高さ＋式のはみ出し」まで伸ばして、
    // 行の下の空きだけを広げる。式そのものは基準線にぴったりのまま。コピーは Tag の無いこの部品を拾わない

    private const string SpacerTag = "math-spacer";

    /// <summary>文中の式の直後に置く、高さ0のつっかえ棒（式の画像が出来たら SyncSpacer で高さを合わせる）</summary>
    private static InlineUIContainer MakeMathSpacer() =>
        new(new Border { Width = 0, Height = 0, IsHitTestVisible = false })
        { BaselineAlignment = BaselineAlignment.TextTop, Tag = SpacerTag };

    /// <summary>式 im の直後のつっかえ棒の高さを、基準線より下へ below（DIP）はみ出すのに合わせる。
    /// はみ出しが字の下の高さに収まる小さな式は 0（文字の行の高さで足りる）</summary>
    private void SyncSpacer(SnapImage im, double below)
    {
        if (im.Parent is not InlineUIContainer c || c.NextInline is not InlineUIContainer { Tag: SpacerTag, Child: FrameworkElement fe })
            return;
        double ascent, descent;
        try
        {
            var ff = _body.FontFamily;
            ascent = Math.Max(0, ff.Baseline * _body.FontSize);
            descent = Math.Max(0, (ff.LineSpacing - ff.Baseline) * _body.FontSize);
        }
        catch { ascent = _body.FontSize; descent = _body.FontSize * 0.25; }
        // つっかえ棒の上の端は行のいちばん上（背の高い式なら式の上の端、そうでなければ字の上の端）に揃うので、
        // 「式の高さ全部」と「字の上の高さ＋はみ出し」の大きい方にすれば、どちらでも式の下の端まで届く（実測で確かめた）
        double want = below > descent ? Math.Ceiling(Math.Max(im.Height, ascent + below)) : 0;
        if (Math.Abs(fe.Height - want) > 0.01) fe.Height = want;
    }

    private void RefitMathImages()
    {
        for (int i = _mathImgs.Count - 1; i >= 0; i--)
        {
            if (_mathImgs[i].TryGetTarget(out var im) && _natSizes.TryGetValue(im, out var n)) ApplyFit(im, n);
            else _mathImgs.RemoveAt(i);
        }
    }

    private InlineUIContainer MakeImageInline(MathImage img, MathSeg seg)
    {
        bool display = seg.Kind == MathSegKind.Display;
        var im = new SnapImage
        {
            Source = img.Bmp,
            Width = img.W,
            Height = img.H,
            Stretch = Stretch.Fill,
            Tag = seg.Raw,   // $ 付きの原文字（コピー経路26）
            IsHitTestVisible = true,
            // 文中の式: 下の余白（基準線より下）を行の高さに数えず、文字の基準線へ合わせる（下の行と重ならないよう、
            // 直後のつっかえ棒で行の下を広げる。修正J48）。余白は下の ApplyFit で付ける
        };
        RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
        im.ContextMenu = BuildKatexMenu(seg);
        var nat = new NatSize { W = img.W, H = img.H, MarginBottom = display ? 0 : -Math.Max(0, img.H - img.Base) };
        _natSizes.Add(im, nat);
        _mathImgs.Add(new WeakReference<SnapImage>(im));
        ApplyFit(im, nat);
        return new InlineUIContainer(im) { BaselineAlignment = BaselineAlignment.Baseline };
    }

    /// <summary>式の右クリックメニュー（KaTeX版）。画像でコピーは3倍の解像度で撮り直し、黒い文字・透明な地（E12）</summary>
    private ContextMenu BuildKatexMenu(MathSeg seg)
    {
        var miTex = new MenuItem { Header = "LaTeXでコピー" };
        miTex.Click += (_, _) => CopyText(FormulaCopyText(seg.Raw));
        var miImg = new MenuItem { Header = "画像でコピー" };
        miImg.Click += async (_, _) => await CopyKatexImage(seg);
        var menu = new ContextMenu();
        menu.Items.Add(miTex);
        menu.Items.Add(miImg);
        return menu;
    }

    private async Task CopyKatexImage(MathSeg seg)
    {
        LastInteractUtc = DateTime.UtcNow;
        try
        {
            // 慣例どおり黒(#000000)・地は透明（E12）。表示用とは色が違うのでキャッシュも別
            var (_, font, _) = MathParams();
            // 大きすぎて3倍で撮れない式は、撮れる倍率まで下げて撮る（欠けた画像は渡さない。F19）
            var img = await MathRenderer.RenderAsync(seg.Tex, seg.Kind == MathSegKind.Display, Colors.Black, null, font, 3.0, 1.0);
            if (img is null) { _host.SetStatus("式を画像でコピーできませんでした"); return; }
            Clipboard.SetDataObject(BuildImageData(img.Bmp, img.Scale > 0 ? img.Scale : 3.0), true);
            _host.SetStatus(img.Scale > 0 && img.Scale < 2.99 ? "式が大きいため、画質を下げて画像をコピーしました" : "式の画像をコピーしました");
        }
        catch (Exception ex)
        {
            AppLog.Error("式の画像コピー失敗", ex);
            _host.SetStatus("コピーに失敗しました（app.log参照）");
        }
    }

    /// <summary>WpfMathの FormulaControl を解答欄の色・字の大きさで作る。
    /// 式として解析できない（HasError）なら null（呼び出し側が原文字へ戻す）。
    /// Tag には $ 付きの原文字を入れておく（コピー経路26: 選択コピーと
    /// 「LaTeXでコピー」のどちらもこの文字列そのものを出す）</summary>
    private FormulaControl? MakeFormula(MathSeg seg)
    {
        var fc = new FormulaControl
        {
            Formula = seg.Tex,
            Scale = Math.Max(1.0, _body.FontSize),
            Foreground = _body.Foreground,
            Tag = seg.Raw,
            IsTabStop = false,   // 式がタブ移動で焦点を拾わない（読み取り専用だから）
            // 透明でも当たり判定を持たせる: 透けないと式の隙間への右クリックが
            // 本文側に抜けて、式メニューではなく本文のコピーメニューが出る
            Background = Brushes.Transparent,
        };
        if (fc.HasError) return null;
        fc.ContextMenu = BuildFormulaMenu(fc, seg.Raw);
        return fc;
    }

    /// <summary>式の右クリックメニュー（手順26）。「LaTeXでコピー」「画像でコピー」</summary>
    private ContextMenu BuildFormulaMenu(FormulaControl fc, string raw)
    {
        var miTex = new MenuItem { Header = "LaTeXでコピー" };
        miTex.Click += (_, _) => CopyText(FormulaCopyText(raw));
        var miImg = new MenuItem { Header = "画像でコピー" };
        miImg.Click += (_, _) => CopyFormulaImage(fc);
        var menu = new ContextMenu();
        menu.Items.Add(miTex);
        menu.Items.Add(miImg);
        return menu;
    }

    /// <summary>「画像でコピー」用のクリップボード内容（E12）。透明付きの PNG（"PNG" 形式。Word・PowerPoint・
    /// ブラウザ・チャットアプリが読む）と、白地に合成した不透明の Bitmap（透明を扱えない古い形式。ペイント等）を
    /// 同時に入れる。img は黒い文字・透明な地のもの。
    /// 画素は細かく持ち（pixelsPerDip＝3 なら解答欄の3倍）、PNG には 96×pixelsPerDip dpi（3倍なら 288dpi）の解像度情報を付ける
    /// （F13b）。貼った時、Word・PowerPoint は解像度情報を見て解答欄で見えている大きさで置く（見ないアプリは画素のまま大きく出る）</summary>
    internal static DataObject BuildImageData(BitmapSource img, double pixelsPerDip = 3.0)
    {
        var data = new DataObject();
        var pngMs = new System.IO.MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(WithDpi(img, 96.0 * pixelsPerDip)));
        enc.Save(pngMs);
        pngMs.Position = 0;
        data.SetData("PNG", pngMs);
        data.SetData(DataFormats.Bitmap, OnWhite(img));
        return data;
    }

    /// <summary>画素はそのまま、解像度情報（dpi）だけ付け替えた画像</summary>
    internal static BitmapSource WithDpi(BitmapSource src, double dpi)
    {
        var fc = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = fc.PixelWidth * 4;
        var px = new byte[stride * fc.PixelHeight];
        fc.CopyPixels(px, stride, 0);
        var bs = BitmapSource.Create(fc.PixelWidth, fc.PixelHeight, dpi, dpi, PixelFormats.Bgra32, null, px, stride);
        bs.Freeze();
        return bs;
    }

    /// <summary>透明付きの画像を白地に合成した不透明の画像にする</summary>
    internal static BitmapSource OnWhite(BitmapSource img)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double wd = img.PixelWidth * 96.0 / img.DpiX, hd = img.PixelHeight * 96.0 / img.DpiY;
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, wd, hd));
            dc.DrawImage(img, new Rect(0, 0, wd, hd));
        }
        var rtb = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, img.DpiX, img.DpiY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgr32, null, 0);
        conv.Freeze();
        return conv;
    }

    /// <summary>WpfMath の予備の経路の「画像でコピー」（E12）。式を黒い文字で作り直して、地は透明のまま3倍で撮る。
    /// クリップボードは KaTeX 版と同じ2形式（透明 PNG と白地 Bitmap）</summary>
    private void CopyFormulaImage(FrameworkElement src)
    {
        LastInteractUtc = DateTime.UtcNow;
        try
        {
            if (src is not FormulaControl orig)
            {
                _host.SetStatus("式を画像でコピーできませんでした");
                return;
            }
            var fc = new FormulaControl { Formula = orig.Formula, Scale = orig.Scale, Foreground = Brushes.Black };
            fc.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = fc.DesiredSize.Width, h = fc.DesiredSize.Height;
            if (w < 1 || h < 1)
            {
                _host.SetStatus("式を画像でコピーできませんでした");
                return;
            }
            fc.Arrange(new Rect(0, 0, w, h));
            fc.UpdateLayout();
            const double scale = 3.0, pad = 6;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var vb = new VisualBrush(fc)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                };
                dc.DrawRectangle(vb, null, new Rect(pad, pad, w, h));
            }
            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling((w + pad * 2) * scale),
                (int)Math.Ceiling((h + pad * 2) * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            Clipboard.SetDataObject(BuildImageData(rtb, scale), true);
            _host.SetStatus("式の画像をコピーしました");
        }
        catch (Exception ex)
        {
            AppLog.Error("式の画像コピー失敗", ex);
            _host.SetStatus("コピーに失敗しました（app.log参照）");
        }
    }

    /// <summary>フォント・色が変わったら式込みの本文を作り直す（ApplyStyleから）</summary>
    private void RebuildCurrent()
    {
        NewDocGen();
        _body.Document.Blocks.Clear();
        if (_tablesShown && _tableParts is not null) BuildTableDoc();
        else BuildMathDoc();
    }
}

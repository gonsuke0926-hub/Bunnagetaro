using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bunnagetaro5.Services;

/// <summary>
/// 送信画像の加工。
/// 送る前の加工：長辺2000pxを超えたら縮小、短辺640px未満なら拡大。
/// 真っ黒な画像（保護された動画など）は送らず「真っ黒でした」と出す。
/// 差分検知は ChangeDetector 側で行う。
/// </summary>
public static class ImagePrep
{
    public const int MaxSideSend = 2000;   // 送信画像の長辺上限（仕様5）
    public const int MinSideSend = 640;    // 極小窓は拡大して送る（仕様5）

    /// <summary>真っ黒な画像（保護された動画など）を弾く。PILの convert("L").getextrema()[1] &lt; 10 と同じ。
    /// ※ WPF の BitmapSource.CopyPixels は MTA で SizeOf が狂うことがあるため、
    ///   Gray8化 → WriteableBitmap に実体化 → BackBuffer を直接読む（CopyPixelsを使わない）。</summary>
    public static bool IsBlackFrame(BitmapSource img)
    {
        try
        {
            var conv = new FormatConvertedBitmap(img, PixelFormats.Gray8, null, 0);
            var mat = new WriteableBitmap(conv);
            int w = mat.PixelWidth, h = mat.PixelHeight, stride = mat.BackBufferStride;
            var buf = new byte[stride * h];
            mat.Lock();
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(mat.BackBuffer, buf, 0, buf.Length);
            }
            finally
            {
                mat.Unlock();
            }
            int max = 0;
            for (int y = 0; y < h; y++)
            {
                int baseOff = y * stride;
                for (int x = 0; x < w; x++)
                    if (buf[baseOff + x] > max) max = buf[baseOff + x];
            }
            return max < 10;
        }
        catch (Exception)
        {
            // 例外時は false（弾かない）
            return false;
        }
    }

    /// <summary>送信用に長辺2000pxへ縮小／極小は拡大。
    /// 変えないときは同じ参照を返す。</summary>
    public static BitmapSource SendPrep(BitmapSource img)
    {
        int w = img.PixelWidth, h = img.PixelHeight;
        int longSide = Math.Max(w, h);
        double s;
        if (longSide > MaxSideSend) s = (double)MaxSideSend / longSide;
        else if (longSide < MinSideSend) s = (double)MinSideSend / longSide;
        else return img;
        // int(w * s) の切り捨ては Python と同じ（max(1, ...) も同じ）
        int w2 = Math.Max(1, (int)(w * s));
        int h2 = Math.Max(1, (int)(h * s));
        var visual = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
        {
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(img, new System.Windows.Rect(0, 0, w2, h2));
        }
        // RenderTargetBitmap が受け付けるのは Pbgra32（要事前乗算）。縮小後の透過は出さない。
        var rtb = new RenderTargetBitmap(w2, h2, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}

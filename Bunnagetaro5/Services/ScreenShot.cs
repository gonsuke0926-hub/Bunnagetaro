using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Size = System.Drawing.Size;

namespace Bunnagetaro5.Services;

/// <summary>
/// 画面撮影（7.3 / Python版 tw_image.grab_region 相当）。
/// 撮影自体は物理px範囲の取り込みだけでUIスレッドで呼んでよい。
/// 変換・縮小・PNG化はバックグラウンドで（呼び手＝手順13がやる）。
/// </summary>
public static class ScreenShot
{
    /// <summary>物理pxの矩形をそのまま撮る（CopyFromScreen = BitBlt相当、all_screens相当）</summary>
    public static Bitmap GrabRegion(int x, int y, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Bitmap GrabRegion(Native.RECT r)
        => GrabRegion(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>変化検知の巡回用: BGRA の生バイトだけ拿到す（BitmapSource 化しない。
    /// 自動トリガは1秒ごとに撮るので変換のコストを払わない）。行間は4ピクセル境界の
    /// ため stride == w*4（CopyFromScreen の Bitmap は必ず4バイト揃え）</summary>
    public static ChangeDetector.Frame GrabFrame(Native.RECT r)
    {
        using var bmp = GrabRegion(r);
        var area = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var buf = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            return new ChangeDetector.Frame(buf, bmp.Width, bmp.Height);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// Bitmap → 凍結済み BitmapSource（ImagePrep の縮小・is_black 判定へ渡す形へ）。
    /// HBITMAP経由。GDIハンドルは必ず DeleteObject で返す（リーク防止）
    /// </summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    /// <summary>PNGバイト列（送信用base64やraw保存は呼び手が使う。7.3「PNG化はバックグラウンド」の部品）</summary>
    public static byte[] PngBytes(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

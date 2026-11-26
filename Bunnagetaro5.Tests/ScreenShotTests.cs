using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 手順12: 撮影の下支え（7.3）。画面そのものを見る検査は _check\shot9 で実施
/// （PROGRESS参照）。ここでは変換・PNG・最小サイズの純粋部分を検証する。
/// </summary>
public class ScreenShotTests
{
    /// <summary>BitmapSourceの画素を (B,G,R,A) の並びで読む（手順10確立の BackBuffer 直読し）</summary>
    private static byte[] ReadBgra(BitmapSource src)
    {
        var wb = new WriteableBitmap(src);
        int stride = wb.PixelWidth * 4;
        var buf = new byte[stride * wb.PixelHeight];
        wb.Lock();
        try { Marshal.Copy(wb.BackBuffer, buf, 0, buf.Length); }
        finally { wb.Unlock(); }
        return buf;
    }

    [Fact]
    public void BitmapからBitmapSourceへの変換で画素が変わらない()
    {
        using var bmp = new Bitmap(3, 2, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 200, 10, 5));
        bmp.SetPixel(2, 1, Color.FromArgb(255, 1, 2, 3));

        var src = ScreenShot.ToBitmapSource(bmp);
        Assert.Equal(3, src.PixelWidth);
        Assert.Equal(2, src.PixelHeight);
        Assert.True(src.CanFreeze);

        var px = ReadBgra(src);
        Assert.Equal((byte)5, px[0]);   // B
        Assert.Equal((byte)10, px[1]);  // G
        Assert.Equal((byte)200, px[2]); // R
        Assert.Equal((byte)255, px[3]); // A
        int off = (1 * 3 + 2) * 4;      // (x=2,y=1)
        Assert.Equal((byte)3, px[off]);
        Assert.Equal((byte)2, px[off + 1]);
        Assert.Equal((byte)1, px[off + 2]);
    }

    [Fact]
    public void PNGバイト列は画素を保つ()
    {
        using var bmp = new Bitmap(4, 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                bmp.SetPixel(x, y, x == 0 ? Color.Black : Color.White);

        var bytes = ScreenShot.PngBytes(ScreenShot.ToBitmapSource(bmp));
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);   // PNGシグネチャ
        Assert.Equal((byte)'P', bytes[1]);

        using var ms = new MemoryStream(bytes);
        var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat,
                                       BitmapCacheOption.OnLoad);
        var px = ReadBgra(dec.Frames[0]);
        Assert.Equal((byte)0, px[0]);     // (0,0) 黒
        Assert.Equal((byte)255, px[4]);   // (1,0) 白
        Assert.Equal((byte)255, px[px.Length - 4]); // 最終画素 白
    }

    [Fact]
    public void 画面撮影は頼んだ大きさの画像を返す()
    {
        // 内容は环境依存（デスクトップ）なので、APIが生きていて
        // 正しい寸法の Bitmap が返ることだけ確かめる
        using var bmp = ScreenShot.GrabRegion(0, 0, 24, 18);
        Assert.Equal(24, bmp.Width);
        Assert.Equal(18, bmp.Height);
    }

    [Fact]
    public void 幅0や高さ0でも画像は作れる()
    {
        using var bmp = ScreenShot.GrabRegion(0, 0, 0, -5);
        Assert.Equal(1, bmp.Width);
        Assert.Equal(1, bmp.Height);
    }

    [Fact]
    public void WaitScreenSettledは投げずに返る()
    {
        // 待ちの長さは环境（合成の間隔）しだい。例外を上げず制御が戻ることだけ
        Native.WaitScreenSettled(2);
        Native.WaitScreenSettled(0);   // frames<1 は1回扱い（Python版 max(1,frames)）
    }
}

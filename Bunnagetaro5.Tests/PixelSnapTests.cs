using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>式の画像を整数の物理pxへ寄せる計算</summary>
public class PixelSnapTests
{
    [Theory]
    [InlineData(1.0, 12.37, -0.37)]
    [InlineData(1.0, 12.5, 0.5)]
    [InlineData(1.0, 7.0, 0.0)]
    [InlineData(1.25, 10.0, 0.0)]            // 12.5px → 13px: +0.5px = +0.4 DIP
    [InlineData(1.5, 10.1, 0.0)]
    public void 端数を打ち消す量は物理pxで整数へ寄せる(double dpi, double posDip, double _)
    {
        double off = PixelSnap.OffsetDip(posDip, dpi);
        double phys = (posDip + off) * dpi;
        Assert.Equal(System.Math.Round(phys), phys, 6);
        Assert.True(System.Math.Abs(off * dpi) <= 0.5 + 1e-9);
    }

    [Fact]
    public void 具体例_100と125と150パーセント()
    {
        Assert.Equal(-0.37, PixelSnap.OffsetDip(12.37, 1.0), 6);
        Assert.Equal(0.4, PixelSnap.OffsetDip(10.0, 1.25), 6);       // 12.5px → 13px（0.5px = 0.4DIP）
        Assert.Equal(0.0, PixelSnap.OffsetDip(10.0, 1.5), 6);   // 15px は動かさない
        Assert.Equal(-0.1, PixelSnap.OffsetDip(10.1, 1.5), 6);  // 15.15px → 15px
    }

    [Fact]
    public void 等倍の時だけ混ぜない描き方になる判定()
    {
        Assert.True(PixelSnap.IsOneToOne(45, 45.0, 1.0));
        Assert.True(PixelSnap.IsOneToOne(75, 60.0, 1.25));
        Assert.False(PixelSnap.IsOneToOne(45, 45.0, 1.25));
    }
}

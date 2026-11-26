using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>WM_WINDOWPOSCHANGING の「これから行く矩形」の取り出し</summary>
public class WindowPosChangingTests
{
    private static readonly Native.RECT Cur = new() { Left = 100, Top = 200, Right = 500, Bottom = 400 };

    private static Native.WINDOWPOS Wp(int x, int y, int cx, int cy, uint flags)
        => new() { x = x, y = y, cx = cx, cy = cy, flags = flags };

    [Fact]
    public void 移動とサイズの両方が指定なら新しい矩形()
    {
        var r = Native.TargetRect(Wp(10, 20, 300, 100, 0), Cur)!.Value;
        Assert.Equal((10, 20, 310, 120), (r.Left, r.Top, r.Right, r.Bottom));
    }

    [Fact]
    public void NOMOVEなら位置は今のまま_NOSIZEなら大きさは今のまま()
    {
        var a = Native.TargetRect(Wp(0, 0, 300, 100, 0x0002), Cur)!.Value;
        Assert.Equal((100, 200, 400, 300), (a.Left, a.Top, a.Right, a.Bottom));
        var b = Native.TargetRect(Wp(10, 20, 0, 0, 0x0001), Cur)!.Value;
        Assert.Equal((10, 20, 410, 220), (b.Left, b.Top, b.Right, b.Bottom));
    }

    [Fact]
    public void 何も変わらない_最小化の退避座標_極小は無視()
    {
        Assert.Null(Native.TargetRect(Wp(0, 0, 0, 0, 0x0003), Cur));
        Assert.Null(Native.TargetRect(Wp(-32000, -32000, 160, 28, 0), Cur));
        Assert.Null(Native.TargetRect(Wp(0, 0, 5, 5, 0), Cur));
    }
}

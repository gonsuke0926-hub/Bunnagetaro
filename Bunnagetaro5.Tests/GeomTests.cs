using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>Geom: NormRect / ClampToVirtual の動作確認</summary>
public class GeomTests
{
    [Fact]
    public void NormRectは正方向のドラッグをそのまま矩形へ()
    {
        Assert.Equal((10, 20, 100, 50), Geom.NormRect(10, 20, 110, 70));
    }

    [Fact]
    public void NormRectは逆方向ドラッグも吸収()
    {
        Assert.Equal((10, 20, 100, 50), Geom.NormRect(110, 70, 10, 20));
    }

    [Fact]
    public void NormRectは斜め四方八方の混合も吸収()
    {
        // x は逆向き、y は向き → 左上は(min,min)
        Assert.Equal((5, 8, 40, 30), Geom.NormRect(45, 8, 5, 38));
    }

    [Fact]
    public void NormRectは零点を0x0の矩形へ()
    {
        Assert.Equal((7, 9, 0, 0), Geom.NormRect(7, 9, 7, 9));
    }

    [Fact]
    public void NormRectは負座標でも式が変わらない()
    {
        // 左に伸びる副モニタ想い
        Assert.Equal((-500, 10, 400, 200), Geom.NormRect(-100, 10, -500, 210));
    }

    [Fact]
    public void ClampToVirtualは内側なら何もしない()
    {
        Assert.Equal((100, 100, 640, 400),
                     Geom.ClampToVirtual(100, 100, 640, 400, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ClampToVirtualは右下へはみ出すと内側へ寄せる()
    {
        Assert.Equal((1280, 680, 640, 400),
                     Geom.ClampToVirtual(99999, 99999, 640, 400, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ClampToVirtualは左上の外も寄せる_副モニタの負原点で()
    {
        // 左に副モニタ（x が -1920 から）ある形
        Assert.Equal((-1920, 0, 640, 400),
                     Geom.ClampToVirtual(-99999, -99999, 640, 400, -1920, 0, 3840, 1080));
    }

    [Fact]
    public void ClampToVirtualは画面より大きい大きさを画面サイズまで詰める()
    {
        Assert.Equal((0, 0, 1920, 1080),
                     Geom.ClampToVirtual(100, 100, 5000, 5000, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ClampToVirtualはゼロサイズでも最低1pxは残す()
    {
        Assert.Equal((10, 20, 1, 1), Geom.ClampToVirtual(10, 20, 0, 0, 0, 0, 1920, 1080));
    }

    [Fact]
    public void ClampToVirtualは異常metricsでは何もせずに返す()
    {
        // GetSystemMetrics が 0 を返す環境での暴走防止（Python と同じ早帰り）
        Assert.Equal((50, 60, 640, 400), Geom.ClampToVirtual(50, 60, 640, 400, 0, 0, 0, 0));
    }

    [Fact]
    public void CursorPosは取れる方の実機呼びで非null()
    {
        // 実機ではカーソル位置は必ず取れる（取れなかったOS環境は放置しない）
        Assert.NotNull(Geom.CursorPos());
    }
}

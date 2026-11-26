using System;
using Bunnagetaro5.Input;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FrameSurface（外・枠・中の3色の四角を差分の帯だけ塗り直す部品）の正しさと、仕事量が辺の長さに比例すること</summary>
public class FrameSurfaceTests
{
    private const int SL = 100, ST = 50, SW = 400, SH = 300;

    private static uint Expected((int X, int Y, int W, int H)? r, FrameSurface.Style s, int x, int y)
    {
        if (r is not { } rc) return s.Outside;
        if (x < rc.X || x >= rc.X + rc.W || y < rc.Y || y >= rc.Y + rc.H) return s.Outside;
        int th = Math.Max(1, s.Thickness);
        if (rc.W <= 2 * th || rc.H <= 2 * th) return s.Border;
        if (x < rc.X + th || x >= rc.X + rc.W - th || y < rc.Y + th || y >= rc.Y + rc.H - th) return s.Border;
        return s.Inside;
    }

    private static void RunRandom(uint outside, uint inside, int seed)
    {
        using var fs = new FrameSurface();
        Assert.True(fs.Reset(SL, ST, SW, SH, outside));
        var rnd = new Random(seed);
        var style = new FrameSurface.Style(outside, inside, 0xFF0067C0, 3);
        uint[] borders = { 0xFF0067C0, 0xFFFF0000, 0x80004020 };
        for (int i = 0; i < 200; i++)
        {
            (int X, int Y, int W, int H)? rect;
            int kind = rnd.Next(10);
            if (kind == 0) rect = null;
            else if (kind <= 2) rect = (SL + rnd.Next(-20, SW), ST + rnd.Next(-20, SH), rnd.Next(1, 2 * style.Thickness + 1), rnd.Next(1, 120));   // 細い（W <= 2t）
            else if (kind == 3) rect = (SL + rnd.Next(-20, SW), ST + rnd.Next(-20, SH), rnd.Next(1, 120), rnd.Next(1, 2 * style.Thickness + 1));   // 低い（H <= 2t）
            else rect = (SL + rnd.Next(-150, SW + 50), ST + rnd.Next(-150, SH + 50), rnd.Next(1, 450), rnd.Next(1, 350));   // 画面の外にはみ出す
            if (rnd.Next(6) == 0)
                style = style with { Border = borders[rnd.Next(borders.Length)], Thickness = rnd.Next(1, 6) };
            // 中の色は固定（Outside は Reset 以外で変えられない）
            fs.Draw(IntPtr.Zero, rect, style, 255);
            for (int y = ST; y < ST + SH; y++)
                for (int x = SL; x < SL + SW; x++)
                {
                    uint want = Expected(rect, style, x, y);
                    uint got = fs.Sample(x, y);
                    if (want != got)
                        Assert.Fail($"step {i} rect={rect} style={style} pixel=({x},{y}) want={want:X8} got={got:X8}");
                }
        }
    }

    [Fact]
    public void J65_覆い向けの色でランダムに四角を動かしても全画素が期待どおり()
        => RunRandom(0x40000000, 0x01000000, 1234);

    [Fact]
    public void J65_ダミー枠向けの色でランダムに四角を動かしても全画素が期待どおり()
        => RunRandom(0, 0, 4321);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void J65_角を5px動かした時に塗る画素は面積よりずっと少ない(bool snipLike)
    {
        uint outside = snipLike ? 0x40000000u : 0u;
        uint inside = snipLike ? 0x01000000u : 0u;
        using var fs = new FrameSurface();
        Assert.True(fs.Reset(0, 0, 5120, 1440, outside));
        var style = new FrameSurface.Style(outside, inside, 0xFF0067C0, 3);
        fs.Draw(IntPtr.Zero, (200, 100, 4000, 1200), style, 255);
        fs.Draw(IntPtr.Zero, (200, 100, 4005, 1205), style, 255);
        long area = 4005L * 1205;
        Assert.True(fs.LastPaintedPx > 0);
        Assert.True(fs.LastPaintedPx < area / 20, $"painted {fs.LastPaintedPx} of area {area}");
        Assert.True(fs.LastBands <= 4);
        // 動かした後の見た目も正しい
        Assert.Equal(0xFF0067C0u, fs.Sample(200 + 4005 - 1, 600));
        Assert.Equal(inside, fs.Sample(3000, 600));
        Assert.Equal(outside, fs.Sample(200 + 4005 + 5, 600));
    }
}

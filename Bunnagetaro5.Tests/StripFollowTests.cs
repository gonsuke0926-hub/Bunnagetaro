using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND3 D1: 窓の矩形 → バーの予測矩形 → 帯の矩形（巡回経由と同じ値になる）</summary>
public class StripFollowTests
{
    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
    private static readonly Native.RECT Vd = R(0, 0, 1920, 1080);

    private static (Native.RECT? Bar, Native.RECT? Strip) Chain(Native.RECT wr, bool linked = true,
        (int, int)? pos = null, (int, int)? off = null)
    {
        var p = ChromeBar.PredictPos(wr, wr, 600, 40, pos, off, linked, Vd, null);
        Native.RECT? bar = p is { } q ? ChromeBar.RectAt(q.X, q.Y, 600, 40) : null;
        var strip = AnswerStrip.Pick(wr, wr, bar, Vd, 520, 140);
        return (bar, strip);
    }

    private static void Eq(Native.RECT? r, int l, int t, int rr, int b)
    {
        Assert.NotNull(r);
        Assert.Equal((l, t, rr, b), (r!.Value.Left, r.Value.Top, r.Value.Right, r.Value.Bottom));
    }

    [Fact]
    public void 窓の下にバーと帯が並ぶ()
    {
        var (bar, strip) = Chain(R(200, 200, 800, 600));
        Eq(bar, 200, 600, 800, 640);
        Eq(strip, 200, 644, 720, 784);
    }

    [Fact]
    public void 画面下端でもバーは窓の下に置き帯はバーの下に置く()
    {
        // F38: 窓の下端 1050（下端まで 30px >= 24px）→ バーの上辺 = 窓の下辺（1050）、帯の上辺 = バーの下辺 + 4（1094）
        var (bar, strip) = Chain(R(200, 700, 800, 1050));
        Eq(bar, 200, 1050, 800, 1090);
        Eq(strip, 200, 1094, 720, 1234);

        // 窓の下端が 1070（24px 未満）→ バーはハンドル 24px が見える 1056 で止まり、帯はその下（1100）
        (bar, strip) = Chain(R(200, 900, 800, 1070));
        Eq(bar, 200, 1056, 800, 1096);
        Eq(strip, 200, 1100, 720, 1240);
    }

    [Fact]
    public void 窓が画面いっぱいでもバーはハンドル最低限を残し帯はその下()
    {
        var wr = R(100, 20, 700, 1070);
        var (bar, strip) = Chain(wr);
        Eq(bar, 100, 1056, 700, 1096);
        Eq(strip, 100, 1100, 620, 1240);
    }

    [Fact]
    public void 窓を動かすたびに帯はバーの予測矩形に貼り付く()
    {
        for (int x = 0; x <= 1300; x += 100)
        {
            var wr = R(x, 300, x + 600, 700);
            var (bar, strip) = Chain(wr);
            Assert.NotNull(bar);
            Assert.NotNull(strip);
            Assert.Equal(bar!.Value.Bottom + 4, strip!.Value.Top);
        }
    }
}

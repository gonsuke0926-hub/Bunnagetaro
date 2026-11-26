using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>F7: 思考欄の更新は 200ms に1回だけ画面へ出す（4000字の入れ直しのレイアウトが1回 25〜45ms のため）</summary>
public class ThinkThrottleTests
{
    [Fact]
    public void 最初は出し_200ms未満は出さず_200ms以上で出す()
    {
        long last = long.MinValue;
        Assert.True(ThrowController.ShouldPushThink(1000, ref last));      // 最初の1回は必ず出す
        Assert.Equal(1000, last);
        Assert.False(ThrowController.ShouldPushThink(1040, ref last));     // 本文と同じ40ms刻みでは出さない
        Assert.False(ThrowController.ShouldPushThink(1199, ref last));
        Assert.Equal(1000, last);                                          // 出さなかった時は前回時刻を進めない
        Assert.True(ThrowController.ShouldPushThink(1200, ref last));
        Assert.Equal(1200, last);
        Assert.Equal(200, ThrowController.ThinkPushMinMs);
    }

    [Fact]
    public void 四十msごとに五秒流しても出すのは25回前後()
    {
        long last = long.MinValue;
        int pushed = 0;
        for (long t = 0; t < 5000; t += 40)
            if (ThrowController.ShouldPushThink(t, ref last)) pushed++;
        Assert.InRange(pushed, 23, 26);   // 以前は125回
    }
}

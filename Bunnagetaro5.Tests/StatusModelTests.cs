using Bunnagetaro5.Status;
using Xunit;

namespace Bunnagetaro5.Tests;

public sealed class StatusModelTests
{
    [Fact]
    public void ぶん投げ中は点が進み_思考中の印も出る()
    {
        var m = new StatusModel { Busy = true };
        Assert.Equal("ぶん投げ中.", m.Tick());
        m.Thinking = true;
        Assert.Equal("思考中..", m.Tick());
    }

    [Fact]
    public void 前提の理由は待機中より優先_温め中はその次()
    {
        var m = new StatusModel { EnvProblem = "モデル未導入", WarmRunning = true };
        Assert.Equal("モデル未導入", m.Tick());
        m.EnvProblem = "";
        Assert.Equal("AIモデル準備中", m.Tick());
    }

    [Fact]
    public void 前提の理由は自動トリガの巡回が進んでも点が動かない()
    {
        foreach (var why in new[] { "vision非対応", "AIサーバー未起動", "モデル未導入", "モデル未選択" })
        {
            var m = new StatusModel { EnvProblem = why, TriggerEnabled = () => true, TrigDetect = true };
            for (int i = 0; i < 8; i++)
            {
                m.TrigTicks = i;
                Assert.Equal(why, m.Tick());
            }
        }
    }

    [Fact]
    public void 一言は12秒以内ならそのまま出る_既定は待機中()
    {
        var m = new StatusModel();
        m.Set("キャプチャ窓を開きました");
        Assert.Equal("キャプチャ窓を開きました", m.Tick());
        m.TriggerEnabled = () => true;
        m.TrigDetect = true;
        Assert.Equal("動作検知中", m.Tick());
    }
}

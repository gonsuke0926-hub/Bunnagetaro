using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>H2: 欄の幅に収まらない式だけ縦横比を保って縮める</summary>
public class MathFitTests
{
    [Fact]
    public void 狭い幅で広い式は幅に収まり縦横比が同じ()
    {
        var (w, h) = AnswerView.FitToWidth(600, 40, 300);
        Assert.Equal(300, w, 6);
        Assert.Equal(600.0 / 40, w / h, 6);
    }

    [Theory]
    [InlineData(200, 30, 300)]
    [InlineData(300, 30, 300)]
    [InlineData(500, 30, 0)]    // 幅が未定なら縮めない
    public void 収まる式や幅未定は大きさが変わらない(double w0, double h0, double avail)
    {
        var (w, h) = AnswerView.FitToWidth(w0, h0, avail);
        Assert.Equal(w0, w);
        Assert.Equal(h0, h);
    }
}

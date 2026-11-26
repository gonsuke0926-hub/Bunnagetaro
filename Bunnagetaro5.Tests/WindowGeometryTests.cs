using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>メイン窓の位置・大きさの保存と復元</summary>
public class WindowGeometryTests
{
    [Fact]
    public void ParsesTkStyleGeometry()
    {
        Assert.True(WindowGeometry.TryParse("940x660+100+50", out var g));
        Assert.Equal(940, g.W);
        Assert.Equal(660, g.H);
        Assert.Equal(100, g.X);
        Assert.Equal(50, g.Y);
    }

    [Fact]
    public void ParsesNegativeOffsets()
    {
        // tk は左にはみ出すと "+-8" の形にする
        Assert.True(WindowGeometry.TryParse("940x660+-8+-12", out var g));
        Assert.Equal(-8, g.X);
        Assert.Equal(-12, g.Y);
    }

    [Fact]
    public void ClampsToMinSize()
    {
        Assert.True(WindowGeometry.TryParse("300x200+10+10", out var g));
        Assert.Equal(WindowGeometry.MinW, g.W);
        Assert.Equal(WindowGeometry.MinH, g.H);
    }

    [Theory]
    [InlineData("")]
    [InlineData("940x660")]           // 位置無しは復元しない（tkは常に出す形）
    [InlineData("abc")]
    [InlineData("940x660+100")]
    [InlineData("99999999999999x660+1+1")]
    public void RejectsMalformedGeometry(string geo)
    {
        Assert.False(WindowGeometry.TryParse(geo, out _));
    }

    [Fact]
    public void FormatRoundTrip()
    {
        var s = WindowGeometry.Format(-8, 50, 940, 660);
        Assert.Equal("940x660+-8+50", s);
        Assert.True(WindowGeometry.TryParse(s, out var g));
        Assert.Equal(-8, g.X);
    }

    [Fact]
    public void OnMonitorAcceptsVisiblePlace()
    {
        // 開発機の主モニタの左上付近は必ずモニタの上
        Assert.True(WindowGeometry.OnMonitor(100, 100, 940, 660));
    }

    [Fact]
    public void OnMonitorRejectsFarOffscreen()
    {
        // モニタを外した後に残ったはずれ位置は false（位置はOS任せへ）
        Assert.False(WindowGeometry.OnMonitor(10_000_000, 10_000_000, 940, 660));
    }

    [Fact]
    public void OnMonitorRejectsBadSize()
    {
        Assert.False(WindowGeometry.OnMonitor(100, 100, 0, 660));
        Assert.False(WindowGeometry.OnMonitor(100, 100, 940, -5));
    }
}

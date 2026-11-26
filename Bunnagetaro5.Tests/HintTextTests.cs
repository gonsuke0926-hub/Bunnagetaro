using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>窓を開いた直後の案内: クイックぶん投げが OFF（既定）ならボタンだけ、ON なら押すボタンも案内する</summary>
public class HintTextTests
{
    [Fact]
    public void クイックOFFならぶん投げるボタンだけを案内する()
    {
        string t = CaptureSession.HintTextFor(false, "wheel");
        Assert.Equal(CaptureSession.HintText, t);
        Assert.DoesNotContain("ドラッグ", t);
        Assert.Equal(CaptureSession.HintText, CaptureSession.HintTextFor(true, "off"));
    }

    [Theory]
    [InlineData("wheel", "ホイール")]
    [InlineData("back", "「戻る」")]
    [InlineData("forward", "「進む」")]
    [InlineData("both", "「戻る」か「進む」")]
    public void クイックONなら押したままドラッグするボタンを案内する(string button, string expect)
    {
        string t = CaptureSession.HintTextFor(true, button);
        Assert.Contains("「ぶん投げる」ボタン", t);
        Assert.Contains(expect, t);
        Assert.Contains("押したままドラッグ", t);
    }
}

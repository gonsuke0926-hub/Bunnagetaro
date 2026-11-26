using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// バーの置き場所の計算。単位は物理px。
/// </summary>
public class ChromePlaceTests
{
    private static Native.RECT R(int l, int t, int r, int b) =>
        new() { Left = l, Top = t, Right = r, Bottom = b };

    // 1920x1080 の一枚画面。窓は (100,100)-(740,500)、内側は 8px 絞った枠
    private static readonly Native.RECT Vd = R(0, 0, 1920, 1080);
    private const int WinL = 100, WinT = 100, WinR = 740, WinB = 500;
    private static readonly Native.RECT Client = R(108, 108, 732, 492);
    private const int BarW = 300, BarH = 36;

    [Fact]
    public void 重なり判定は触れるだけだと重ならない()
    {
        Assert.True(ChromePlace.Intersects(0, 0, 10, 10, 5, 5, 15, 15));
        Assert.False(ChromePlace.Intersects(0, 0, 10, 10, 10, 0, 20, 10));  // 右辺が触れるだけ
        Assert.False(ChromePlace.Intersects(0, 0, 10, 10, 0, 10, 10, 20));  // 下辺が触れるだけ
    }

    [Fact]
    public void 収まり判定は窓の内側と画面外を弾く()
    {
        Assert.True(ChromePlace.Fits(108, 500, BarW, BarH, Vd, Client));     // 窓の下
        Assert.False(ChromePlace.Fits(300, 300, BarW, BarH, Vd, Client));    // 窓の内側
        Assert.False(ChromePlace.Fits(1900, 500, BarW, BarH, Vd, Client));   // 画面からはみ出す
        Assert.False(ChromePlace.Fits(-1, 500, BarW, BarH, Vd, Client));     // 左も同様
    }

    [Fact]
    public void 連結の既定はclient左端基準で窓の下に置く()
    {
        var pick = ChromePlace.Pick(WinL, WinT, WinR, WinB, Client, BarW, BarH,
            null, null, true, Vd);
        Assert.Equal((108, 500), pick!.Value);
    }

    [Fact]
    public void 窓の下に余白が無い時も窓の下に置き_全部出る時だけハンドル最低限を残す()
    {
        // 窓の下辺が 1050（下端 1080 まで 30px >= 24px）→ 窓の下（1050）に置き、下へはみ出す
        var pick = ChromePlace.Pick(100, 650, 740, 1050, R(108, 658, 732, 1042),
            BarW, BarH, null, null, true, Vd);
        Assert.Equal((108, 1050), pick!.Value);

        // 全部が画面の外へ出る時（winB=1400）はハンドルが最低限（MinVisibleHandle=24px）見える位置（1080-24=1056）で止める
        pick = ChromePlace.Pick(100, 1000, 740, 1400, R(108, 1008, 732, 1392),
            BarW, BarH, null, null, true, Vd);
        Assert.Equal((108, 1080 - ChromePlace.MinVisibleHandle), pick!.Value);
        // 分離モードの記憶位置が外れた時も窓の上へは行かない
        pick = ChromePlace.Pick(100, 1000, 740, 1400, R(108, 1008, 732, 1392),
            BarW, BarH, (1900, 2000), null, false, Vd);
        Assert.Equal((108, 1080 - ChromePlace.MinVisibleHandle), pick!.Value);
    }

    [Fact]
    public void 分離モードは記憶位置をまず守る()
    {
        var pick = ChromePlace.Pick(WinL, WinT, WinR, WinB, Client, BarW, BarH,
            (1500, 640), null, false, Vd);
        Assert.Equal((1500, 640), pick!.Value);
    }

    [Fact]
    public void 記憶位置が外れてもオフセット候補を次に試す()
    {
        // pos は画面右端外で全候補外れ（この画面は幅1300）。オフセットは窓の下側に収まる
        var pick = ChromePlace.Pick(WinL, WinT, WinR, WinB, Client, BarW, BarH,
            (1250, 300), (0, 460), false, R(0, 0, 1300, 1080));
        Assert.Equal((100, 560), pick!.Value);   // 窓＋オフセット
    }

    [Fact]
    public void バー単独ドラッグ中は狙った場所を最優先する()
    {
        var pick = ChromePlace.Pick(WinL, WinT, WinR, WinB, Client, BarW, BarH,
            (1500, 640), (0, 460), true, Vd, (800, 620));
        Assert.Equal((800, 620), pick!.Value);
        // 窓の内側に突っ込んだドラッグ位置は使わず既定経路へ落ちる
        pick = ChromePlace.Pick(WinL, WinT, WinR, WinB, Client, BarW, BarH,
            null, null, true, Vd, (300, 300));
        Assert.Equal((108, 500), pick!.Value);
    }

    [Fact]
    public void 窓が全画面でも隠さずハンドル最低限が見える位置へ置く()
    {
        // 窓が全画面を塞いでいる（client が仮想デスクトップ一杯）。ハンドルが最低限（24px）見える位置へ
        var pick = ChromePlace.Pick(0, 0, 1920, 1080, R(0, 0, 1920, 1080),
            BarW, BarH, (50, 50), (0, 0), false, Vd);
        Assert.Equal((0, 1080 - ChromePlace.MinVisibleHandle), pick!.Value);
    }
}

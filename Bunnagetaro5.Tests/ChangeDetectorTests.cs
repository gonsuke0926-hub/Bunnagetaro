using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>変化検知の純粋関数</summary>
public class ChangeDetectorTests
{
    private static ChangeDetector.Frame Solid(int w, int h, byte gray)
    {
        var b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            b[i * 4] = gray; b[i * 4 + 1] = gray; b[i * 4 + 2] = gray; b[i * 4 + 3] = 255;
        }
        return new(b, w, h);
    }

    /// <summary>左半分だけ別の明るさへ変えた枠（境界1マスは無視できる大きなブロック）</summary>
    private static ChangeDetector.Frame Split(int w, int h, byte left, byte right)
    {
        var b = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = x < w / 2 ? left : right;
                int i = (y * w + x) * 4;
                b[i] = v; b[i + 1] = v; b[i + 2] = v; b[i + 3] = 255;
            }
        return new(b, w, h);
    }

    [Fact]
    public void 同じ枠なら変化ゼロ()
    {
        var a = ChangeDetector.Fingerprint(Solid(120, 60, 90));
        var b = ChangeDetector.Fingerprint(Solid(120, 60, 90));
        Assert.Equal(a.Length, ChangeDetector.FpW * ChangeDetector.FpH);
        Assert.Equal(0.0, ChangeDetector.ChangeRatio(a, b));
    }

    [Fact]
    public void 半分変われば割合は半分弱()
    {
        var a = ChangeDetector.Fingerprint(Split(140, 70, 20, 200));
        var b = ChangeDetector.Fingerprint(Solid(140, 70, 20));
        double r = ChangeDetector.ChangeRatio(a, b);
        Assert.InRange(r, 0.40, 0.55);   // 境界マスのにじみでちょうど半分にはならない
    }

    [Fact]
    public void 明るさ差がdelta以下なら変わらない()
    {
        var a = ChangeDetector.Fingerprint(Solid(100, 60, 100));
        var b = ChangeDetector.Fingerprint(Solid(100, 60, 130));   // 差30 ≤ 40
        Assert.Equal(0.0, ChangeDetector.ChangeRatio(a, b));
        // 同じ差でも delta を下げれば変わる（obs_delta補正の理屈の確認。移植では40固定）
        Assert.Equal(1.0, ChangeDetector.ChangeRatio(a, b, 20));
    }

    [Fact]
    public void 明るさ差がdeltaを超えれば全部変わった()
    {
        var a = ChangeDetector.Fingerprint(Solid(100, 60, 100));
        var b = ChangeDetector.Fingerprint(Solid(100, 60, 150));   // 差50 > 40
        Assert.Equal(1.0, ChangeDetector.ChangeRatio(a, b));
    }

    [Fact]
    public void 一点だけの変更は割合ではごく小さく誤差扱い()
    {
        var f = Solid(96, 54, 100);
        for (int i = 0; i < 4; i++) f.Bgra[i] = 250;   // 左上1ピクセルだけ明るく
        var a = ChangeDetector.Fingerprint(Solid(96, 54, 100));
        var b = ChangeDetector.Fingerprint(f);
        double r = ChangeDetector.ChangeRatio(a, b);
        Assert.True(r > 0 && r < 0.01, "既定の trig_change_ratio=0.01 より小さいはず");
    }

    [Fact]
    public void 色はPILのLと同じ明るさで灰度になる()
    {
        // 純色: PIL L = 299R+587G+114B（/1000 四捨五入）
        var red = Solid(120, 60, 0);
        for (int i = 0; i < 120 * 60; i++) { red.Bgra[i * 4] = 0; red.Bgra[i * 4 + 1] = 0; red.Bgra[i * 4 + 2] = 255; }
        var fp = ChangeDetector.Fingerprint(red);
        Assert.All(fp, v => Assert.Equal(76, v));      // 255*299/1000 = 76.2 → 76

        var green = Solid(120, 60, 0);
        for (int i = 0; i < 120 * 60; i++) { green.Bgra[i * 4] = 0; green.Bgra[i * 4 + 1] = 255; green.Bgra[i * 4 + 2] = 0; }
        Assert.All(ChangeDetector.Fingerprint(green), v => Assert.Equal(150, v));   // 149.7 → 150

        var blue = Solid(120, 60, 0);
        for (int i = 0; i < 120 * 60; i++) { blue.Bgra[i * 4] = 255; blue.Bgra[i * 4 + 1] = 0; blue.Bgra[i * 4 + 2] = 0; }
        Assert.All(ChangeDetector.Fingerprint(blue), v => Assert.Equal(29, v));     // 29.1 → 29
    }

    [Fact]
    public void 縮小でも小さい要素を取りこぼさない()
    {
        // 96x54の1/4枠に縦ジマ（幅2pxの明暗）。指紋5184マスにジマが乗った跡が残る
        var f = Solid(192, 108, 30);
        for (int y = 0; y < 108; y++)
            for (int x = 40; x < 60; x += 4)          // 2列明るいを交互に
                for (int dx = 0; dx < 2; dx++)
                {
                    int i = ((y * 192) + (x + dx)) * 4;
                    f.Bgra[i] = f.Bgra[i + 1] = f.Bgra[i + 2] = 240;
                }
        var flat = ChangeDetector.Fingerprint(Solid(192, 108, 30));
        var stripes = ChangeDetector.Fingerprint(f);
        Assert.True(ChangeDetector.ChangeRatio(flat, stripes) > 0.0, "ジマ部分が变化として立つか");
    }

    [Fact]
    public void 黒枠判定はPILの明るさ最大10未満と同じ()
    {
        Assert.True(ChangeDetector.IsBlackFrame(Solid(40, 20, 0)));
        Assert.True(ChangeDetector.IsBlackFrame(Solid(40, 20, 9)));
        // ほぼ黒でも明るさ10以上が1ピクセルあれば黒ではない
        var f = Solid(40, 20, 0);
        f.Bgra[7 * 4 + 2] = 255;
        Assert.False(ChangeDetector.IsBlackFrame(f));
        Assert.False(ChangeDetector.IsBlackFrame(Solid(40, 20, 120)));
    }
}

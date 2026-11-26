using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 解答欄 outside の帯の置き場所の計算と、縁リサイズの HitTest。単位は物理px。
/// </summary>
public class AnswerStripTests
{
    private static Native.RECT R(int l, int t, int r, int b) =>
        new() { Left = l, Top = t, Right = r, Bottom = b };

    // 1920x1080 の一枚画面。窓は (100,100)-(740,500)、内側は 8px 絞った枠
    private static readonly Native.RECT Vd = R(0, 0, 1920, 1080);
    private static readonly Native.RECT Win = R(100, 100, 740, 500);
    private static readonly Native.RECT Client = R(108, 108, 732, 492);
    private const int Sw = 520, Sh = 140;

    private static Native.RECT BarBelow() => R(108, 504, 408, 540);   // 窓の下に出る既定のバー
    private static Native.RECT BarAbove() => R(108, 60, 408, 96);     // 窓の上に出るバー

    [Fact]
    public void J78_大きさを変える時は前と今の中身の重なりを画面の同じ場所に残す()
    {
        static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
        // 左の縁で 100px 広げる: 残すのは前の中身の所（右へずらして写さない）
        var k = AnswerStrip.KeepRect(R(200, 600, 720, 740), R(100, 600, 720, 740))!.Value;
        Assert.Equal((200, 600, 720, 740), (k.Left, k.Top, k.Right, k.Bottom));
        // 上の縁で 50px 縮める: 残すのは今の中身の所
        var k2 = AnswerStrip.KeepRect(R(200, 600, 720, 740), R(200, 650, 720, 740))!.Value;
        Assert.Equal((200, 650, 720, 740), (k2.Left, k2.Top, k2.Right, k2.Bottom));
        // 重ならなければ残さない
        Assert.Null(AnswerStrip.KeepRect(R(0, 0, 10, 10), R(20, 20, 30, 30)));
    }

    [Fact]
    public void バーが窓の下なら帯はバーの下にバー左端基準で置く()
    {
        var p = AnswerStrip.Pick(Win, Client, BarBelow(), Vd, Sw, Sh);
        Assert.NotNull(p);
        Assert.Equal(new Native.RECT { Left = 108, Top = 544, Right = 628, Bottom = 684 }, p!.Value);
    }

    [Fact]
    public void 帯の左端はいつもバーの左端に揃う_横にずれない()
    {
        var p = AnswerStrip.Pick(Win, Client, BarBelow(), Vd, Sw, Sh);
        Assert.Equal(108, p!.Value.Left);   // バーの左端（strip_offset_x は使わない）
        Assert.Equal(544, p.Value.Top);
        // バーがずれれば帯もそのまま付いてくる
        var p2 = AnswerStrip.Pick(Win, Client, R(300, 504, 600, 540), Vd, Sw, Sh);
        Assert.Equal(300, p2!.Value.Left);
        // 幅を変えても（リサイズ後でも）左端は同じ
        Assert.Equal(300, AnswerStrip.Pick(Win, Client, R(300, 504, 600, 540), Vd, 300, 200)!.Value.Left);
    }

    [Fact]
    public void バーが右端に寄っていても画面右端から帯だけはみ出さない()
    {
        var p = AnswerStrip.Pick(Win, Client, R(1700, 504, 2000, 540), Vd, Sw, Sh);
        Assert.Equal(1920 - Sw, p!.Value.Left);
    }

    [Fact]
    public void バーが窓の上でも外の解答欄はバーの下に出る()
    {
        var p = AnswerStrip.Pick(Win, Client, BarAbove(), Vd, Sw, Sh);
        Assert.NotNull(p);
        Assert.Equal(96 + 4, p!.Value.Top);   // バーの下4pxすきあい
        Assert.Equal(108, p.Value.Left);
    }

    [Fact]
    public void バーが無い時は窓の下にクライアント左端基準で置く()
    {
        var p = AnswerStrip.Pick(Win, Client, null, Vd, Sw, Sh);
        Assert.Equal(new Native.RECT { Left = 108, Top = 504, Right = 628, Bottom = 644 }, p!.Value);
    }

    [Fact]
    public void バー無しで下が収まらなくても窓の下に置き画面外へはみ出す()
    {
        // 画面の下端が窓の直近でも、窓と重ねず窓の下に置いて画面の下へ切る
        var vd = R(0, 0, 1920, 520);
        var p = AnswerStrip.Pick(Win, Client, null, vd, Sw, Sh);
        Assert.Equal(Win.Bottom + 4, p!.Value.Top);
        Assert.Equal(Win.Bottom + 4 + Sh, p.Value.Bottom);
    }

    [Fact]
    public void バー付きで収まらなくてもバーの下に置き画面外へはみ出す()
    {
        // バーが窓の下に出ている状態で画面下端が近くても、バーの下に置き画面の下へ切る
        var bar = BarBelow();
        var vd = R(0, 0, 1920, 570);
        var p = AnswerStrip.Pick(Win, Client, bar, vd, Sw, Sh);
        Assert.Equal(bar.Bottom + 4, p!.Value.Top);
        Assert.True(p.Value.Top >= bar.Bottom);       // バーとも窓とも重ならない
    }

    [Fact]
    public void バーの上が画面並みに低くても下に置く()
    {
        var win = R(100, 10, 740, 100);
        var client = R(108, 18, 732, 92);
        var bar = R(108, 104, 408, 140);
        var vd = R(0, 0, 1920, 160);
        var p = AnswerStrip.Pick(win, client, bar, vd, Sw, Sh);
        Assert.Equal(bar.Bottom + 4, p!.Value.Top);
    }

    [Fact]
    public void バー無しで窓の下に全く入らなくても窓の下に置く()
    {
        var win = R(100, 10, 740, 400);
        var client = R(108, 18, 732, 392);
        var vd = R(0, 0, 1920, 430);
        var p = AnswerStrip.Pick(win, client, null, vd, Sw, Sh);
        Assert.Equal(win.Bottom + 4, p!.Value.Top);
    }

    [Fact]
    public void 最小寸法260x100未満は引き上げる()
    {
        var p = AnswerStrip.Pick(Win, Client, BarBelow(), Vd, 10, 10);
        Assert.Equal(AnswerStrip.MinW, p!.Value.Right - p.Value.Left);
        Assert.Equal(AnswerStrip.MinH, p.Value.Bottom - p.Value.Top);
    }

    [Fact]
    public void 最大は仮想画面ぶんまで()
    {
        var p = AnswerStrip.Pick(Win, Client, BarBelow(), Vd, 99999, 99999);
        Assert.Equal(1920, p!.Value.Right - p.Value.Left);
        Assert.Equal(1080, p.Value.Bottom - p.Value.Top);
    }

    // ---------------- 縁リサイズの当たり判定 ----------------

    [Fact]
    public void J79_四隅は右下だけ角になり_左上は縁ではなく_右上は右_左下は下()
    {
        Assert.Equal(0, AnswerStrip.HitTest(0, 0, 520, 140, 8));
        Assert.Equal(Native.HTRIGHT, AnswerStrip.HitTest(519, 0, 520, 140, 8));
        Assert.Equal(Native.HTBOTTOM, AnswerStrip.HitTest(0, 139, 520, 140, 8));
        Assert.Equal(Native.HTBOTTOMRIGHT, AnswerStrip.HitTest(519, 139, 520, 140, 8));
    }

    [Fact]
    public void J79_右と下の辺だけHTで_左と上の辺と中は0_HTCAPTIONも返さない()
    {
        // 帯の位置はバーが決めるので掴んで移動は無い（HTCAPTION=2 を返さないこと）
        Assert.Equal(0, AnswerStrip.HitTest(0, 70, 520, 140, 8));
        Assert.Equal(Native.HTRIGHT, AnswerStrip.HitTest(519, 70, 520, 140, 8));
        Assert.Equal(0, AnswerStrip.HitTest(260, 0, 520, 140, 8));
        Assert.Equal(Native.HTBOTTOM, AnswerStrip.HitTest(260, 139, 520, 140, 8));
        Assert.Equal(0, AnswerStrip.HitTest(260, 70, 520, 140, 8));
        Assert.Equal(0, AnswerStrip.HitTest(8, 70, 520, 140, 8));    // 帯の太さの外
        Assert.NotEqual(Native.HTCAPTION, AnswerStrip.HitTest(260, 0, 520, 140, 8));
    }
}

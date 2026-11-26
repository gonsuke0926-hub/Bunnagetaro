using System;
using System.IO;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 解答欄のスタイル値（色・透明度・大きさの正規化、薄い色の混色）とストリーミング差分表示の判定ロジック。
/// </summary>
public sealed class PanelStyleTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public PanelStyleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_pstyle_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>既定設定で読み、指定したキーだけ上書きした store</summary>
    private ConfigStore Cfg()
    {
        var store = new ConfigStore(_paths);
        store.Load();
        return store;
    }

    // ------------------------------------------------------------ 正規化

    [Fact]
    public void 既定設定なら既定の見た目()
    {
        var st = PanelStyle.From(Cfg());
        Assert.Equal("#f5f5f5", st.Fg);
        Assert.Equal("#202020", st.Bg);
        Assert.Equal(0.65, st.Alpha, 3);
        Assert.Equal(12, st.FontSize);
        Assert.Equal("", st.FontFamily);
    }

    [Fact]
    public void 大文字の十六進は小文字へ直して保つ()
    {
        var cfg = Cfg();
        cfg.SetStr("overlay_fg", "#F0F0A0");
        cfg.SetStr("overlay_bg", "#102030");
        var st = PanelStyle.From(cfg);
        Assert.Equal("#f0f0a0", st.Fg);
        Assert.Equal("#102030", st.Bg);
    }

    [Theory]
    [InlineData("not a color")]
    [InlineData("#12345")]
    [InlineData("ff0000")]
    [InlineData("#gg0000")]
    public void 読めない色は既定へ戻る(string bad)
    {
        var cfg = Cfg();
        cfg.SetStr("overlay_fg", bad);
        cfg.SetStr("overlay_bg", bad);
        var st = PanelStyle.From(cfg);
        Assert.Equal(PanelStyle.DefaultFg, st.Fg);
        Assert.Equal(PanelStyle.DefaultBg, st.Bg);
    }

    [Theory]
    [InlineData(0.02, 0.15)]   // 薄すぎは下限
    [InlineData(0.15, 0.15)]
    [InlineData(0.5, 0.5)]     // 普通はそのまま
    [InlineData(1.0, 1.0)]
    [InlineData(3.0, 1.0)]     // 濃すぎは上限
    public void 透明度は015から10の範囲に収める(double raw, double want)
    {
        var cfg = Cfg();
        cfg.SetDouble("overlay_alpha", raw);
        Assert.Equal(want, PanelStyle.From(cfg).Alpha, 3);
    }

    [Theory]
    [InlineData(4, 8)]      // 小さすぎは下限
    [InlineData(12, 12)]
    [InlineData(72, 72)]
    [InlineData(200, 72)]   // 大きすぎは上限
    public void 文字の大きさは8から72の範囲に収める(int raw, int want)
    {
        var cfg = Cfg();
        cfg.SetInt("overlay_font_size", raw);
        Assert.Equal(want, PanelStyle.From(cfg).FontSize);
    }

    // ------------------------------------------------------------ 薄い色

    [Fact]
    public void 既定の薄い色は文字色を背景へ45パーセント寄せた値()
    {
        // #f5f5f5 を #202020 へ 45%: 245*0.55+32*0.45=149.15→149=#95
        Assert.Equal("#959595", PanelStyle.MixToward("#f5f5f5", "#202020", 0.45));
        var st = PanelStyle.From(Cfg());
        Assert.Equal("#959595", st.ThinkHex);
        Assert.Equal("#959595", st.ScrollThumbHex);
    }

    [Fact]
    public void 混ぜ直してもThinkHexは毎回同じ値になる()
    {
        var st = PanelStyle.From(Cfg());
        string once = PanelStyle.MixToward(st.Fg, st.Bg, 0.45);
        string twice = PanelStyle.MixToward(once, st.Bg, 0.45);
        Assert.NotEqual(once, twice);          // 混ぜ直すと違う値（当然）
        Assert.Equal(once, st.ThinkHex);       // が、ThinkHexは毎回同じ作り直し
        Assert.Equal(once, st.ThinkHex);
    }

    [Fact]
    public void 読めない色は既定同士で混ぜる()
    {
        // aが読めない→(#f0f0f0,#202020)で45%: 240*0.55+32*0.45=146.4→146=#92
        Assert.Equal("#929292", PanelStyle.MixToward("zzz", "#202020", 0.45));
    }

    [Theory]
    [InlineData(-0.5, "#f5f5f5")]   // t負は0扱い＝aのまま
    [InlineData(0.0, "#f5f5f5")]
    [InlineData(1.0, "#202020")]
    [InlineData(2.0, "#202020")]    // t>1は1扱い＝bのまま
    public void 寄せ具合は0から1の外側ならクランプ(double t, string want)
    {
        Assert.Equal(want, PanelStyle.MixToward("#f5f5f5", "#202020", t));
    }

    // ------------------------------------------------------------ 差分表示

    [Fact]
    public void 同じ文頭で長ければ続き()
    {
        Assert.True(StreamText.IsContinuation("", "あ"));
        Assert.True(StreamText.IsContinuation("吾輩は", "吾輩は猫である"));
        Assert.False(StreamText.IsContinuation("吾輩は猫", "吾輩は犬"));  // 途中で食い違い
        Assert.False(StreamText.IsContinuation("吾輩は猫である", "吾輩は猫"));  // 短くなったら続きではない
        Assert.False(StreamText.IsContinuation("同じ", "同じ"));            // 更新なしはfalse
    }

    [Fact]
    public void 続きのときは末尾分だけ取り出す()
    {
        Assert.Equal("である", StreamText.Tail("吾輩は猫", "吾輩は猫である"));
        Assert.Equal("", StreamText.Tail("abc", "abc"));   // 単独では空（gateはIsContinuation）
    }

    [Fact]
    public void 思考は最後の4000字だけ残す()
    {
        Assert.Equal(4000, StreamText.ThinkMaxChars);
        string shortText = "短い思考";
        Assert.Equal(shortText, StreamText.ThinkTail(shortText));
        string longText = new string('あ', 5000);
        string tail = StreamText.ThinkTail(longText);
        Assert.Equal(4000, tail.Length);
        Assert.Equal(longText.Substring(1000), tail);   // 末尾ぶんが残る
    }
}

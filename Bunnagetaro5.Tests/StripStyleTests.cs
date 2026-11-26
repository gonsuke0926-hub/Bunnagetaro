using System.IO;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J101: 外窓（枠の外に出す解答欄）の色と不透明度は枠の中と別</summary>
public sealed class StripStyleTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public StripStyleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_strip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ConfigStore LoadWith(string? json)
    {
        Directory.CreateDirectory(_paths.Root);
        if (json is not null) File.WriteAllText(_paths.ConfigJson, json);
        var s = new ConfigStore(_paths);
        s.Load();
        return s;
    }

    [Fact]
    public void J101_初めての時は既定の色で不透明()
    {
        var st = PanelStyle.ForStrip(LoadWith(null));
        Assert.Equal(PanelStyle.DefaultBg, st.Bg);
        Assert.Equal(PanelStyle.DefaultFg, st.Fg);
        Assert.Equal(1.0, st.Alpha);
    }

    [Fact]
    public void J101_前の版の設定は枠の中の色を引き継ぐ_不透明のまま()
    {
        var s = LoadWith("{\"overlay_bg\":\"#112233\",\"overlay_fg\":\"#ddeeff\",\"overlay_alpha\":0.5}");
        Assert.Equal("#112233", s.Settings.StripBg);
        Assert.Equal("#ddeeff", s.Settings.StripFg);
        var st = PanelStyle.ForStrip(s);
        Assert.Equal(1.0, st.Alpha);
        Assert.Equal(0.5, PanelStyle.From(s).Alpha);   // 枠の中は今まで通り
    }

    [Fact]
    public void J101_外窓の色と不透明度は枠の中と別に効く_範囲の外は寄せる()
    {
        var s = LoadWith("{\"overlay_bg\":\"#112233\",\"strip_bg\":\"#AA0000\",\"strip_fg\":\"bad\",\"strip_alpha\":0.05}");
        var st = PanelStyle.ForStrip(s);
        Assert.Equal("#aa0000", st.Bg);
        Assert.Equal(PanelStyle.DefaultFg, st.Fg);
        Assert.Equal(0.15, st.Alpha);
        Assert.Equal("#112233", PanelStyle.From(s).Bg);
        s.Settings.StripAlpha = 0.7;
        Assert.Equal(0.7, PanelStyle.ForStrip(s).Alpha);
    }

    [Fact]
    public void J102_設定で外窓の色と不透明度を変えるとその場で外窓へ効く()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = LoadWith(null);
            var owner = new System.Windows.Window { Left = -10000, Width = 200, Height = 200, ShowInTaskbar = false };
            var session = new Bunnagetaro5.Session.CaptureSession(cfg, owner, new Bunnagetaro5.Status.StatusModel(), _ => { }, () => "");
            var strip = new AnswerStrip(cfg, session);
            typeof(Bunnagetaro5.Session.CaptureSession).GetField("_strip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(session, strip);
            System.Windows.Media.Color RootColor() =>
                ((System.Windows.Media.SolidColorBrush)((System.Windows.Controls.Border)typeof(Bunnagetaro5.Views.AnswerView)
                    .GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(strip.View)!).Background).Color;
            cfg.Settings.StripBg = "#aa0000";
            Assert.Equal(System.Windows.Media.Color.FromArgb(255, 0xaa, 0, 0), RootColor());
            cfg.Settings.StripAlpha = 0.5;
            Assert.Equal(128, RootColor().A);
            Assert.Equal(System.Windows.Media.Colors.Transparent, ((System.Windows.Media.SolidColorBrush)strip.Background).Color);
            cfg.Settings.OverlayBg = "#00aa00";   // 枠の中の色は外窓に効かない
            Assert.Equal(0xaa, RootColor().R);
            strip.Close();
            owner.Close();
        });
    }
}

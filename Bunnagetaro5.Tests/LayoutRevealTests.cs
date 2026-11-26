using System;
using System.IO;
using System.Reflection;
using System.Windows;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J112: 区画（マスク）が渡された時点で、文字を待たずに解答欄の窓を出す</summary>
public class LayoutRevealTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public LayoutRevealTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_layreveal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    private MainWindow Open(string place)
    {
        TestUtil.EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetBool("chrome_linked", true);
        cfg.SetBool("quick_enabled", false);
        CfgTestUtil.SetPlace(cfg, place);
        var w = new MainWindow(_paths, cfg);
        w.Show();
        w.Session.OpenCapture(animate: false);
        TestUtil.Pump(600);
        return w;
    }

    private static readonly OcrBlock[] Regions =
    {
        new("", 0.10, 0.10, 0.30, 0.10),
        new("", 0.50, 0.60, 0.40, 0.12),
    };

    [Fact]
    public void J112_窓の中_区画を渡すと文字の前に解答欄が出てマスクが見える_撮影中は黙る()
    {
        TestUtil.OnSta(() =>
        {
            var w = Open("inside");
            try
            {
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                ((IThrowOps)w.Session).BeginRun();   // ふつうの流れと同じく、ぶん投げの始まりから
                TestUtil.WaitUntil(() => !panel.IsVisible, 3000);
                Assert.False(panel.IsVisible);
                ((IThrowOps)w.Session).SetLayout(Regions, capturePending: true);
                TestUtil.Pump(300);
                Assert.False(panel.IsVisible);                      // 撮影中は出さない
                ((IThrowOps)w.Session).SetLayout(Regions, capturePending: false);
                TestUtil.WaitUntil(() => panel.IsVisible, 3000);
                Assert.True(panel.IsVisible);                       // 文字はまだ無いが窓は出る
                Assert.Equal("", panel.ModelText);
                Assert.True(panel.View.LayoutShown);                // マスクを見せている
                Assert.Equal(2, panel.View.LayoutMasks.Count);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J114_外の帯は位置配置せず読む順で上から並べる()
    {
        TestUtil.OnSta(() =>
        {
            var w = Open("outside");
            try
            {
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                ((IThrowOps)w.Session).BeginRun();
                ((IThrowOps)w.Session).SetLayout(Regions);
                TestUtil.Pump(200);
                Assert.False(strip.View.LayoutShown);
                ((IThrowOps)w.Session).Stream("", "いち\nに", capturePending: false);
                TestUtil.WaitUntil(() => strip.IsVisible, 3000);
                Assert.True(strip.IsVisible);
                Assert.False(strip.View.LayoutShown);
                Assert.Equal("いち\nに", strip.ModelText);
                Assert.Equal(1, strip.View.Body.Opacity);
            }
            finally { w.Close(); }
        });
    }
}

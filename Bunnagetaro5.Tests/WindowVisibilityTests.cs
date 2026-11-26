using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>WindowVisibility の状態管理と即座の出し入れを確かめる</summary>
public class WindowVisibilityTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public WindowVisibilityTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_vis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    [Fact]
    public void J90_閉じる途中と終わる途中で見回りが回っても外の解答欄とバーが出し直されない()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000), "バーが出ない");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");

                // 1) Closing 中に見回りを止めず TickInsurance が走っても出し直されない
                bool stripClosed = false;
                strip.Closed += (_, _) => stripClosed = true;
                double stripMin = 1.0, stripRise = 0.0;
                w.Session.CloseCaptureAll();
                Assert.Equal(WindowPhase.Closing, w.Session.VisibilityForTests.Phase);

                var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                tick.Tick += (_, _) =>
                {
                    if (stripClosed) { tick.Stop(); return; }
                    w.Session.PlacerForTests.TickInsurance();
                    double op = strip.Opacity;
                    if (op > stripMin + 1e-3) stripRise = Math.Max(stripRise, op - stripMin);
                    stripMin = Math.Min(stripMin, op);
                };
                tick.Start();
                Assert.True(TestUtil.WaitUntil(() => stripClosed && !bar.IsVisible, 3000), "閉じるが完了しない");
                tick.Stop();
                Assert.True(stripRise < 0.05, $"閉じる途中に外の解答欄が出し直された（+{stripRise:0.00}）");

                // 2) Quitting 中に見回りが回っても出し直されない
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000), "再オープンでバーが出ない");
                var strip2 = Get<AnswerStrip>(w.Session, "_strip");
                var bar2 = Get<ChromeBar>(w.Session, "_bar");
                strip2.ShowFinal("答え2");
                Assert.True(TestUtil.WaitUntil(() => strip2.IsVisible && !WindowMotion.IsRunning(strip2), 3000), "外の解答欄2が出ない");

                w.QuitFadeMs = WindowMotion.CloseMs;
                bool sawFadeOut = false;
                var tick2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                tick2.Tick += (_, _) =>
                {
                    w.Session.PlacerForTests.TickInsurance();
                    if (strip2.IsVisible && WindowMotion.IsFadingOut(strip2)) sawFadeOut = true;
                };
                tick2.Start();
                w.QuitApp();
                tick2.Stop();
                Assert.True(sawFadeOut, "終了時の薄れが開始されていない");
                Assert.False(strip2.IsVisible);
                Assert.False(bar2.IsVisible);
            }
            finally { WindowMotion.Cancel(w); try { w.Close(); } catch { } }
        });
    }

    [Fact]
    public void J90_クイックで隠している途中に見回りやMovingが来てもバーと外の解答欄が勝手に出ない()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000));
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("前の答え");
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000));

                var capH = Native.GetAncestor(new WindowInteropHelper(cap).Handle, Native.GA_ROOT);
                Native.GetWindowRect(capH, out var cr);
                w.Quick.BeginAt((cr.Left + cr.Right) / 2, (cr.Top + cr.Bottom) / 2);
                Assert.True(TestUtil.WaitUntil(() => !cap.IsVisible && !bar.IsVisible && !strip.IsVisible, 3000), "クイックで隠れない");

                // Moving と 250ms 保険が来ても、隠したまま出ない
                var moved = new Native.RECT { Left = cr.Left + 40, Top = cr.Top + 40, Right = cr.Right + 40, Bottom = cr.Bottom + 40 };
                w.Session.PlacerForTests.PlaceAll(moved);
                w.Session.PlacerForTests.TickInsurance();
                Assert.False(bar.IsVisible);
                Assert.False(strip.IsVisible);

                w.Quick.CancelRestore();
                Assert.True(TestUtil.WaitUntil(() => cap.IsVisible && bar.IsVisible && strip.IsVisible, 3000), "取り消しで戻らない");
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J90_撮影中は窓の中の解答欄へ表示要求や最小化復帰が来ても出ない()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "inside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var panel = Get<AnswerPanel>(w.Session, "_panel");

                // 撮影開始状態にする
                typeof(ThrowController).GetProperty("CapturePending", F)!.SetValue(w.Throw, true);
                ((IThrowOps)w.Session).BeginRun();
                Assert.True(w.Session.VisibilityForTests.IsCapturing);
                Assert.False(panel.IsVisible);

                // 撮影中に最小化→復帰や直接の表示要求が来ても出ない
                cap.WindowState = WindowState.Minimized;
                cap.WindowState = WindowState.Normal;
                panel.ApplyBody("撮影中に割り込んだ文字");
                ((IThrowOps)w.Session).Stream("", "撮影中のストリーム", capturePending: true);
                Assert.False(panel.IsVisible);

                // 撮影完了後にストリームが来たら出る
                typeof(ThrowController).GetProperty("CapturePending", F)!.SetValue(w.Throw, false);
                ((IThrowOps)w.Session).Stream("", "撮影後の答え", capturePending: false);
                Assert.True(panel.IsVisible);
                Assert.Equal("撮影後の答え", panel.ModelText);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J90_外の解答欄は答え到着や置き場所切替で250ms見回りを待たずに即座に出し入れが始まる()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000));
                var strip = Get<AnswerStrip>(w.Session, "_strip");

                // 250ms 巡回タイマーを完全に止めて、巡回に頼らずその場で出し入れされることを確かめる
                Get<DispatcherTimer>(w.Session, "_chromeSync").Stop();
                strip.ClearAndHide();
                Assert.False(strip.IsVisible);

                ((IThrowOps)w.Session).Stream("", "即座に出る答え", capturePending: false);
                Assert.True(strip.IsVisible);   // 250ms 待ちなしで即出る

                // outside -> inside 切り替えで、250ms を待たずに即座に薄れ始める
                CfgTestUtil.SetPlace(cfg, "inside");
                Assert.True(WindowMotion.IsFadingOut(strip));
                Assert.True(TestUtil.WaitUntil(() => !strip.IsVisible, 3000), "inside 切り替え後に外の解答欄が隠れない");

                // inside -> outside へ戻した時も、250ms を待たずに即座に再表示される
                CfgTestUtil.SetPlace(cfg, "outside");
                Assert.True(strip.IsVisible);
                Assert.Equal("即座に出る答え", strip.ModelText.Trim());
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J92_窓の中の解答欄が出ている時にクイックを取り消すと答えが戻り_確定では勝手に出ない()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "inside");

            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Assert.True(TestUtil.WaitUntil(() => Get<ChromeBar>(w.Session, "_bar").IsVisible, 3000));
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                panel.ShowFinal("残る答え");
                Assert.True(TestUtil.WaitUntil(() => panel.IsVisible && !WindowMotion.IsRunning(panel), 3000), "解答欄が出ない");

                var capH = Native.GetAncestor(new WindowInteropHelper(cap).Handle, Native.GA_ROOT);
                Native.GetWindowRect(capH, out var cr);
                int cx = (cr.Left + cr.Right) / 2, cy = (cr.Top + cr.Bottom) / 2;

                // 1) 完全に隠れてからのクイック取り消しで答えが戻る
                w.Quick.BeginAt(cx, cy);
                Assert.True(TestUtil.WaitUntil(() => !cap.IsVisible && !panel.IsVisible, 3000), "クイックで隠れない");
                w.Quick.CancelRestore();
                Assert.True(TestUtil.WaitUntil(() => cap.IsVisible && panel.IsVisible && !WindowMotion.IsRunning(panel), 3000),
                    "取り消しで窓の中の解答欄が戻らない");
                Assert.Equal("残る答え", panel.ModelText);

                // 2) 薄れ途中の即取り消しでも答えが戻る
                w.Quick.BeginAt(cx, cy);
                Assert.True(WindowMotion.IsFadingOut(panel));
                w.Quick.CancelRestore();
                Assert.True(TestUtil.WaitUntil(() => panel.IsVisible && !WindowMotion.IsRunning(panel), 3000),
                    "薄れ途中の取り消しで解答欄が戻らない");
                Assert.Equal("残る答え", panel.ModelText);

                // 3) 確定（fade=false）では勝手に戻らない（これから撮影して新しいぶん投げへ進むため）
                w.Quick.BeginAt(cx, cy);
                Assert.True(TestUtil.WaitUntil(() => !cap.IsVisible && !panel.IsVisible, 3000), "2回目クイックで隠れない");
                w.Session.QuickPlace(cr.Left + 20, cr.Top + 20, cr.Right - cr.Left, cr.Bottom - cr.Top, fade: false);
                Assert.False(panel.IsVisible);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }
}

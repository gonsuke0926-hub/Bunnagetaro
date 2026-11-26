using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J10d: 行の訳が終わって外れる書き起こし文の残りを、日本語の末へ吸い込ませる</summary>
public sealed class SuckTests : IDisposable
{
    private readonly string _temp;

    public SuckTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_suck_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        AppLog.Init(new Paths(Path.Combine(_temp, "Bunnagetaro5")));
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static void EnsureApp()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    private sealed class Host : IAnswerHost
    {
        public bool FollowupSend(string question, bool withImage) => false;
        public void TouchPanelGen() { }
        public void SetStatus(string msg) { }
        public Native.RECT? BarRectNow() => null;
        public void PlacePanel(Native.RECT? target = null) { }
        public void PlaceStrip(Native.RECT? barRect) { }
        public bool DeferPanelWhileDragging(Action again) => false;
        public void ResetPanelRun() { }
        public bool RequestPanelShow() => false;
        public bool IsPanelFadingOut() => false;
        public void HidePanelForCapture() { }
        public void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
        public void RequestStripShow() { }
        public void HideStripNow() { }
        public void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
    }

    /// <summary>見えている本文で試す（画面の外に出した窓に載せる）。時計はテストが進める</summary>
    private static void WithShownView(Action<AnswerView, Action<long>> body)
    {
        OnSta(() =>
        {
            EnsureApp();
            long now = 0;
            WindowMotion.Clock = () => now;
            var v = new AnswerView(new Host(), 6, 90);
            var w = new Window
            {
                Content = v, Width = 400, Height = 300, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            try
            {
                w.Show();
                v.UpdateLayout();
                body(v, ms => { now += ms; WindowMotion.Tick(now); });
            }
            finally
            {
                w.Close();
                WindowMotion.Clock = null!;
            }
        });
    }


    // ---------------- 文字の計算 ----------------

    [Fact]
    public void J10d_行の後ろが外れた所を見つける()
    {
        var r = SuckText.Removed("こん is a\nWorld two", "こんにちは\nWorld two");
        Assert.Empty(r);   // 前の行の頭ではない（上書きが進んだだけ）
        r = SuckText.Removed("こんにちは greeting\nWorld two", "こんにちは\nWorld two");
        Assert.Single(r);
        Assert.Equal((0, "こんにちは", " greeting"), r[0]);
    }

    [Fact]
    public void J10d_残りは左から速さどおりに食われ_行が変わったら外れる()
    {
        var gs = new List<SuckText.Ghost> { new() { Line = 0, Base = "訳", Suffix = "abcdefghij", StartMs = 0, StepMs = 10, StepChars = 1 } };   // 100字/秒
        Assert.Equal("訳abcdefghij\n次", SuckText.Compose("訳\n次", gs, 0));
        Assert.Equal("訳fghij\n次", SuckText.Compose("訳\n次", gs, 50));   // 50ms で5字
        Assert.Equal("訳\n次", SuckText.Compose("訳\n次", gs, 100));
        Assert.Empty(gs);
        gs.Add(new() { Line = 0, Base = "訳", Suffix = "abc", StartMs = 0, StepMs = 100, StepChars = 1 });
        Assert.Equal("訳き直し", SuckText.Compose("訳き直し", gs, 0));     // 行が変わった
        Assert.Empty(gs);
    }

    [Fact]
    public void J10e_食い方は訳と同じ刻みで_間は止まって見える()
    {
        var gs = new List<SuckText.Ghost> { new() { Line = 0, Base = "訳", Suffix = "abcdefghijkl", StartMs = 0, StepMs = 40, StepChars = 6 } };
        Assert.Equal("訳abcdefghijkl", SuckText.Compose("訳", gs, 39));   // 次の刻みまでは動かない
        Assert.Equal("訳ghijkl", SuckText.Compose("訳", gs, 40));        // 刻みでまとめて6字
        Assert.Equal("訳ghijkl", SuckText.Compose("訳", gs, 79));
        Assert.Equal(80, gs[0].EndMs);
    }

    [Fact]
    public void J10d_上書きの速さを流れから覚える()
    {
        var m = new SuckText.RateMeter();
        string src = new string('x', 300);
        string prev = src;
        for (int i = 1; i <= 10; i++)
        {
            string next = new string('あ', i * 2) + src[(i * 6)..];   // 40ms ごとに英文6字
            m.Observe(prev, next, i * 40);
            prev = next;
        }
        Assert.NotNull(m.Cps);
        Assert.InRange(m.Cps!.Value, 140, 160);   // 6字/40ms = 150字/秒
        Assert.NotNull(m.Step);
        Assert.Equal(40, m.Step!.Value.StepMs, 3);
        Assert.Equal(6, m.Step!.Value.StepChars, 3);
        // 行の訳が終わって残りが外れた所は数えない
        m.Observe(prev, new string('あ', 20), 440);
        Assert.InRange(m.Cps!.Value, 140, 160);
    }

    [Fact]
    public void J118_残りが減らない回が混ざっても刻みが遅くならない()
    {
        var m = new SuckText.RateMeter();
        string src = "Hello world example text";
        m.Observe(src, "こ" + src[2..], 0);
        // 残りが減らない更新が何回か挟まっても _ms を膨らませない
        m.Observe("こ" + src[2..], "こん" + src[2..], 40);
        m.Observe("こん" + src[2..], "こんに" + src[2..], 80);
        m.Observe("こんに" + src[2..], "こんに" + src[8..], 120);   // 40ms で 6字減る
        Assert.NotNull(m.Step);
        Assert.Equal(40, m.Step!.Value.StepMs, 3);
        Assert.Equal(6, m.Step!.Value.StepChars, 3);
    }

    [Fact]
    public void J10d_長い残りでも上限の時間で食べ切る()
    {
        Assert.Equal((SuckText.DefaultStepMs, SuckText.DefaultStepChars), SuckText.EffectiveStep(null, 10));
        Assert.Equal((50.0, 2.0), SuckText.EffectiveStep((50, 2), 100));     // 覚えた刻みのまま（2.5秒）
        Assert.Equal((50.0, 2.0), SuckText.EffectiveStep((50, 2), 300));     // 7.5秒でも訳の刻みのまま
        var (ms, ch) = SuckText.EffectiveStep((50, 2), 1200);                // 安全の上限（10秒）だけ
        Assert.Equal(50.0, ms, 3);
        Assert.Equal(6.0, ch, 3);
    }

    [Fact]
    public void J10e_刻みは次のぶん投げへ持ち越し_今の流れで見たらすぐそれを使う()
    {
        var m = new SuckText.RateMeter();
        Assert.Null(m.Step);
        string src = new string('x', 300), prev = src;
        for (int i = 1; i <= 5; i++)
        {
            string next = new string('あ', i) + src[(i * 3)..];   // 80ms ごとに英文3字
            m.Observe(prev, next, i * 80);
            prev = next;
        }
        Assert.Equal((80.0, 3.0), m.Step);
        m.Reset();                       // 次のぶん投げ
        Assert.Equal((80.0, 3.0), m.Step);   // まだ見ていない間は前の刻み
        prev = src;
        m.Observe(prev, "ああ" + src[10..], 1000);
        m.Observe("ああ" + src[10..], "あああ" + src[20..], 1020);   // 1回見たらもう今の刻み
        Assert.Equal((20.0, 10.0), m.Step);
    }

    // ---------------- 解答欄 ----------------

    [Fact]
    public void J10d_書き終わりで残りの英文が日本語の末へ吸い込まれる_答えの文はすぐ訳だけ()
    {
        WithShownView((v, advance) =>
        {
            v.ApplyBody("こんにちは。 is a greeting");
            v.ShowFinal("こんにちは。");
            Assert.Equal("こんにちは。", v.ModelText);           // コピー・ログはすぐ訳だけ
            Assert.Equal("こんにちは。 is a greeting", v.ShownText);
            Assert.True(v.Sucking);
            advance(39);   // 既定の刻み（40ms ごとに6字）の前は動かない
            Assert.Equal("こんにちは。 is a greeting", v.ShownText);
            advance(1);
            Assert.Equal("こんにちは。greeting", v.ShownText);
            advance(1000);
            Assert.False(v.Sucking);
            Assert.Equal("こんにちは。", v.ShownText);
        });
    }

    [Fact]
    public void J10d_次の行へ移ったら前の行の残りを吸い込みながら次の行の上書きが進む()
    {
        WithShownView((v, advance) =>
        {
            v.ApplyBody("一行目 rest one\nLine two here");
            v.ApplyBody("一行目\nLine two here");           // 1行目の訳が終わった
            Assert.True(v.Sucking);
            Assert.Equal("一行目 rest one\nLine two here", v.ShownText);
            advance(40);
            v.ApplyBody("一行目\n二e two here");            // 2行目の上書きは止まらない
            Assert.Equal("一行目one\n二e two here", v.ShownText);   // 既定の刻み 40ms で6字
            advance(1000);
            Assert.Equal("一行目\n二e two here", v.ShownText);
        });
    }

    [Fact]
    public void J10d_ふつうに続きを足すだけなら吸い込みは出ない()
    {
        WithShownView((v, _) =>
        {
            v.ApplyBody("途中まで");
            v.ShowFinal("途中までの答え");
            Assert.False(v.Sucking);
            Assert.Equal("途中までの答え", v.ShownText);
        });
    }

    [Fact]
    public void J10d_次のぶん投げが始まったら吸い込みの途中でも外す()
    {
        WithShownView((v, advance) =>
        {
            v.ApplyBody("前 the rest of English");
            v.ShowFinal("前");
            advance(10);
            v.ResetRun();
            Assert.False(v.Sucking);
            Assert.Equal("", v.ShownText);
        });
    }

    [Fact]
    public void J10d_見えていない本文では吸い込まずすぐ外す()
    {
        OnSta(() =>
        {
            EnsureApp();
            var v = new AnswerView(new Host(), 6, 90);
            v.ApplyBody("前 the rest of English");
            v.ShowFinal("前");
            Assert.False(v.Sucking);
            Assert.Equal("前", v.ShownText);
        });
    }
}

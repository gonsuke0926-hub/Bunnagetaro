using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>修正J80: 撮影の直前ではない所で、窓が即消えずに薄れて消える</summary>
public class FadeOutTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public FadeOutTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_fadeout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    private MainWindow Open(string place)
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetBool("chrome_linked", true);
        cfg.SetBool("quick_enabled", false);
        CfgTestUtil.SetPlace(cfg, place);
        var w = new MainWindow(_paths, cfg);
        w.Show();
        w.Session.OpenCapture(animate: false);
        Pump(600);
        return w;
    }

    [Fact]
    public void J81_失敗した時も外の解答欄は消えず前の中身が残る()
    {
        OnSta(() =>
        {
            var w = Open("outside");
            try
            {
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Pump(900);
                Assert.True(strip.IsVisible);
                ((IThrowOps)w.Session).HideAnswers();
                Assert.False(WindowMotion.IsFadingOut(strip));
                Pump(600);
                Assert.True(strip.IsVisible);                    // 消えない
                Assert.Equal("答え", strip.ModelText.Trim());     // 前の中身のまま
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J80_失敗した時は窓の中の解答欄が即消えず薄れて消える()
    {
        OnSta(() =>
        {
            var w = Open("inside");
            try
            {
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                panel.ShowFinal("答え");
                Pump(900);
                Assert.True(panel.IsVisible);
                ((IThrowOps)w.Session).HideAnswers();
                Assert.True(panel.IsVisible);
                Assert.True(WindowMotion.IsFadingOut(panel));
                Pump(600);
                Assert.False(panel.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J81_ぶん投げを始めても外の解答欄の案内は消えず_答えが届くと案内を空にしてから答えになる()
    {
        OnSta(() =>
        {
            var w = Open("outside");
            try
            {
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                string hint = w.Session.HintForTests.HintShown;
                if (!strip.IsVisible || strip.ModelText.Trim() != hint.Trim())
                {
                    // 案内が出ていなければ出す（最初の窓は案内が残る）
                    strip.ApplyBody(hint);
                    Pump(900);
                }
                // PC が忙しい時は出るまで少しかかる（見回りは 250ms ごと）。最大2秒待つ
                for (int i = 0; i < 20 && !strip.IsVisible; i++) Pump(100);
                Assert.True(strip.IsVisible);
                ((IThrowOps)w.Session).BeginRun();
                Pump(600);
                Assert.True(strip.IsVisible);                    // 案内のまま残る
                Assert.False(WindowMotion.IsFadingOut(strip));
                Assert.Equal(hint.Trim(), strip.ModelText.Trim());
                ((IThrowOps)w.Session).Stream("", "答えの始まり", false);
                Pump(300);
                Assert.True(strip.IsVisible);
                Assert.Equal("答えの始まり", strip.ModelText.Trim());   // 案内の続きにならない
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J83_途中参加の動きは始まりの時刻から数えた進み具合から始まる()
    {
        OnSta(() =>
        {
            long now = 1000;
            WindowMotion.Clock = () => now;
            try
            {
                var key = new object();
                double first = -1;
                WindowMotion.RunFrom(key, 200, t => t, p => { if (first < 0) first = p; }, null, start: 900);
                Assert.Equal(0.5, first, 3);                 // 100ms 前に始まっていた → 半分から
                WindowMotion.Cancel(key);
                double f2 = -1;
                WindowMotion.RunFrom(key, 200, t => t, p => { if (f2 < 0) f2 = p; }, null, start: 2000);
                Assert.Equal(0.0, f2, 3);                    // 先の時刻なら始まりの値（修正J86: その時刻まで止まる）
                WindowMotion.Cancel(key);
            }
            finally { WindowMotion.Clock = null!; }
        });
    }

    [Fact]
    public void J83_バーの閉じるで外の解答欄は薄れたまま閉じ_見回りに出し直されない()
    {
        OnSta(() =>
        {
            var w = Open("outside");
            try
            {
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Pump(900);
                Assert.True(strip.IsVisible);
                bool closed = false;
                strip.Closed += (_, _) => closed = true;
                double last = 1.0, rise = 0;
                var peek = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                peek.Tick += (_, _) =>
                {
                    if (closed) { peek.Stop(); return; }
                    double o = strip.Opacity;
                    if (o > last + 1e-3) rise = Math.Max(rise, o - last);
                    last = o;
                };
                peek.Start();
                w.Session.CloseCaptureAll();
                Pump(WindowMotion.CloseMs + 400);   // 見回り（250ms）を越えて待つ
                peek.Stop();
                Assert.True(closed, "外の解答欄が閉じていない");
                Assert.True(rise < 0.05, $"閉じる途中で濃くなった（出し直された。+{rise:0.00}）");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J83_窓を開く時_外の解答欄はキャプチャ窓と同じ濃さから途中参加する()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                double? diff = null;
                var peek = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(5) };
                peek.Tick += (_, _) =>
                {
                    var strip = (AnswerStrip?)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_strip", F)!.GetValue(w.Session);
                    var cap = (CaptureWindow?)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_capture", F)!.GetValue(w.Session);
                    if (strip is null || cap is null || !strip.IsVisible) return;
                    diff = Math.Abs(strip.Opacity - cap.Opacity);
                    peek.Stop();
                };
                peek.Start();
                w.Session.OpenCapture(animate: true);
                Pump(WindowMotion.OpenMs + 400);
                peek.Stop();
                Assert.True(diff.HasValue, "外の解答欄（案内）が出なかった");
                Assert.True(diff!.Value < 0.3, $"外の解答欄がキャプチャ窓より遅れて出始めた（濃さの差 {diff.Value:0.00}）");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J84_窓を開いたその場で外の解答欄の案内も出始める_Loadedを待たない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(300);
                // 1回開いて閉じる（バーの大きさが決まる。2回目からは開いたその場でバーの大きさが分かる）
                w.Session.OpenCapture(animate: false);
                Pump(600);
                w.Session.CloseCaptureAll();
                Pump(600);
                w.Session.OpenCapture(animate: true);
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                if (bar.BarRectNow() is not { } br || br.Right - br.Left < 60) return;   // バーの大きさがまだ決まらない環境（Loaded を待つ形のまま）
                Assert.True(strip.IsVisible, "開いたその場で外の解答欄が出ていない（Loaded を待っている）");
                Assert.True(WindowMotion.IsRunning(strip));   // ふわっと出ている最中
                Pump(WindowMotion.OpenMs + 300);
                Assert.Equal(Native.GetWindowRect(Native.GetAncestor(new System.Windows.Interop.WindowInteropHelper(strip).Handle, Native.GA_ROOT), out var sr) ? sr.Left : -1,
                             bar.BarRectNow()!.Value.Left);   // 左端はバーにそろう
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J85_始め直しは指定の時刻から数え直して今の進み具合を当てる()
    {
        OnSta(() =>
        {
            long now = 1000;
            WindowMotion.Clock = () => now;
            try
            {
                var key = new object();
                double last = -1;
                WindowMotion.Run(key, 200, t => t, p => last = p);
                now = 1100;
                WindowMotion.Tick(now);
                Assert.Equal(0.5, last, 3);
                Assert.True(WindowMotion.Restart(key, 1100));   // 今から始まったことにする
                Assert.Equal(0.0, last, 3);
                now = 1200;
                WindowMotion.Tick(now);
                Assert.Equal(0.5, last, 3);
                WindowMotion.Cancel(key);
                Assert.False(WindowMotion.Restart(key, 1200));  // 走っていなければ何もしない
            }
            finally { WindowMotion.Clock = null!; }
        });
    }

    [Fact]
    public void J85_窓を開いて戻った時点で_キャプチャ窓とバーと外の解答欄はそろって出始めにいる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(300);
                w.Session.OpenCapture(animate: false);
                Pump(600);
                w.Session.CloseCaptureAll();
                Pump(600);
                w.Session.OpenCapture(animate: true);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                // 開く処理の間に時計が進んでいない（始め直している）: どれもまだ出始めの濃さ
                Assert.True(WindowMotion.IsRunning(cap) && cap.Opacity < 0.2, $"キャプチャ窓 {cap.Opacity:0.00}");
                Assert.True(WindowMotion.IsRunning(bar) && bar.Opacity < 0.2, $"バー {bar.Opacity:0.00}");
                if (strip.IsVisible)
                    Assert.True(WindowMotion.IsRunning(strip) && strip.Opacity < 0.2, $"外の解答欄 {strip.Opacity:0.00}");
                Pump(WindowMotion.OpenMs + 300);
                Assert.Equal(1.0, cap.Opacity, 2);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J86_止めておいた動きは時計が進んでも始まりの値のままで_始め直すと動く()
    {
        OnSta(() =>
        {
            long now = 1000;
            WindowMotion.Clock = () => now;
            try
            {
                var key = new object();
                double last = -1;
                WindowMotion.RunFrom(key, 200, t => t, p => last = p, null, WindowMotion.Held);
                Assert.True(WindowMotion.IsHeld(key));
                now = 1500;
                WindowMotion.Tick(now);
                Assert.Equal(0.0, last, 3);                 // 止まったまま
                Assert.True(WindowMotion.IsRunning(key));
                WindowMotion.Restart(key, now);
                Assert.False(WindowMotion.IsHeld(key));
                now = 1600;
                WindowMotion.Tick(now);
                Assert.Equal(0.5, last, 3);                 // 動き出した
                WindowMotion.Cancel(key);
            }
            finally { WindowMotion.Clock = null!; }
        });
    }

    [Fact]
    public void J86_起動して最初に開いた時も外の解答欄はその場で出て_開く間にキャプチャ窓の濃さは一度も下がらない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(300);
                double last = 0, drop = 0;
                CaptureWindow? capRef = null;
                var peek = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(5) };
                peek.Tick += (_, _) =>
                {
                    capRef ??= (CaptureWindow?)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_capture", F)!.GetValue(w.Session);
                    if (capRef is not { IsVisible: true } c) return;
                    if (c.Opacity < last - 0.02) drop = Math.Max(drop, last - c.Opacity);
                    last = c.Opacity;
                };
                peek.Start();
                w.Session.OpenCapture(animate: true);   // このアプリで初めて開く
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                Assert.True(strip.IsVisible, "初めて開いた時に、外の解答欄がその場で出ていない（Loaded を待っている）");
                Assert.False(WindowMotion.IsHeld(cap));   // 開き終わった時点で動き出している
                Assert.False(WindowMotion.IsHeld(bar));
                Assert.False(WindowMotion.IsHeld(strip));
                Pump(WindowMotion.OpenMs + 400);
                peek.Stop();
                Assert.True(drop < 0.02, $"開く間にキャプチャ窓の濃さが下がった（-{drop:0.00}）");
                Assert.Equal(1.0, cap.Opacity, 2);
                Assert.Equal(1.0, strip.Opacity, 2);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J82_外の解答欄は透過する窓で_濃さを下げると画面でも薄くなる()
    {
        OnSta(() =>
        {
            var w = Open("outside");
            try
            {
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Pump(900);
                Assert.True(strip.AllowsTransparency);
                var h = Native.GetAncestor(new System.Windows.Interop.WindowInteropHelper(strip).Handle, Native.GA_ROOT);
                long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
                Assert.True((ex & Native.WS_EX_LAYERED) != 0, "WS_EX_LAYERED が付いていない（窓全体の濃さが効かない）");
                // 下地の色（HwndTarget.BackgroundColor）が不透明だと、濃さを下げても下地が残る（J77 の作り）
                var src = System.Windows.Interop.HwndSource.FromHwnd(h);
                Assert.True(src!.CompositionTarget!.BackgroundColor.A < 255, "下地が不透明");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J80_終わる時は見えている窓がいっしょに薄れてから閉じる()
    {
        OnSta(() =>
        {
            var w = Open("outside");
            var strip = Get<AnswerStrip>(w.Session, "_strip");
            strip.ShowFinal("答え");
            Pump(900);
            var cap = Get<CaptureWindow>(w.Session, "_capture");
            Assert.True(cap.IsVisible);
            bool fadingAtClose = false;
            w.QuitFadeMs = WindowMotion.CloseMs;
            // 薄れている間に様子を見る（QuitApp の前処理で最初の数十msを使っても取り逃さないよう、薄れている間ずっと測る）
            var peek = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            double minStripOpacity = 1;
            peek.Tick += (_, _) =>
            {
                if (cap.IsVisible && WindowMotion.IsFadingOut(cap) && strip.IsVisible && WindowMotion.IsFadingOut(strip))
                {
                    fadingAtClose = true;
                    minStripOpacity = Math.Min(minStripOpacity, strip.Opacity);
                }
            };
            peek.Start();
            var sw = Stopwatch.StartNew();
            w.QuitApp();
            peek.Stop();
            sw.Stop();
            Assert.True(fadingAtClose, "終わる時に窓が薄れていない");
            Assert.True(minStripOpacity < 0.95, $"外の解答欄の濃さが下がっていない（{minStripOpacity:0.00}）");   // 修正J82
            Assert.True(sw.ElapsedMilliseconds >= WindowMotion.CloseMs - 20, $"薄れを待たずに閉じた（{sw.ElapsedMilliseconds}ms）");
            Assert.False(cap.IsVisible);
            Assert.False(strip.IsVisible);   // 修正J82: 薄れた後に見回りが出し直さない
            Assert.False(w.IsVisible);
        });
    }

    [Fact]
    public void J88_ログの窓をトレイにしまう時とバージョン情報窓は待たずにすぐ隠れて閉じる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            cfg.Settings.CloseToTray = true;
            var w = new MainWindow(_paths, cfg);
            bool mainClosed = false;
            w.Closed += (_, _) => mainClosed = true;
            try
            {
                w.Show();
                Pump(200);
                w.Close();   // トレイへしまう（Pump なしで即隠れる）
                Assert.False(mainClosed);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.False(WindowMotion.IsFadingOut(w));

                w.ShowAndActivate();
                Assert.True(w.IsVisible);
            }
            finally { w.QuitApp(); Pump(200); }

            var about = new AboutWindow { Left = -3000, Top = 0 };
            bool aboutClosed = false;
            about.Closed += (_, _) => aboutClosed = true;
            try
            {
                about.Show();
                var ok = ((StackPanel)about.Content).Children.OfType<DockPanel>().Single()
                    .Children.OfType<Button>().Single(b => b.Content as string == "OK");
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(aboutClosed);
                Assert.False(WindowMotion.IsRunning(about));
            }
            finally { if (!aboutClosed) about.Close(); }

            var notices = new ThirdPartyNoticesWindow { Left = -3000, Top = 0 };
            bool noticesClosed = false;
            notices.Closed += (_, _) => noticesClosed = true;
            try
            {
                notices.Show();
                var close = ((Grid)notices.Content).Children.OfType<Button>()
                    .Single(b => b.Content as string == "閉じる");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(noticesClosed);
                Assert.False(WindowMotion.IsRunning(notices));
            }
            finally { if (!noticesClosed) notices.Close(); }
        });
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// F34: クイックの時のバー。くっつける OFF なら消さない・動かさない。
/// ON なら枠と同じ吸い込みで消し、新しい位置へ置いてからふわっと出す（出てから動いて見えない）。
/// マウス・キーは動かさない（QuickThrowController.BeginAt / CancelRestore と QuickPlace を直接呼ぶ）
/// </summary>
public class QuickBarTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public QuickBarTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_quickbar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    [Fact]
    public void J36_離しが見えないPollは1回目は見送り_2回続いた時だけ取り消す()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(200);
                w.Quick.BeginAt(600, 400);
                Assert.True(Get<bool>(w.Quick, "_quickFollow"));
                // フックにはドラッグも離しも無い（離しを書いている隙間に当たった時と同じ見え方）
                w.Quick.Poll();
                Assert.True(Get<bool>(w.Quick, "_quickFollow"));   // 1回目では取り消さない
                w.Quick.Poll();
                Assert.False(Get<bool>(w.Quick, "_quickFollow"));  // 2回続いたら取り損ねとして元へ戻す
                Assert.False(Get<bool>(w.Quick, "_lostUpSeen"));
                Pump(300);
            }
            finally { w.Close(); }
        });
    }

    private static Native.RECT Rect(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        Native.GetWindowRect(h, out var r);
        return r;
    }

    private static (int, int, int, int) T4(Native.RECT r) => (r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>古いバーと重ならない、画面の中の新しい四角（物理px）</summary>
    private static (int X, int Y, int W, int H) FarRect(Native.RECT bar, Native.RECT oldWin)
    {
        var vd = Native.VirtualDesktop();
        foreach (var (x, y) in new[] { (vd.Left + 40, vd.Top + 40), (vd.Right - 400, vd.Top + 40), (vd.Left + 40, vd.Bottom - 350) })
        {
            if (!ChromePlace.Intersects(x, y, 320, 240, bar.Left, bar.Top, bar.Right, bar.Bottom) &&
                !(x == oldWin.Left && y == oldWin.Top))
                return (x, y, 320, 240);
        }
        return (vd.Left + 40, vd.Top + 40, 320, 240);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    private static bool Cloaked(Window win)
        => DwmGetWindowAttribute(new WindowInteropHelper(win).Handle, 14 /*DWMWA_CLOAKED*/, out int v, sizeof(int)) == 0 && v != 0;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 窓が無い時のクイックは窓とバーを出さずに開き_離すと新しい四角で初めて出る(bool linked)
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", linked);
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(200);
                Assert.Null(Get<CaptureWindow?>(w.Session, "_capture"));
                w.Quick.BeginAt(600, 400);          // 窓が無いところで押した
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                Assert.False(cap.IsVisible);        // 開いたが出していない（一瞬出て吸い込まれる、が無い）
                Assert.False(bar.IsVisible);        // くっつける OFF でも、まだ出していないバーは出さない
                Assert.False(Cloaked(cap));         // 隠しの cloak は外してある（後で普通に出せる）
                Assert.False(Cloaked(bar));
                Assert.True(w.Session.QuickOpenedHidden);
                Pump(400);
                Assert.False(cap.IsVisible);
                Assert.False(bar.IsVisible);        // 巡回が来ても出ない
                Assert.False(panel.IsVisible);      // 開いた直後の案内（A3）も出ない
                // 離した: 新しい四角へ置いてから初めて出る
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { 500, 300, 420, 260, false });
                Pump(400);
                Assert.True(cap.IsVisible);
                Assert.True(bar.IsVisible);
                Assert.False(Cloaked(cap));
                var r = Rect(cap);
                Assert.Equal((500, 300, 920, 560), (r.Left, r.Top, r.Right, r.Bottom));
                Assert.False(w.Session.QuickOpenedHidden);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 窓が無い時のクイックを取り消すと窓は閉じて押す前に戻る()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                Pump(200);
                w.Quick.BeginAt(600, 400);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                Assert.False(cap.IsVisible);
                w.Quick.CancelRestore();            // 小さい四角・Esc など
                Pump(300);
                Assert.Null(Get<CaptureWindow?>(w.Session, "_capture"));
                Assert.False(cap.IsVisible);
                Assert.False(w.Session.QuickOpenedHidden);
                // 窓が出ている時の取り消しは今まで通り（元の位置で見せる）
                w.Session.OpenCapture(animate: false);
                Pump(300);
                var cap2 = Get<CaptureWindow>(w.Session, "_capture");
                var r0 = Rect(cap2);
                w.Quick.BeginAt((r0.Left + r0.Right) / 2, (r0.Top + r0.Bottom) / 2);
                Pump(300);
                w.Quick.CancelRestore();
                Pump(400);
                Assert.Same(cap2, Get<CaptureWindow>(w.Session, "_capture"));
                Assert.True(cap2.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void くっつけるOFFならクイックの間もバーは消えず位置も変わらない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", false);
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                Assert.True(bar.IsVisible);
                var barBefore = Rect(bar);
                var capRect = Rect(cap);
                int cx = (capRect.Left + capRect.Right) / 2, cy = (capRect.Top + capRect.Bottom) / 2;

                w.Quick.BeginAt(cx, cy);
                Pump(400);                          // 吸い込みが終わるまで
                Assert.False(cap.IsVisible);         // 枠は消える
                Assert.True(bar.IsVisible);          // バーは消えない
                Assert.False(WindowMotion.IsFadingOut(bar));
                Assert.Equal(ChromeBar.RestOpacity, bar.Opacity, 3);
                Assert.Equal(T4(barBefore), T4(Rect(bar)));

                var n = FarRect(barBefore, capRect);
                var seen = new List<bool>();
                cap.Moving += _ => seen.Add(bar.IsVisible);
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { n.X, n.Y, n.W, n.H, false });
                Pump(300);
                Assert.True(cap.IsVisible);
                Assert.True(bar.IsVisible);
                Assert.All(seen, v => Assert.True(v));   // 途中で消えたり出直したりしない
                Assert.Equal(T4(barBefore), T4(Rect(bar)));   // 離れて置いたバーは動かない
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void くっつけるONならバーは吸い込まれて消え_新しい位置へ置いてからふわっと出る()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                Assert.True(bar.IsVisible);
                var barBefore = Rect(bar);
                var capRect = Rect(cap);
                int cx = (capRect.Left + capRect.Right) / 2, cy = (capRect.Top + capRect.Bottom) / 2;

                w.Quick.BeginAt(cx, cy);
                Assert.True(WindowMotion.IsFadingOut(bar));   // 枠と同じ吸い込みが始まっている
                Pump(400);
                Assert.False(cap.IsVisible);
                Assert.False(bar.IsVisible);
                Assert.Equal(ChromeBar.RestOpacity, bar.Opacity, 3);   // 次に出す時のため元の見え方へ戻っている
                Pump(600);                                            // 250ms の巡回が来ても勝手に出ない
                Assert.False(bar.IsVisible);

                var n = FarRect(barBefore, capRect);
                var visibleAtMoving = new List<bool>();
                cap.Moving += _ => visibleAtMoving.Add(bar.IsVisible);
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { n.X, n.Y, n.W, n.H, false });
                Assert.All(visibleAtMoving, v => Assert.False(v));    // 窓が動いた時に、古い位置・薄れずに出ていない
                // 出た最初のフレームで、すでに新しい窓の下にある（出た後に位置が変わらない）
                Assert.True(bar.IsVisible);
                var nRc = new Native.RECT { Left = n.X, Top = n.Y, Right = n.X + n.W, Bottom = n.Y + n.H };
                var predicted = ChromeBar.PredictPos(nRc, nRc, barBefore.Right - barBefore.Left, barBefore.Bottom - barBefore.Top,
                    null, null, linked: true, Native.VirtualDesktop(), null);
                Assert.NotNull(predicted);
                var atShow = Rect(bar);
                Assert.Equal(predicted!.Value.X, atShow.Left);
                Assert.Equal(predicted.Value.Y, atShow.Top);
                Assert.True(WindowMotion.IsRunning(bar));              // ふわっと出ている最中
                Assert.True(bar.Opacity < ChromeBar.RestOpacity);      // 最初は薄い
                Pump(400);
                Assert.Equal(ChromeBar.RestOpacity, bar.Opacity, 3);
                Assert.Equal(T4(atShow), T4(Rect(bar)));               // 出た後に動いていない

                // 小さい四角で取り消して元へ戻る時も、置いてから出す
                var preCap = Rect(cap);
                w.Quick.BeginAt(preCap.Left + 50, preCap.Top + 50);
                Pump(400);
                Assert.False(bar.IsVisible);
                w.Quick.CancelRestore();
                Assert.True(bar.IsVisible);
                Assert.Equal(T4(atShow), T4(Rect(bar)));
                Pump(400);
                Assert.Equal(T4(atShow), T4(Rect(bar)));
                Assert.True(cap.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void F3_バーだけが動く直前に外の解答欄が同じフレームでその位置へ置かれる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", false);
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Pump(700);   // 巡回が出す
                Assert.True(strip.IsVisible);
                var b0 = Rect(bar);
                int bw = b0.Right - b0.Left, bh = b0.Bottom - b0.Top;
                var vd = Native.VirtualDesktop();
                // バーの新しい位置（窓から離れた、画面の中）。実バーはまだ動かさない
                int nx = Math.Min(b0.Left + 150, vd.Right - bw - 10), ny = b0.Top;
                if (nx == b0.Left) nx = Math.Max(vd.Left, b0.Left - 150);
                var nb = ChromeBar.RectAt(nx, ny, bw, bh);
                ((Bunnagetaro5.Session.IBarActions)w.Session).BarMovingTo(nb);
                var s = Rect(strip);
                Assert.Equal(nx, s.Left);                 // 外の解答欄の左端が、これから行くバーの左端
                Assert.Equal(T4(b0), T4(Rect(bar)));      // 実バーは（呼んだ時点では）まだ動いていない＝先に置いている
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void F4_開く時の外の解答欄はバーと同時にふわっと始まり_同じ長さで終わる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("quick_enabled", false);
            CfgTestUtil.SetPlace(cfg, "outside");
            // 動きの時刻は差し替えた時計で進める（実時間だと、遅い PC・高い拡大率で Pump(90) の間に
            // 180ms 過ぎてバーの動きが終わってしまうことがある。ノート 250% で 10 回に 1〜2 回）
            long now = 0; WindowMotion.Clock = () => now;
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();          // 動き付き（既定）。バーも外の解答欄も時刻 0 で始まる
                Pump(90);                         // 巡回（250ms）より前（窓の出し入れは実時間のまま）
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                now = 90; WindowMotion.Tick(now);
                Assert.True(WindowMotion.IsRunning(bar));
                Assert.True(strip.IsVisible);                  // 巡回を待たずもう出始めている
                Assert.True(WindowMotion.IsRunning(strip));
                Assert.True(WindowMotion.CurrentOpacity(strip, 1.0) < 1.0);
                now = WindowMotion.OpenMs - 1; WindowMotion.Tick(now);
                Assert.True(WindowMotion.IsRunning(bar));
                Assert.True(WindowMotion.IsRunning(strip));    // バーより早くは終わらない
                now = WindowMotion.OpenMs; WindowMotion.Tick(now);   // OpenMs が過ぎたあと
                Assert.False(WindowMotion.IsRunning(bar));
                Assert.False(WindowMotion.IsRunning(strip));   // 同じ長さで終わる
                Assert.Equal(1.0, WindowMotion.CurrentOpacity(strip, 1.0), 3);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J88_透過しない窓はHideFadeもSuckInも領域を絞らず待たずにすぐ隠れる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            long now = 0; WindowMotion.Clock = () => now;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = false, ShowInTaskbar = false,
                Left = -10000, Top = 0, Width = 400, Height = 200, ShowActivated = false,
                Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.White },
            };
            try
            {
                w.Show();
                var h = new WindowInteropHelper(w).Handle;
                Native.GetWindowRect(h, out var r0);
                int hidden = 0;
                WindowMotion.HideFade(w, 1.0, WindowMotion.CloseMs, () => hidden++, WindowMotion.OpenCloseScale);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.Equal(1, hidden);
                Assert.Equal(0, Native.GetWindowRgnBox(h, out _));

                // 吸い込みも透過しない窓では領域を絞らず即隠れる
                w.Show();
                WindowMotion.SuckIn(w, r0.Left + 20, r0.Top + 10, 1.0, WindowMotion.QuickMs, () => hidden++);
                Assert.False(w.IsVisible);
                Assert.False(WindowMotion.IsRunning(w));
                Assert.Equal(2, hidden);
                Assert.Equal(0, Native.GetWindowRgnBox(h, out _));
            }
            finally { WindowMotion.Cancel(w); WindowMotion.Clock = null!; w.Close(); }
        });
    }

    [Fact]
    public void F34残り_くっつけるONのクイックで外の解答欄も吸い込みで消え_新しい位置に置いてからふわっと出る()
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
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("前の答え");
                // 整理（2026-11-22）: 決まった時間ではなく、条件がそろうまで待つ（PC が忙しい時に時々落ちていた）
                Assert.True(TestUtil.WaitUntil(() => strip.IsVisible && !WindowMotion.IsRunning(strip), 3000), "外の解答欄が出ない");
                var capRect = Rect(cap);
                var barBefore = Rect(bar);
                w.Quick.BeginAt((capRect.Left + capRect.Right) / 2, (capRect.Top + capRect.Bottom) / 2);
                Assert.True(WindowMotion.IsFadingOut(strip));            // 吸い込みが始まっている
                // 縮んでいる（修正J82: 外の解答欄も透過する窓になったので、ほかの窓と同じく中身を縮めて見せる。窓の見える範囲の絞りは使わない）
                Assert.True(TestUtil.WaitUntil(() => ((FrameworkElement)strip.Content).RenderTransform is System.Windows.Media.ScaleTransform st0 && st0.ScaleX < 1.0, 300, 5),
                            "吸い込みで縮んでいない");
                Assert.True(TestUtil.WaitUntil(() => !strip.IsVisible && !bar.IsVisible, 3000), "吸い込みの後に外の解答欄・バーが隠れない");

                var n = FarRect(barBefore, capRect);
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { n.X, n.Y, n.W, n.H, false });
                // 出た最初のフレームで、バー・外の解答欄とも新しい位置にあり、ふわっと出ている最中
                Assert.True(strip.IsVisible);
                Assert.True(WindowMotion.IsRunning(strip));
                Assert.True(WindowMotion.CurrentOpacity(strip, 1.0) < 1.0);
                var s0 = Rect(strip);
                Assert.Equal(Rect(bar).Left, s0.Left);                    // 左端はバーに揃う
                Assert.True(TestUtil.WaitUntil(() => !WindowMotion.IsRunning(strip), 3000), "ふわっとが終わらない");
                Assert.Equal(T4(s0), T4(Rect(strip)));                    // 出た後に位置が変わらない
                Assert.Equal(0, Native.GetWindowRgnBox(new WindowInteropHelper(strip).Handle, out _));   // 窓の見える範囲の絞りは無い
                Assert.False(((FrameworkElement)strip.Content).RenderTransform is System.Windows.Media.ScaleTransform st1 && st1.ScaleX < 1.0);   // 等倍に戻っている
                Assert.Equal("前の答え", strip.ModelText);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void バーの2行目ステータス表示が1行目のハンドル左端に揃う()
    {
        OnSta(() =>
        {
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(200);
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var handle = Get<System.Windows.Controls.Canvas>(bar, "_handle");
                var status = Get<System.Windows.Controls.TextBlock>(bar, "_statusText");
                Assert.Equal(HorizontalAlignment.Left, status.HorizontalAlignment);
                double handleX = handle.TransformToVisual(bar).Transform(new System.Windows.Point(0, 0)).X;
                double statusX = status.TransformToVisual(bar).Transform(new System.Windows.Point(0, 0)).X;
                Assert.InRange(Math.Abs(statusX - handleX), 0, 1.0);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J20_バーでプリセットを切り替えると_たずねる欄がそのプリセットの設定になる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            // 「OCR」だけ専用の設定で「たずねる」欄を出す。グローバルは既定の OFF
            var arr = (System.Text.Json.Nodes.JsonArray)cfg.GetRaw("prompts")!;
            var ocr = arr.OfType<System.Text.Json.Nodes.JsonObject>().First(p => ConfigStore.NodeToStr(p["name"]) == "OCR");
            ocr["use_global"] = false;
            ocr["settings"] = new System.Text.Json.Nodes.JsonObject { ["show_follow_row"] = true };
            cfg.Settings.ShowFollowRow = false;
            cfg.Settings.SelectPreset("翻訳");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(200);
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                var row = Get<System.Windows.Controls.DockPanel>(panel.View, "_followRow");
                Assert.Equal(Visibility.Collapsed, row.Visibility);

                Assert.True(w.Session.BarPresetSelect("OCR"));
                Pump(50);
                Assert.Equal(Visibility.Visible, row.Visibility);     // OCR の専用の設定（ON）

                Assert.True(w.Session.BarPresetSelect("翻訳"));
                Pump(50);
                Assert.Equal(Visibility.Collapsed, row.Visibility);   // 翻訳はグローバル（OFF）へ戻る
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J24b_式の多い答えが出ている時にバーでプリセットを切り替えても_答えを組み直さない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.Settings.SelectPreset("数式");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(200);
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                panel.View.ResetRun();
                panel.View.ShowFinal(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"式{i}: $\\frac{{a_{i}}}{{b_{i}}} + \\sqrt{{x^{i}}}$")));
                Pump(100);
                int before = panel.View.StyleRebuilds;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Assert.True(w.Session.BarPresetSelect("OCR"));
                Assert.True(w.Session.BarPresetSelect("翻訳"));
                Assert.True(w.Session.BarPresetSelect("数式"));
                long ms = sw.ElapsedMilliseconds;
                Assert.Equal(before, panel.View.StyleRebuilds);   // 見た目は同じなので組み直さない
                Assert.True(ms < 1500, $"切り替え3回で {ms} ms");
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J18b_長いステータスを出したままでも_ステータスの入れ物を並べ直し続けない()
    {
        // バーと同じ形（1行目の幅でバーの幅が決まり、2行目に StatusFitBox）を、画面に出した窓で確かめる。
        // 前は 48 文字ぶんより長い文で、測る幅と並べる幅が食い違って並べ直しが止まらず、画面のスレッドが塞がった
        OnSta(() =>
        {
            EnsureAppResources();
            var text = new System.Windows.Controls.TextBlock
            {
                FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var box = new ChromeBar.StatusFitBox { BaseWidth = 300, Child = text };
            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var row1 = new System.Windows.Controls.Border { Width = 520, Height = 20 };
            System.Windows.Controls.Grid.SetRow(box, 1);
            grid.Children.Add(row1);
            grid.Children.Add(box);
            var win = new Window { Content = grid, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowActivated = false, Left = 0, Top = 0 };
            try
            {
                win.Show();
                foreach (var msg in new[] { "待機中", "モデル: PP-OCRv6（内蔵）（Ollama を使わずにアプリの中で読みます）", new string('あ', 200) })
                {
                    text.Text = msg;
                    Pump(300);                       // 落ち着くまで
                    int n0 = box.ArrangeCount;
                    Pump(1000);                      // その後の1秒で並べ直した回数
                    Assert.True(box.ArrangeCount - n0 <= 2, $"「{msg[..Math.Min(20, msg.Length)]}」を出している間に1秒で {box.ArrangeCount - n0} 回並べ直した");
                    Assert.Equal(520, grid.ActualWidth, 1);                       // バーの中身は伸びない
                    Assert.True(text.ActualWidth <= 520.5);
                }
            }
            finally { win.Close(); }
        });
    }

    [Fact]
    public void J18_ステータスはバーの幅いっぱいまで出て_長い文でもバーは伸びない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(200);
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var status = Get<System.Windows.Controls.TextBlock>(bar, "_statusText");
                var box = Get<ChromeBar.StatusFitBox>(bar, "_statusBox");
                double barW0 = bar.ActualWidth;

                // 全角で 30 字（前の 48 文字ぶんの固定幅では入らなかった長さ）
                bar.SetStatusText("内蔵のOCRで読みました。行ごとに改行した文を出しています");
                bar.UpdateLayout();
                Assert.Equal(barW0, bar.ActualWidth, 1);                       // バーは伸びない
                Assert.True(box.ActualWidth > box.BaseWidth + 1,               // 48文字ぶんより広く使える
                    $"box={box.ActualWidth} base={box.BaseWidth}");
                status.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double full = status.DesiredSize.Width;
                if (full <= box.ActualWidth)
                    Assert.Equal(full, status.ActualWidth, 1);                 // 入るなら切らずに全部出る

                // とても長い文でもバーは伸びず、バーの幅で切れる
                bar.SetStatusText(new string('あ', 200));
                bar.UpdateLayout();
                Assert.Equal(barW0, bar.ActualWidth, 1);
                Assert.True(status.ActualWidth <= box.ActualWidth + 0.5);
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    // ---- 修正J69 ----
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [Fact]
    public void J69_作業領域からはみ出す四角を見分ける()
    {
        var mon = Native.VirtualDesktop();
        var prim = new Native.RECT { Left = 0, Top = 0, Right = Native.GetSystemMetrics(Native.SM_CXSCREEN), Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN) };
        int cx = (prim.Left + prim.Right) / 2, cy = (prim.Top + prim.Bottom) / 2;
        var work = Native.WorkAreaAt(cx, cy);
        Assert.NotNull(work);
        var w = work!.Value;
        // 作業領域の真ん中あたりの小さな四角は、はみ出さない
        var inside = new Native.RECT { Left = cx - 50, Top = cy - 50, Right = cx + 50, Bottom = cy + 50 };
        Assert.False(Native.OutsideWorkArea(inside));
        if (w.Bottom < prim.Bottom)
        {
            // 下にタスクバー: 作業領域の下端をまたぐ四角ははみ出す
            var cross = new Native.RECT { Left = cx - 50, Top = w.Bottom - 20, Right = cx + 50, Bottom = w.Bottom + 20 };
            Assert.True(Native.OutsideWorkArea(cross));
        }
        else
        {
            // 下にタスクバーが無い: モニタの下端をまたぐ四角ははみ出す
            var cross = new Native.RECT { Left = cx - 50, Top = prim.Bottom - 20, Right = cx + 50, Bottom = prim.Bottom + 20 };
            Assert.True(Native.OutsideWorkArea(cross));
        }
    }

    [Fact]
    public void J69_窓の下がタスクバーにかかる時バーは位置はそのままでタスクバーより手前に出る()
    {
        OnSta(() =>
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
            if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return;   // 下のタスクバーが無い環境

            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetBool("chrome_linked", true);
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var bar = Get<ChromeBar>(w.Session, "_bar");
                var capH = new WindowInteropHelper(cap).Handle;
                var barH = new WindowInteropHelper(bar).Handle;
                Native.GetWindowRect(capH, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                // 窓の下端を、作業領域の下端のすぐ上へ（バーの下半分がタスクバーに重なる）
                int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom - 5 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(300);
                bar.PositionChrome();
                Pump(300);
                // タスクバーを押した後と同じ状態にする（タスクバーを最前面の中で一番手前へ）。できない環境では確かめない
                const uint GW_HWNDPREV = 3;
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Pump(100);
                bool trayAbove = false;
                for (var h = GetWindow(barH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    if (h == tray) { trayAbove = true; break; }
                if (!trayAbove) return;
                // 窓を少し動かしてバーを置き直す（ふつうに使っていて PositionChrome が呼ばれる時と同じ）
                Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(300);
                bar.PositionChrome();
                Pump(300);

                Native.GetWindowRect(capH, out var cr2);
                Native.GetWindowRect(barH, out var br);
                Assert.Equal(cr2.Bottom, br.Top);            // F38: 位置は窓の真下のまま（J69 は動かさない）
                Assert.True(Native.OutsideWorkArea(br));     // タスクバーにかかっている

                for (var h = GetWindow(barH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    Assert.NotEqual(tray, h);                // 手前側にタスクバーはいない＝バーがタスクバーより上
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void J70_窓の中の解答欄がタスクバーにかかる時はタスクバーより手前に出る()
    {
        OnSta(() =>
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
            if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return;   // 下のタスクバーが無い環境

            EnsureAppResources();
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
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var capH = new WindowInteropHelper(cap).Handle;
                var panel = Get<AnswerPanel>(w.Session, "_panel");
                panel.ShowFinal("答えです。続き");
                Assert.True(panel.IsVisible);
                Pump(500);
                IntPtr tgtH = new WindowInteropHelper(panel).Handle;
                Assert.NotEqual(IntPtr.Zero, tgtH);
                Native.GetWindowRect(capH, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom + 40 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(600);
                const uint GW_HWNDPREV = 3;
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Pump(100);
                bool trayAbove = false;
                for (var h = GetWindow(tgtH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    if (h == tray) { trayAbove = true; break; }
                if (!trayAbove) return;   // タスクバーを手前に出せない環境
                // 窓を1px動かして置き直す
                Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(600);
                panel.PositionPanel();
                Pump(300);
                Native.GetWindowRect(tgtH, out var tr);
                Assert.True(Native.OutsideWorkArea(tr));     // タスクバーにかかっている
                for (var h = GetWindow(tgtH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    Assert.NotEqual(tray, h);                // 手前側にタスクバーはいない
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }

    [Fact]
    public void J70_外の解答欄がタスクバーにかかる時はタスクバーより手前に出る()
    {
        OnSta(() =>
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
            if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return;   // 下のタスクバーが無い環境

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
                w.Session.OpenCapture(animate: false);
                Pump(600);
                var cap = Get<CaptureWindow>(w.Session, "_capture");
                var capH = new WindowInteropHelper(cap).Handle;
                var strip = Get<AnswerStrip>(w.Session, "_strip");
                strip.ShowFinal("答え");
                Pump(700);
                Assert.True(strip.IsVisible);
                Pump(500);
                IntPtr tgtH = new WindowInteropHelper(strip).Handle;
                Assert.NotEqual(IntPtr.Zero, tgtH);
                Native.GetWindowRect(capH, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom - 5 - ch;
                Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(600);
                const uint GW_HWNDPREV = 3;
                Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Pump(100);
                bool trayAbove = false;
                for (var h = GetWindow(tgtH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    if (h == tray) { trayAbove = true; break; }
                if (!trayAbove) return;   // タスクバーを手前に出せない環境
                // 窓を1px動かして置き直す
                Native.SetWindowPos(capH, IntPtr.Zero, nx + 1, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Pump(600);

                Native.GetWindowRect(tgtH, out var tr);
                Assert.True(Native.OutsideWorkArea(tr));     // タスクバーにかかっている
                for (var h = GetWindow(tgtH, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    Assert.NotEqual(tray, h);                // 手前側にタスクバーはいない
            }
            finally { WindowMotion.Cancel(w); w.Close(); }
        });
    }
    // ---- 修正J71 ----
    private static bool TrayIsAbove(IntPtr tray, IntPtr h)
    {
        const uint GW_HWNDPREV = 3;
        for (var x = GetWindow(h, GW_HWNDPREV); x != IntPtr.Zero; x = GetWindow(x, GW_HWNDPREV))
            if (x == tray) return true;
        return false;
    }

    /// <summary>タスクバーを押した後と同じ状態にする（最前面の中で一番手前へ）。手前に来たら true</summary>
    private static bool ForceTrayAbove(IntPtr tray, IntPtr h)
    {
        Native.SetWindowPos(tray, new IntPtr(-1), 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Pump(100);
        return TrayIsAbove(tray, h);
    }

    /// <summary>"bar" / "inside" / "outside" の窓をタスクバーに重ねて一度手前に出し、(tray, 対象の hwnd) を返す。できない環境では null</summary>
    private (MainWindow w, IntPtr tray, IntPtr tgtH, IntPtr capH, object tgt)? J71Setup(string kind)
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        int sw = Native.GetSystemMetrics(Native.SM_CXSCREEN), sh = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        var work0 = Native.WorkAreaAt(sw / 2, sh / 2);
        if (tray == IntPtr.Zero || work0 is null || work0.Value.Bottom >= sh) return null;   // 下のタスクバーが無い環境

        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetBool("chrome_linked", true);
        cfg.SetBool("quick_enabled", false);
        if (kind != "bar") CfgTestUtil.SetPlace(cfg, kind);
        var w = new MainWindow(_paths, cfg);
        w.Show();
        w.Session.OpenCapture(animate: false);
        Pump(600);
        var cap = Get<CaptureWindow>(w.Session, "_capture");
        var capH = new WindowInteropHelper(cap).Handle;
        object tgt;
        if (kind == "inside") { var p = Get<AnswerPanel>(w.Session, "_panel"); p.ShowFinal("答えです。続き"); Pump(500); tgt = p; }
        else if (kind == "outside") { var st = Get<AnswerStrip>(w.Session, "_strip"); st.ShowFinal("答え"); Pump(1200); tgt = st; }
        else tgt = Get<ChromeBar>(w.Session, "_bar");
        var tgtH = new WindowInteropHelper((Window)tgt).Handle;
        Native.GetWindowRect(capH, out var cr);
        int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
        int nx = Math.Max(0, (sw - cw) / 2), ny = work0.Value.Bottom + (kind == "inside" ? 40 : -5) - ch;
        Native.SetWindowPos(capH, IntPtr.Zero, nx, ny, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Pump(600);
        if (tgt is ChromeBar b) { b.PositionChrome(); Pump(300); }
        if (tgt is AnswerPanel pp) { pp.PositionPanel(); Pump(300); }
        return (w, tray, tgtH, capH, tgt);
    }

    [Fact]
    public void J71_タスクバーを押した後は同じ場所のまま置き直してもバーはタスクバーの裏のまま()
    {
        OnSta(() =>
        {
            if (J71Setup("bar") is not { } s) return;
            try
            {
                var bar = (ChromeBar)s.tgt;
                Native.GetWindowRect(s.tgtH, out var b0);
                if (!Native.OutsideWorkArea(b0)) return;
                if (TrayIsAbove(s.tray, s.tgtH)) return;   // 一度は手前に出ているはず（出ていなければ他のテストの範囲）
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                bar.PositionChrome();
                Pump(300);
                Native.GetWindowRect(s.tgtH, out var b1);
                Assert.Equal(b0.Top, b1.Top);
                Assert.Equal(b0.Left, b1.Left);
                Assert.True(TrayIsAbove(s.tray, s.tgtH));   // 同じ場所の置き直しでは手前に出し直さない
            }
            finally { w_close(s.w); }
        });
    }

    [Fact]
    public void J71_タスクバーを押した後は同じ場所のまま置き直しても窓の中の解答欄はタスクバーの裏のまま()
    {
        OnSta(() =>
        {
            if (J71Setup("inside") is not { } s) return;
            try
            {
                var panel = (AnswerPanel)s.tgt;
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                panel.PositionPanel();
                Pump(300);
                Native.GetWindowRect(s.tgtH, out var r1);
                Assert.Equal(r0.Top, r1.Top);
                Assert.Equal(r0.Left, r1.Left);
                Assert.True(TrayIsAbove(s.tray, s.tgtH));
            }
            finally { w_close(s.w); }
        });
    }

    [Fact]
    public void J71_タスクバーを押した後は同じ場所のまま置き直しても外の解答欄はタスクバーの裏のまま()
    {
        OnSta(() =>
        {
            if (J71Setup("outside") is not { } s) return;
            try
            {
                var strip = (AnswerStrip)s.tgt;
                var bar = Get<ChromeBar>(s.w.Session, "_bar");
                var barH = new WindowInteropHelper(bar).Handle;
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                Native.GetWindowRect(barH, out var br);
                strip.Sync(br);   // バーの今の場所のまま置き直す（巡回）
                Pump(300);
                Native.GetWindowRect(s.tgtH, out var r1);
                Assert.Equal((r0.Left, r0.Top, r0.Right, r0.Bottom), (r1.Left, r1.Top, r1.Right, r1.Bottom));   // 場所は変わらない
                Assert.True(TrayIsAbove(s.tray, s.tgtH));
            }
            finally { w_close(s.w); }
        });
    }

    [Fact]
    public void J71_出し直した時は同じ場所でも窓の中の解答欄はタスクバーより手前に出る()
    {
        OnSta(() =>
        {
            if (J71Setup("inside") is not { } s) return;
            try
            {
                var panel = (AnswerPanel)s.tgt;
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                // 隠して、同じ場所で出し直す
                panel.Hide();
                Pump(300);
                Assert.False(panel.IsVisible);
                bool forced = false;
                panel.IsVisibleChanged += (_, e) =>
                {
                    if (e.NewValue is not true) return;
                    // Render より先に走る優先度で（Show が終わった後・手前出しの前に）タスクバーを手前へ
                    panel.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
                    {
                        Native.SetWindowPos(s.tray, new IntPtr(-1), 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        forced = true;
                    }));
                };
                panel.Show();
                Pump(800);
                Assert.True(forced);
                Assert.True(panel.IsVisible);
                Native.GetWindowRect(s.tgtH, out var r1);
                Assert.Equal(r0.Top, r1.Top);
                Assert.Equal(r0.Left, r1.Left);
                Assert.False(TrayIsAbove(s.tray, s.tgtH));
            }
            finally { w_close(s.w); }
        });
    }

    [Fact]
    public void J71_出し直した時は同じ場所でも外の解答欄はタスクバーより手前に出る()
    {
        OnSta(() =>
        {
            if (J71Setup("outside") is not { } s) return;
            try
            {
                var strip = (AnswerStrip)s.tgt;
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                // Show 直後は窓が手前に来るので、出し直しの手前出し（Render 優先度で後から走る）を見るため、
                // 出た通知の中（手前出しが走る前）でタスクバーを手前へ戻す
                bool forced = false;
                strip.IsVisibleChanged += (_, e) =>
                {
                    if (e.NewValue is not true) return;
                    // Render より先に走る優先度で（Show が終わった後・手前出しの前に）タスクバーを手前へ
                    strip.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
                    {
                        Native.SetWindowPos(s.tray, new IntPtr(-1), 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        forced = true;
                    }));
                };
                strip.Hide();
                Pump(200);
                strip.Show();
                Pump(800);
                Assert.True(forced);
                Assert.True(strip.IsVisible);
                Native.GetWindowRect(s.tgtH, out var r1);
                Assert.Equal((r0.Left, r0.Top), (r1.Left, r1.Top));
                Assert.False(TrayIsAbove(s.tray, s.tgtH));
            }
            finally { w_close(s.w); }
        });
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("inside")]
    [InlineData("outside")]
    public void J72_このアプリの窓がアクティブになった後はタスクバーが手前に戻っても少し後にまた手前へ出る_タスクバーがアクティブなら裏のまま(string kind)
    {
        OnSta(() =>
        {
            if (J71Setup(kind) is not { } s) return;
            try
            {
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                var front = s.w.Session.TaskbarFrontForTests;
                front.AppIsForeground = () => true;   // テストの間はほかのアプリがアクティブなことがあるので、このアプリがアクティブとみなす
                // タスクバーがアクティブになった（人がタスクバーを押した）: 裏のまま
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                front.OnForeground(s.tray);
                Pump(700);
                Assert.True(TrayIsAbove(s.tray, s.tgtH), "タスクバーを押した後なのに手前へ出た");
                // このアプリの窓がアクティブになり、その後タスクバーが自分を手前へ出した: 少し後に手前へ戻る
                front.OnForeground(s.capH);
                Pump(50);
                ForceTrayAbove(s.tray, s.tgtH);
                Pump(700);
                Assert.False(TrayIsAbove(s.tray, s.tgtH), "アプリの窓がアクティブになった後、手前へ戻らない");
            }
            finally { w_close(s.w); }
        });
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("inside")]
    [InlineData("outside")]
    public void J73_アクティブになった後にタスクバーが何度手前に来てもすぐ戻り_裏と手前を行き来しない(string kind)
    {
        OnSta(() =>
        {
            if (J71Setup(kind) is not { } s) return;
            try
            {
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                var front = s.w.Session.TaskbarFrontForTests;
                front.AppIsForeground = () => true;   // テストの間はほかのアプリがアクティブなことがあるので、このアプリがアクティブとみなす
                front.OnForeground(s.capH);
                Assert.True(front.Watching);
                // タスクバーが自分を手前へ出すのを何度かまねる。どれも 30ms 以内に戻る（前は 150ms・450ms まで裏のままだった）
                for (int i = 0; i < 4; i++)
                {
                    if (!ForceTrayAbove(s.tray, s.tgtH)) return;
                    Pump(30);
                    Assert.False(TrayIsAbove(s.tray, s.tgtH), $"{i} 回目: 30ms たってもタスクバーの裏のまま");
                }
                Pump(TaskbarFront.WatchMs + 200);
                Assert.False(front.Watching);   // 見張りはしばらくで終わる（タスクバーを押したら裏のままにできる）
                Assert.True(ForceTrayAbove(s.tray, s.tgtH));
                Pump(100);
                Assert.True(TrayIsAbove(s.tray, s.tgtH), "見張りが終わった後も手前へ戻した");
            }
            finally { w_close(s.w); }
        });
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("inside")]
    [InlineData("outside")]
    public void J74_タスクバーを押した後はしばらく待っても裏のまま(string kind)
    {
        OnSta(() =>
        {
            if (J71Setup(kind) is not { } s) return;
            try
            {
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                Pump(TaskbarFront.WatchMs + 300);              // 見張りが終わるまで待つ
                if (!ForceTrayAbove(s.tray, s.tgtH)) return;  // タスクバーを押した
                s.w.Session.TaskbarFrontForTests.OnForeground(s.tray);
                for (int i = 0; i < 20; i++)
                {
                    Pump(100);
                    Assert.True(TrayIsAbove(s.tray, s.tgtH), $"{(i + 1) * 100}ms 後に手前へ戻った");
                }
            }
            finally { w_close(s.w); }
        });
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("inside")]
    [InlineData("outside")]
    public void J74_見張りの途中でもタスクバーがアクティブになったら手前へ戻すのをやめる(string kind)
    {
        OnSta(() =>
        {
            if (J71Setup(kind) is not { } s) return;
            try
            {
                Native.GetWindowRect(s.tgtH, out var r0);
                if (!Native.OutsideWorkArea(r0)) return;
                var front = s.w.Session.TaskbarFrontForTests;
                bool appFg = true;
                front.AppIsForeground = () => appFg;
                front.OnForeground(s.capH);            // このアプリがアクティブになった（見張り開始）
                Pump(50);
                Assert.True(front.Watching);
                // 見張りの途中（800ms より前）にタスクバーを押した: アクティブがタスクバーへ移り、タスクバーが手前に来る
                appFg = false;
                front.OnForeground(s.tray);
                Pump(50);
                Assert.True(ForceTrayAbove(s.tray, s.tgtH), "タスクバーを押した後なのに、手前へ戻されてタスクバーを手前に出せない");
                for (int i = 0; i < 10; i++)
                {
                    Pump(100);
                    Assert.True(TrayIsAbove(s.tray, s.tgtH), $"タスクバーを押してから {(i + 1) * 100}ms 後に手前へ戻った");
                }
                Assert.False(front.Watching);
            }
            finally { w_close(s.w); }
        });
    }

    private static void w_close(MainWindow w) { WindowMotion.Cancel(w); w.Close(); }
}

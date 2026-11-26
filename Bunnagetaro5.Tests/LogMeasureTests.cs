using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Bunnagetaro5.Views;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>計測（人が触っていない時に、画面の外の窓で・入力を注入せずに）。
/// 環境変数 B5_MEASURE=1 の時だけ動く。結果は B5_MEASURE_OUT のファイルへ書く。</summary>
public class LogMeasureTests
{
    private static readonly bool On = Environment.GetEnvironmentVariable("B5_MEASURE") == "1";

    private static void Run(Func<Task> body)
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
                }
                var d = Dispatcher.CurrentDispatcher;
                var t = d.InvokeAsync(body).Task.Unwrap();
                var frame = new DispatcherFrame();
                _ = t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                t.GetAwaiter().GetResult();
            }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (err is not null) throw err;
    }

    private static void Collect<T>(DependencyObject root, List<T> acc) where T : DependencyObject
    {
        if (root is T t) acc.Add(t);
        for (int i = 0, c = VisualTreeHelper.GetChildrenCount(root); i < c; i++)
            Collect(VisualTreeHelper.GetChild(root, i), acc);
    }

    /// <summary>見えているカード（画面にかかっているもの）の式のうち、まだ画像になっていない数</summary>
    private static int PendingVisible(ListBox lst, ScrollViewer sc, out int total)
    {
        var views = new List<AnswerView>();
        Collect(lst, views);
        int pending = 0; total = 0;
        foreach (var v in views)
        {
            if (!v.IsVisible) continue;
            var top = v.TranslatePoint(new Point(0, 0), sc).Y;
            if (top + v.ActualHeight < 0 || top > sc.ViewportHeight) continue;
            foreach (var b in v.Body.Document.Blocks)
            {
                if (b is not Paragraph p) continue;
                foreach (var il in p.Inlines)
                {
                    if (il is InlineUIContainer) { total++; continue; }
                    if (il is Run r && r.Text.StartsWith("$")) { total++; pending++; }
                }
            }
        }
        return pending;
    }

    private static async Task<double> WaitAllShown(Window win, ListBox lst, ScrollViewer sc, Stopwatch sw, int timeoutMs = 120000)
    {
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            win.UpdateLayout();
            if (PendingVisible(lst, sc, out int total) == 0 && total > 0) return sw.Elapsed.TotalMilliseconds;
            await Task.Delay(5);
        }
        return -1;
    }

    private static double Pct(List<double> xs, double p)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)];
    }

    [Fact]
    public void H4a_計測_数式入りカード100件()
    {
        if (!On) return;
        Measure(100, withImages: false, "measure.txt");
    }

    /// <summary>ログを全部（台帳の上限 200 件）出す前と後で比べる。サムネイル画像付き・メモリも測る。</summary>
    [Fact]
    public void J14_計測_数式と画像入りカード200件()
    {
        if (!On) return;
        Measure(LogStore.MaxEntries, withImages: true, "measure_j14.txt");
    }

    /// <summary>スクロールバーのつまみを素早く引いた時（文だけのカード）。つまみに DragStarted・DragDelta・DragCompleted を
    /// 送って、人が約1秒で端から端へ引く動きを真似る（マウスは動かさない）。DragDelta は別のスレッドから 16ms ごとに入力の順位で積む。</summary>
    [Fact]
    public void J15_計測_つまみを素早く引いた時()
    {
        if (!On) return;
        string temp = Path.Combine(Path.GetTempPath(), "b5_measure_shared");
        Directory.CreateDirectory(temp);
        var outFile = Path.Combine(temp, "measure_j15.txt");
        var lines = new List<string>();
        foreach (int count in new[] { 60, 200 })
        {
            var paths = new Paths(Path.Combine(temp, "Bunnagetaro5_" + Guid.NewGuid().ToString("N")));
            AppLog.Init(paths);
            Run(async () =>
            {
                var store = new LogStore(paths);
                var rnd = new Random(5);
                for (int i = 0; i < count; i++)
                    store.InsertAndDump(new JsonObject
                    {
                        ["id"] = "t" + i,
                        ["ts"] = $"2026-11-18 {i / 60 % 24:00}:{i % 60:00}:00",
                        ["status"] = "ok",
                        ["text"] = string.Join("\n\n", Enumerable.Range(0, 1 + i % 4).Select(k =>
                            $"段落{k}。" + new string('文', 40 + rnd.Next(300)))),
                    });
                var page = new LogPage(store, _ => { }, () => PanelStyle.From(new ConfigStore(paths)));
                var win = new Window { Width = 760, Height = 900, Content = page, ShowActivated = false, ShowInTaskbar = false,
                                       WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = 0 };
                win.Show();
                win.UpdateLayout();
                await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(300);
                var sc = (ScrollViewer)typeof(LogPage).GetProperty("Sc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
                var sb = (System.Windows.Controls.Primitives.ScrollBar)sc.Template.FindName("PART_VerticalScrollBar", sc);
                var thumb = sb.Track.Thumb;
                for (int round = 0; round < 2; round++)   // 1回目は下→上、2回目は上→下
                {
                    const int Steps = 60;
                    double trackLen = sb.Track.ActualHeight - thumb.ActualHeight;
                    double step = (round == 0 ? -1 : 1) * trackLen / Steps;
                    var lat = new List<double>();
                    var gaps = new List<double>();
                    var clock = Stopwatch.StartNew();
                    // ぷるぷるの計測: 描くコマごとのつまみの位置（Track の中の上端）とスクロールできる高さ
                    var thumbYs = new List<double>(); var extents = new List<double>();
                    EventHandler onRender = (_, _) => { try { thumbYs.Add(thumb.TranslatePoint(new Point(0, 0), sb.Track).Y); extents.Add(sc.ScrollableHeight); } catch { } };
                    CompositionTarget.Rendering += onRender;
                    thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                    var d = win.Dispatcher;
                    var poster = Task.Run(async () =>
                    {
                        for (int k = 0; k < Steps; k++)
                        {
                            double posted = clock.Elapsed.TotalMilliseconds;
                            _ = d.BeginInvoke(DispatcherPriority.Input, () =>
                            {
                                lat.Add(clock.Elapsed.TotalMilliseconds - posted);
                                gaps.Add(Math.Abs(sb.Value - sc.VerticalOffset));   // 次に動かす直前: 中身がつまみにどれだけ追いついているか
                                thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, step));
                            });
                            await Task.Delay(16);
                        }
                    });
                    while (!poster.IsCompleted) await Task.Delay(5);
                    await d.InvokeAsync(() => { }, DispatcherPriority.Input);
                    double dragEnd = clock.Elapsed.TotalMilliseconds;
                    // 止めてから（離す前）中身がつまみに追いつくまで
                    while (Math.Abs(sb.Value - sc.VerticalOffset) > 1 && clock.Elapsed.TotalMilliseconds - dragEnd < 5000)
                        await Task.Delay(5);
                    await d.InvokeAsync(() => { }, DispatcherPriority.Render);
                    double caught = clock.Elapsed.TotalMilliseconds - dragEnd;
                    CompositionTarget.Rendering -= onRender;
                    thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false));
                    await d.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    lines.Add($"カード {count} 枚・{(round == 0 ? "下→上" : "上→下")}: 入力の待ち 中央値 {Pct(lat, 0.5):F1} ms / p95 {Pct(lat, 0.95):F1} ms / 最大 {lat.Max():F1} ms・" +
                              $"次に動かす直前の中身とつまみのずれ 中央値 {Pct(gaps, 0.5):F0} px / 最大 {gaps.Max():F0} px・止めてから追いつくまで {caught:F0} ms・引き終わりまで {dragEnd:F0} ms（入力 {lat.Count} 回）");
                    { double dir = round == 0 ? -1 : 1; int back = 0; double maxBack = 0; for (int q = 1; q < thumbYs.Count; q++) { double mv = (thumbYs[q] - thumbYs[q - 1]) * dir; if (mv < -0.5) { back++; maxBack = Math.Max(maxBack, -mv); } }
                      int extCh = 0; for (int q = 1; q < extents.Count; q++) if (Math.Abs(extents[q] - extents[q - 1]) > 0.5) extCh++;
                      lines.Add($"    つまみ: 描いたコマ {thumbYs.Count}・逆向きに戻った {back} 回（最大 {maxBack:F1} px）・スクロールできる高さが変わった {extCh} 回（{(extents.Count > 0 ? extents.Min() : 0):F0}〜{(extents.Count > 0 ? extents.Max() : 0):F0} px）"); }
                }
                win.Close();
            });
        }
        File.WriteAllLines(outFile, lines);
    }
    /// <summary>画面のスクショに近い 1280x800 の PNG（文字の帯を並べた絵）を1枚作る</summary>
    private static byte[] MakeShotPng()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1280, 800));
            var rnd = new Random(3);
            for (int y = 20; y < 780; y += 22)
                for (int x = 20; x < 1240; x += rnd.Next(30, 90))
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))),
                                     null, new Rect(x, y, rnd.Next(15, 60), 12));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static long PrivateMb()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64 / (1024 * 1024);
    }

    private static void Measure(int count, bool withImages, string outName)
    {
        string temp = Path.Combine(Path.GetTempPath(), "b5_measure_shared");
        Directory.CreateDirectory(temp);
        var paths = new Paths(Path.Combine(temp, "Bunnagetaro5_" + Guid.NewGuid().ToString("N")));
        AppLog.Init(paths);
        var outFile = Environment.GetEnvironmentVariable("B5_MEASURE_OUT") ?? Path.Combine(temp, outName);
        var lines = new List<string>();
        Run(async () =>
        {
            MathRenderer.ResetForTests();
            await MathRenderer.Start(paths);
            Assert.True(MathRenderer.Ready);
            var store = new LogStore(paths);
            byte[]? png = withImages ? MakeShotPng() : null;
            if (png is not null) Directory.CreateDirectory(paths.ImagesDir);
            for (int i = 0; i < count; i++)
            {
                var e = new JsonObject
                {
                    ["id"] = "m" + i,
                    ["ts"] = $"2026-11-12 {i / 60 % 24:00}:{i % 60:00}:00",
                    ["status"] = "ok",
                    ["text"] = $"問{i}の答え。\n\n$$\\int_{{0}}^{{{i + 1}}} \\frac{{x^{{{i % 7 + 2}}}}}{{1+x^2}}\\,dx = \\sum_{{k=1}}^{{{i + 3}}} \\frac{{1}}{{k^{{2}}}}$$\n\n続きの文章 $a_{{{i}}}+b_{{{i}}}$ を使う。\n\n$$\\sqrt{{{i + 2}}} = \\frac{{{i + 1}}}{{\\sqrt{{{i + 2}}}}}$$ おわり。",
                };
                if (png is not null)
                {
                    string name = $"m{i}.png";
                    File.WriteAllBytes(Path.Combine(paths.ImagesDir, name), png);
                    e["image"] = name;
                }
                store.InsertAndDump(e);
            }
            long memBefore = PrivateMb();
            var swOpen = Stopwatch.StartNew();
            var page = new LogPage(store, _ => { }, () => PanelStyle.From(new ConfigStore(paths)));
            var win = new Window { Width = 760, Height = 900, Content = page, ShowActivated = false, ShowInTaskbar = false,
                                   WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = 0 };
            win.Show();
            var lst = (ListBox)page.FindName("Lst");
            var sc = (ScrollViewer)typeof(LogPage).GetProperty("Sc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
            var items = (System.Collections.ObjectModel.ObservableCollection<object>)
                typeof(LogPage).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
            int cards = items.OfType<LogCardVm>().Count();
            lines.Add($"0. 台帳 {count} 件・画面のカード {cards} 枚・画像 {(withImages ? "あり(1280x800)" : "なし")}");
            // ① ログを開いてから見えている式が全部出るまで（開いた時は一番下＝新しい方）
            var sw = swOpen;
            win.UpdateLayout();
            page.ShowAtBottom();
            double t1 = await WaitAllShown(win, lst, sc, sw);
            PendingVisible(lst, sc, out int total1);
            lines.Add($"1. 開いてから見えている式が全部出るまで: {t1:F0} ms（見えている式 {total1} 個、skipped={MathRenderer.SkippedCancelled}）");
            if (withImages)
            {
                var tsw = Stopwatch.StartNew();
                while (items.OfType<LogCardVm>().Any(c => c.HasImageFile && c.ThumbSource is null) && tsw.ElapsedMilliseconds < 60000)
                    await Task.Delay(20);
                lines.Add($"1b. 全カードのサムネイルが読み終わるまで: 開いてから {sw.Elapsed.TotalMilliseconds:F0} ms");
                long memAfter = PrivateMb();
                lines.Add($"1c. メモリ（プライベート）: 開く前 {memBefore} MB → 開いた後 {memAfter} MB（+{memAfter - memBefore} MB）");
            }

            // ② 窓の幅を広げ縮めする時の1フレーム（レイアウト＋描画まで）
            var frames = new List<double>();
            for (int k = 0; k < 120; k++)
            {
                double w = 760 + (k % 2 == 0 ? 1 : -1) * (10 + (k % 30) * 6);
                var fsw = Stopwatch.StartNew();
                win.Width = w;
                win.UpdateLayout();
                await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                fsw.Stop();
                frames.Add(fsw.Elapsed.TotalMilliseconds);
            }
            lines.Add($"2. 幅の広げ縮め 1フレーム: 中央値 {Pct(frames, 0.5):F1} ms / p95 {Pct(frames, 0.95):F1} ms（{frames.Count}回）");

            // ③ 一気に先頭（古い側・未描画）へスクロールして止めた所の式が出るまで
            // 先に全体を一気に流して通り過ぎさせる → 止めた所の式が後回しにならないか
            win.Width = 760;
            win.UpdateLayout();
            var rnd = new Random(7);
            int skippedBefore = MathRenderer.SkippedCancelled;
            for (double off = sc.VerticalOffset; off > 0; off -= 700)
            {
                sc.ScrollToVerticalOffset(Math.Max(0, off));
                win.UpdateLayout();
                await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
            sc.ScrollToVerticalOffset(sc.ScrollableHeight * 0.45);   // 止めた所（まだ描いていない中ほど）
            win.UpdateLayout();
            var sw3 = Stopwatch.StartNew();
            double t3 = await WaitAllShown(win, lst, sc, sw3);
            PendingVisible(lst, sc, out int total3);
            lines.Add($"3. 一気にスクロールして止めた所の式が出るまで: {t3:F0} ms（止めた所の式 {total3} 個、取り消して飛ばした式 {MathRenderer.SkippedCancelled - skippedBefore} 個）");
            win.Close();
            MathRenderer.Shutdown(5000);
        });
        File.WriteAllLines(outFile, lines);
    }
}

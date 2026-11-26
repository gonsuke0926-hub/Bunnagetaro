using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 手順28: 性能の確認。1万字を超える思考を流しているあいだ、UIスレッドが50ms以上
/// 止まらないこと（指示書L492）。流し方は実アプリと同じ便で、裏の「workerスレッド」
/// から60字ごとに BeginInvoke（MainWindow の Push と同じ形）。遅れは
/// DispatcherTimer の tick 間隔そのもので測り、操作1個のapply実時間も併記する。
/// 値は app.log（テスト用一時）へログも残す（指示書の「テストかログ」の両方）。
/// </summary>
// 整理（2026-11-22）: 時間を測って合否を決めるので、PC が忙しいと落ちる。普段の全体の流しでは外す（AGENTS.md「テストの流し方」）
[Trait("Category", "Perf")]
public class PanelPerfTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public PanelPerfTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        _temp = Path.Combine(Path.GetTempPath(), "b5_perf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private const int LimitMs = 50;   // 指示書の手順28の50ms

    // ---- helpers（PanelMathTests と同じ形） ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(TimeSpan.FromSeconds(90));
        if (err is not null) throw err;
        return result;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic |
                                 System.Reflection.BindingFlags.Instance)?.GetValue(o);

    private MainWindow NewPanel(out AnswerPanel panel)
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(w.Dispatcher, 250);
        w.Session.OpenCapture();
        Pump(w.Dispatcher, 250);
        panel = (AnswerPanel)Fld(w.Session, "_panel")!;
        Assert.NotNull(panel);
        return w;
    }

    // ---- 流れの組み立て ----

    /// <summary>1万字超を作るための日本語文の連結（4つごとに改行）</summary>
    private static string MakeText(int units)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < units; i++)
        {
            sb.Append("思考の文章 ").Append(i).Append(" 番目、文字を足して長さを稼いでいく。実測のための模擬だが流す道具立ては本物。");
            if (i % 4 == 3) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static (int ticks, double maxGap, double maxApply, double maxProbe, double probeAt,
                    int probes, int done, int sent, string slowGaps, string slowApply)
    StreamAndMeasure(MainWindow w, AnswerPanel panel, string full, Action<string> feed)
    {
        var d = w.Dispatcher;
        // 実アプリのworkerと同じ間隔・同じ「これまでの全文」ペイロードで流す
        // （実アプリは40ms間引きなので、この速さでUIが詰まるなら本番でも50ms停止が出る）
        const int unit = 200;

        // 面を一度出しておく初回コスト（窓のShow・最初のレイアウト）は計測の外で済ませる
        feed(full[..unit]);
        Pump(d, 300);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        int done = 0, sent = 0, ticks = 0, probes = 0;
        double maxApply = 0, maxGap = 0, maxProbe = 0, probeAt = 0, prev = -1;
        var slowGaps = new List<string>();
        var slowApply = new List<string>();
        bool feederDone = false, stopScheduled = false;
        double stopAt = 0;
        var swAll = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        // ハートビート兼プローブ。Windowsのタイマー粒度（約15.6ms）のせいで
        // DispatcherTimer の tick 間隔そのものは空いている時でも20〜30ms開く。
        // なので「50ms止まっていない」の判定は、tickから同じ優先度で積み直した
        // 何もしない操作が走り出すまでの待ち時間（＝UIスレッドが仕事で塞がっている時間）で測る。
        var timer = new DispatcherTimer(DispatcherPriority.Normal, d)
        { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            double t0 = swAll.Elapsed.TotalMilliseconds;
            d.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                double waited = swAll.Elapsed.TotalMilliseconds - t0;
                probes++;
                if (waited > maxProbe) { maxProbe = waited; probeAt = t0; }
            }));
            if (prev >= 0 && t0 - prev > 20) slowGaps.Add($"t={t0:F0}ms gap={t0 - prev:F1} done={Volatile.Read(ref done)}");
            if (prev >= 0) maxGap = Math.Max(maxGap, t0 - prev);
            prev = t0;
            if (!stopScheduled && feederDone && Volatile.Read(ref done) >= Volatile.Read(ref sent))
            {
                stopScheduled = true;
                stopAt = t0 + 200;   // 最後の流しの後のレイアウト・描画まで見てから閉める
            }
            if ((stopScheduled && t0 >= stopAt) || swAll.ElapsedMilliseconds > 25000)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();

        // workerスレッド便: 裏から200字ごとの「これまでの全文」を BeginInvoke で積む
        var th = new Thread(() =>
        {
            for (int cut = 2 * unit; ; cut += unit)
            {
                int take = Math.Min(cut, full.Length);
                string payload = full[..take];
                d.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    var sw = Stopwatch.StartNew();
                    feed(payload);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    if (ms > 20) slowApply.Add($"t={swAll.Elapsed.TotalMilliseconds:F0} apply={ms:F1} len={payload.Length}");
                    if (ms > maxApply) maxApply = ms;
                    Interlocked.Increment(ref done);
                }));
                Interlocked.Increment(ref sent);
                if (take >= full.Length) break;
                Thread.Sleep(40);   // 実アプリのworker側40ms間引きと同じ刻み
            }
            feederDone = true;
        });
        th.IsBackground = true;
        th.Start();

        Dispatcher.PushFrame(frame);
        timer.Stop();
        return (ticks, maxGap, maxApply, maxProbe, probeAt, probes, done, sent,
                string.Join(" | ", slowGaps), string.Join(" | ", slowApply));
    }

    // ---- tests ----

    [Fact]
    public void 万字超の思考ストリーム中にUIスレッドは50ms以上止まらない()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            string full = MakeText(240);
            Assert.True(full.Length > 10000, $"模擬だが {full.Length} 字（1万字超が必要）");

            // 思考欄は本文deltaで初めて出る面なので、面だけは先に出しておく
            panel.ApplyBody("ストリーム準備");
            Pump(w.Dispatcher, 300);

            var r = StreamAndMeasure(w, panel, full, panel.ApplyThink);
            AppLog.Info($"perf think chunks={r.done} ticks={r.ticks} probes={r.probes} " +
                        $"maxGap={r.maxGap:F1}ms maxApply={r.maxApply:F1}ms maxProbe={r.maxProbe:F1}ms@{r.probeAt:F0}");
            _out.WriteLine($"think流し: 字数={full.Length} chunks={r.sent} 最大apply={r.maxApply:F1}ms 最大待ち={r.maxProbe:F1}ms tick間隔最大(参考)={r.maxGap:F1}ms");

            Assert.Equal(r.sent, r.done);                       // 全部UI側で処理済み
            Assert.True(r.sent >= 40, $"delta数={r.sent}（1万字超を200字・40ms間隔で流す）");
            Assert.True(r.ticks >= 40, $"タイマーtick={r.ticks}（計測が動いていない）");
            Assert.True(r.probes >= 40, $"プローブ={r.probes}");
            Assert.True(r.maxApply < LimitMs, $"操作1個の最大 {r.maxApply:F1}ms ≥ {LimitMs}ms / slowApply=[{r.slowApply}]");
            Assert.True(r.maxProbe < LimitMs, $"積み直し操作の待ち最大 {r.maxProbe:F1}ms ≥ {LimitMs}ms（t={r.probeAt:F0}）/ slowGaps=[{r.slowGaps}]");

            // 中身も本物: 思考欄には最後の4000字が載っている
            var think = (TextBlock)Fld(panel.View, "_think")!;
            Assert.Equal(StreamText.ThinkTail(full), think.Text);
            w.Close();
            return true;
        });
    }

    [Fact]
    public void 万字超の本文ストリームでも止まらない_続きものfast_path()
    {
        OnSta(() =>
        {
            var w = NewPanel(out var panel);
            string full = MakeText(230);
            Assert.True(full.Length > 10000);

            var r = StreamAndMeasure(w, panel, full, panel.ApplyBody);
            AppLog.Info($"perf body chunks={r.done} ticks={r.ticks} probes={r.probes} " +
                        $"maxGap={r.maxGap:F1}ms maxApply={r.maxApply:F1}ms maxProbe={r.maxProbe:F1}ms@{r.probeAt:F0}");
            _out.WriteLine($"body流し: 字数={full.Length} chunks={r.sent} 最大apply={r.maxApply:F1}ms 最大待ち={r.maxProbe:F1}ms tick間隔最大(参考)={r.maxGap:F1}ms");

            Assert.Equal(r.sent, r.done);
            Assert.True(r.sent >= 40, $"delta数={r.sent}");
            Assert.True(r.probes >= 40, $"プローブ={r.probes}");
            Assert.True(r.maxApply < LimitMs, $"操作1個の最大 {r.maxApply:F1}ms ≥ {LimitMs}ms / slowApply=[{r.slowApply}]");
            Assert.True(r.maxProbe < LimitMs, $"積み直し操作の待ち最大 {r.maxProbe:F1}ms ≥ {LimitMs}ms（t={r.probeAt:F0}）/ slowGaps=[{r.slowGaps}]");

            // 全文が正しく載っている（fast pathを貫通して ModelText も全文）
            Assert.Equal(full, panel.ModelText);
            var body = (RichTextBox)Fld(panel.View, "_body")!;
            Assert.True(body.Document.Blocks.Count >= 1);
            w.Close();
            return true;
        });
    }
}

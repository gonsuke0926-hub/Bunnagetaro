using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;
using Xunit.Abstractions;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 計測: 長い思考が流れている間のカクつきの手がかりを、2つに分けて測る。
///  A. OllamaClient の string.Concat(thinkParts)（思考のかけらのたびに全文を作り直す）の1回の時間
///  B. AnswerView.ApplyThink（末尾4000字を TextBlock へ入れ直し＋ScrollToEnd）の1回の時間。
///     Text の入れ直し・ScrollToEnd・それぞれのレイアウトを分けて測る。窓を SetWindowPos で動かす1回の時間も併記。
/// 値は出力へ。判定は「測れたこと」だけ（時間の閾値では落とさない）。マウス・キーは使わない
/// </summary>
public class ThinkCostProbeTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _temp;
    private readonly Paths _paths;

    public ThinkCostProbeTests(ITestOutputHelper o)
    {
        _o = o;
        _temp = Path.Combine(Path.GetTempPath(), "b5_thinkcost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Stat(List<double> v)
    {
        if (v.Count == 0) return "(なし)";
        var s = v.OrderBy(x => x).ToList();
        return $"n={s.Count} 中央値={s[s.Count / 2]:F3}ms p95={s[(int)(s.Count * 0.95)]:F3}ms 最大={s[^1]:F3}ms";
    }

    [Fact]
    public void A_ThinkPartsのConcat1回の時間()
    {
        // 思考のかけらは数文字ずつ届く想定（日本語で1〜4字）。10k / 30k / 100k 字
        foreach (int total in new[] { 10_000, 30_000, 100_000 })
        {
            var parts = new List<string>();
            int n = 0;
            while (n < total) { parts.Add("思考の断片".Substring(0, 1 + n % 4)); n += parts[^1].Length; }
            for (int i = 0; i < 50; i++) _ = string.Concat(parts);   // 温め
            var t = new List<double>();
            for (int i = 0; i < 300; i++)
            {
                long a = Stopwatch.GetTimestamp();
                var s = string.Concat(parts);
                t.Add(Ms(Stopwatch.GetTimestamp() - a));
                GC.KeepAlive(s);
            }
            _o.WriteLine($"A Concat {total}字（かけら {parts.Count} 個）: {Stat(t)}");
            _o.WriteLine($"   -> 最大でも 25回/秒 x 中央値 = {25 * t.OrderBy(x => x).ToList()[t.Count / 2]:F3} ms/秒");
        }
        Assert.True(true);
    }

    private static T Fld<T>(object o, string name)
        => (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void PumpPriority(DispatcherPriority p)
    {
        var f = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(p, new Action(() => f.Continue = false));
        Dispatcher.PushFrame(f);
    }

    [Fact]
    public void B_ApplyThinkの内訳と窓を動かす1回の時間()
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { RunB(); } catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException();
        if (err is not null) throw err;
    }

    private void RunB()
    {
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
        }
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetStr("result_place", "inside");
        var w = new MainWindow(_paths, cfg);
        try
        {
            w.Show();
            Pump(250);
            w.Session.OpenCapture(animate: false);
            Pump(300);
            var panel = Fld<AnswerPanel>(w.Session, "_panel");
            panel.ApplyBody("計測の準備");
            Pump(400);
            var view = panel.View;
            var think = Fld<TextBlock>(view, "_think");
            var sv = Fld<ScrollViewer>(view, "_thinkSv");
            var cap = Fld<CaptureWindow>(w.Session, "_capture");
            var capH = new WindowInteropHelper(cap).Handle;

            var sb = new StringBuilder();
            for (int i = 0; sb.Length < 14000; i++)
                sb.Append("思考の文章 ").Append(i).Append(" 番目、文字を足して長さを稼いでいく。実測のための模擬だが流す道具立ては本物。").Append(i % 4 == 3 ? "\n" : "");
            string full = sb.ToString();

            var apply = new List<double>(); var layoutAfter = new List<double>(); var renderAfter = new List<double>();
            var applyBig = new List<double>(); var layoutBig = new List<double>(); var renderBig = new List<double>();
            var textOnly = new List<double>(); var textLayout = new List<double>();
            var scrollOnly = new List<double>(); var scrollLayout = new List<double>();
            var moveCall = new List<double>(); var moveBase = new List<double>();

            Native.GetWindowRect(capH, out var r0);
            int dx = 0;
            // 窓を動かす1回の時間の基準（流していない時）
            for (int i = 0; i < 40; i++)
            {
                dx = (dx + 3) % 60;
                long a0 = Stopwatch.GetTimestamp();
                Native.SetWindowPos(capH, IntPtr.Zero, r0.Left + dx, r0.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                moveBase.Add(Ms(Stopwatch.GetTimestamp() - a0));
                PumpPriority(DispatcherPriority.Render);
            }

            for (int cut = 200; cut <= full.Length; cut += 200)
            {
                string payload = full[..cut];
                long a = Stopwatch.GetTimestamp();
                view.ApplyThink(payload);                                  // 本物の経路（Text 入れ直し＋ScrollToEnd）
                double tApply = Ms(Stopwatch.GetTimestamp() - a);
                a = Stopwatch.GetTimestamp();
                panel.UpdateLayout();                                      // 強制レイアウト（本来は次のフレームで走る分）
                double tLay = Ms(Stopwatch.GetTimestamp() - a);
                a = Stopwatch.GetTimestamp();
                PumpPriority(DispatcherPriority.Render);                   // 描画の積み残し
                double tRen = Ms(Stopwatch.GetTimestamp() - a);
                (cut <= 4000 ? apply : applyBig).Add(tApply);
                (cut <= 4000 ? layoutAfter : layoutBig).Add(tLay);
                (cut <= 4000 ? renderAfter : renderBig).Add(tRen);

                // 窓を動かす1回（流している最中の SetWindowPos。バー・解答欄が同じフレームで動く）
                dx = (dx + 3) % 60;
                a = Stopwatch.GetTimestamp();
                Native.SetWindowPos(capH, IntPtr.Zero, r0.Left + dx, r0.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                moveCall.Add(Ms(Stopwatch.GetTimestamp() - a));
                PumpPriority(DispatcherPriority.Render);

                // 内訳（4000字を超えてから）: Text だけ入れ直した時 / ScrollToEnd だけの時
                if (cut > 4000)
                {
                    string tail = StreamText.ThinkTail(payload + "x");   // いつも違う文字列にして入れ直しを起こす
                    a = Stopwatch.GetTimestamp();
                    think.Text = tail;
                    textOnly.Add(Ms(Stopwatch.GetTimestamp() - a));
                    a = Stopwatch.GetTimestamp();
                    panel.UpdateLayout();
                    textLayout.Add(Ms(Stopwatch.GetTimestamp() - a));
                    a = Stopwatch.GetTimestamp();
                    sv.ScrollToEnd();
                    scrollOnly.Add(Ms(Stopwatch.GetTimestamp() - a));
                    a = Stopwatch.GetTimestamp();
                    panel.UpdateLayout();
                    scrollLayout.Add(Ms(Stopwatch.GetTimestamp() - a));
                }
            }
            _o.WriteLine($"B 思考欄の高さ上限={sv.MaxHeight:F0} 実高さ={sv.ActualHeight:F0} テキスト長={think.Text.Length}");
            _o.WriteLine($"B ApplyThink 4000字まで（足し込み）  : {Stat(apply)}");
            _o.WriteLine($"B   その後のレイアウト               : {Stat(layoutAfter)}");
            _o.WriteLine($"B   その後の描画待ち(Render)         : {Stat(renderAfter)}");
            _o.WriteLine($"B ApplyThink 4000字超（毎回4000字入れ直し）: {Stat(applyBig)}");
            _o.WriteLine($"B   その後のレイアウト               : {Stat(layoutBig)}");
            _o.WriteLine($"B   その後の描画待ち(Render)         : {Stat(renderBig)}");
            _o.WriteLine($"B 内訳: Text入れ直しだけ              : {Stat(textOnly)}");
            _o.WriteLine($"B 内訳: Text入れ直し後のレイアウト    : {Stat(textLayout)}");
            _o.WriteLine($"B 内訳: ScrollToEndだけ               : {Stat(scrollOnly)}");
            _o.WriteLine($"B 内訳: ScrollToEnd後のレイアウト     : {Stat(scrollLayout)}");
            _o.WriteLine($"B 窓を動かす SetWindowPos 1回（流していない）: {Stat(moveBase)}");
            _o.WriteLine($"B 窓を動かす SetWindowPos 1回（思考を流した直後）: {Stat(moveCall)}");
        }
        finally { w.Close(); }
    }
}

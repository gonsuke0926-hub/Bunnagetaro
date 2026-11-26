using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 自動で始めたぶん投げは、画面が変わった時（AutoFire）に打ち切って投げ直す。
/// 手動・追加の問いは打ち切らない。応答を返さない偽サーバーへ向けて「ぶん投げ中」を保つ。
/// </summary>
public class AutoRestartTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private TcpListener? _lis;
    private readonly List<TcpClient> _clients = new();

    public AutoRestartTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_autorestart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { _lis?.Stop(); } catch { }
        lock (_clients) foreach (var c in _clients) { try { c.Close(); } catch { } }
        try { Directory.Delete(_temp, true); } catch { }
    }

    /// <summary>接続を受けるだけで何も返さない（ぶん投げが止まったまま続く）</summary>
    private string StartHangingServer()
    {
        _lis = new TcpListener(IPAddress.Loopback, 0);
        _lis.Start();
        var lis = _lis;
        var th = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var c = lis.AcceptTcpClient();
                    lock (_clients) _clients.Add(c);
                }
            }
            catch { }
        }) { IsBackground = true };
        th.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)lis.LocalEndpoint).Port;
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static bool PumpUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Pump(30);
        }
        return cond();
    }

    private static void OnSta(Action body) => TestUtil.OnSta(body, 90);

    private const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, F)!.GetValue(o)!;

    [Fact]
    public void 自動のぶん投げの最中のAutoFireは打ち切って1回だけ投げ直す_人のやめるは予約を消す()
    {
        string url = StartHangingServer();
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("base_url", url);
            cfg.SetStr("result_place", "inside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(600);
                var thr = w.Throw;

                // 自動トリガがOFFなら、AutoFire は何もしない
                Assert.False(thr.AutoFire());
                Assert.False(w.Throw.Status.Busy);

                w.Session.BarAutoTrig(true);
                Assert.True(thr.AutoFire());
                Assert.True(thr.Status.Busy);
                Assert.True(thr.AutoRunInFlight);
                Assert.Equal("auto", Get<string>(thr, "_runKind"));

                // 画面が変わって止まった → 打ち切って、終わったあとに1回投げ直す
                Assert.True(thr.AutoFire());
                Assert.True(Get<bool>(thr, "_restartAuto"));
                Assert.False(thr.AutoRunInFlight);   // やめ要求済み。続けて来ても二重に予約しない
                Assert.False(thr.AutoFire());

                // 打ち切りが終わり、新しい自動のぶん投げが始まっている
                Assert.True(PumpUntil(() => !Get<bool>(thr, "_restartAuto") && thr.AutoRunInFlight, 30000));
                Assert.True(thr.Status.Busy);

                // 人の「やめる」は、投げ直しの予約も取り消す（終わっても勝手に始めない）
                Assert.True(thr.AutoFire());
                Assert.True(Get<bool>(thr, "_restartAuto"));
                thr.CancelTranslate();
                Assert.False(Get<bool>(thr, "_restartAuto"));
                Assert.True(PumpUntil(() => !thr.Status.Busy, 30000));
                Pump(500);
                Assert.False(thr.Status.Busy);       // 投げ直されていない
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void 手動のぶん投げの最中のAutoFireは打ち切らない()
    {
        string url = StartHangingServer();
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("base_url", url);
            cfg.SetStr("result_place", "inside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(600);
                var thr = w.Throw;
                w.Session.BarAutoTrig(true);
                thr.TranslateNow();                  // 手動
                Assert.True(thr.Status.Busy);
                Assert.False(thr.AutoRunInFlight);
                Assert.False(thr.AutoFire());        // 打ち切らない
                Assert.False(Get<bool>(thr, "_restartAuto"));
                Assert.False(Get<bool>(thr, "_cancelRequested"));
                thr.CancelTranslate();
                Assert.True(PumpUntil(() => !thr.Status.Busy, 30000));
            }
            finally { w.Close(); }
        });
    }
}

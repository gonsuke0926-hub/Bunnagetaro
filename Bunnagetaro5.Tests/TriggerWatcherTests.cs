using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>手順19: 自動トリガ巡回（動いた→落ち着いた→発火、クールダウン、busy、
/// 同じ訳の再発防止、窓移動の基準直し）。Tick を直接刻んで調べる（スレッドは回さない）</summary>
public class TriggerWatcherTests
{
    private static ChangeDetector.Frame Gray(int w, int h, byte v)
    {
        var b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            b[i * 4] = v; b[i * 4 + 1] = v; b[i * 4 + 2] = v; b[i * 4 + 3] = 255;
        }
        return new(b, w, h);
    }

    private sealed class Rig
    {
        public Native.RECT Rect = new() { Left = 10, Top = 20, Right = 130, Bottom = 80 };
        public bool Enabled = true, Busy;
        public bool AutoRun;                          // 今のぶん投げが自動で始めたもの（F11）
        public DateTime Clock = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double sec) => Clock = Clock.AddSeconds(sec);
        public int Fires;
        public readonly List<bool> Detect = new();
        public TrigParams P = new();
        private readonly Queue<ChangeDetector.Frame> _script = new();
        private ChangeDetector.Frame? _last;

        public readonly TriggerWatcher W;

        public Rig()
        {
            W = new TriggerWatcher(
                () => P, () => Enabled, () => Busy, () => AutoRun,
                () => Rect,
                r => { var f = _script.Count > 0 ? _script.Dequeue() : _last; _last = f; return f; },
                () => { Fires++; return true; },
                v => Detect.Add(v), () => Clock);
        }

        public void Push(byte gray) => _script.Enqueue(Gray(120, 60, gray));
        public void Tick() => W.Tick();
    }

    [Fact]
    public void 既定はのぞく間隔0点5秒_最短間隔1秒()
    {
        var p = new TrigParams();
        Assert.Equal(0.5, p.Interval);
        Assert.Equal(1.0, p.Cooldown);
        var cfg = new ConfigStore(new Paths(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "b5_trigdef_" + Guid.NewGuid().ToString("N"), "Bunnagetaro5")));
        cfg.Load();
        Assert.Equal(0.5, cfg.Settings.TrigPollInterval);
        Assert.Equal(1.0, cfg.Settings.TrigCooldown);
    }

    [Fact]
    public void 静かなだけなら発火しない()
    {
        var r = new Rig();
        for (int i = 0; i < 8; i++) { r.Push(90); r.Tick(); }
        Assert.Equal(0, r.Fires);
    }

    [Fact]
    public void 動いて落ち着いたら1回だけ発火()
    {
        var r = new Rig();
        r.Push(40); r.Tick();          // 基準
        r.Push(40); r.Tick();          // ratio 0
        r.Push(200); r.Tick();         // 動いた（ratio 1 > 0.01）
        r.Push(200); r.Tick();         // 静か 1回目
        r.Push(200); r.Tick();         // 静か 2回目 → 発火
        Assert.Equal(1, r.Fires);
        for (int i = 0; i < 5; i++) { r.Push(200); r.Tick(); }   // 同じまま
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void 最短間隔の間は監視せず_明けてから動いて止まったら発火()
    {
        var r = new Rig();
        r.P.Cooldown = 3.0;
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();
        r.Push(200); r.Tick();
        r.Push(200); r.Tick();         // 1回目の発火（時刻 0）
        Assert.Equal(1, r.Fires);
        r.Advance(1.0);                // クールダウン中（1秒）: 監視・撮影・動作検知しない
        r.Tick();
        r.Tick();
        r.Tick();
        Assert.Equal(1, r.Fires);
        Assert.False(r.Detect[^1]);
        r.Advance(2.5);                // 前回の発火から3.5秒 → 明けた
        r.Push(40); r.Tick();          // 明け直後に基準を取り直す
        r.Push(150); r.Tick();         // 明けてから動いた
        r.Push(150); r.Tick();
        r.Push(150); r.Tick();
        Assert.Equal(2, r.Fires);
    }

    [Fact]
    public void 最短間隔0なら間隔なしで続けて発火できる()
    {
        var r = new Rig();
        r.P.Cooldown = 0.0;   // 既定は 1 秒（2026-11-15）。ここは 0 の時の動きを見る
        r.Push(40); r.Tick();
        r.Push(200); r.Tick(); r.Push(200); r.Tick(); r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
        r.Push(40); r.Tick(); r.Push(40); r.Tick(); r.Push(40); r.Tick();   // 時計が進まなくても発火
        Assert.Equal(2, r.Fires);
    }

    [Fact]
    public void 手動の発火は数えない_最短間隔の起点は自動の発火だけ()
    {
        var r = new Rig();
        r.P.Cooldown = 3.0;
        r.Busy = true;                 // 手動で始めたぶん投げが済んだ直後（AutoRun は false）
        r.Push(40); r.Tick();
        r.Push(200); r.Tick(); r.Push(200); r.Tick(); r.Push(200); r.Tick();
        Assert.Equal(0, r.Fires);
        r.Busy = false;                // 終わった直後でも、自動の発火が無いので間隔には引っかからない
        r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void F11_自動のぶん投げの最中に画面が変わって止まったら打ち切って投げ直す()
    {
        var r = new Rig();
        r.P.Cooldown = 0.0;   // 最短間隔の間は見ない（I10）ので、打ち切りの動きだけを見るため 0
        r.Push(40); r.Tick();
        r.Push(200); r.Tick(); r.Push(200); r.Tick(); r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
        r.Busy = true; r.AutoRun = true;   // 自動で始めたぶん投げの最中
        r.Push(40); r.Tick();              // 動いた
        r.Push(40); r.Tick();              // 静か1
        Assert.Equal(1, r.Fires);
        r.Push(40); r.Tick();              // 静か2 → 打ち切って投げ直し（onFire）
        Assert.Equal(2, r.Fires);
    }

    [Fact]
    public void F11_手動や追加の問いの最中は打ち切らない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Busy = true; r.AutoRun = false;
        r.Push(200); r.Tick();
        for (int i = 0; i < 5; i++) { r.Push(200); r.Tick(); }
        Assert.Equal(0, r.Fires);
        r.Busy = false;                    // 手動が終わったら、残っていた「動いた」で発火
        r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void F11_打ち切りと投げ直しも最短間隔より短くしない()
    {
        var r = new Rig();
        r.P.Cooldown = 5.0;
        r.Push(40); r.Tick();
        r.Push(200); r.Tick(); r.Push(200); r.Tick(); r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
        r.Busy = true; r.AutoRun = true;
        r.Advance(2.0);                // クールダウン中（2秒）: 監視しない
        r.Tick(); r.Tick(); r.Tick(); r.Tick();
        Assert.Equal(1, r.Fires);
        Assert.False(r.Detect[^1]);
        r.Advance(4.0);                // 6秒 → 明けた後に変化→静止で打ち切って投げ直し
        r.Push(40); r.Tick();
        r.Push(150); r.Tick(); r.Push(150); r.Tick(); r.Push(150); r.Tick();
        Assert.Equal(2, r.Fires);
    }

    [Fact]
    public void F11_投げている映像と同じに落ち着いたなら打ち切らない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick(); r.Push(200); r.Tick(); r.Push(200); r.Tick();   // 発火（映像200）
        Assert.Equal(1, r.Fires);
        r.Busy = true; r.AutoRun = true;
        r.Push(40); r.Tick();              // 動いて
        r.Push(200); r.Tick();             // 元の映像へ戻る
        r.Push(200); r.Tick(); r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);          // 同じ映像なので投げ直さない
    }

    [Fact]
    public void ぶん投げ中は待って空いたら発火()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();
        r.Busy = true;
        for (int i = 0; i < 4; i++) { r.Push(200); r.Tick(); }
        Assert.Equal(0, r.Fires);
        r.Busy = false;
        r.Push(200); r.Tick();
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void 前回ぶん投げた映像と同じなら発火しない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();
        r.Push(200); r.Tick();
        r.Push(200); r.Tick();         // 発火（last_fire = 200の映像）
        Assert.Equal(1, r.Fires);
        r.Push(40); r.Tick();          // 動く（200→40）
        r.Push(200); r.Tick();         // また200へ（ratio 1 → 静か数え直し）
        r.Push(200); r.Tick();         // 静か1
        r.Push(200); r.Tick();         // 静か2 → last_fire とほぼ同じ → 見送り
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void 窓が動いたら基準を作り直すだけで発火しない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        for (int i = 0; i < 4; i++)   // 枠を動かしながら映像もがらっと変わる（窓ドラッグ）
        {
            r.Rect = new Native.RECT { Left = 10 + 30 * (i + 1), Top = 20, Right = 130 + 30 * (i + 1), Bottom = 80 };
            r.Push(i % 2 == 0 ? (byte)200 : (byte)40);
            r.Tick();
        }
        Assert.Equal(0, r.Fires);
    }

    [Fact]
    public void OFFの間の変化は数えない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();         // 動いた
        r.Enabled = false;
        r.Push(200); r.Tick();         // 巡回で状態を捨てる
        r.Enabled = true;
        for (int i = 0; i < 4; i++) { r.Push(200); r.Tick(); }   // 基準を作り直すだけ
        Assert.Equal(0, r.Fires);
    }

    [Fact]
    public void 黒い枠は基準にならない()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(0); r.Tick();           // 黒枠 → 基準ごと捨てる
        r.Push(200); r.Tick();         // 黒明け。前回が黒なので数えない（排他フルスクリーン明け）
        for (int i = 0; i < 4; i++) { r.Push(200); r.Tick(); }
        Assert.Equal(0, r.Fires);
    }

    [Fact]
    public void 動作検知中は動いた次の巡回から静かになるまで立つ()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();         // ここで changing
        r.Push(200); r.Tick();         // 検知中=true と報告される
        Assert.Contains(true, r.Detect);
        r.Push(200); r.Tick();         // 発火で検知終（false報告）
        Assert.False(r.Detect[r.Detect.Count - 1]);
    }

    [Fact]
    public void 設定は指定範囲に収める()
    {
        var p = TrigParams.From(999, 9, 9, 999, 999);
        Assert.Equal(10.0, p.Interval);
        Assert.Equal(1.0, p.ChangeTh);
        Assert.Equal(0.5, p.StableTh);
        Assert.Equal(100, p.StableNeed);
        Assert.Equal(60.0, p.Cooldown);
        var q = TrigParams.From(-5, -1, -1, 0, -1);
        Assert.Equal(0.2, q.Interval);
        Assert.Equal(0.0, q.ChangeTh);
        Assert.Equal(0.0, q.StableTh);
        Assert.Equal(1, q.StableNeed);
        Assert.Equal(0.0, q.Cooldown);
    }

    [Fact]
    public void 静かの要求回数は設定どおり()
    {
        var r = new Rig();
        r.P.StableNeed = 4;
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();
        for (int i = 0; i < 3; i++) { r.Push(200); r.Tick(); }   // 静か3回ではまだ
        Assert.Equal(0, r.Fires);
        r.Push(200); r.Tick();         // 4回目 → 発火
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void 静かが途中で見直れば数え直し()
    {
        var r = new Rig();
        r.Push(40); r.Tick();
        r.Push(200); r.Tick();
        r.Push(200); r.Tick();          // 静か1
        r.Push(150); r.Tick();          // delta超えの変化（ratio1）→ still changing、stable=0
        r.Push(150); r.Tick();          // 静か1（数え直し）
        Assert.Equal(0, r.Fires);       // まだ2回に届かない
        r.Push(150); r.Tick();          // 静か2 → 発火
        Assert.Equal(1, r.Fires);
    }

    [Fact]
    public void 自動トリガのまま閉じてもJoinで3秒固まらない()
    {
        Exception? err = null;
        long closeMs = -1;
        var th = new Thread(() =>
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "b5_g8_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(temp);
            try
            {
                if (System.Windows.Application.Current is null)
                {
                    var app = new System.Windows.Application
                    { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
                }
                var paths = new Paths(System.IO.Path.Combine(temp, "Bunnagetaro5"));
                AppLog.Init(paths);
                var cfg = new ConfigStore(paths);
                cfg.Load();
                cfg.Settings.Mode = "auto";
                cfg.Settings.TrigPollInterval = 0.2;
                var w = new MainWindow(paths, cfg);
                w.Throw.EnvChecks = false;
                w.Show();
                // 巡回スレッドが動いている状態で、裏から TrigRectNow を呼ぶのと同時に Close() しても固まらない
                var trigRect = typeof(MainWindow).GetMethod("TrigRectNow",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var bg = Task.Run(() => trigRect.Invoke(w, null));
                Thread.Sleep(30);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                w.Close();
                closeMs = sw.ElapsedMilliseconds;
                bg.Wait(1000);
            }
            catch (Exception e) { err = e; }
            finally { try { System.IO.Directory.Delete(temp, true); } catch { } }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        Assert.True(th.Join(TimeSpan.FromSeconds(15)));
        if (err is not null) throw err;
        Assert.True(closeMs < 1500, $"Close took {closeMs} ms (expected < 1500 ms)");
    }

    private sealed class BlockingCancelHandler : System.Net.Http.HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    [Fact]
    public void ぶん投げ中に閉じるとやめるが呼ばれてログがやめましたで確定する()
    {
        Exception? err = null;
        var handler = new BlockingCancelHandler();
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "b5_g9_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            OllamaClient.SetHttp(new System.Net.Http.HttpClient(handler));
            var th = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Application.Current is null)
                    {
                        var app = new System.Windows.Application
                        { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
                        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
                    }
                    var paths = new Paths(System.IO.Path.Combine(temp, "Bunnagetaro5"));
                    AppLog.Init(paths);
                    var cfg = new ConfigStore(paths);
                    cfg.Load();
                    var w = new MainWindow(paths, cfg);
                    w.Throw.EnvChecks = false;
                    w.Show();
                    w.Session.OpenCapture();

                    var px = new byte[16 * 16 * 4];
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                        16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 16 * 4);
                    bmp.Freeze();
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    typeof(Bunnagetaro5.Session.ThrowController).GetField("_lastImg", flags)!.SetValue(w.Throw, bmp);
                    typeof(Bunnagetaro5.Session.ThrowController).GetField("_answerText", flags)!.SetValue(w.Throw, "前の答え");

                    Assert.True(w.Throw.FollowupSend("これは何？", withImg: false));
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    var timer = new System.Windows.Threading.DispatcherTimer(
                        TimeSpan.FromMilliseconds(20),
                        System.Windows.Threading.DispatcherPriority.Background,
                        (_, _) =>
                        {
                            if (handler.Entered.Task.IsCompleted || DateTime.UtcNow > deadline)
                                frame.Continue = false;
                        },
                        w.Dispatcher);
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    timer.Stop();
                    Assert.True(handler.Entered.Task.IsCompleted);
                    Assert.True(w.Status.Busy);

                    w.Close();
                }
                catch (Exception e) { err = e; }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            Assert.True(th.Join(TimeSpan.FromSeconds(15)));
            if (err is not null) throw err;

            var verifyPaths = new Paths(System.IO.Path.Combine(temp, "Bunnagetaro5"));
            var disk = new LogStore(verifyPaths);
            Assert.Single(disk.Entries);
            Assert.Equal("failed", LogStore.GetStr(disk.Entries[0], "status"));
            Assert.Equal("（ぶん投げをやめました）", LogStore.GetStr(disk.Entries[0], "text"));
        }
        finally
        {
            OllamaClient.SetHttp(null!);
            try { System.IO.Directory.Delete(temp, true); } catch { }
        }
    }
}

using System.Threading;

namespace Bunnagetaro5.Services;

/// <summary>巡回1周の設定（trig_*。Python の param() と同じく読む時 clamp。
/// 範囲は TRIG_FIELDS と一致）</summary>
public sealed class TrigParams
{
    /// <summary>のぞく間隔・最短間隔の既定（1秒・0秒では鈍いので 0.5秒・1秒）</summary>
    public const double DefaultInterval = 0.5, DefaultCooldown = 1.0;
    public double Interval = DefaultInterval;    // trig_poll_interval（0.2〜10秒）
    public double ChangeTh = 0.01;   // trig_change_ratio（0〜1。動いたと見る割合）
    public double StableTh = 0.005;  // trig_stable_ratio（0〜0.5。静かと見る割合）
    public int StableNeed = 2;       // trig_stable_samples（1〜100。静かの連続回数）
    public double Cooldown = DefaultCooldown;    // trig_cooldown（0〜60秒。自動トリガの最短間隔。前回の自動発火から数える）

    public static double Param(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

    /// <summary>設定ページと同じ範囲でclamし直す（手編集で外れても暴れない）</summary>
    public static TrigParams From(double interval, double change, double stable, int samples, double cooldown)
        => new()
        {
            Interval = Param(interval, 0.2, 10.0),
            ChangeTh = Param(change, 0.0, 1.0),
            StableTh = Param(stable, 0.0, 0.5),
            StableNeed = (int)Param(samples, 1, 100),
            Cooldown = Param(cooldown, 0.0, 60.0),
        };
}

/// <summary>
/// 自動トリガの背景巡回。
/// 割合が trig_change_ratio を越えたら「動いた」。その後 trig_stable_ratio 未満が
/// trig_stable_samples 回続いたら「落ち着いた」としてぶん投げを1回頼む。
/// 前回の自動発火から trig_cooldown 秒は発火しない（手動・追加の問いは数えない）。
/// 手動・追加の問いで busy の間、最短間隔の間は「動いた」を消さずに毎巡回リトライする。
/// 自動で始めたぶん投げの最中に、画面がはっきり変わって止まったら、
/// 打ち切って投げ直す（onFire が打ち切りと投げ直しを引き受ける）。間隔より短くはしない。
/// 窓も撮影も全部注入した.delegate経由（本体はUIスレッド、テストは合成画像）。
/// 状態はこの巡回スレッドだけが触る（Tick はテストから直接呼べる）。</summary>
public sealed class TriggerWatcher
{
    private readonly Func<TrigParams> _params;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _busy;
    private readonly Func<bool> _autoRun;       // 今のぶん投げが自動で始めたもの（やめ要求前）なら true
    private readonly Func<DateTime> _now;       // 時計（テストで差し替え）
    private DateTime _lastFire = DateTime.MinValue;   // 前回の自動発火（打ち切り→投げ直しも含む）の時刻
    private readonly Func<Native.RECT?> _getRect;
    private readonly Func<Native.RECT, ChangeDetector.Frame?> _grab;
    private readonly Func<bool> _onFire;        // 実際にぶん投げが始まったら true（指紋を控える）
    private readonly Action<bool>? _onDetect;   // 「動作検知中」の表示へ（_trig_detect）

    private byte[]? _prev;
    private bool _changing;
    private int _stable;
    private byte[]? _lastFireFp;    // 前回ぶん投げた瞬間の映像（同じ訳の再発防止）
    private Native.RECT? _prevRect;

    private Thread? _th;
    private ManualResetEventSlim? _stop;

    public TriggerWatcher(Func<TrigParams> p, Func<bool> enabled, Func<bool> busy,
                          Func<bool> autoRun, Func<Native.RECT?> getRect,
                          Func<Native.RECT, ChangeDetector.Frame?> grab,
                          Func<bool> onFire, Action<bool>? onDetect = null,
                          Func<DateTime>? now = null)
    {
        _params = p;
        _enabled = enabled;
        _busy = busy;
        _autoRun = autoRun;
        _now = now ?? (() => DateTime.UtcNow);
        _getRect = getRect;
        _grab = grab;
        _onFire = onFire;
        _onDetect = onDetect;
    }

    public void Start()
    {
        if (_th is not null) return;
        _stop = new ManualResetEventSlim(false);
        _th = new Thread(Loop) { IsBackground = true, Name = "TriggerWatcher" };
        _th.Start();
    }

    public void Stop()
    {
        _stop?.Set();
        try { _th?.Join(3000); } catch { }
        _th = null;
        _stop = null;
    }

    private void Loop()
    {
        var stop = _stop!;
        while (true)
        {
            double iv;
            try { iv = _params().Interval; } catch { iv = 1.0; }
            if (stop.Wait(TimeSpan.FromSeconds(iv))) break;
            try { Tick(); }
            catch (Exception e) { AppLog.Error("自動トリガの巡回に失敗", e); }
        }
    }

    /// <summary>巡回1周（そのままテスト可能）</summary>
    public void Tick()
    {
        var p = _params();
        if (!_enabled()) { Reset(); return; }
        // 前回の自動発火から最短間隔の間は監視・動作検知をしない
        if ((_now() - _lastFire).TotalSeconds < p.Cooldown) { Reset(); return; }
        var rect = _getRect();
        if (rect is not { } r) { Reset(); return; }
        var frame = _grab(r);
        if (frame is not { } f || f.W <= 0 || f.H <= 0 ||
            (long)f.Bgra.Length < (long)f.W * f.H * 4) { Reset(); return; }
        if (ChangeDetector.IsBlackFrame(f)) { Reset(); return; }
        var fp = ChangeDetector.Fingerprint(f);
        if (_prevRect is not { } pr || pr.Left != r.Left || pr.Top != r.Top ||
            pr.Right != r.Right || pr.Bottom != r.Bottom)
        {
            // 窓が動いた・大きさを変えた: 新旧位置の差は「画面が塗り替わった」に
            // 化けるので数えず、新しい位置の映像だけを基準に据え直す
            // （Python の _win_drag / _geom_ts 見当たりの代わり。この移植では
            //  panel・バーは撮影に写らないので矩形の一致判定だけで足りる）
            _prev = fp; _prevRect = r; _changing = false; _stable = 0;
            _onDetect?.Invoke(false);
            return;
        }
        _onDetect?.Invoke(_changing);   // 前巡回までの「動作検知中」（Python _trig_detect）
        if (_prev is null) { _prev = fp; _changing = false; _stable = 0; return; }
        double ratio = ChangeDetector.ChangeRatio(fp, _prev, ChangeDetector.ChangeDelta);
        _prev = fp;
        if (ratio > p.ChangeTh) { _changing = true; _stable = 0; return; }
        if (!_changing) return;
        _stable = ratio < p.StableTh ? _stable + 1 : 0;
        if (_stable < p.StableNeed) return;
        // 手動・追加の問いで busy なら「動いた」を残して毎巡回リトライ（打ち切らない）。
        // 自動で始めたぶん投げの最中は通す（onFire が打ち切って投げ直す）
        if (_busy() && !_autoRun()) return;
        // 前回ぶん投げた映像とほぼ同じなら発火しない（同じ訳の再発防止。
        //  panel が撮影に写らないので Python の panel_fp / 撮り直し比較は要らない）
        if (_lastFireFp is not null &&
            ChangeDetector.ChangeRatio(fp, _lastFireFp, ChangeDetector.ChangeDelta) <= p.StableTh)
        {
            _changing = false; _stable = 0;
            _onDetect?.Invoke(false);
            return;
        }
        _changing = false; _stable = 0;
        _onDetect?.Invoke(false);
        if (_onFire())   // put 成功後だけ更新（Pythonと同じ）
        {
            _lastFireFp = fp;
            _lastFire = _now();
        }
    }

    private void Reset()
    {
        _prev = null; _prevRect = null; _changing = false; _stable = 0;
        _onDetect?.Invoke(false);
    }
}

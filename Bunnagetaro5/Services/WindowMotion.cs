using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bunnagetaro5.Services;

/// <summary>
/// 窓の出る・消える動き（ANIM_ROUND1）の部品。各窓は「出す・消す・閉じる」を頼むだけ。
///
/// 大原則:
///  1. 撮影前の隠しは動きを付けない（<see cref="HideNow"/>。走っている動きを打ち切って即 Hide）。
///  2. 動きを待って操作を止めない（<see cref="Cancel"/> で即打ち切れる）。
///  3. 連打・取り消しに強い（消えかけに出すを頼めば、今の透明度から出る方へ戻る。
///     消えかけに消すを重ねても二重に始めない）。
///  5. 位置は SetWindowPos を毎フレーム（WPF の Left/Top アニメは使わない）。
///     時間は <see cref="CompositionTarget.Rendering"/> のたびに <see cref="Tick"/> で進める。
/// </summary>
public static class WindowMotion
{
    // ---- 時間・大きさの一覧（DECISIONS にも残す） ----
    public const int OpenMs = 180;          // E1 キャプチャ窓・バーを開く
    public const int CloseMs = 180;         // E2 閉じる
    public const double OpenCloseScale = 0.96;
    public const int PanelFadeMs = 150;     // E3 解答欄・帯の出入り
    public const int HintFadeMs = 200;      // E4 2回目以降の案内が消える
    // F28 クイック開始の吸い込み（人が見て決める数値はここの3つだけ。窓の矩形は動かさず中身を縮める）
    public const int QuickMs = 120;         // 吸い込みの時間（FIX_ROUND4 E4: 90ms → F28: 120ms）
    public const double QuickScaleTo = 0.15;// 吸い込み先の縮小率（1.0→0.15。大きさは ease-in＝だんだん速く）
    public const double QuickFadeStart = 0.5;// 透明度は時間のここまでほぼそのまま、残りで一気に消える（0〜1）
    public const int QuickPanelFadeMs = 90; // F28(追加): 解答欄（窓に重ねる側）の薄れ。吸い込み(QuickMs)とは別。元の 90ms
    public const int QuickRestoreMs = 120;  // E5 小さい四角で元へ戻る時の出（FIX_ROUND4 E4）
    /// <summary>修正J91: 透過窓の ShowFade 開始時に古い DWM 絵が一瞬出ないよう cloak を保つ Rendering フレーム数と安全タイマー</summary>
    internal const int ShowCloakFrames = 2;
    internal const int ShowCloakGuardMs = 60;

    // ---------------- イージング・補間（純関数） ----------------

    /// <summary>経過 elapsedMs / 全体 totalMs を 0〜1 に。時間切れで 1（最終値）</summary>
    public static double Progress(long elapsedMs, int totalMs)
        => totalMs <= 0 || elapsedMs >= totalMs ? 1.0 : Math.Max(0.0, (double)elapsedMs / totalMs);

    /// <summary>ふわっと出る・薄れる用（最初速く→ゆっくり止まる）</summary>
    public static double EaseOut(double t) { t = Math.Clamp(t, 0, 1); double u = 1 - t; return 1 - u * u * u; }

    /// <summary>吸い込み用（最初ゆっくり→加速）</summary>
    public static double EaseIn(double t) { t = Math.Clamp(t, 0, 1); return t * t * t; }

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>F28 吸い込みの大きさ。経過 t（0〜1）で from→QuickScaleTo を ease-in で（だんだん速く）</summary>
    public static double SuckScale(double from, double t) => Lerp(from, QuickScaleTo, EaseIn(t));

    /// <summary>F28 吸い込みの透明度。時間の前半（QuickFadeStart まで）は from のまま、後半で一気に 0 へ</summary>
    public static double SuckAlpha(double from, double t)
        => Lerp(from, 0, Math.Clamp((t - QuickFadeStart) / (1.0 - QuickFadeStart), 0, 1));

    /// <summary>矩形 a から b へ t（0〜1）の補間（物理px。四捨五入）</summary>
    public static Native.RECT LerpRect(Native.RECT a, Native.RECT b, double t) => new()
    {
        Left = (int)Math.Round(Lerp(a.Left, b.Left, t), MidpointRounding.AwayFromZero),
        Top = (int)Math.Round(Lerp(a.Top, b.Top, t), MidpointRounding.AwayFromZero),
        Right = (int)Math.Round(Lerp(a.Right, b.Right, t), MidpointRounding.AwayFromZero),
        Bottom = (int)Math.Round(Lerp(a.Bottom, b.Bottom, t), MidpointRounding.AwayFromZero),
    };

    /// <summary>点 (px,py) を中心にした size×size の矩形（吸い込み先）</summary>
    public static Native.RECT SquareAt(int px, int py, int size) => new()
    {
        Left = px - size / 2, Top = py - size / 2, Right = px - size / 2 + size, Bottom = py - size / 2 + size,
    };

    // ---------------- 動きのエンジン（窓に依らない） ----------------

    private sealed class Job
    {
        public object Key = null!;
        public long Start;
        public int Ms;
        public Func<double, double> Ease = EaseOut;
        public Action<double> Apply = _ => { };
        public Action? Done;
    }

    // 動きは UI スレッドのもの（CompositionTarget.Rendering もそのスレッドで来る）。
    // スレッドごとに持たせて、テストが別スレッドで時計を差し替えても互いに干渉しない
    [ThreadStatic] private static List<Job>? _jobs;
    private static List<Job> Jobs => _jobs ??= new List<Job>();
    [ThreadStatic] private static bool _hooked;

    private static readonly Stopwatch Clk = Stopwatch.StartNew();
    /// <summary>現在時刻(ms)。テストが差し替えて時間を進める</summary>
    [ThreadStatic] private static Func<long>? _clock;
    public static Func<long> Clock
    {
        get => _clock ?? (() => Clk.ElapsedMilliseconds);
        set => _clock = value;
    }

    /// <summary>key（窓など）の動きを始める。同じ key の動きは先に打ち切る（Done は呼ばない）。
    /// apply には イージング後の 0〜1 を毎フレーム渡す。最後は必ず 1 で呼んでから done</summary>
    public static void Run(object key, int ms, Func<double, double> ease, Action<double> apply, Action? done = null)
        => RunFrom(key, ms, ease, apply, done, Clock());

    /// <summary>修正J83: 時刻 start(ms。<see cref="Clock"/> の値) に始まっていたものとして動きを始める。
    /// 遅れて出る窓を、先に動き始めた窓と同じ進み具合から途中参加させる（同じ時に出終わる）。
    /// 修正J86: start が先の時刻なら、その時刻まで始まりの値（0）のまま止めておく（<see cref="Held"/> を渡し、後で <see cref="Restart"/> で動かす）</summary>
    public static void RunFrom(object key, int ms, Func<double, double> ease, Action<double> apply, Action? done, long start)
    {
        Cancel(key);
        long now = Clock();
        var j = new Job { Key = key, Start = start, Ms = ms, Ease = ease, Apply = apply, Done = done };
        Jobs.Add(j);
        apply(ease(Progress(now - start, ms)));
        EnsureHook();
    }

    /// <summary>修正J86: 「まだ始めない」印の時刻。これを start に渡した動きは、<see cref="Restart"/> されるまで始まりの値のまま止まる</summary>
    public const long Held = long.MaxValue / 4;

    /// <summary>修正J86: key の動きが <see cref="Held"/> で止めてあるか</summary>
    public static bool IsHeld(object key) => Jobs.Exists(j => ReferenceEquals(j.Key, key) && j.Start >= Held);

    /// <summary>修正J85: 走っている key の動きを、時刻 start(ms) に始まったものとして数え直す（今の進み具合で1回当てる）。
    /// 走っていなければ何もしない（false）</summary>
    public static bool Restart(object key, long start)
    {
        long now = Clock();
        if (start > now) start = now;
        bool any = false;
        foreach (var j in Jobs)
        {
            if (!ReferenceEquals(j.Key, key)) continue;
            j.Start = start;
            try { j.Apply(j.Ease(Progress(now - start, j.Ms))); }
            catch (Exception ex) { AppLog.Error("motion restart", ex); }
            any = true;
        }
        return any;
    }

    /// <summary>key の動きを即打ち切る（Done は呼ばない）。走っていたら true</summary>
    public static bool Cancel(object key)
    {
        int n = Jobs.RemoveAll(j => ReferenceEquals(j.Key, key));
        if (key is Window w) Uncloak(w);
        return n > 0;
    }

    public static bool IsRunning(object key) => Jobs.Exists(j => ReferenceEquals(j.Key, key));

    /// <summary>時刻 now(ms) まで全部の動きを進める。時間切れの動きは最終値で apply → done → 除去</summary>
    public static void Tick(long now) => TickCore(now, fromRendering: false);

    private static void TickCore(long now, bool fromRendering)
    {
        // 再入ガード: Apply の中の SetWindowPos が同期メッセージを回し、その中で Rendering が
        // 再び来て Tick が入れ子になると、時間が進んだ入れ子側が動きを終わらせてしまう（実測）
        if (_ticking) return;
        _ticking = true;
        try
        {
            foreach (var j in Jobs.ToArray())
            {
                if (!Jobs.Contains(j)) continue;   // 他の動きの Done で打ち切られた
                if (j.Key is Window win && States.TryGetValue(win, out var cst) && cst.CloakFrames > 0)
                {
                    if (fromRendering)
                    {
                        // 修正J91: 透明度 0 の新フレーム（新しい位置・答え）が DWM へ渡るまで cloak したまま 0 を保つ
                        if (j.Start < Held) j.Start = now;
                        try { j.Apply(0.0); }
                        catch (Exception ex) { AppLog.Error("motion cloak apply", ex); }
                        cst.CloakFrames--;
                        if (cst.CloakFrames <= 0) Uncloak(win);
                        continue;
                    }
                    // テストからの直接 Tick 呼び出しでは cloak 待ちを解除してそのまま進める
                    Uncloak(win);
                }
                double p = Progress(now - j.Start, j.Ms);
                try { j.Apply(j.Ease(p)); }
                catch (Exception ex) { AppLog.Error("motion apply", ex); p = 1.0; }
                if (p >= 1.0)
                {
                    Jobs.Remove(j);
                    try { j.Done?.Invoke(); }
                    catch (Exception ex) { AppLog.Error("motion done", ex); }
                }
            }
            if (Jobs.Count == 0) Unhook();
        }
        finally { _ticking = false; }
    }

    [ThreadStatic] private static bool _ticking;

    private static void OnRendering(object? s, EventArgs e) => TickCore(Clock(), fromRendering: true);

    private static void EnsureHook()
    {
        if (_hooked) return;
        try
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
        catch (Exception) { /* UIスレッド外（テスト）は Tick を直接呼ぶ */ }
    }

    private static void Unhook()
    {
        if (!_hooked) return;
        try { CompositionTarget.Rendering -= OnRendering; } catch (Exception) { }
        _hooked = false;
    }

    // ---------------- 窓へ当てる ----------------

    private sealed class State
    {
        public double Opacity = double.NaN;   // NaN = まだ触っていない（rest とみなす）
        public double Scale = 1.0;
        public bool Out;                      // 消える動きの最中
        public IntPtr CloakHwnd;              // 修正J91: 再表示ちらつき防止で DWM cloak 中の HWND
        public int CloakFrames;               // cloak を外すまでに待つ Rendering フレーム数
        public DispatcherTimer? CloakGuard;   // Rendering が止まった環境向けの安全タイマー
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();
    private static State St(Window w) => States.GetOrCreateValue(w);

    internal static bool IsCloaked(Window w) => States.TryGetValue(w, out var st) && st.CloakHwnd != IntPtr.Zero;

    private static void Uncloak(Window w)
    {
        if (!States.TryGetValue(w, out var st)) return;
        st.CloakGuard?.Stop();
        st.CloakGuard = null;
        st.CloakFrames = 0;
        var h = st.CloakHwnd;
        st.CloakHwnd = IntPtr.Zero;
        if (h != IntPtr.Zero && Native.IsWindow(h)) Native.Cloak(h, false);
    }

    private static void BeginShowCloak(Window w, State st, IntPtr hwnd)
    {
        st.CloakHwnd = hwnd;
        st.CloakFrames = ShowCloakFrames;
        st.CloakGuard?.Stop();
        var guard = new DispatcherTimer(TimeSpan.FromMilliseconds(ShowCloakGuardMs), DispatcherPriority.Render, (_, _) =>
        {
            if (st.CloakHwnd == IntPtr.Zero) return;
            Uncloak(w);
            long now = Clock();
            foreach (var j in Jobs)
                if (ReferenceEquals(j.Key, w) && j.Start < Held) j.Start = now;
        }, w.Dispatcher);
        st.CloakGuard = guard;
    }

    /// <summary>窓が今消えかけか（連打で二度始めない・「出す」が来たら戻す、の判定）</summary>
    public static bool IsFadingOut(Window w) => States.TryGetValue(w, out var s) && s.Out && IsRunning(w);

    /// <summary>窓の今の透明度（触っていなければ rest）</summary>
    public static double CurrentOpacity(Window w, double rest)
        => States.TryGetValue(w, out var s) && !double.IsNaN(s.Opacity) ? s.Opacity : rest;

    /// <summary>透過する窓の濃さを変える。透過しない窓は WPF が WS_EX_LAYERED を付けさせず濃さが効かないため何もしない（修正J88）</summary>
    public static void SetOpacity(Window w, double a)
    {
        if (!w.AllowsTransparency) return;
        a = Math.Clamp(a, 0, 1);
        St(w).Opacity = a;
        w.Opacity = a;
    }

    /// <summary>透過する窓の中身のルートを指定基準（省略時は中心）で拡縮して見せる（窓の矩形は変えない）。
    /// 窓の大きさは変えないので中身の折り返しは動かない。透過しない窓は何もしない（修正J88）</summary>
    public static void SetScale(Window w, double s, Point? origin = null)
    {
        if (!w.AllowsTransparency) return;
        var stt = St(w);
        stt.Scale = s;
        if (w.Content is not FrameworkElement root) return;
        if (Math.Abs(s - 1.0) < 1e-6)
        {
            if (root.RenderTransform is ScaleTransform) root.RenderTransform = Transform.Identity;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            return;
        }
        var o = origin ?? new Point(0.5, 0.5);
        root.RenderTransformOrigin = o;
        if (root.RenderTransform is ScaleTransform st) { st.ScaleX = s; st.ScaleY = s; }
        else root.RenderTransform = new ScaleTransform(s, s);
    }

    private static void Reset(Window w, double rest)
    {
        try { SetOpacity(w, rest); SetScale(w, 1.0); } catch (Exception) { }
        St(w).Out = false;
    }

    /// <summary>出す（E1/E3）。消えている窓は Show して 0 から、消えかけなら今の透明度から出る方へ戻す。
    /// すでに出ていて動きも無ければ何もしない。rest = 出し終わりの透明度（設定値）。
    /// 透過しない窓は動きを付けずすぐ出す（修正J88）</summary>
    public static void ShowFade(Window w, double rest, int ms, double fromScale = 1.0, long? startedAt = null)
    {
        if (!w.AllowsTransparency)
        {
            Cancel(w);
            St(w).Out = false;
            if (!w.IsVisible) w.Show();
            return;
        }
        var st = St(w);
        bool wasOut = IsFadingOut(w);
        bool wasRunning = IsRunning(w);
        Cancel(w);
        double from;
        double s0;
        bool cloaked = false;
        IntPtr cloakHwnd = IntPtr.Zero;
        if (!w.IsVisible)
        {
            from = 0;
            if (startedAt is null)
            {
                try
                {
                    var h = new WindowInteropHelper(w).EnsureHandle();
                    if (h != IntPtr.Zero && Native.Cloak(h, true))
                    {
                        cloaked = true;
                        cloakHwnd = h;
                    }
                }
                catch (Exception ex) { AppLog.Error("motion show cloak", ex); }
            }
            SetOpacity(w, 0);          // 出る前に 0 にしてからでないと1フレーム全開が写る
            try
            {
                w.Show();
            }
            catch
            {
                if (cloaked && cloakHwnd != IntPtr.Zero && Native.IsWindow(cloakHwnd))
                    Native.Cloak(cloakHwnd, false);
                throw;
            }
            SetOpacity(w, 0);          // 帯（レイヤード）は HWND が出来てから効く
            SetScale(w, fromScale);
            s0 = fromScale;
        }
        else if (wasOut || wasRunning)
        {
            // 消えかけ・出かけの途中: 今の見え方から rest へ向かい直す
            from = CurrentOpacity(w, rest);
            s0 = st.Scale;
        }
        else
        {
            if (!double.IsNaN(st.Opacity) && Math.Abs(st.Opacity - rest) > 1e-3) SetOpacity(w, rest);
            return;
        }
        st.Out = false;
        // 修正J83: startedAt があれば、その時刻に出始めていたものとして途中から（消えている窓を出す時だけ）
        long start = startedAt is { } sa && from == 0 ? sa : Clock();
        RunFrom(w, ms, EaseOut, p =>
        {
            SetOpacity(w, Lerp(from, rest, p));
            SetScale(w, Lerp(s0, 1.0, p));
        }, () => { Uncloak(w); Reset(w, rest); }, start);
        if (cloaked) BeginShowCloak(w, st, cloakHwnd);
    }

    private static void StartOut(Window w, double rest, int ms, double toScale, Action final)
    {
        var st = St(w);
        double from = CurrentOpacity(w, rest);
        double s0 = st.Scale;
        st.Out = true;
        Run(w, ms, EaseOut, p =>
        {
            SetOpacity(w, Lerp(from, 0, p));
            SetScale(w, Lerp(s0, toScale, p));
        }, () =>
        {
            st.Out = false;
            final();
        });
    }

    /// <summary>消す（E3/E4）。薄れてから Hide。もう消えている・消えかけなら何もしない。
    /// onHidden は Hide した直後に呼ぶ（打ち切られたら呼ばない）。
    /// 透過しない窓は待たずにすぐ Hide して onHidden を呼ぶ（修正J88）</summary>
    public static void HideFade(Window w, double rest, int ms, Action? onHidden = null, double toScale = 1.0)
    {
        if (!w.IsVisible || IsFadingOut(w)) return;
        Cancel(w);
        if (!w.AllowsTransparency)
        {
            St(w).Out = false;
            try { w.Hide(); } catch (InvalidOperationException) { }
            onHidden?.Invoke();
            return;
        }
        StartOut(w, rest, ms, toScale, () =>
        {
            try { w.Hide(); } catch (InvalidOperationException) { }
            Reset(w, rest);
            onHidden?.Invoke();
        });
    }

    /// <summary>閉じる（E2）。薄れて縮んでから Close。消えかけなら何もしない（連打で再発火しない）。
    /// 既に出ていない窓・透過しない窓は待たずにすぐ Close する（修正J88）</summary>
    public static void CloseFade(Window w, double rest, int ms, double toScale, Action? beforeClose = null)
    {
        if (IsFadingOut(w)) return;
        if (!w.AllowsTransparency || !w.IsVisible)
        {
            Cancel(w);
            St(w).Out = false;
            beforeClose?.Invoke();
            try { w.Close(); } catch (InvalidOperationException) { }
            return;
        }
        Cancel(w);
        StartOut(w, rest, ms, toScale, () =>
        {
            beforeClose?.Invoke();
            try { w.Close(); } catch (InvalidOperationException) { }
        });
    }

    /// <summary>撮影前の隠し: 動きを付けない。走っている動きを打ち切って即 Hide し、見え方を rest へ戻す</summary>
    public static void HideNow(Window w, double rest)
    {
        Jobs.RemoveAll(j => ReferenceEquals(j.Key, w));
        try { if (w.IsVisible) w.Hide(); } catch (InvalidOperationException) { }
        Uncloak(w);
        Reset(w, rest);
    }

    /// <summary>走っている動きを打ち切り、見え方を rest（透明度）・等倍へ戻す（出したまま）。
    /// クイックのドラッグが始まった時など、吸い込みを即やめる用</summary>
    public static void Restore(Window w, double rest)
    {
        Cancel(w);
        Reset(w, rest);
    }

    // ---------------- E5: 吸い込み ----------------

    /// <summary>キャプチャ窓の矩形は動かさず、押した点 (px,py)（物理px）を窓の物理矩形に対する割合にして
    /// RenderTransformOrigin に据え、中身のルートを 1.0→QuickScaleTo(0.15) へ ease-in で縮め、透明度は後半で一気に落とし（F28）、
    /// 終わったら Hide → Reset → onHidden。途中で <see cref="Restore"/> すれば即やめられる（FIX_ROUND4 E4）</summary>
    public static void SuckIn(Window w, int px, int py, double rest, int ms, Action? onHidden = null)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (!w.AllowsTransparency || !w.IsVisible || hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var from))
        {
            HideNow(w, rest);
            onHidden?.Invoke();
            return;
        }
        Cancel(w);
        int fw = from.Right - from.Left, fh = from.Bottom - from.Top;
        var origin = new Point(fw > 0 ? (double)(px - from.Left) / fw : 0.5,
                               fh > 0 ? (double)(py - from.Top) / fh : 0.5);
        var st = St(w);
        double o0 = CurrentOpacity(w, rest);
        double s0 = st.Scale;
        st.Out = true;
        Run(w, ms, t => t, t =>   // 時間そのものを渡し、大きさと透明度を別々の曲線にする（F28）
        {
            SetOpacity(w, SuckAlpha(o0, t));
            SetScale(w, SuckScale(s0, t), origin);
        }, () =>
        {
            st.Out = false;
            try { w.Hide(); } catch (InvalidOperationException) { }
            Reset(w, rest);
            onHidden?.Invoke();
        });
    }
}

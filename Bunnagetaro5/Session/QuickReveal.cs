using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Session;

/// <summary>
/// クイックぶん投げ時のキャプチャ窓・バーの隠し／出し／I4 DWM Cloak 解除／配置（QuickPlace）を担当する。
/// CaptureSession から分割（段階4・整理）。動き・WindowMotion 呼び出しは WindowVisibility 側へ任せる。
/// </summary>
internal sealed class QuickReveal
{
    internal const int RevealMaxMs = 100;

    private readonly ConfigStore _cfg;
    private readonly WindowVisibility _vis;
    private readonly Func<CaptureWindow?> _getCapture;
    private readonly Func<ChromeBar?> _getBar;
    private readonly Action _openForQuick;

    private IntPtr _revealHwnd;
    private EventHandler? _revealOnRender;
    private DispatcherTimer? _revealGuard;
    private readonly List<Action> _afterReveal = new();

    // 修正J45: クイックの確定位置がまれに左上へずれる件の調べ用（狙った四角・隠しを外した理由・その後の本当の位置）
    private readonly Stopwatch _revealSw = new();
    private (int L, int T, int W, int H)? _quickWant;

    /// <summary>隠し中か（テスト用）</summary>
    internal bool RevealPending => _revealHwnd != IntPtr.Zero;

    /// <summary>F34: くっつける ON なら、バーと外の解答欄は窓の枠といっしょに消える（OFF は消さない）</summary>
    private bool BarFollowsQuick => _cfg.Settings.ChromeLinked;

    internal QuickReveal(ConfigStore cfg, WindowVisibility vis,
                         Func<CaptureWindow?> getCapture, Func<ChromeBar?> getBar, Action openForQuick)
    {
        _cfg = cfg;
        _vis = vis;
        _getCapture = getCapture;
        _getBar = getBar;
        _openForQuick = openForQuick;
    }

    /// <summary>DWM の cloak を掛けてから Show（画面には合成されないまま、初期化だけ進む）</summary>
    internal static void ShowCloaked(Window w)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        Native.Cloak(h, true);
        w.Show();
    }

    internal static void Uncloak(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) Native.Cloak(h, false);
    }

    internal bool CaptureAlive()
    {
        var cap = _getCapture();
        var hwnd = cap is null ? IntPtr.Zero : new WindowInteropHelper(cap).Handle;
        return !(cap is null || hwnd == IntPtr.Zero || !Native.IsWindow(hwnd));
    }

    internal void QuickHidePanels(int px, int py) => _vis.QuickHidePanels(px, py, BarFollowsQuick);

    /// <summary>キャプチャ窓を開いて (窓, HWND) を返す。既に開いていて壊れていなければ
    /// それをそのまま。開けられなければ null（Python版の IsWindow 再確認と同じ段取り）</summary>
    internal (CaptureWindow Window, IntPtr Hwnd)? EnsureCaptureForQuick()
    {
        var cap = _getCapture();
        IntPtr hwnd = cap is null ? IntPtr.Zero : new WindowInteropHelper(cap).Handle;
        if (cap is null || hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            _openForQuick();   // 画面に出さずに開く（離した時に新しい四角で初めて出る）
            cap = _getCapture();
            hwnd = cap is null ? IntPtr.Zero : new WindowInteropHelper(cap).Handle;
            if (cap is null || hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
                return null;
        }
        return (cap, hwnd);
    }

    /// <summary>押した瞬間: キャプチャ窓とバーをまとめて消す</summary>
    internal void QuickHideWindow(int px, int py) => _vis.QuickHideWindow(px, py, BarFollowsQuick, () => FinishReveal());

    /// <summary>消した窓・バーを出す</summary>
    internal void QuickShowWindow(bool fade = false) => _vis.QuickShowWindow(fade, ShowCloakedUntilRendered);

    // ---- 離した瞬間のちらつき対策（I4） ----
    // 吸い込みで消した窓には、途中まで縮んだ絵が残っている。新しい大きさで WPF が描き直す前に Show すると
    // その小さい絵が一瞬出る（起動して1発目は描き直しが遅いので目立つ）。DWM の cloak で画面に出さないまま Show し、
    // 2フレーム描けてから（最長 RevealMaxMs）cloak を外す。その間はダミー枠が出たままなので空白は出ない
    private void ShowCloakedUntilRendered(CaptureWindow c)
    {
        FinishReveal();
        var h = new WindowInteropHelper(c).Handle;
        bool cloaked = h != IntPtr.Zero && Native.Cloak(h, true);
        _vis.RestoreForReveal(c);
        c.Show();
        if (!cloaked) return;
        _revealHwnd = h;
        _revealSw.Restart();
        int frames = 0;
        _revealOnRender = (_, _) => { if (++frames >= 2) FinishReveal("render"); };
        CompositionTarget.Rendering += _revealOnRender;
        _revealGuard = new DispatcherTimer(TimeSpan.FromMilliseconds(RevealMaxMs), DispatcherPriority.Render,
            (_, _) => FinishReveal("timeout"), c.Dispatcher);
    }

    /// <summary>隠しを外して、待っていた後始末（ダミー枠を消す）を呼ぶ。何度呼んでもよい。
    /// by: 隠しを外した理由（"render"=2フレーム描けた・"timeout"=最長の時間が来た。ほかは次の操作で打ち切り）</summary>
    private void FinishReveal(string by = "next")
    {
        if (_revealOnRender is { } r) CompositionTarget.Rendering -= r;
        _revealOnRender = null;
        _revealGuard?.Stop();
        _revealGuard = null;
        if (_revealHwnd != IntPtr.Zero)
        {
            var hwnd = _revealHwnd;
            try { Native.Cloak(hwnd, false); } catch { }
            _revealHwnd = IntPtr.Zero;
            LogQuickPlaced(hwnd, by, _revealSw.ElapsedMilliseconds);
        }
        if (_afterReveal.Count == 0) return;
        var list = _afterReveal.ToArray();
        _afterReveal.Clear();
        foreach (var a in list)
        {
            try { a(); } catch (Exception ex) { AppLog.Error("quick reveal after", ex); }
        }
    }

    /// <summary>修正J45: 隠しを外した時と、その 300ms 後の窓の本当の位置を、狙った四角と比べて app.log に書く</summary>
    private void LogQuickPlaced(IntPtr hwnd, string by, long ms)
    {
        if (_quickWant is not { } want) return;
        _quickWant = null;
        static string R(Native.RECT r) => $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}";
        static bool Same(Native.RECT r, (int L, int T, int W, int H) w) =>
            r.Left == w.L && r.Top == w.T && r.Right - r.Left == w.W && r.Bottom - r.Top == w.H;
        string wantS = $"{want.L},{want.T} {want.W}x{want.H}";
        bool ok = Native.GetWindowRect(hwnd, out var now);
        AppLog.Info($"quick placed: want={wantS} now={(ok ? R(now) : "?")} reveal={by} {ms}ms");
        if (ok && !Same(now, want))
            AppLog.Warning($"quick place drift (reveal): want={wantS} now={R(now)}");
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            if (!Native.IsWindow(hwnd) || !Native.GetWindowRect(hwnd, out var r2)) return;
            if (!Same(r2, want)) AppLog.Warning($"quick place drift (+300ms): want={wantS} now={R(r2)}");
        };
        later.Start();
    }

    /// <summary>本物の窓が画面に出てから a を呼ぶ（隠し中でなければ今すぐ）</summary>
    internal void AfterQuickShown(Action a)
    {
        if (_revealHwnd == IntPtr.Zero) { a(); return; }
        _afterReveal.Add(a);
    }

    /// <summary>キャプチャ窓（とバー）を矩形（物理px）へまとめて寄せる。キャプチャ窓の枠と膜が
    /// そのまま「撮る四角」の目印になる（自前の別窓は足さない:
    /// Python版 _quick_place の方針）。隠れている途中でも位置だけは合わせてから出す</summary>
    internal void QuickPlace(int l, int t, int w, int h, bool fade = false)
    {
        var cap = _getCapture();
        if (cap is null) return;
        var hwnd = new WindowInteropHelper(cap).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool wasHidden = !cap.IsVisible || _vis.QuickHidden;
        bool sameRect = Native.GetWindowRect(hwnd, out var cur) &&
                        cur.Left == l && cur.Top == t &&
                        cur.Right - cur.Left == w && cur.Bottom - cur.Top == h;
        if (sameRect && !wasHidden)
            return;
        if (!fade) _quickWant = (l, t, w, h);   // 修正J45: 確定（離した時）の狙った四角。取り消して戻す時（fade）は書かない
        if (!sameRect)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, l, t, w, h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        QuickShowWindow(fade);
        if (_revealHwnd == IntPtr.Zero) _quickWant = null;   // 修正J45: 隠して出す道を通らなかった（次の確定に持ち越さない）
        // 窓が隠れていて Moving が来なかった時（または同じ矩形で出すだけの時）だけバーを置き直す（E5）
        if (wasHidden)
            _getBar()?.PositionChrome();
    }
}

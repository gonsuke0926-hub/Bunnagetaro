using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Input;

/// <summary>
/// 画面を少し暗くして範囲ドラッグのための覆い。全モニタ（仮想デスクトップ）いっぱいに出す。
/// 置き場所は物理px（仮想デスクトップ矩形）。撮影には必ず写らない（WDA_EXCLUDEFROMCAPTURE。
/// allow_capture_overlay の設定には従わない）。押す・離す・取り消しは QuickThrowController へ知らせるだけ。
/// 32bpp DIB の変更範囲だけを専用スレッド（DwmFlush 同期）から UpdateLayeredWindowIndirect で更新する。
/// 覆いを WPF 窓ではなく素の Win32 の窓にした。WPF の透過窓（AllowsTransparency）は WPF の描画スレッドも
/// 同じ窓へ「全面透明」の中身を貼るので、出始めに自前の暗い膜が1フレーム消えてちらつくことがあった。
/// 窓は UI スレッドで作る（WPF の Dispatcher がメッセージを回すので、押す・離す・Esc は今まで通り UI スレッドで来る）。
/// 出す前に全画面の大きさで暗い膜の絵を貼り、それから見せる。ふわっと出る動きも専用スレッドで、
/// 絵は貼り直さず UpdateLayeredWindow の全体の不透明度だけを変える。
/// </summary>
internal sealed class SnipOverlay
{
    /// <summary>暗さ（0〜255 の不透明度。64 ≒ 0.25 の「ちょっとだけ暗く」）</summary>
    internal const byte DimAlpha = 64;

    /// <summary>暗い膜がふわっと出る長さ（ms。ほかのふわっと出る動きと同じ ease-out）</summary>
    internal const int FadeInMs = 100;

    // プリマルチプライド 32bpp 画素（BGRA、リトルエンディアン uint = 0xAARRGGBB）。
    // 暗い膜は黒 alpha=DimAlpha、明るい穴は alpha=1（見た目透明だが、クリック・カーソルを受けられる）。
    private const uint DimPixel = (uint)DimAlpha << 24;
    private const uint HolePixel = 0x01000000u;

    private readonly DispatcherTimer _timeout;
    private readonly bool _allowCapture;
    private readonly object _dibLock = new();
    private readonly ManualResetEventSlim _wake = new(false);

    private IntPtr _hwnd;
    private readonly FrameSurface _surf = new();   // 変わった帯だけを塗って貼る
    private double _dimProgress;
    private long _fadeStart;

    private volatile bool _down;
    private volatile bool _finished;
    private volatile bool _selecting;
    private (int X0, int Y0, int MinW, int MinH, int Border)? _sel;
    private bool _holeShown;
    private (int X, int Y, int W, int H)? _holeRect;
    private (int X, int Y, int W, int H)? _borderRect;
    private bool _belowMin;
    private bool _borderVisible;
    private byte _borderAlpha;
    private Thread? _thread;
    private double _frameMs = 16.7;
    private long _lastFlushTicks;
    private volatile Func<(int X, int Y)?> _cursorProvider = Geom.CursorPos;

    /// <summary>今の膜の濃さ（0〜1。出始めは 0、FadeInMs で 1＝DimAlpha の暗さ）</summary>
    internal double DimProgress { get { lock (_dibLock) return _dimProgress; } }

    /// <summary>枠の四角（物理px。出していない間は null）。明るい所（HoleRect）と同じ四角</summary>
    internal (int X, int Y, int W, int H)? BorderRect { get { lock (_dibLock) return _borderRect; } }

    /// <summary>枠を出しているか</summary>
    internal bool BorderVisible { get { lock (_dibLock) return _borderVisible; } }

    /// <summary>枠の色の不透明度（0〜255）。四角が最小未満の間は半分（ダミー枠と同じ）</summary>
    internal byte BorderAlpha { get { lock (_dibLock) return _borderAlpha; } }

    /// <summary>今のカーソル位置（物理px）。既定は Geom.CursorPos（テストで差し替える）</summary>
    internal Func<(int X, int Y)?> CursorProvider
    {
        get => _cursorProvider;
        set => _cursorProvider = value;
    }

    /// <summary>四角の中（暗くしない所）。物理px。四角を選び始める前は null</summary>
    internal (int X, int Y, int W, int H)? HoleRect { get { lock (_dibLock) return _holeRect; } }

    /// <summary>窓ハンドル（閉じた後は Zero）</summary>
    internal IntPtr Hwnd => _hwnd;

    /// <summary>窓が画面に出ているか</summary>
    internal bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>専用スレッドが動いているか（テスト用）</summary>
    internal bool ThreadAlive => _thread is { IsAlive: true };

    /// <summary>閉じた（Finish の最後に1回）</summary>
    internal event EventHandler? Closed;

    /// <summary>左ボタンを押した（物理px）</summary>
    internal Action<int, int>? Began { get; set; }
    /// <summary>左ボタンを離した（物理px）</summary>
    internal Action<int, int>? Released { get; set; }
    /// <summary>取り消された（理由つき）</summary>
    internal Action<string>? Cancelled { get; set; }

    /// <summary>四角を描き始めた（押した後。ここから先は窓の出し入れで外れても取り消さない）</summary>
    internal bool Dragging { get => _down; set => _down = value; }

    internal SnipOverlay(TimeSpan timeout, bool allowCapture = false)
    {
        _allowCapture = allowCapture;
        _timeout = new DispatcherTimer(DispatcherPriority.Normal) { Interval = timeout };
        _timeout.Tick += (_, _) => Cancel("時間切れ");
    }

    /// <summary>覆いを出す。窓を作り、暗い膜の絵を貼ってから見せる（見えた時にはもう絵がある）。
    /// 作れなければ例外（呼び側が取り消す）</summary>
    internal void Run()
    {
        if (_finished) return;
        var v = Native.VirtualDesktop();
        int vw = v.Right - v.Left, vh = v.Bottom - v.Top;
        if (vw <= 0 || vh <= 0) throw new InvalidOperationException("仮想デスクトップの大きさが取れない");

        var inst = GetModuleHandle(null);
        EnsureClass(inst);
        var h = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED,
            ClassName, "", WS_POPUP, v.Left, v.Top, vw, vh, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (h == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx " + Marshal.GetLastWin32Error());
        _hwnd = h;
        Live[h] = this;
        Native.ApplyCaptureAffinity(h, _allowCapture);   // 配信・録画に映す設定に合わせる（見せる前に）

        lock (_dibLock)
        {
            _dimProgress = 0;
            if (!_surf.Reset(v.Left, v.Top, vw, vh, DimPixel) || !_surf.PresentFull(h, SourceAlpha(0)))
                throw new InvalidOperationException("暗い膜の絵を貼れない " + Marshal.GetLastWin32Error());
        }

        // 絵を貼ってから見せる。HWND_TOPMOST = -1、SWP_NOMOVE|SWP_NOSIZE|SWP_SHOWWINDOW
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        Native.SetForegroundWindow(h);   // Esc を受け取る（今までの Activate と同じ）
        SetFocus(h);

        _fadeStart = Stopwatch.GetTimestamp();
        _lastFlushTicks = 0;
        _thread = new Thread(FrameLoop) { IsBackground = true, Name = "SnipOverlayFrame" };
        _thread.Start();
        _timeout.Start();
    }

    // ---------------- 暗い膜・明るい穴・枠 ----------------

    /// <summary>四角を選び始めた（押した点・物理px）。以後は専用スレッドが DwmFlush ごとに穴と枠を更新する</summary>
    internal void BeginSelection(int x0, int y0, int minW, int minH, int border)
    {
        _sel = (x0, y0, minW, minH, border);
        _holeShown = false;
        UpdateHole();
        _selecting = true;
        _wake.Set();
    }

    /// <summary>専用スレッド: ふわっと出る間と四角を選んでいる間だけ1フレームごとに回る。どちらでもない間は眠る</summary>
    private void FrameLoop()
    {
        try
        {
            while (!_finished)
            {
                bool fading;
                lock (_dibLock) fading = _dimProgress < 1.0;
                if (!fading && !_selecting)
                {
                    _wake.Wait();   // 四角を選び始めるか、閉じるまで（CPU 0）
                    continue;
                }
                if (!FlushFrame()) Thread.Sleep(8);
                if (_finished) break;
                long t0 = Stopwatch.GetTimestamp();
                if (fading) StepFade();
                if (_selecting) UpdateHole();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms > _frameMs * 0.5)
                {
                    int skip = (int)(ms / _frameMs);
                    for (int i = 0; i < skip && !_finished; i++)
                        if (!FlushFrame()) { Thread.Sleep(8); break; }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SnipOverlay: 描画スレッドが落ちた", ex);
        }
    }

    private bool FlushFrame()
    {
        try
        {
            if (Native.DwmFlush() != 0) return false;
        }
        catch { return false; }
        long now = Stopwatch.GetTimestamp();
        if (_lastFlushTicks != 0)
        {
            double gap = (now - _lastFlushTicks) * 1000.0 / Stopwatch.Frequency;
            if (gap >= 1.0 && gap < _frameMs * 1.5 && gap < 50) _frameMs = Math.Min(_frameMs, gap);
        }
        _lastFlushTicks = now;
        return true;
    }

    /// <summary>ふわっと出る1コマ。絵はそのままで、窓全体の不透明度だけを変える（全画面の絵を貼り直さない）</summary>
    private void StepFade()
    {
        double elapsed = (Stopwatch.GetTimestamp() - _fadeStart) * 1000.0 / Stopwatch.Frequency;
        double p = elapsed >= FadeInMs ? 1.0 : WindowMotion.EaseOut(elapsed / FadeInMs);
        lock (_dibLock)
        {
            if (_finished) return;
            _dimProgress = p;
            if (!_surf.Ready) return;
            if (!FrameSurface.PresentAlpha(_hwnd, SourceAlpha(p))) _surf.PresentFull(_hwnd, SourceAlpha(p));
        }
    }

    private void UpdateHole()
    {
        if (_finished || _sel is not { } sel) return;
        var p = _cursorProvider();
        if (p is null) return;
        // ダミー枠（QuickMarker）と同じ。押した点から 2px 以内のうちは出さない
        if (!_holeShown && Math.Max(Math.Abs(p.Value.X - sel.X0), Math.Abs(p.Value.Y - sel.Y0)) <= 2) return;
        _holeShown = true;
        var (rc, below) = Geom.QuickDragRect(sel.X0, sel.Y0, p.Value.X, p.Value.Y, sel.MinW, sel.MinH, sel.Border);

        var c = CaptureBorder.Color;
        byte a = CaptureBorder.AlphaByte(CaptureBorder.Alpha);
        if (below) a = (byte)(a / 2);
        uint colorRef = (uint)(c.R | (c.G << 8) | (c.B << 16));
        uint borderPx = QuickMarker.PremulArgb(colorRef, a);
        int b = Math.Max(1, sel.Border);

        lock (_dibLock)
        {
            if (_finished) return;
            if (_holeRect == rc && _belowMin == below && _borderAlpha == a && _borderVisible) return;
            _holeRect = rc;
            _borderRect = rc;
            _belowMin = below;
            _borderAlpha = a;
            _borderVisible = true;
            if (!_surf.Ready) return;
            // 前と今の四角の4辺の帯だけを塗り直して、帯ごとに貼る（面積ではなく辺の長さに比例）
            byte ca = SourceAlpha(_dimProgress);
            if (!_surf.Draw(_hwnd, rc, new FrameSurface.Style(DimPixel, HolePixel, borderPx, b), ca))
                _surf.PresentFull(_hwnd, ca);
        }
    }

    /// <summary>直近の更新で塗った画素の数（テスト用。大きい四角でも辺の長さに比例することを見る）</summary>
    internal long LastPaintedPx { get { lock (_dibLock) return _surf.LastPaintedPx; } }

    /// <summary>テスト用: 画面座標 (物理px) の DIB 画素 (0xAARRGGBB) を読む</summary>
    internal uint SampleDibPixel(int screenX, int screenY)
    {
        lock (_dibLock) return _surf.Sample(screenX, screenY);
    }

    // ---------------- 窓全体の不透明度 ----------------

    private static byte SourceAlpha(double progress)
        => (byte)Math.Clamp((int)Math.Round(255.0 * Math.Clamp(progress, 0.0, 1.0)), 4, 255);

    // ---------------- 閉じる・取り消す ----------------

    /// <summary>覆いを閉じる（通知は出さない）。2回目以降は何もしない。UI スレッドから呼ぶ</summary>
    internal void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timeout.Stop();
        _wake.Set();
        _thread?.Join(500);
        _thread = null;
        var h = _hwnd;
        if (h != IntPtr.Zero)
        {
            try { if (GetCapture() == h) ReleaseCapture(); } catch { }
            try { DestroyWindow(h); } catch (Exception ex) { AppLog.Error("SnipOverlay: 窓を壊せない", ex); }
            Live.TryRemove(h, out _);
        }
        lock (_dibLock)
        {
            _surf.Dispose();
            _hwnd = IntPtr.Zero;
        }
        try { Closed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { AppLog.Error("SnipOverlay: Closed", ex); }
    }

    private void Cancel(string reason)
    {
        if (_finished) return;
        var cb = Cancelled;
        Finish();
        cb?.Invoke(reason);
    }

    /// <summary>Esc と同じ取り消し（テスト用）</summary>
    internal void CancelFromKey() => Cancel("Esc");

    // ---------------- 窓のメッセージ（UI スレッド） ----------------

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
            {
                if (_finished || _down) return IntPtr.Zero;
                var p = Geom.CursorPos();
                if (p is null) { Cancel("カーソル位置が取れない"); return IntPtr.Zero; }
                _down = true;
                _timeout.Stop();
                SetCapture(hwnd);
                Began?.Invoke(p.Value.X, p.Value.Y);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (_finished || !_down) return IntPtr.Zero;
                var p = Geom.CursorPos();
                if (p is null) { Cancel("カーソル位置が取れない"); return IntPtr.Zero; }
                Released?.Invoke(p.Value.X, p.Value.Y);   // 閉じるのは受けた側（確定の順序を持つ）
                return IntPtr.Zero;
            }
            case WM_RBUTTONDOWN:
                Cancel("右クリック");
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                return IntPtr.Zero;   // 右クリックのメニュー（WM_CONTEXTMENU）を出さない
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if ((int)wp == VK_ESCAPE) { Cancel("Esc"); return IntPtr.Zero; }
                break;
            case WM_ACTIVATE:
                // 押した後は窓の出し入れで外れるので見ない
                if (((int)wp & 0xFFFF) == 0 /* WA_INACTIVE */ && !_down && !_finished)
                {
                    Cancel("ほかの窓へ移った");
                    return IntPtr.Zero;
                }
                break;   // アクティブになった時は DefWindowProc がキーボードの入力先にする
            case WM_CLOSE:
                Cancel("閉じた");   // Alt+F4 など
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);   // 中身は UpdateLayeredWindow の絵だけ。何も塗らない
            case WM_NCDESTROY:
                Live.TryRemove(hwnd, out _);
                break;
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    // 窓の種類はプロセスで1つ（覆いは何度も作るので、そのたびに登録しない）
    private const string ClassName = "Bunnagetaro5SnipOverlay";
    private static readonly ConcurrentDictionary<IntPtr, SnipOverlay> Live = new();
    private static readonly WndProc StaticProc = ProcThunk;   // GC されないよう保持
    private static readonly object ClassLock = new();
    private static bool _classReady;

    private static void EnsureClass(IntPtr inst)
    {
        lock (ClassLock)
        {
            if (_classReady) return;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
                hInstance = inst,
                hCursor = LoadCursor(IntPtr.Zero, new IntPtr(IDC_CROSS)),   // 十字カーソル
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */) throw new InvalidOperationException("RegisterClassEx " + err);
            }
            _classReady = true;
        }
    }

    private static IntPtr ProcThunk(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        try
        {
            if (Live.TryGetValue(hwnd, out var o)) return o.OnMessage(hwnd, msg, wp, lp);
        }
        catch (Exception ex)
        {
            AppLog.Error($"SnipOverlay: メッセージ 0x{msg:X4} の処理で落ちた", ex);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    // ---------------- Win32 ----------------

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
    private const uint WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014, WM_NCDESTROY = 0x0082,
                       WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104,
                       WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int VK_ESCAPE = 0x1B;
    private const int IDC_CROSS = 32515;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }


    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

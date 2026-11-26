using System.Diagnostics;
using System.Runtime.InteropServices;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Input;

/// <summary>
/// クイックぶん投げで四角を描いている間の軽いダミー枠（FIX_ROUND4 E9）。
/// 専用スレッド（QuickMarker）が自分のメッセージループと素の Win32 窓を1つ持ち、
/// 画面の1フレーム（DwmFlush）ごとにカーソルまでの四角へ枠を描き直す。WPF も UI スレッドも通らない。
/// 見た目はキャプチャ窓の縁（Accent 一色・太さ BorderPhys の枠だけ）。修正J65: 窓は仮想デスクトップ全体の大きさで動かさず、
/// 中の絵（外も中も alpha 0・枠だけ不透明度 alpha の 32bpp プリマルチプライド）の変わった帯だけを
/// UpdateLayeredWindowIndirect で貼る（FrameSurface。1回の仕事が四角の面積ではなく辺の長さに比例する）。
/// 画面に写らない（WDA_EXCLUDEFROMCAPTURE）・クリックを素通し・アクティブにならない。
/// 作れなかった／落ちた時は Failed になり AppLog に1行。呼び側は「ダミー無し」で続ける。
/// </summary>
internal sealed class QuickMarker : IDisposable
{
    private const int WM_START = 0x0401, WM_STOP = 0x0402, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                       WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint PM_REMOVE = 1;
    private const uint WM_QUIT = 0x0012;

    private uint _colorRef;               // 0x00BBGGRR（WM_START で _req の値に更新）
    private int _border;
    private byte _alpha = 0xB3;
    // 修正J65: 窓は仮想デスクトップ全体の大きさで動かさず、中の絵（外も中も透明・枠だけ色）の変わった帯だけを貼る。
    // 今までの「四角の大きさの窓を毎フレーム作り直して全部貼る」は、大きい四角で 1 回が面積に比例して重かった。
    // 絵は専用スレッドだけが触る
    private readonly FrameSurface _surf = new();
    private uint _drawnArgb;              // いま描いている枠の色（プリマルチプライド）
    private (uint ColorRef, byte Alpha, int Border) _reqStyle;
    private Thread? _th;
    private IntPtr _hwnd;
    private volatile bool _failed;
    private volatile bool _active;
    private bool _disposed;
    private readonly object _lk = new();
    private (int X0, int Y0, int MinW, int MinH) _req;
    private bool _reqExt;                 // H6: 外から矩形を渡される形（カーソルを追わない）
    private (int X, int Y, int W, int H) _extRect;
    private bool _reqAllowCapture;        // 修正J120: 次の Start で使う「配信・録画に映す」設定
    private WndProc? _proc;               // GC されないよう保持
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>accentRgb: 枠の色。border: 枠の太さ（物理px）</summary>
    public QuickMarker(byte r, byte g, byte b, int border)
    {
        _colorRef = (uint)(r | (g << 8) | (b << 16));
        _border = Math.Max(1, border);
        _reqStyle = (_colorRef, _alpha, _border);
    }

    /// <summary>次の Start で使う色・不透明度・太さ（設定 capture_border_*。Start の時点の値で描く）</summary>
    public void SetStyle(byte r, byte g, byte b, byte alpha, int border)
    {
        lock (_lk) _reqStyle = ((uint)(r | (g << 8) | (b << 16)), alpha, Math.Max(1, border));
    }

    /// <summary>修正J120: 次の Start で使う「配信・録画に映す」設定（Start の時点の値で切り替える）</summary>
    public void SetAllowCapture(bool allow)
    {
        lock (_lk) _reqAllowCapture = allow;
    }

    /// <summary>作れなかった・落ちた（以後ダミー無し）</summary>
    public bool Failed => _failed;
    /// <summary>四角を追っている最中（Start 済みで Stop 前）</summary>
    public bool Active => _active;
    /// <summary>窓が画面に出ているか</summary>
    public bool IsShown => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);
    /// <summary>テスト用: 窓ハンドル</summary>
    internal IntPtr Hwnd => _hwnd;

    /// <summary>テスト用: いま絵に使っている不透明度（F31: 最小未満は半分）</summary>
    internal byte DrawnAlpha => (byte)(_drawnArgb >> 24);

    /// <summary>修正J65: 直近の更新で塗った画素の数（テスト用。大きい四角でも辺の長さに比例することを見る）</summary>
    internal long LastPaintedPx => _surf.LastPaintedPx;
    /// <summary>テスト用: スレッドが生きているか</summary>
    internal bool ThreadAlive => _th is { IsAlive: true };

    /// <summary>追従を始める（x0,y0 は押した点・物理px）。出せなければ false</summary>
    public bool Start(int x0, int y0, int minW, int minH)
    {
        if (_failed || _disposed) return false;
        if (!EnsureThread()) return false;
        lock (_lk) { _req = (x0, y0, minW, minH); _reqExt = false; }
        return PostMessage(_hwnd, WM_START, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>H6: カーソルを追わず、外から渡す矩形（Place）へ動く形で始める。出せなければ false。
    /// キャプチャ窓の上・左の縁を引いている間の見た目に使う（初期矩形はすぐ出す）</summary>
    public bool StartExternal(int x, int y, int w, int h)
    {
        if (_failed || _disposed) return false;
        if (!EnsureThread()) return false;
        lock (_lk) { _reqExt = true; _extRect = (x, y, Math.Max(1, w), Math.Max(1, h)); }
        return PostMessage(_hwnd, WM_START, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>H6: 外から矩形を渡す（物理px。最後の値が次の1フレームで置かれる）</summary>
    public void Place(int x, int y, int w, int h)
    {
        lock (_lk) { _extRect = (x, y, Math.Max(1, w), Math.Max(1, h)); }
    }

    /// <summary>隠して待機（CPU 0）へ戻す。何度呼んでもよい</summary>
    public void Stop()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_STOP, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Stop して、窓が実際に消えるまで少し待つ（撮影の前の確認用。最大 timeoutMs）</summary>
    public bool StopAndWaitHidden(int timeoutMs = 200)
    {
        Stop();
        var sw = Stopwatch.StartNew();
        while (IsShown && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(1);
        if (IsShown)
        {
            Stop();
            try { Native.DwmFlush(); } catch { }
        }
        return !IsShown;
    }

    private bool EnsureThread()
    {
        if (_th is not null) return !_failed;
        _th = new Thread(ThreadMain) { IsBackground = true, Name = "QuickMarker" };
        _th.SetApartmentState(ApartmentState.STA);
        _th.Start();
        _ready.Wait(2000);
        if (_hwnd == IntPtr.Zero && !_failed)
        {
            _failed = true;
            AppLog.Error("QuickMarker: 窓の用意が間に合わなかった。以後ダミー枠なし");
        }
        return !_failed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _th?.Join(1000);
    }

    // ---------------- 専用スレッド ----------------

    private void ThreadMain()
    {
        try
        {
            _proc = Proc;
            string cls = "Bunnagetaro5QuickMarker_" + Environment.ProcessId + "_" + GetHashCode();
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = cls,
            };
            if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx " + Marshal.GetLastWin32Error());
            var h = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                cls, "", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx " + Marshal.GetLastWin32Error());
            Native.ApplyCaptureAffinity(h, false);
            _hwnd = h;
            _ready.Set();
            Loop();
        }
        catch (Exception ex)
        {
            _failed = true;
            _active = false;
            AppLog.Error("QuickMarker: スレッドが落ちた。以後ダミー枠なし", ex);
        }
        finally { _surf.Dispose(); _ready.Set(); }
    }

    private bool _shown;
    private (int X, int Y, int W, int H) _last;
    private bool _lastBelow;              // F31: いま半透明（最小未満）で描いているか
    private (int X0, int Y0, int MinW, int MinH) _cur;
    private bool _curExt;
    private double _frameMs = 16.7;       // 見つけた最小のフレーム間隔（リフレッシュに合わせて自動で可変）
    private long _lastFlushTicks;

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_START:
                bool allowCapture;
                lock (_lk) { _cur = _req; _curExt = _reqExt; (_colorRef, _alpha, _border) = _reqStyle; allowCapture = _reqAllowCapture; _shownRect = null; }
                Native.ApplyCaptureAffinity(hwnd, allowCapture);
                _shown = false;
                _last = default;
                _lastBelow = false;
                _lastFlushTicks = 0;
                PrepareSurface(hwnd);
                _active = true;   // 要求を読み込んでから動き出す（Start/Stop はメッセージの順に効く）
                return IntPtr.Zero;
            case WM_STOP:
                _active = false;
                ShowWindow(hwnd, 0 /* SW_HIDE */);
                _shown = false;
                _last = default;
                // 隠した後で枠を消しておく（次に出す時に前の枠が1フレーム見えない。塗るのは枠の帯だけ）
                if (_surf.Drawn is not null)
                    _surf.Draw(hwnd, null, new FrameSurface.Style(0, 0, _drawnArgb, _border), 255);
                return IntPtr.Zero;
            case 0x0014: // WM_ERASEBKGND: 中身は UpdateLayeredWindow の絵だけ。何も塗らない
                return new IntPtr(1);
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wp, lp);
    }

    private void Loop()
    {
        while (true)
        {
            if (!_active)
            {
                // 待機: メッセージが来るまで眠る（CPU 0）
                int r = GetMessage(out var m, IntPtr.Zero, 0, 0);
                if (r <= 0) return;
                TranslateMessage(ref m);
                DispatchMessage(ref m);
                continue;
            }
            // 1) 画面の1フレームを待つ
            long t0;
            if (!FlushFrame()) Thread.Sleep(8);
            t0 = Stopwatch.GetTimestamp();
            // 2〜4) カーソルまでの四角へ
            Update();
            // 更新が1フレームの半分を超えたら、その分フレームを飛ばす（E5 の QuickFollowBudget = 0.5）
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > _frameMs * 0.5)
            {
                int skip = (int)(ms / _frameMs);
                for (int i = 0; i < skip && _active; i++)
                    if (!FlushFrame()) { Thread.Sleep(8); break; }
            }
            // 5) 溜まったメッセージを捌く
            while (PeekMessage(out var pm, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (pm.message == WM_QUIT) return;
                TranslateMessage(ref pm);
                DispatchMessage(ref pm);
            }
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
            if (gap >= 1.0 && gap < _frameMs * 1.5 && gap < 50) _frameMs = Math.Min(_frameMs, gap);   // 最小の間隔＝1フレーム
        }
        _lastFlushTicks = now;
        return true;
    }

    private void Update()
    {
        (int X, int Y, int W, int H) rc;
        bool below;
        if (_curExt)
        {
            // H6: 外から渡された矩形へ（変わっていなければ何もしない）
            lock (_lk) { rc = _extRect; }
            below = false;
        }
        else
        {
            if (CursorProvider() is not { } cp) return;
            var p = new Native.POINT { X = cp.X, Y = cp.Y };
            // 修正J49: マウスで描いている時に離したら、カーソルを追うのをやめて離した点で止まる
            // （確定する四角と同じ終点。離した後のマウスの動きを追うと、出てきた窓とずれて見えた）
            if (Released?.Invoke() == true)
            {
                if (ReleasedEnd?.Invoke() is not { } e) return;   // UI が拾った後は最後に描いた枠のまま
                p = new Native.POINT { X = e.X, Y = e.Y };
            }
            var c = _cur;
            // 押した点から 2px 以内のうちは出さない（押しているだけのあいだ画面が見える）
            if (!_shown && Math.Max(Math.Abs(p.X - c.X0), Math.Abs(p.Y - c.Y0)) <= 2) return;
            // F31: カーソルまでの四角そのまま（最小で広げない）。最小未満の間は半透明（離すと取り消しの合図）
            (rc, below) = Geom.QuickDragRect(c.X0, c.Y0, p.X, p.Y, c.MinW, c.MinH, _border);
        }
        if (_shown && rc == _last && below == _lastBelow) return;   // 同じ四角なら何もしない
        _lastBelow = below;
        // 修正J65: 窓は動かさず、前と今の枠の帯だけを塗り直して貼る（H12 と同じく位置と絵が1度に変わる）
        byte a = below ? (byte)(_alpha / 2) : _alpha;
        if (!_surf.Ready) return;
        uint argb = PremulArgb(_colorRef, a);
        var style = new FrameSurface.Style(0, 0, argb, _border);
        if (!_surf.Draw(_hwnd, rc, style, 255) && !_surf.PresentFull(_hwnd, 255))
            return;
        _drawnArgb = argb;
        if (!_shown)
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOACTIVATE | SWP_SHOWWINDOW | 0x1 /* NOSIZE */ | 0x2 /* NOMOVE */);
        _last = rc;
        lock (_lk) _shownRect = rc;   // 修正J45: 調べ用（UI のスレッドから読む）
        _shown = true;
    }

    private (int X, int Y, int W, int H)? _shownRect;

    /// <summary>修正J60b: 追うカーソルの位置（物理px）。テストで本物のマウスに頼らないよう差し替えられる</summary>
    internal volatile Func<(int X, int Y)?> CursorProvider = Geom.CursorPos;

    /// <summary>修正J49: 離したか（true の間はカーソルを追わない）。ダミー枠のスレッドから毎フレーム呼ぶ。null なら追い続ける</summary>
    internal volatile Func<bool>? Released;

    /// <summary>修正J49: 離した点（確定する四角の終点）。読めなければ null（最後に描いた枠のまま）</summary>
    internal volatile Func<(int X, int Y)?>? ReleasedEnd;

    /// <summary>修正J45: いま画面に出しているダミー枠の四角（物理px。まだ出していなければ null）</summary>
    internal (int X, int Y, int W, int H)? ShownRect { get { lock (_lk) return _shownRect; } }

    // ---------------- 枠の絵（H12） ----------------

    /// <summary>colorRef(0x00BBGGRR)・alpha から、プリマルチプライドの 0xAARRGGBB を作る</summary>
    internal static uint PremulArgb(uint colorRef, byte alpha)
    {
        uint r = colorRef & 0xFF, g = (colorRef >> 8) & 0xFF, b = (colorRef >> 16) & 0xFF;
        uint pr = (r * alpha + 127) / 255, pg = (g * alpha + 127) / 255, pb = (b * alpha + 127) / 255;
        return ((uint)alpha << 24) | (pr << 16) | (pg << 8) | pb;
    }

    /// <summary>枠だけ色（プリマルチプライド）・中は 0（透明）の w×h の絵を buf に作る（buf は足りなければ作り直す）。
    /// 太さが半分以上で内側が無い時は全面が枠。</summary>
    internal static void BuildFrame(ref uint[] buf, int w, int h, int border, uint colorRef, byte alpha)
    {
        int n = w * h;
        if (buf.Length < n) buf = new uint[n];
        else Array.Clear(buf, 0, n);
        uint px = PremulArgb(colorRef, alpha);
        int b = Math.Max(1, border);
        if (w <= 2 * b || h <= 2 * b) { Array.Fill(buf, px, 0, n); return; }
        Array.Fill(buf, px, 0, b * w);                       // 上辺
        Array.Fill(buf, px, (h - b) * w, b * w);             // 下辺
        for (int y = b; y < h - b; y++)
        {
            Array.Fill(buf, px, y * w, b);                   // 左辺
            Array.Fill(buf, px, y * w + w - b, b);           // 右辺
        }
    }

    /// <summary>修正J65: 窓を仮想デスクトップ全体の大きさにそろえる（出す前、隠れている間に。画面の並びが変わった時だけ作り直す）。
    /// 修正J75: 四方を 1px ずつ小さくする（SurfaceRect）</summary>
    private void PrepareSurface(IntPtr hwnd)
    {
        var v = SurfaceRect(Native.VirtualDesktop());
        int w = v.Right - v.Left, h = v.Bottom - v.Top;
        if (_surf.Ready && _surf.Left == v.Left && _surf.Top == v.Top && _surf.Width == w && _surf.Height == h) return;
        if (_surf.Reset(v.Left, v.Top, w, h, 0)) _surf.PresentFull(hwnd, 255);
    }

    /// <summary>修正J75: ダミー枠の窓の矩形。仮想デスクトップの四方を 1px ずつ小さくする。
    /// 画面をぴったり（以上）おおう一番手前の窓があると、Windows が「全画面のアプリが動いている」と見なし、
    /// 窓をつかんでいる間におやすみモード（タスクバーのベルマーク）が付き、タスクバーも裏へ下がった
    /// （SHQueryUserNotificationState が QUNS_BUSY になる。1px でも足りなければ全画面とは見なされない）。
    /// 一番外の 1px には枠が描かれない</summary>
    internal static Native.RECT SurfaceRect(Native.RECT virtualDesktop)
    {
        var v = virtualDesktop;
        if (v.Right - v.Left <= 2 || v.Bottom - v.Top <= 2) return v;
        return new Native.RECT { Left = v.Left + 1, Top = v.Top + 1, Right = v.Right - 1, Bottom = v.Bottom - 1 };
    }

    // ---------------- Win32 ----------------

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public int Size, W, H; public short Planes, Bpp; public int Comp, SizeImg, XPpm, YPpm, ClrUsed, ClrImp; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

using System.Runtime.InteropServices;

namespace Bunnagetaro5.Services;

/// <summary>
/// Win32の呼び出し（P/Invoke）。Python版 tw_win32.py に対応。
/// 窓の位置・キャプチャ・透過・フックなど、アプリ全体の Win32 をここへまとめる。
/// </summary>
public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>モニタに掛かっていなければ NULL を返すフラグ</summary>
    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisibleNative(IntPtr h);

    /// <summary>修正J69: 点 (x,y)（物理px）に一番近いモニタの作業領域（タスクバーを除いた矩形）。取れなければ null</summary>
    public static RECT? WorkAreaAt(int x, int y)
    {
        try
        {
            var m = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (m == IntPtr.Zero) return null;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(m, ref mi) ? mi.rcWork : null;
        }
        catch { return null; }
    }

    /// <summary>修正J70: 窓 h を置いた矩形 r がモニタの作業領域からはみ出している（タスクバーに重なりうる）なら、
    /// 最前面の中の一番手前へ出す（位置・大きさは変えない）。タスクバーも最前面の窓なので、後から出し直した方が手前に描かれる</summary>
    public static void RaiseIfOutsideWorkArea(IntPtr h, RECT r)
    {
        if (h == IntPtr.Zero || !IsWindowVisibleNative(h) || !OutsideWorkArea(r)) return;
        SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>修正J71: 窓 h の今の矩形で <see cref="RaiseIfOutsideWorkArea"/> する（出した直後など、置き場所が変わらない時用）</summary>
    public static void RaiseIfOutsideWorkArea(IntPtr h)
    {
        if (h != IntPtr.Zero && GetWindowRect(h, out var r)) RaiseIfOutsideWorkArea(h, r);
    }

    /// <summary>修正J69: 矩形 r（物理px）が、その真ん中があるモニタの作業領域からはみ出している（タスクバーに重なりうる）か</summary>
    public static bool OutsideWorkArea(RECT r)
    {
        if (WorkAreaAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2) is not { } w) return false;
        return r.Left < w.Left || r.Top < w.Top || r.Right > w.Right || r.Bottom > w.Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect([In] ref RECT lprc, uint dwFlags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetWindowRgnBox(IntPtr hWnd, out RECT lprc);

    // ---------------- 手順11: キャプチャ窓で使うもの（tw_win32.py 対応） ----------------

    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>WM_WINDOWPOSCHANGING の lParam（これから行く位置・大きさ）</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>
    /// WM_WINDOWPOSCHANGING が運ぶ「これから行く矩形」。位置もサイズも変わらない時と、
    /// 最小化の退避座標・極小の矩形は null（呼び側は従来の LocationChanged 経路に任せる）。
    /// NOMOVE/NOSIZE の側は現在の矩形（cur）のまま
    /// </summary>
    public static RECT? TargetRect(WINDOWPOS wp, RECT cur)
    {
        const uint NOMOVE = 0x0002, NOSIZE = 0x0001;
        if ((wp.flags & NOMOVE) != 0 && (wp.flags & NOSIZE) != 0) return null;
        int x = (wp.flags & NOMOVE) != 0 ? cur.Left : wp.x;
        int y = (wp.flags & NOMOVE) != 0 ? cur.Top : wp.y;
        int w = (wp.flags & NOSIZE) != 0 ? cur.Right - cur.Left : wp.cx;
        int h = (wp.flags & NOSIZE) != 0 ? cur.Bottom - cur.Top : wp.cy;
        if (x <= -30000 || y <= -30000 || w < 20 || h < 20) return null;
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCLBUTTONDOWN = 0x00A1;

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTBORDER = 18;
    /// <summary>中を素通し（tw_band.py HTTRANSPARENT=-1。下の窓へ渡す）</summary>
    public const int HTTRANSPARENT = -1;

    /// <summary>カーソルの形を決めるメッセージ（tw_band.py WM_SETCURSOR=0x0020）</summary>
    public const int WM_SETCURSOR = 0x0020;

    // 標準カーソル（tw_band.py IDC_* と同じ値）
    public const int IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643;
    public const int IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646;

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    /// <summary>主モニタの解像度（Python版 winfo_screenwidth/height と同じ基準）</summary>
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>その画面座標で最前面の窓。透過画素は抜けて下の窓が返る（確認用の生テスト）</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute,
        ref int pvAttribute, int cbAttribute);

    /// <summary>DWM の隠し（cloak）。窓は出たまま描かれるが、画面には合成されない</summary>
    public const int DWMWA_CLOAK = 13;

    public static bool Cloak(IntPtr hwnd, bool on)
    {
        try
        {
            int v = on ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>窓を出す・消す時のWindowsアニメーションを切る（tw_win32.no_transitions）</summary>
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    public static bool NoTransitions(IntPtr hwnd)
    {
        try
        {
            int v = 1;
            return DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // ---------------- 手順12: 写り込み防止と撮影（7.2/7.3） ----------------

    /// <summary>窓の写り込み方針。EXCLUDEFROMCAPTURE でスクショ系APIから消える（Win10 2004〜）</summary>
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>現在の affinity を読み出す（BOOL+out の形。値返しではない）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    /// <summary>修正J30: 配信・録画に映す設定に応じて写り込み方針（WDA_NONE / WDA_EXCLUDEFROMCAPTURE）を切り替える</summary>
    public static bool ApplyCaptureAffinity(IntPtr hWnd, bool allowCapture)
        => hWnd != IntPtr.Zero && SetWindowDisplayAffinity(hWnd, allowCapture ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);

    /// <summary>DWMの合成を1回やり取りして返す（画面の書き換え完了待ち）</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    /// <summary>
    /// 画面の合成（DWM）が frames 回描き直すのを待つ。窓を消した・動かした「後の」
    /// 画面を撮るため（tw_win32.wait_screen_settled）。決め打ちの秒数は使わない。
    /// DwmFlushが使えない時だけ短く眠って代える。どのスレッドから呼んでもよい
    /// </summary>
    public static void WaitScreenSettled(int frames = 2)
    {
        for (int i = 0; i < Math.Max(1, frames); i++)
        {
            try
            {
                if (DwmFlush() == 0) continue;
            }
            catch (DllNotFoundException) { /* 超古い環境の想定。眠って代える */ }
            Thread.Sleep(50);
        }
    }

    // ---------------- 手順14: バー（配置・ドラッグ・透過。tw_win32/tw_place 対応） ----------------

    /// <summary>GetAncestor の種類。2 = ルートウィンドウ（Tkの winfo_id → 実の窓）</summary>
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>クリック素通し（tw_win32 の WS_EX_TRANSPARENT）</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>仮想デスクトップ全体の矩形（tw_place._toolbar_rect_fits の 76〜79）</summary>
    public const int SM_XMONITOR = 76;
    public const int SM_YMONITOR = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>全モニタをまとめた外接矩形（物理px）。バーの収まる範囲の判定に使う</summary>
    public static RECT VirtualDesktop()
    {
        int x = GetSystemMetrics(SM_XMONITOR);
        int y = GetSystemMetrics(SM_YMONITOR);
        return new RECT
        {
            Left = x,
            Top = y,
            Right = x + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = y + GetSystemMetrics(SM_CYVIRTUALSCREEN),
        };
    }

    /// <summary>
    /// キャプチャ窓の「内側」の画面矩形（物理px、tw_win32.client_rect_screen）。
    /// 最小化中・壊れた窓・極端に小さい窓は null（バーの収まり判定に使わない）
    /// </summary>
    public static RECT? ClientRectScreen(IntPtr hwnd)
    {
        hwnd = GetAncestor(hwnd, GA_ROOT);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return null;
        if (!GetClientRect(hwnd, out var c)) return null;
        if (c.Right - c.Left < 20 || c.Bottom - c.Top < 20) return null;
        var tl = new POINT { X = c.Left, Y = c.Top };
        var br = new POINT { X = c.Right, Y = c.Bottom };
        if (!ClientToScreen(hwnd, ref tl) || !ClientToScreen(hwnd, ref br)) return null;
        return new RECT { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
    }

    /// <summary>
    /// 窓をクリック素通しにする／戻す（tw_win32.set_click_through）。
    /// SetWindowLongPtrW の戻り値は「変更前」の style なので当てにせず、
    /// 必ず読み直して反映を確認する（v4.4の実測対策のまま）
    /// </summary>
    public static bool SetClickThrough(IntPtr hwnd, bool enable)
    {
        hwnd = GetAncestor(hwnd, GA_ROOT);
        if (hwnd == IntPtr.Zero) return false;
        long cur = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        long want = enable ? cur | WS_EX_TRANSPARENT : cur & ~WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(want));
        long now = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        return ((now & WS_EX_TRANSPARENT) != 0) == enable;
    }

    // ---------------- 手順16: 窓外解答帯の縁リサイズ（tw_band / tw_strip） ----------------

    public const int GWL_STYLE = -16;
    /// <summary>WS_THICKFRAME: OSの縁ドラッグリサイズを効かせる（tw_win32 L32相当）</summary>
    public const int WS_THICKFRAME = 0x00040000;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>マウスで窓の移動・リサイズを終えた瞬間（tw_bandのsubclassprocが見る印）</summary>
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_SIZING = 0x0214;
    public const int WM_CANCELMODE = 0x001F;
    public const int WMSZ_LEFT = 1;
    public const int WMSZ_RIGHT = 2;
    public const int WMSZ_TOP = 3;
    public const int WMSZ_TOPLEFT = 4;
    public const int WMSZ_TOPRIGHT = 5;
    public const int WMSZ_BOTTOM = 6;
    public const int WMSZ_BOTTOMLEFT = 7;
    public const int WMSZ_BOTTOMRIGHT = 8;

    /// <summary>縁の掴み帯の厚さの基準（SM_CXSIZEFRAME。tw_band は 6px との大きい方で使う）</summary>
    public const int SM_CXSIZEFRAME = 32;

    /// <summary>DWMが描く窓枠と見かけのタイトルバーの色（Win10 2004〜）。
    /// COLORREF=0x00BBGGRR。DWMWA_COLOR_DEFAULT(1)で既定へ戻す。
    /// 特殊値は DWMWA_COLOR_INVALID=2 と DWMWA_COLOR_NONE=0xFFFFFFFE（枠の描画だけ消す。
    /// tw_bandがバーの枠に使う）。BORDER/CAPTION色は DwmGetWindowAttribute で
    /// 読み戻せない（E_INVALIDARG。実測）ので set の hr だけ見られる。
    /// Win11では上辺の枠が塗られない不具合対策にBORDERとCAPTIONの両方を塗る
    /// （translator_window._strip_frame_color）</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_COLOR_DEFAULT = 1;
    public const int DWMWA_COLOR_INVALID = 2;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>古いWindows（2004より前）で付かない時は false。使う側は握る</summary>
    public static bool SetFrameColors(IntPtr hwnd, int borderRef, int captionRef)
    {
        try
        {
            int a = borderRef, b = captionRef;
            int r1 = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref a, sizeof(int));
            int r2 = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref b, sizeof(int));
            return r1 == 0 && r2 == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}

using System.Runtime.InteropServices;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Input;

/// <summary>
/// 修正J65: 仮想デスクトップ大の 32bpp プリマルチプライド DIB に「外・枠・中」の3色の四角を1つ描き、
/// 四角が動いた時は変わりうる帯（4辺の帯）だけを塗り直して、その帯ごとに UpdateLayeredWindowIndirect で貼る部品。
/// 範囲ドラッグの覆い（SnipOverlay：外＝暗い膜・中＝ほぼ透明）とダミー枠（QuickMarker：外も中も透明）が使う。
/// 1回の仕事は四角の面積ではなく辺の長さに比例する（5120×1440 の実測: 全体を貼る 約3.0ms・幅8pxの帯 約0.05ms・全面を塗る 約2.0ms）。
/// スレッドの面倒は見ない（呼び側が1つのスレッドかロックで使う）。
/// </summary>
internal sealed class FrameSurface : IDisposable
{
    /// <summary>描く色（どれもプリマルチプライドの 0xAARRGGBB）と枠の太さ（物理px）</summary>
    internal readonly record struct Style(uint Outside, uint Inside, uint Border, int Thickness);

    private IntPtr _memDc, _dib, _dibOld, _bits;
    private int _w, _h, _left, _top;
    private (int X, int Y, int W, int H)? _drawn;   // DIB に描いてある四角（画面の物理px）
    private Style _drawnStyle;

    internal int Left => _left;
    internal int Top => _top;
    internal int Width => _w;
    internal int Height => _h;
    internal bool Ready => _bits != IntPtr.Zero;
    /// <summary>いま DIB に描いてある四角（物理px。無ければ null）</summary>
    internal (int X, int Y, int W, int H)? Drawn => _drawn;

    /// <summary>直近の Draw で貼った帯の数と、塗った画素の数（テスト・調べ用）</summary>
    internal int LastBands { get; private set; }
    internal long LastPaintedPx { get; private set; }

    /// <summary>画面の矩形 (物理px) に合わせて DIB を用意し、全面を outside で塗る（四角は無い状態）。
    /// 大きさが変わった時だけ作り直す</summary>
    internal unsafe bool Reset(int left, int top, int w, int h, uint outside)
    {
        if (w <= 0 || h <= 0) return false;
        if (_memDc == IntPtr.Zero)
        {
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            if (_memDc == IntPtr.Zero) return false;
        }
        if (_dib == IntPtr.Zero || _w != w || _h != h)
        {
            var bmi = new BITMAPINFO { Size = 40, W = w, H = -h, Planes = 1, Bpp = 32 };
            IntPtr nb = CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (nb == IntPtr.Zero) return false;
            IntPtr prev = SelectObject(_memDc, nb);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            else _dibOld = prev;
            _dib = nb; _bits = bits; _w = w; _h = h;
        }
        _left = left; _top = top;
        new Span<uint>((void*)_bits, w * h).Fill(outside);
        _drawn = null;
        _drawnStyle = new Style(outside, outside, outside, 1);
        return true;
    }

    /// <summary>四角を rect（物理px。null なら消す）へ描き直し、変わりうる帯だけを塗って帯ごとに貼る。
    /// 貼れなかった帯があれば false（呼び側が PresentFull で全体を貼り直す）</summary>
    internal bool Draw(IntPtr hwnd, (int X, int Y, int W, int H)? rect, Style style, byte constAlpha)
    {
        if (!Ready) return false;
        var bands = new List<(int L, int T, int R, int B)>(4);
        var old = _drawn;
        bool styleChanged = style != _drawnStyle;
        if (old is null && rect is null) { LastBands = 0; LastPaintedPx = 0; return true; }
        if (old == rect && !styleChanged) { LastBands = 0; LastPaintedPx = 0; return true; }
        if (style.Outside != _drawnStyle.Outside)
            throw new InvalidOperationException("外の色を変える時は Reset で塗り直す");
        if ((old is null) != (rect is null) && style.Inside == style.Outside && _drawnStyle.Inside == _drawnStyle.Outside
            && !IsSmall((old ?? rect)!.Value, Math.Max(style.Thickness, _drawnStyle.Thickness)))
        {
            // 中が外と同じ色（ダミー枠）の出し始め・消す: 塗るのは枠の4本の帯だけ
            var r0 = (old ?? rect)!.Value;
            int t = Math.Max(style.Thickness, _drawnStyle.Thickness);
            int L = r0.X, T = r0.Y, R = r0.X + r0.W, B = r0.Y + r0.H;
            bands.Add((L, T, R, T + t));
            bands.Add((L, B - t, R, B));
            bands.Add((L, T + t, L + t, B - t));
            bands.Add((R - t, T + t, R, B - t));
        }
        else if (old is null || rect is null || IsSmall(old.Value, Math.Max(style.Thickness, _drawnStyle.Thickness)) || IsSmall(rect.Value, Math.Max(style.Thickness, _drawnStyle.Thickness))
            || styleChanged && style.Inside != _drawnStyle.Inside)
        {
            // 出し始め・消す・小さい四角・中の色が変わった: 前と今の四角を合わせた範囲をまとめて
            var a = old ?? rect!.Value;
            var b = rect ?? old!.Value;
            bands.Add((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X + a.W, b.X + b.W), Math.Max(a.Y + a.H, b.Y + b.H)));
        }
        else
        {
            // 4辺の帯。前と今の同じ辺の間＋枠の太さ。帯の外の画素は、前と今で「外・枠・中」のどれに当たるかが変わらない
            var o = old.Value; var n = rect.Value;
            int t = Math.Max(style.Thickness, _drawnStyle.Thickness);
            int oL = o.X, oT = o.Y, oR = o.X + o.W, oB = o.Y + o.H;
            int nL = n.X, nT = n.Y, nR = n.X + n.W, nB = n.Y + n.H;
            int yT = Math.Min(oT, nT), yB = Math.Max(oB, nB), xL = Math.Min(oL, nL), xR = Math.Max(oR, nR);
            bands.Add((Math.Min(oL, nL), yT, Math.Max(oL, nL) + t, yB));   // 左
            bands.Add((Math.Min(oR, nR) - t, yT, Math.Max(oR, nR), yB));   // 右
            bands.Add((xL, Math.Min(oT, nT), xR, Math.Max(oT, nT) + t));   // 上
            bands.Add((xL, Math.Min(oB, nB) - t, xR, Math.Max(oB, nB)));   // 下
        }

        _drawn = rect;
        _drawnStyle = style;
        long painted = 0;
        bool ok = true;
        int n2 = 0;
        foreach (var bd in bands)
        {
            // 画面の物理px → DIB の中の座標
            int l = Math.Clamp(bd.L - _left, 0, _w), tp = Math.Clamp(bd.T - _top, 0, _h);
            int r = Math.Clamp(bd.R - _left, 0, _w), bt = Math.Clamp(bd.B - _top, 0, _h);
            if (l >= r || tp >= bt) continue;
            painted += Paint(l, tp, r, bt, rect, style);
            n2++;
            if (hwnd != IntPtr.Zero && !PresentDirty(hwnd, l, tp, r, bt, constAlpha)) ok = false;
        }
        LastBands = n2;
        LastPaintedPx = painted;
        return ok;
    }

    private static bool IsSmall((int X, int Y, int W, int H) r, int t) => r.W <= 2 * t || r.H <= 2 * t;

    /// <summary>DIB の中の範囲 [l,r)×[t,b) を、四角 rect（画面の物理px）が描かれた時の色で塗る。塗った画素数を返す</summary>
    private unsafe long Paint(int l, int t, int r, int b, (int X, int Y, int W, int H)? rect, Style s)
    {
        uint* buf = (uint*)_bits;
        Fill(buf, l, t, r, b, s.Outside);
        long n = (long)(r - l) * (b - t);
        if (rect is not { } rc) return n;
        int th = Math.Max(1, s.Thickness);
        int x0 = rc.X - _left, y0 = rc.Y - _top, x1 = x0 + rc.W, y1 = y0 + rc.H;
        if (rc.W <= 2 * th || rc.H <= 2 * th)
        {
            FillClip(buf, l, t, r, b, x0, y0, x1, y1, s.Border);
            return n;
        }
        if (s.Inside != s.Outside) FillClip(buf, l, t, r, b, x0 + th, y0 + th, x1 - th, y1 - th, s.Inside);
        FillClip(buf, l, t, r, b, x0, y0, x1, y0 + th, s.Border);             // 上
        FillClip(buf, l, t, r, b, x0, y1 - th, x1, y1, s.Border);             // 下
        FillClip(buf, l, t, r, b, x0, y0 + th, x0 + th, y1 - th, s.Border);   // 左
        FillClip(buf, l, t, r, b, x1 - th, y0 + th, x1, y1 - th, s.Border);   // 右
        return n;
    }

    private unsafe void FillClip(uint* buf, int cl, int ct, int cr, int cb, int l, int t, int r, int b, uint px)
        => Fill(buf, Math.Max(cl, l), Math.Max(ct, t), Math.Min(cr, r), Math.Min(cb, b), px);

    private unsafe void Fill(uint* buf, int l, int t, int r, int b, uint px)
    {
        l = Math.Clamp(l, 0, _w); r = Math.Clamp(r, 0, _w); t = Math.Clamp(t, 0, _h); b = Math.Clamp(b, 0, _h);
        int len = r - l;
        if (len <= 0 || t >= b) return;
        for (int y = t; y < b; y++) new Span<uint>(buf + (long)y * _w + l, len).Fill(px);
    }

    /// <summary>DIB 全体を窓へ貼る（位置・大きさも合わせる）</summary>
    internal bool PresentFull(IntPtr hwnd, byte constAlpha)
    {
        if (hwnd == IntPtr.Zero || !Ready) return false;
        var dst = new POINT { X = _left, Y = _top };
        var sz = new SIZE { W = _w, H = _h };
        var src = new POINT();
        var bf = new BLENDFUNCTION { SourceConstantAlpha = constAlpha, AlphaFormat = 1 };
        return UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref sz, _memDc, ref src, 0, ref bf, 2);
    }

    /// <summary>絵・位置・大きさはそのままで、窓全体の不透明度だけを変える（hdcSrc = NULL）</summary>
    internal static bool PresentAlpha(IntPtr hwnd, byte constAlpha)
    {
        if (hwnd == IntPtr.Zero) return false;
        var bf = new BLENDFUNCTION { SourceConstantAlpha = constAlpha, AlphaFormat = 1 };
        return UpdateLayeredWindowAlpha(hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref bf, 2);
    }

    private unsafe bool PresentDirty(IntPtr hwnd, int l, int t, int r, int b, byte constAlpha)
    {
        var dst = new POINT { X = _left, Y = _top };
        var sz = new SIZE { W = _w, H = _h };
        var src = new POINT();
        var bf = new BLENDFUNCTION { SourceConstantAlpha = constAlpha, AlphaFormat = 1 };
        var cl = new RECT { Left = l, Top = t, Right = r, Bottom = b };
        var info = new UPDATELAYEREDWINDOWINFO
        {
            cbSize = (uint)sizeof(UPDATELAYEREDWINDOWINFO),
            pptDst = &dst, psize = &sz, hdcSrc = _memDc, pptSrc = &src, pblend = &bf, dwFlags = 2, prcDirty = &cl,
        };
        return UpdateLayeredWindowIndirect(hwnd, ref info);
    }

    /// <summary>画面座標 (物理px) の画素 (0xAARRGGBB)。DIB の外なら 0（テスト用）</summary>
    internal unsafe uint Sample(int screenX, int screenY)
    {
        if (!Ready) return 0;
        int x = screenX - _left, y = screenY - _top;
        if ((uint)x >= (uint)_w || (uint)y >= (uint)_h) return 0;
        return ((uint*)_bits)[(long)y * _w + x];
    }

    public void Dispose()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_dibOld != IntPtr.Zero) SelectObject(_memDc, _dibOld);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            DeleteDC(_memDc);
        }
        _memDc = _dib = _dibOld = _bits = IntPtr.Zero;
        _w = _h = 0;
        _drawn = null;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public int Size, W, H; public short Planes, Bpp; public int Comp, SizeImg, XPpm, YPpm, ClrUsed, ClrImp; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UPDATELAYEREDWINDOWINFO
    {
        public uint cbSize;
        public IntPtr hdcDst;
        public POINT* pptDst;
        public SIZE* psize;
        public IntPtr hdcSrc;
        public POINT* pptSrc;
        public uint crKey;
        public BLENDFUNCTION* pblend;
        public uint dwFlags;
        public RECT* prcDirty;
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    private static extern bool UpdateLayeredWindowAlpha(IntPtr h, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindowIndirect(IntPtr h, ref UPDATELAYEREDWINDOWINFO ulwi);
}

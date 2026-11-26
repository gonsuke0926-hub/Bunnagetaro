namespace Bunnagetaro5.Services;

/// <summary>
/// クイックぶん投げが使う幾何の小さな道具立て。Python版 tw_input.py の
/// norm_rect / clamp_to_virtual / cursor_pos の移植（物理px・仮想デスクトップ基準）。
/// 数字だけの話はここで単体に晒し、Win32を呼ぶ所は薄く保つ。
/// </summary>
public static class Geom
{
    /// <summary>
    /// 2点から (left, top, w, h) 物理px矩形へ。逆方向ドラッグ・零点も吸収
    /// （Python版 norm_rect と同じ式）
    /// </summary>
    public static (int L, int T, int W, int H) NormRect(int x0, int y0, int x1, int y1)
    {
        int l = Math.Min(x0, x1);
        int r = Math.Max(x0, x1);
        int t = Math.Min(y0, y1);
        int b = Math.Max(y0, y1);
        return (l, t, r - l, b - t);
    }

    /// <summary>
    /// 仮想デスクトップ（複数モニタ全体）に収まるよう矩形を寄せる。
    /// 幅・高さは仮想画面を越えない。収める場所が無い場合（metricsが0を返す
    /// 異常環境）はそのまま返して暴走を防ぐ（Python版 clamp_to_virtual と同じ）
    /// </summary>
    public static (int X, int Y, int W, int H) ClampToVirtual(int x, int y, int w, int h)
        => ClampToVirtual(x, y, w, h,
                          Native.GetSystemMetrics(Native.SM_XMONITOR),
                          Native.GetSystemMetrics(Native.SM_YMONITOR),
                          Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
                          Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>metricsを inject できる版（単体テスト用。実値版は上から呼ぶだけ）</summary>
    internal static (int X, int Y, int W, int H) ClampToVirtual(
        int x, int y, int w, int h, int vx, int vy, int vw, int vh)
    {
        if (vw <= 0 || vh <= 0)
            return (x, y, w, h);
        w = Math.Max(1, Math.Min(w, vw));
        h = Math.Max(1, Math.Min(h, vh));
        x = Math.Max(vx, Math.Min(x, vx + vw - w));
        y = Math.Max(vy, Math.Min(y, vy + vh - h));
        return (x, y, w, h);
    }

    /// <summary>F31 見た目のための最小の大きさ（枠の太さ×2＋2px）。これより小さいと枠が見えなくなる</summary>
    public static int QuickMarkerVisualMin(int border) => 2 * Math.Max(1, border) + 2;

    /// <summary>F31 クイックで四角を描いている間のダミー枠の矩形（物理px）。押した点と今のカーソルを対角にした
    /// 四角そのまま（設定の最小幅・高さでは広げない。仮想画面には収める）。小さすぎて見えなくならないよう
    /// 枠の太さ×2＋2px だけは確保する（見た目だけ。判定には使わない）。belowMin は「このまま離すと
    /// 小さすぎて取り消し」（四角の元の大きさが minW/minH 未満）。離した時の判定は QuickThrowController.Release が
    /// NormRect で行う（同じ基準）</summary>
    public static ((int X, int Y, int W, int H) Rect, bool BelowMin) QuickDragRect(
        int x0, int y0, int x1, int y1, int minW, int minH, int border)
        => QuickDragRect(x0, y0, x1, y1, minW, minH, border,
                         Native.GetSystemMetrics(Native.SM_XMONITOR),
                         Native.GetSystemMetrics(Native.SM_YMONITOR),
                         Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
                         Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    internal static ((int X, int Y, int W, int H) Rect, bool BelowMin) QuickDragRect(
        int x0, int y0, int x1, int y1, int minW, int minH, int border,
        int vx, int vy, int vw, int vh)
    {
        var (l, t, w, h) = NormRect(x0, y0, x1, y1);
        bool below = w < minW || h < minH;
        int vis = QuickMarkerVisualMin(border);
        return (ClampToVirtual(l, t, Math.Max(w, vis), Math.Max(h, vis), vx, vy, vw, vh), below);
    }

    /// <summary>現在ポインタの仮想デスクトップ物理px (x, y)。取れなければ None</summary>
    public static (int X, int Y)? CursorPos()
    {
        if (!Native.GetCursorPos(out var p))
            return null;
        return (p.X, p.Y);
    }
}

using System.Text.RegularExpressions;

namespace Bunnagetaro5.Services;

/// <summary>
/// メイン窓の位置・大きさ。"WxH+X+Y"（物理ピクセル）の文字列として
/// config の main_geometry に保存する。
/// 「見えるモニタの上か」の判定も含む。
/// </summary>
public static class WindowGeometry
{
    public readonly record struct Geo(int W, int H, int X, int Y);

    // tk の wm geometry は常に "WxH+X+Y"（左にはみ出すと "+-8" になる）
    private static readonly Regex GeoRe =
        new(@"^(\d+)x(\d+)\+(-?\d+)\+(-?\d+)$", RegexOptions.Compiled);

    public const int MinW = 640;
    public const int MinH = 480;

    /// <summary>読み取りと最小サイズの下限カットまで</summary>
    public static bool TryParse(string? geo, out Geo g)
    {
        g = default;
        var m = GeoRe.Match(geo ?? "");
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var w) || !int.TryParse(m.Groups[2].Value, out var h) ||
            !int.TryParse(m.Groups[3].Value, out var x) || !int.TryParse(m.Groups[4].Value, out var y))
            return false;
        g = new Geo(Math.Max(MinW, w), Math.Max(MinH, h), x, y);
        return true;
    }

    /// <summary>保存用の文字列。負の位置は "+-8" の形（tkと同じ）</summary>
    public static string Format(int x, int y, int w, int h) => $"{w}x{h}+{x}+{y}";

    /// <summary>
    /// 窓位置の復元用: 窓全体ではなく「上端の帯の真ん中あたり」がどれかのモニタに
    /// 掛かっているか（物理px）。端が少し掛かるだけだと、掴めず動かせない窓になる。
    /// 判定できなければ false＝位置はOS任せ
    /// </summary>
    public static bool OnMonitor(int x, int y, int w, int h, int grip = 40)
    {
        if (w <= 0 || h <= 0) return false;
        int cx = x + w / 2;
        var r = new Native.RECT
        {
            Left = cx - grip,
            Top = y,
            Right = cx + grip,
            Bottom = y + Math.Min(grip, h),
        };
        return Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }
}

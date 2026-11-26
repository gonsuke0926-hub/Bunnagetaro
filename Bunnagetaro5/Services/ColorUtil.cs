using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Bunnagetaro5.Services;

/// <summary>
/// 設定欄の #rrggbb と色（Brush・Win32のCOLORREF）の間に立ち。
/// 判定はPython版 save_settings と同じ正規表現（# + 16進6桁）。
/// </summary>
public static class ColorUtil
{
    private static readonly Regex HexRe = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    public static bool IsHexColor(string? s) => s is not null && HexRe.IsMatch(s);

    /// <summary>Brushへ。読めなければ null。</summary>
    public static SolidColorBrush? ToBrush(string? hex)
    {
        if (!IsHexColor(hex)) return null;
        var r = int.Parse(hex!.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var br = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
        br.Freeze();
        return br;
    }

    /// <summary>#rrggbb → Win32 COLORREF（0x00BBGGRR）。読めなければ null。</summary>
    public static uint? ToColorRef(string? hex)
    {
        if (!IsHexColor(hex)) return null;
        var r = int.Parse(hex!.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (uint)((b << 16) | (g << 8) | r);
    }

    /// <summary>COLORREF → #rrggbb（小文字。tkinterの色選択と同じ形）。</summary>
    public static string FromColorRef(uint cref)
    {
        int r = (int)(cref & 0xFF), g = (int)((cref >> 8) & 0xFF), b = (int)((cref >> 16) & 0xFF);
        return "#" + r.ToString("x2", CultureInfo.InvariantCulture)
                     + g.ToString("x2", CultureInfo.InvariantCulture)
                     + b.ToString("x2", CultureInfo.InvariantCulture);
    }
}

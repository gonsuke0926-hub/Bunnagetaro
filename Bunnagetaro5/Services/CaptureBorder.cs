using System.Windows.Media;

namespace Bunnagetaro5.Services;

/// <summary>キャプチャ窓の枠の今の見た目（E11。設定 capture_border_color / _alpha / _px）。
/// 縁を描く CaptureWindow・撮る範囲の削り幅・掴める幅・クイックのダミー枠が同じ値を見る。
/// ぶん投げのワーカーは窓へ触れず設定も持たないので、UI 側が Update で置いた値を読む。
/// 既定値（#0067C0・0.70・4px）は今までの見た目・撮る範囲・掴める幅と1画素も変わらない。</summary>
public static class CaptureBorder
{
    public const string DefaultColor = "#0067C0";
    public const double DefaultAlpha = 0.70;
    public const int DefaultPx = 4;

    private static volatile int _px = DefaultPx;
    private static Color _color = Color.FromRgb(0x00, 0x67, 0xC0);
    private static double _alpha = DefaultAlpha;

    /// <summary>縁の太さ（物理px 1〜16）</summary>
    public static int Px => _px;
    /// <summary>掴める幅（物理px）。太さ＋4、最低8（200%窓でも掴める余裕。既定4pxでは従来の8）</summary>
    public static int HitBand => HitBandFor(_px);
    public static int HitBandFor(int px) => Math.Max(8, px + 4);
    public static Color Color => _color;
    public static double Alpha => _alpha;
    /// <summary>不透明度 → 0〜255（既定 0.70 は従来の 0xB3 になる）</summary>
    public static byte AlphaByte(double alpha) => (byte)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255, MidpointRounding.AwayFromZero);

    public static void Update(AppSettings s) => Set(s.CaptureBorderColor, s.CaptureBorderAlpha, s.CaptureBorderPx);

    public static void Set(string hex, double alpha, int px)
    {
        var b = ColorUtil.ToBrush(hex);
        _color = b?.Color ?? Color.FromRgb(0x00, 0x67, 0xC0);
        _alpha = Math.Clamp(alpha, 0.15, 1.0);
        _px = Math.Clamp(px, 1, 16);
    }

    /// <summary>縁のブラシ（半透明の色。Freeze 済み）</summary>
    public static SolidColorBrush Brush()
    {
        var c = _color;
        c.A = AlphaByte(_alpha);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

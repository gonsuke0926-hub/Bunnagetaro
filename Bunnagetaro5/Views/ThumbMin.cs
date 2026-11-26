namespace Bunnagetaro5;

/// <summary>細スクロールバーの棒の最小長。Track.ViewportSize へ入れる値を作る。
/// 棒の長さ比 = viewport/(max-min+viewport) が 最小長/トラック長 に満たなければ、
/// その比になるよう viewport を水増しする。純関数 <see cref="Inflate"/> をテストする</summary>
public sealed class ThumbMin : System.Windows.Data.IMultiValueConverter
{
    public static readonly ThumbMin Instance = new();
    public const double MinThumbPx = 24;

    public static double Inflate(double viewport, double min, double max, double trackLen)
    {
        double range = max - min;
        if (double.IsNaN(viewport) || double.IsNaN(range) || range <= 0 || viewport <= 0 ||
            double.IsNaN(trackLen) || trackLen <= MinThumbPx * 2) return viewport;
        double need = MinThumbPx / trackLen;   // 0 < need < 0.5
        double ratio = viewport / (range + viewport);
        if (ratio >= need) return viewport;
        return need * range / (1 - need);
    }

    public object Convert(object[] v, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        try
        {
            double vp = (double)v[0], mn = (double)v[1], mx = (double)v[2];
            bool vertical = v[3] is System.Windows.Controls.Orientation o && o == System.Windows.Controls.Orientation.Vertical;
            double len = vertical ? (double)v[4] : (double)v[5];
            return Inflate(vp, mn, mx, len);
        }
        catch (Exception) { return v.Length > 0 && v[0] is double d ? d : 0.0; }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

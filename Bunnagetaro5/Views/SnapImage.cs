using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bunnagetaro5.Views;

/// <summary>画素に揃える計算。式の画像は文字の組みで半端な位置（y=12.37px など）に置かれ、
/// 1:1 の画像だと前後の画素が混ざってにじむ。端数を打ち消して整数の物理pxへ寄せる。</summary>
internal static class PixelSnap
{
    /// <summary>位置 posDip（DIP）を、いちばん近い整数の物理pxへ寄せるための移動量（DIP）。
    /// dpiScale は 1.0 / 1.25 / 1.5 など</summary>
    public static double OffsetDip(double posDip, double dpiScale)
    {
        if (dpiScale <= 0) return 0;
        double phys = posDip * dpiScale;
        return (Math.Round(phys, MidpointRounding.AwayFromZero) - phys) / dpiScale;
    }

    /// <summary>画像の画素数が表示の物理pxと同じ（等倍）か</summary>
    public static bool IsOneToOne(int pixels, double dipSize, double dpiScale)
        => Math.Abs(pixels - dipSize * dpiScale) < 0.01;
}

/// <summary>式の画像。レイアウトのたび（とスクロールのたび）に画面上の位置の端数を測り、
/// 打ち消す RenderTransform（配置は変えないので再レイアウトを起こさない）を掛けて整数の物理pxへ寄せる。
/// 等倍の時は混ぜない描き方（NearestNeighbor）、そうでなければ HighQuality。</summary>
internal sealed class SnapImage : Image
{
    private double _dx, _dy;
    private ScrollViewer? _sv;

    public SnapImage()
    {
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayout;
            _sv = FindScroll();
            if (_sv is not null) _sv.ScrollChanged += OnScroll;
            Snap();
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayout;
            if (_sv is not null) _sv.ScrollChanged -= OnScroll;
            _sv = null;
        };
    }

    private ScrollViewer? FindScroll()
    {
        DependencyObject? d = this;
        while (d is not null)
        {
            if (d is ScrollViewer sv) return sv;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnLayout(object? s, EventArgs e) => Snap();
    private void OnScroll(object? s, ScrollChangedEventArgs e) => Snap();

    /// <summary>テスト・診断用: いま掛けている打ち消し量（DIP）</summary>
    internal (double X, double Y) AppliedOffset => (_dx, _dy);

    private void Snap()
    {
        try
        {
            if (!IsLoaded || VisualTreeHelper.GetParent(this) is not Visual parent) return;
            var root = PresentationSource.FromVisual(this)?.RootVisual;
            if (root is null) return;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var off = VisualTreeHelper.GetOffset(this);   // 親の中の配置位置（RenderTransform は含まない）
            var pos = parent.TransformToAncestor(root).Transform(new Point(off.X, off.Y));
            double dx = PixelSnap.OffsetDip(pos.X, dpi), dy = PixelSnap.OffsetDip(pos.Y, dpi);
            if (Math.Abs(dx - _dx) > 0.0005 || Math.Abs(dy - _dy) > 0.0005)
            {
                _dx = dx; _dy = dy;
                RenderTransform = new TranslateTransform(dx, dy);
            }
            var mode = Source is BitmapSource bs && PixelSnap.IsOneToOne(bs.PixelWidth, Width, dpi)
                       && PixelSnap.IsOneToOne(bs.PixelHeight, Height, dpi)
                ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
            if (RenderOptions.GetBitmapScalingMode(this) != mode) RenderOptions.SetBitmapScalingMode(this, mode);
        }
        catch (InvalidOperationException) { /* 木から外れた直後など。次のレイアウトで測り直す */ }
    }
}

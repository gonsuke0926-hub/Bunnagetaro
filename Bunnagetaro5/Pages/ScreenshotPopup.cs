using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 履歴カードのサムネイルをクリックして開く、原寸の画像窓（python版 _show_popup）。
/// 元の大きさが画面の8割より大きければ縮んで見せる。縁で広げ縮めでき（縦横比保持）、
/// 画像のダブルクリックで開いた時の大きさへ戻る。閉じると消える。
/// </summary>
public sealed class ScreenshotPopup : Window
{
    private readonly Image _img = new();
    private BitmapSource? _bmp;

    /// <summary>H8/J88: 透過しない窓なので動きは付けず Windows の出る動きに任せ、位置合わせだけ先に済ませて出す</summary>
    public void ShowAnimated()
    {
        PlaceBeforeShow();
        WindowMotion.ShowFade(this, 1.0, WindowMotion.OpenMs);
    }

    /// <summary>H8: Show の前に HWND を作って、窓の大きさを測り、最終の位置へ置いておく。
    /// 既定の位置に一瞬出てから動く、を無くす</summary>
    private void PlaceBeforeShow()
    {
        if (IsVisible || _settled) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();   // OnSourceInitialized → ApplySize も走る
            if (Owner is null || _bmp is null) return;
            // 最終の大きさ = 画像の大きさ + 枠・タイトルバー（作った HWND の窓矩形と内側の差）
            if (!Native.GetWindowRect(hwnd, out var wr) || !GetClientRect(hwnd, out var cr)) return;
            var dpi = VisualTreeHelper.GetDpi(Owner);
            double chromeW = ((wr.Right - wr.Left) - (cr.Right - cr.Left)) / dpi.DpiScaleX;
            double chromeH = ((wr.Bottom - wr.Top) - (cr.Bottom - cr.Top)) / dpi.DpiScaleY;
            // 画像が小さい時（拡大率が高い時は 200px の画像でも 150% で 133 DIP）は、窓は最小の大きさ（MinWidth・MinHeight）になる。
            // 見積もりにもそれを入れないと、開いた後に置き直して一瞬ずれる（2026-11-16、150% の PC で H8 のテストが落ちた原因）
            PositionAtOwner(Math.Max(MinWidth, _orig.W + chromeW), Math.Max(MinHeight, _orig.H + chromeH));
        }
        catch (Exception ex) { AppLog.Error("popup preplace failed", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Native.RECT rect);

    /// <summary>
    /// 画像1px=画面1物理px にした時のDIP寸法を、上限（作業領域の8割・DIP）に収まるよう
    /// 縦横比を保って縮める。純関数（テスト用）。
    /// </summary>
    public static (double W, double H) FitSize(int pixW, int pixH, double dpiScaleX, double dpiScaleY,
                                               double maxW, double maxH)
    {
        if (pixW <= 0 || pixH <= 0) return (0, 0);
        double w = pixW / (dpiScaleX > 0 ? dpiScaleX : 1);
        double h = pixH / (dpiScaleY > 0 ? dpiScaleY : 1);
        double f = 1.0;
        if (maxW > 0 && w > maxW) f = Math.Min(f, maxW / w);
        if (maxH > 0 && h > maxH) f = Math.Min(f, maxH / h);
        return (w * f, h * f);
    }

    /// <summary>親窓の中心に置き、作業領域からはみ出す時は作業領域の中へずらす（すべて DIP）。純関数（テスト用）。</summary>
    public static (double Left, double Top) CenterInWorkArea(
        double winW, double winH,
        double ownerLeft, double ownerTop, double ownerW, double ownerH,
        double workLeft, double workTop, double workW, double workH)
    {
        double left = ownerLeft + (ownerW - winW) / 2.0;
        double top = ownerTop + (ownerH - winH) / 2.0;
        if (left + winW > workLeft + workW) left = workLeft + workW - winW;
        if (top + winH > workTop + workH) top = workTop + workH - winH;
        if (left < workLeft) left = workLeft;
        if (top < workTop) top = workTop;
        return (left, top);
    }

    /// <summary>この窓（初回で Owner がある時は Owner）が出ているモニタの作業領域（物理px）。取れなければ主モニタ、それも無ければ null</summary>
    private Native.RECT? WorkAreaPhys()
    {
        var target = (!_settled && Owner is not null) ? Owner : this;
        var hwnd = new WindowInteropHelper(target).Handle;
        if (hwnd == IntPtr.Zero && target != this) hwnd = new WindowInteropHelper(this).Handle;
        var mons = EnvInfo.Monitors();
        if (mons.Count == 0) return null;
        EnvInfo.Monitor pick = mons.FirstOrDefault(m => m.Primary);
        if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
            foreach (var m in mons)
                if (cx >= m.X && cx < m.X + m.W && cy >= m.Y && cy < m.Y + m.H) { pick = m; break; }
        }
        return new Native.RECT { Left = pick.WorkX, Top = pick.WorkY,
                                 Right = pick.WorkX + pick.WorkW, Bottom = pick.WorkY + pick.WorkH };
    }

    /// <summary>画像1px=画面1物理px。上限は出ているモニタの作業領域の8割</summary>
    private void ApplySize()
    {
        if (_bmp is null) return;
        var dpi = VisualTreeHelper.GetDpi((!_settled && Owner is not null) ? (Visual)Owner : this);
        double maxW = SystemParameters.PrimaryScreenWidth * 0.8, maxH = SystemParameters.PrimaryScreenHeight * 0.8;
        if (WorkAreaPhys() is { } wa)
        {
            maxW = (wa.Right - wa.Left) / dpi.DpiScaleX * 0.8;
            maxH = (wa.Bottom - wa.Top) / dpi.DpiScaleY * 0.8;
        }
        var (w, h) = FitSize(_bmp.PixelWidth, _bmp.PixelHeight, dpi.DpiScaleX, dpi.DpiScaleY, maxW, maxH);
        _orig = (w, h);
        if (_settled)
        {
            // DPI が変わった: 原寸を取り直して窓も原寸へ戻す（Manual のまま）
            ResetToOriginal();
        }
        else
        {
            _img.Width = w;   // 初回は SizeToContent が窓の大きさを決める材料
            _img.Height = h;
        }
    }

    /// <summary>F15: 大きさが決まった後で、Owner（メイン窓）の中央へ置く（作業領域内に収める）</summary>
    private void PositionAtOwner(double preW = 0, double preH = 0)
    {
        if (Owner is not { } own) return;
        double w = ActualWidth > 0 ? ActualWidth : (preW > 0 ? preW : Width);
        double h = ActualHeight > 0 ? ActualHeight : (preH > 0 ? preH : Height);
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(own);
        double wl = 0, wt = 0, ww = SystemParameters.WorkArea.Width, wh = SystemParameters.WorkArea.Height;
        if (WorkAreaPhys() is { } wa)
        {
            wl = wa.Left / dpi.DpiScaleX;
            wt = wa.Top / dpi.DpiScaleY;
            ww = (wa.Right - wa.Left) / dpi.DpiScaleX;
            wh = (wa.Bottom - wa.Top) / dpi.DpiScaleY;
        }
        var (left, top) = CenterInWorkArea(w, h, own.Left, own.Top, own.ActualWidth, own.ActualHeight, wl, wt, ww, wh);
        Left = left;
        Top = top;
    }

    /// <summary>開いた時の画像の大きさ（DIP。画像1px=物理1px・上限8割）</summary>
    private (double W, double H) _orig;
    /// <summary>画像以外（タイトルバー・コピー行・枠）が占める大きさ。初回に実測</summary>
    private (double W, double H) _chrome;
    /// <summary>初回に SizeToContent で決めた後、Manual（自由に広げ縮め）へ移った印</summary>
    private bool _settled;

    /// <summary>初回レイアウト後: 窓の大きさを固定値へ移して SizeToContent を Manual に戻し、
    /// 画像は窓に合わせて Uniform で拡縮させる（D3）</summary>
    private void Settle()
    {
        if (_settled || _bmp is null) return;
        _settled = true;
        _chrome = (ActualWidth - _orig.W, ActualHeight - _orig.H);
        double w = ActualWidth, h = ActualHeight;
        SizeToContent = SizeToContent.Manual;
        Width = w;
        Height = h;
        _img.ClearValue(FrameworkElement.WidthProperty);
        _img.ClearValue(FrameworkElement.HeightProperty);
    }

    /// <summary>開いた時の大きさへ戻す（画像ダブルクリック）</summary>
    private void ResetToOriginal()
    {
        if (!_settled) return;
        Width = _orig.W + _chrome.W;
        Height = _orig.H + _chrome.H;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Settle();
        PositionAtOwner();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplySize();
    }

    protected override void OnClosed(EventArgs e)
    {
        WindowMotion.Cancel(this);
        base.OnClosed(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplySize();
    }

    public ScreenshotPopup(string file, string ts, Action<string> setStatus, string? body = null)
    {
        Title = $"スクショ — {ts}";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanResize;   // 広げ縮めできる（大きさは保存しない。D3）
        MinWidth = 160;
        MinHeight = 120;
        Background = (Brush)Application.Current.Resources["Bg"];
        ShowInTaskbar = false;
        Icon = AppIcon.Window();

        // F14・H4b: 下の「画像をコピー」行は無くし、右クリックは「スクショをコピー」だけ（答えのコピーは本文の右クリックで）
        var menu = new ContextMenu();
        var miImg = new MenuItem { Header = "スクショをコピー", IsEnabled = !string.IsNullOrEmpty(file) };
        miImg.Click += (_, _) => LogPage.CopyShotToClipboard(file, setStatus);
        menu.Items.Add(miImg);

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            _bmp = bmp;
            _img.Source = bmp;
            _img.Stretch = Stretch.Uniform;
            _img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { ResetToOriginal(); e.Handled = true; }
            };
            // 寸法は ApplySize（SourceInitialized／DpiChanged）で窓のDPIから決める
        }
        catch (Exception ex)
        {
            AppLog.Error("popup failed", ex);
        }

        _img.ContextMenu = menu;
        ContextMenu = menu;
        Content = _img;
    }
}

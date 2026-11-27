using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 設定ページの行カード。
/// 左に名前と説明、右に操作部品。幅が狭い時は操作部品を自動で下段へ折り返す。
/// 大きい部品は SetBottom でさらにその下段（両列またぎ）に置く。
/// 範囲外の値を保存しなかった時など、説明欄へ理由を出す（DescBox）。
/// </summary>
public class SettingRow : Border
{
    private readonly Grid _grid;
    private readonly StackPanel _left;
    private readonly FrameworkElement _control;
    private readonly TextBlock _nameTb;
    private readonly TextBlock _descTb;
    private bool _wrapped;

    /// <summary>説明欄。理由や「○〜○の数値」をここへ出す。</summary>
    public TextBlock DescBox => _descTb;
    /// <summary>名前欄（見出しを状態で見直す時用）。</summary>
    public TextBlock NameBox => _nameTb;

    public SettingRow(string name, string? desc, FrameworkElement control)
    {
        _control = control;
        var card = Application.Current?.TryFindResource("Card") as Style;
        if (card is not null)
        {
            Style = card;
        }
        else
        {
            // テーマが無い環境（最小のテスト実行など）でも落ちないように自前で描く
            Background = Brush("Surface", Colors.White);
            BorderBrush = Brush("Border", Color.FromRgb(0xE3, 0xE3, 0xE3));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(12);
        }
        Margin = new Thickness(0, 0, 0, 8);

        _grid = new Grid();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _nameTb = new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("Text", Color.FromRgb(0x1B, 0x1B, 0x1B)),
            TextWrapping = TextWrapping.Wrap,
        };
        _descTb = new TextBlock
        {
            Text = desc ?? "",
            FontSize = 12,
            Foreground = Brush("Subtle", Color.FromRgb(0x61, 0x61, 0x61)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        if (string.IsNullOrEmpty(desc)) _descTb.Visibility = Visibility.Collapsed;
        _left.Children.Add(_nameTb);
        _left.Children.Add(_descTb);
        Grid.SetColumn(_left, 0);

        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        _grid.Children.Add(_left);
        _grid.Children.Add(control);
        _grid.SizeChanged += (_, e) => UpdateWrap(e.NewSize.Width);
        Child = _grid;
    }

    private void UpdateWrap(double width)
    {
        if (width <= 0) return;
        _control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cw = _control.DesiredSize.Width;
        // 左の見出し＋説明に最低 130px は残し、入らなければ操作部品を下段へ折り返す
        bool needWrap = cw > 0 && (width - cw - 12) < 130;
        if (needWrap == _wrapped) return;
        _wrapped = needWrap;
        if (needWrap)
        {
            Grid.SetColumnSpan(_left, 2);
            _left.Margin = new Thickness(0);
            Grid.SetRow(_control, 1);
            Grid.SetColumn(_control, 0);
            Grid.SetColumnSpan(_control, 2);
            _control.HorizontalAlignment = HorizontalAlignment.Left;
            _control.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            Grid.SetColumnSpan(_left, 1);
            _left.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRow(_control, 0);
            Grid.SetColumn(_control, 1);
            Grid.SetColumnSpan(_control, 1);
            _control.HorizontalAlignment = HorizontalAlignment.Right;
            _control.Margin = new Thickness(0);
        }
    }

    /// <summary>グループカード内の1行として使う: 外枠・角丸・影を外し、内側の余白だけ残す</summary>
    public SettingRow AsGrouped()
    {
        Style = null;
        Background = Brushes.Transparent;
        BorderBrush = null;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);
        Margin = new Thickness(0);
        Padding = new Thickness(14, 9, 14, 9);
        return this;
    }

    /// <summary>下段（両列またぎ）に大きい部品を置く。</summary>
    public void SetBottom(FrameworkElement el)
    {
        Grid.SetRow(el, 2);
        Grid.SetColumn(el, 0);
        Grid.SetColumnSpan(el, 2);
        el.Margin = new Thickness(0, 10, 0, 0);
        _grid.Children.Add(el);
    }

    /// <summary>説明欄を出す（理由の文言を変える）。空文字なら消す。</summary>
    public void SetDesc(string text)
    {
        _descTb.Text = text;
        _descTb.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Danger の色（範囲外の欄の縁など）。</summary>
    public static Brush DangerBrush() => Brush("Danger", Color.FromRgb(0xC4, 0x2B, 0x1C));
    /// <summary>Warning の色（却下案内など、赤系ではない注意の色）。</summary>
    public static Brush WarningBrush() => Brush("Warning", Color.FromRgb(0x9D, 0x5D, 0x00));
    public static Brush NormalBorderBrush() => Brush("BorderStrong", Color.FromRgb(0xCF, 0xCF, 0xCF));
    public static Brush CardBorderBrush() => Brush("Border", Color.FromRgb(0xE3, 0xE3, 0xE3));
    public static Brush DividerBrush() => Brush("Divider", Color.FromRgb(0xEC, 0xEC, 0xEC));
    public static Brush SubtleSurfaceBrush() => Brush("SubtleSurface", Color.FromRgb(0xF8, 0xF8, 0xF8));
    public static Brush SegTrackBrush() => Brush("SegTrack", Color.FromRgb(0xEF, 0xEF, 0xEF));
    public static Brush AccentBrush() => Brush("Accent", Color.FromRgb(0x00, 0x67, 0xC0));
    public static Brush DisabledBrush() => Brush("Disabled", Color.FromRgb(0xA0, 0xA0, 0xA0));
    public static Brush SubtleBrush() => Brush("Subtle", Color.FromRgb(0x61, 0x61, 0x61));

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        var nb = new SolidColorBrush(fallback);
        nb.Freeze();
        return nb;
    }
}

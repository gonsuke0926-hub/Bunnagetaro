using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 設定ページ的行カード。
/// 左に名前と説明、右に操作部品。大きい部品は SetBottom で下段（両列またぎ）。
/// 範囲外の値を保存しなかった時など、説明欄へ理由を出す（DescBox）。
/// </summary>
public class SettingRow : Border
{
    private readonly Grid _grid;
    private readonly TextBlock _nameTb;
    private readonly TextBlock _descTb;

    /// <summary>説明欄。理由や「○〜○の数値」をここへ出す。</summary>
    public TextBlock DescBox => _descTb;
    /// <summary>名前欄（見出しを状態で見直す時用）。</summary>
    public TextBlock NameBox => _nameTb;

    public SettingRow(string name, string? desc, FrameworkElement control)
    {
        var card = Application.Current?.TryFindResource("Card") as Style;
        if (card is not null)
        {
            Style = card;
        }
        else
        {
            // テーマが無环境（最小のテスト実行など）でも落ちないように自前で描く
            Background = Brush("Surface", Colors.White);
            BorderBrush = Brush("Border", Color.FromRgb(0xE3, 0xE3, 0xE3));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(12);
        }
        Margin = new Thickness(0, 0, 0, 8);

        _grid = new Grid();
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 170 });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 210 });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
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
        left.Children.Add(_nameTb);
        left.Children.Add(_descTb);
        Grid.SetColumn(left, 0);

        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        _grid.Children.Add(left);
        _grid.Children.Add(control);
        Child = _grid;
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
        Padding = new Thickness(14, 10, 14, 10);
        return this;
    }

    /// <summary>下段（両列またぎ）に大きい部品を置く。</summary>
    public void SetBottom(FrameworkElement el)
    {
        Grid.SetRow(el, 1);
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
    public static Brush SubtleBrush() => Brush("Subtle", Color.FromRgb(0x61, 0x61, 0x61));

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        var nb = new SolidColorBrush(fallback);
        nb.Freeze();
        return nb;
    }
}

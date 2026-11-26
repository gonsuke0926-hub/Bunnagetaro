using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>
/// バージョン情報の窓（A6）。アイコン（app_256.png を幅64）と、題名・バージョン・ライセンス表示リンクを出す。
/// これまでは MessageBox だった（画像を出せないため専用の窓にした）。
/// </summary>
public sealed class AboutWindow : Window
{
    public const string AppTitle = "スクショはローカルAIにぶん投げろ！";
    /// <summary>版（csproj の &lt;Version&gt; から。AppVersion）</summary>
    public static readonly string Version = "v" + AppVersion.Text;
    internal const string NoticesResourceName = "THIRD_PARTY_NOTICES.txt";

    /// <summary>exe に埋め込んだサードパーティライセンス文書を読み出す。</summary>
    public static string LoadThirdPartyNotices()
    {
        using var s = typeof(AboutWindow).Assembly.GetManifestResourceStream(NoticesResourceName);
        if (s is null) return "";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public AboutWindow()
    {
        Title = "バージョン情報";
        Icon = AppIcon.Window();
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.TryFindResource("Bg") as Brush ?? Brushes.White;

        var img = new Image
        {
            Source = AppIcon.Png256(),
            Width = 64,
            Height = 64,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Name = "AboutIcon",
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var text = new TextBlock
        {
            Text = AppTitle + "\n" + Version,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 220,
        };
        var licenseBtn = new Button
        {
            Name = "LicenseLink",
            Content = new TextBlock
            {
                Text = "ライセンス表示",
                TextDecorations = TextDecorations.Underline,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 4, 2, 4),
            Foreground = Application.Current?.TryFindResource("Accent") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        licenseBtn.Click += (_, _) =>
        {
            var dlg = new ThirdPartyNoticesWindow { Owner = this };
            dlg.ShowDialog();
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 72,
        };
        ok.Click += (_, _) => WindowMotion.CloseFade(this, 1.0, WindowMotion.CloseMs, 1.0);   // 修正J88: 透過しない窓なので待たずにすぐ閉じる（Windows の消える動きに任せる）

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(ok, Dock.Right);
        footer.Children.Add(ok);
        footer.Children.Add(licenseBtn);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(img);
        row.Children.Add(text);
        var col = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        col.Children.Add(row);
        col.Children.Add(footer);
        Content = col;
    }
}

/// <summary>同梱しているサードパーティライブラリのライセンス表示窓（G14）。</summary>
internal sealed class ThirdPartyNoticesWindow : Window
{
    public ThirdPartyNoticesWindow()
    {
        Title = "サードパーティ ライセンス表示";
        Icon = AppIcon.Window();
        Width = 680;
        Height = 520;
        MinWidth = 420;
        MinHeight = 300;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Application.Current?.TryFindResource("Bg") as Brush ?? Brushes.White;

        var box = new TextBox
        {
            Name = "NoticesText",
            Text = AboutWindow.LoadThirdPartyNotices(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Yu Gothic UI, Meiryo"),
            FontSize = 12,
            Padding = new Thickness(8),
            AcceptsReturn = true,
        };
        var close = new Button
        {
            Content = "閉じる",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            MinWidth = 80,
        };
        close.Click += (_, _) => WindowMotion.CloseFade(this, 1.0, WindowMotion.CloseMs, 1.0);   // 修正J88: 透過しない窓なので待たずにすぐ閉じる（Windows の消える動きに任せる）

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(box, 0);
        Grid.SetRow(close, 1);
        grid.Children.Add(box);
        grid.Children.Add(close);
        Content = grid;
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Bunnagetaro5.Pages;
using Xunit;

namespace Bunnagetaro5.Tests;

public class ThirdPartyNoticesTests
{
    private static void OnSta(Action body)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw new Exception(err.Message, err);
    }

    [Fact]
    public void 埋め込んだ文書にKaTeXとWpfMathとWebView2とNETの見出しが含まれる()
    {
        var text = AboutWindow.LoadThirdPartyNotices();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("KaTeX", text);
        Assert.Contains("WpfMath", text);
        Assert.Contains("WebView2", text);
        Assert.Contains(".NET", text);
        Assert.Contains("ONNX Runtime", text);
        Assert.Contains("PP-OCRv6", text);
        Assert.Contains("DirectML", text);
        Assert.Contains("PP-DocLayoutV3", text);
    }

    [Fact]
    public void バージョン情報の窓にライセンス表示リンクとスクロール可能な表示窓がある()
    {
        OnSta(() =>
        {
            var about = new AboutWindow();
            try
            {
                var root = Assert.IsType<StackPanel>(about.Content);
                var footer = root.Children.OfType<DockPanel>().Single();
                var link = footer.Children.OfType<Button>().Single(b => b.Name == "LicenseLink");
                Assert.NotNull(link);
            }
            finally { about.Close(); }

            var win = new ThirdPartyNoticesWindow();
            try
            {
                var grid = Assert.IsType<Grid>(win.Content);
                var box = grid.Children.OfType<TextBox>().Single(b => b.Name == "NoticesText");
                Assert.True(box.IsReadOnly);
                Assert.Equal(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility);
                Assert.Contains("KaTeX", box.Text);
                Assert.Contains("WpfMath", box.Text);
                Assert.Contains("WebView2", box.Text);
                Assert.Contains(".NET", box.Text);
            }
            finally { win.Close(); }
        });
    }
}

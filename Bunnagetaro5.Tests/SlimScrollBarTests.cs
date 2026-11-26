using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Bunnagetaro5;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>細スクロールバーは縦なら幅だけ8・高さは親いっぱい。棒は最小長を持つ</summary>
public class SlimScrollBarTests
{
    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    private static (ScrollBar Bar, Thumb Thumb) Layout(Orientation o, double viewport)
    {
        var style = (Style)XamlReader.Parse(Bunnagetaro5.Views.AnswerView.SlimSbXaml);
        var bar = new ScrollBar
        {
            Style = style,
            Orientation = o,
            Minimum = 0,
            Maximum = 10000,
            ViewportSize = viewport,
            Resources = { ["PanelSbBg"] = Brushes.Black, ["PanelSbThumb"] = Brushes.Gray },
        };
        var host = new Grid { Width = 300, Height = 300 };
        host.Children.Add(bar);
        host.Measure(new Size(300, 300));
        host.Arrange(new Rect(0, 0, 300, 300));
        host.UpdateLayout();
        bar.ApplyTemplate();
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        return (bar, track.Thumb);
    }

    [Fact]
    public void 縦バーは幅8で高さは親いっぱい()
    {
        OnSta(() =>
        {
            var (bar, _) = Layout(Orientation.Vertical, 300);
            Assert.Equal(8, bar.ActualWidth, 1);
            Assert.True(bar.ActualHeight > 200, "縦バーの高さが潰れている: " + bar.ActualHeight);
        });
    }

    [Fact]
    public void 横バーは高さ8で幅は親いっぱい()
    {
        OnSta(() =>
        {
            var (bar, _) = Layout(Orientation.Horizontal, 300);
            Assert.Equal(8, bar.ActualHeight, 1);
            Assert.True(bar.ActualWidth > 200, "横バーの幅が潰れている: " + bar.ActualWidth);
        });
    }

    [Fact]
    public void 長い答えでも棒は最小長を保つ()
    {
        OnSta(() =>
        {
            var (_, thumb) = Layout(Orientation.Vertical, 5);   // 全体の0.05%
            Assert.True(thumb.ActualHeight >= 23.9, "棒が点になっている: " + thumb.ActualHeight);
        });
    }

    [Fact]
    public void 棒は最小長ぶん実際に描かれる()
    {
        OnSta(() =>
        {
            var (bar, thumb) = Layout(Orientation.Vertical, 5);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
            rtb.Render((UIElement)bar.Parent);
            var px = new byte[300 * 300 * 4];
            rtb.CopyPixels(px, 300 * 4, 0);
            // 棒の色（Gray）が連続する高さを数える（x=4 の列）
            int run = 0;
            for (int y = 0; y < 300; y++)
            {
                int i = (y * 300 + 150) * 4;
                if (px[i] > 100 && px[i] == px[i + 1]) run++;   // Gray=128, 地の Black=0
            }
            Assert.True(run >= 22, $"描かれた棒の長さ {run}px（Thumb.ActualHeight={thumb.ActualHeight}）");
        });
    }

    [Fact]
    public void 水増しは最小長に満たない時だけ()
    {
        // 十分長い棒はそのまま
        Assert.Equal(500, ThumbMin.Inflate(500, 0, 500, 300));
        // 点になる棒は 24/300 の比まで伸びる
        double v = ThumbMin.Inflate(5, 0, 10000, 300);
        Assert.Equal(24.0 / 300, v / (10000 + v), 6);
        // 範囲なし・トラックが短すぎる時は触らない
        Assert.Equal(5, ThumbMin.Inflate(5, 0, 0, 300));
        Assert.Equal(5, ThumbMin.Inflate(5, 0, 100, 40));
    }
}

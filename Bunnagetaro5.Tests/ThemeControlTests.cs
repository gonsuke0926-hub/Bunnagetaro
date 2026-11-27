using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>共通スタイル（ToggleSwitch・SegmentedRadio・フラットComboBox・Slider・バー用部品・淡い区切り線）の検証</summary>
public class ThemeControlTests
{
    [Fact]
    public void J127_ToggleSwitchとSegmentedRadioとComboBoxとSliderのテンプレートが適用され状態に応じて色が変わる()
    {
        TestUtil.OnSta(() =>
        {
            TestUtil.EnsureAppResources();

            // Divider は外枠 Border より明るい（濃い縞にならない）
            var div = (Color)Application.Current.FindResource("DividerColor");
            var bdr = (Color)Application.Current.FindResource("BorderColor");
            Assert.True(div.R > bdr.R && div.G > bdr.G && div.B > bdr.B);

            var swStyle = (Style)Application.Current.FindResource("ToggleSwitch");
            var sw = new CheckBox { Style = swStyle, IsChecked = false };
            var segStyle = (Style)Application.Current.FindResource("SegmentedRadio");
            var seg1 = new RadioButton { Style = segStyle, Content = "A", GroupName = "g", IsChecked = true };
            var seg2 = new RadioButton { Style = segStyle, Content = "B", GroupName = "g", IsChecked = false };
            var cbEdit = new ComboBox { IsEditable = true, ItemsSource = new[] { "x", "y" }, Text = "x", Width = 140 };
            var cbPick = new ComboBox { IsEditable = false, ItemsSource = new[] { "x", "y" }, SelectedIndex = 0, Width = 140 };
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 140 };
            var runBtn = new Button { Style = (Style)Application.Current.FindResource("BarRunButton"), Width = 52 };
            var iconTgl = new ToggleButton { Style = (Style)Application.Current.FindResource("BarIconToggle"), IsChecked = true };

            var host = new StackPanel();
            host.Children.Add(sw);
            host.Children.Add(seg1);
            host.Children.Add(seg2);
            host.Children.Add(cbEdit);
            host.Children.Add(cbPick);
            host.Children.Add(slider);
            host.Children.Add(runBtn);
            host.Children.Add(iconTgl);

            var win = new Window { Content = host, Width = 320, Height = 400, Left = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                win.Show();
                TestUtil.Pump(50);

                // ToggleSwitch: OFF -> ON でつまみが右へ移動し Accent 塗りになる
                var track = (Border)sw.Template.FindName("SwitchTrack", sw);
                var thumb = (System.Windows.Shapes.Ellipse)sw.Template.FindName("SwitchThumb", sw);
                Assert.Equal(3, thumb.Margin.Left, 1);
                sw.IsChecked = true;
                TestUtil.Pump(20);
                Assert.Equal(23, thumb.Margin.Left, 1);
                Assert.Equal((Color)Application.Current.FindResource("AccentColor"), ((SolidColorBrush)track.Background).Color);

                // SegmentedRadio: 選択中だけ白地＋Accent文字
                var seg1Bd = (Border)seg1.Template.FindName("Bd", seg1);
                var seg2Bd = (Border)seg2.Template.FindName("Bd", seg2);
                Assert.Equal(Colors.White, ((SolidColorBrush)seg1Bd.Background).Color);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)seg2Bd.Background).Color);

                // フラット ComboBox: IsEditable=True でも PART_EditableTextBox の親が ComboBox のまま
                var editTb = cbEdit.Template.FindName("PART_EditableTextBox", cbEdit) as TextBox;
                Assert.NotNull(editTb);
                Assert.Same(cbEdit, editTb!.TemplatedParent);
                Assert.Equal(Visibility.Visible, editTb.Visibility);
                var pickTb = cbPick.Template.FindName("PART_EditableTextBox", cbPick) as TextBox;
                Assert.Equal(Visibility.Hidden, pickTb!.Visibility);

                // バー主ボタンは元と同じ淡い青（AccentSoft）、busy 時は淡い赤（DangerSoft）
                var runBd = (Border)runBtn.Template.FindName("Bd", runBtn);
                Assert.Equal((Color)Application.Current.FindResource("AccentSoftColor"), ((SolidColorBrush)runBd.Background).Color);
                runBtn.Tag = "busy";
                TestUtil.Pump(20);
                Assert.Equal((Color)Application.Current.FindResource("DangerSoftColor"), ((SolidColorBrush)runBd.Background).Color);

                // Slider: つまみ（ThumbBd）の上下中心がスライダー本体の中心と一致する
                var sliderTrack = (Track)slider.Template.FindName("PART_Track", slider);
                var thumbBd = (Border)sliderTrack.Thumb.Template.FindName("ThumbBd", sliderTrack.Thumb);
                double thumbCenterY = thumbBd.TranslatePoint(new Point(0, thumbBd.ActualHeight / 2.0), slider).Y;
                Assert.Equal(slider.ActualHeight / 2.0, thumbCenterY, 0.5);

                // ToolTip: 既定の本文 14px に引っ張られず控えめな 10.5px になる（中身の TextBlock も 10.5px）
                var tt = new ToolTip { Content = "くっつける: ON", IsOpen = true };
                try
                {
                    TestUtil.Pump(30);
                    Assert.Equal(10.5, tt.FontSize);
                    var innerTb = FindDescendant<TextBlock>(tt);
                    Assert.NotNull(innerTb);
                    Assert.Equal(10.5, innerTb!.FontSize);
                }
                finally
                {
                    tt.IsOpen = false;
                }
            }
            finally
            {
                win.Close();
            }
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T hit) return hit;
            var sub = FindDescendant<T>(c);
            if (sub is not null) return sub;
        }
        return null;
    }
}

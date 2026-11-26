using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>FIX_ROUND4 E7: 解答欄の「画像も」は「たずねる」と同じ色・チェックで青く塗らない</summary>
public class QuietCheckTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public QuietCheckTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_quiet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60, throwOnTimeout: false);

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    [Fact]
    public void 画像もの色はたずねると同じで青く塗らない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            var w = new MainWindow(_paths, cfg);
            w.Show();
            Pump(w.Dispatcher, 250);
            w.Session.OpenCapture();
            Pump(w.Dispatcher, 250);
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var chk = (CheckBox)Fld(panel.View, "_followChk")!;
            var lbl = (TextBlock)Fld(panel.View, "_followLbl")!;
            Assert.Same(Application.Current.FindResource("QuietCheckBox"), chk.Style);
            Assert.Equal(((SolidColorBrush)lbl.Foreground).Color, ((SolidColorBrush)chk.Foreground).Color);

            // 色・透明度を変えても揃う
            cfg.SetStr("overlay_fg", "#102030");
            cfg.SetStr("overlay_bg", "#e0e0e0");
            panel.View.ApplyStyle(PanelStyle.From(cfg), 200, 200);
            Assert.Equal(((SolidColorBrush)lbl.Foreground).Color, ((SolidColorBrush)chk.Foreground).Color);

            // チェックしても Accent では塗らない（枠は透明の地のまま）
            chk.IsChecked = true;
            chk.ApplyTemplate();
            var box = (Border)chk.Template.FindName("Box", chk);
            Assert.NotNull(box);
            Assert.True(box.Background is null || (box.Background is SolidColorBrush sb && sb.Color.A == 0));

            // 共通スタイルは変えていない（Accent 塗りのまま）
            var common = new CheckBox { IsChecked = true };
            common.Style = (Style)Application.Current.FindResource(typeof(CheckBox));
            Assert.NotSame(chk.Style, common.Style);
            w.Close();
        });
    }
}

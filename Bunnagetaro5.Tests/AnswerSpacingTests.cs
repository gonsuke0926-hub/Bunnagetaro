using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>F35: 解答欄の段落間（まとまりの間）・行間・単一改行の LineBreak 化。</summary>
public class AnswerSpacingTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public AnswerSpacingTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_spacing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(TimeSpan.FromSeconds(60));
        if (err is not null) throw err;
        return result;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static object? CallPriv(object o, string name)
        => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(o, Array.Empty<object>());

    private MainWindow NewWindow(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(w.Dispatcher, 250);
        return w;
    }

    [Fact]
    public void 単一改行は同一段落のLineBreakで空行だけが段落分けになり空段落は作られない()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            w.Session.OpenCapture();
            Pump(w.Dispatcher, 200);
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var body = (RichTextBox)Fld(panel.View, "_body")!;

            // 式なし
            panel.ApplyBody("A\nB\n\nC\n\n\nD");
            Pump(w.Dispatcher, 50);
            var blocks = body.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(3, blocks.Count);
            Assert.All(blocks, p => Assert.True(p.Inlines.Count > 0));
            Assert.Contains(blocks[0].Inlines, i => i is LineBreak);
            Assert.DoesNotContain(blocks[1].Inlines, i => i is LineBreak);

            // 全文コピー・選択コピーの改行確認
            Assert.Equal("A\nB\n\nC\n\n\nD", (string)CallPriv(panel.View, "FullText")!);
            body.SelectAll();
            Assert.Equal("A\r\nB\r\nC\r\nD\r\n", (string)CallPriv(panel.View, "SelectedText")!);

            // 式あり（BuildMathDoc 経路）
            panel.ApplyBody("X\n$x^2$\n\n$$y^2$$\n\nZ\nW");
            Pump(w.Dispatcher, 100);
            var mblocks = body.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(3, mblocks.Count);   // [X\n$x^2$], [$$y^2$$], [Z\nW]
            Assert.All(mblocks, p => Assert.True(p.Inlines.Count > 0));
            Assert.Contains(mblocks[0].Inlines, i => i is LineBreak);
            Assert.Contains(mblocks[2].Inlines, i => i is LineBreak);

            // 式ありの選択コピーでも LineBreak の改行が \r\n として保たれる
            body.SelectAll();
            var sel = (string)CallPriv(panel.View, "SelectedText")!;
            Assert.Equal("X\r\n$x^2$\r\n$$y^2$$\r\nZ\r\nW", sel);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void ストリーミング書き足しで改行が2回に分かれても段落分けとLineBreakが崩れない()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            w.Session.OpenCapture();
            Pump(w.Dispatcher, 200);
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var body = (RichTextBox)Fld(panel.View, "_body")!;

            panel.ApplyBody("一段目\n");
            panel.ApplyBody("一段目\n\n二段目\n");
            panel.ApplyBody("一段目\n\n二段目\n二行目");
            Pump(w.Dispatcher, 50);

            var blocks = body.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(2, blocks.Count);
            Assert.DoesNotContain(blocks[0].Inlines, i => i is LineBreak);
            Assert.Contains(blocks[1].Inlines, i => i is LineBreak);
            Assert.All(blocks, p => Assert.True(p.Inlines.Count > 0));

            w.Close();
            return true;
        });
    }

    [Fact]
    public void まとまりの間と行間が設定で変わり開いている解答欄と外の解答欄にすぐ効く()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            w.Session.OpenCapture();
            Pump(w.Dispatcher, 200);
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var strip = (AnswerStrip)Fld(w.Session, "_strip")!;
            var pBody = (RichTextBox)Fld(panel.View, "_body")!;
            var pThink = (TextBlock)Fld(panel.View, "_think")!;
            var sBody = (RichTextBox)Fld(strip.View, "_body")!;
            var sThink = (TextBlock)Fld(strip.View, "_think")!;

            // 既定値: para_spacing=0.8, line_spacing=0 (指定なし=NaN), FontSize=12
            Assert.Equal(PanelStyle.DefaultParaSpacing, cfg.Settings.AnswerParaSpacing);
            Assert.Equal(PanelStyle.DefaultLineSpacing, cfg.Settings.AnswerLineSpacing);
            Assert.True(double.IsNaN(pBody.Document.LineHeight));
            Assert.True(double.IsNaN(pThink.LineHeight));
            Assert.Equal(LineStackingStrategy.MaxHeight, pBody.Document.LineStackingStrategy);
            Assert.Equal(LineStackingStrategy.MaxHeight, pThink.LineStackingStrategy);

            panel.ApplyBody("A\n\n$$x^2$$\n\nB");
            strip.ApplyBody("A\n\nB");
            Pump(w.Dispatcher, 80);

            var pBlocks = pBody.Document.Blocks.OfType<Paragraph>().ToList();
            // 通常段落の余白は 12 * 0.8 = 9.6（dp直前と最後は0）、dpの上下は 12 * 0.3 = 3.6
            Assert.Equal(3.6, pBlocks[1].Margin.Top, 2);
            Assert.Equal(3.6, pBlocks[1].Margin.Bottom, 2);
            var sBlocks = sBody.Document.Blocks.OfType<Paragraph>().ToList();
            Assert.Equal(9.6, sBlocks[0].Margin.Bottom, 2);
            Assert.Equal(0.0, sBlocks[1].Margin.Bottom, 2);

            // 設定を変更して BarSync 相当の反映を呼ぶ
            cfg.Settings.AnswerParaSpacing = 1.2;
            cfg.Settings.AnswerLineSpacing = 1.6;
            panel.ApplyStyle();
            strip.ApplyStyle();
            Pump(w.Dispatcher, 50);

            Assert.Equal(12 * 1.6, pBody.Document.LineHeight, 2);
            Assert.Equal(12 * 1.6, pThink.LineHeight, 2);
            Assert.Equal(12 * 1.6, sBody.Document.LineHeight, 2);
            Assert.Equal(12 * 1.6, sThink.LineHeight, 2);
            Assert.Equal(12 * 1.2, sBlocks[0].Margin.Bottom, 2);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void 設定ページのまとまりの間と行間が保存と即反映につながる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            int synced = 0;
            var page = new SettingsPage(cfg, () => { }, _ => { });
            cfg.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppSettings.AnswerParaSpacing) or nameof(AppSettings.AnswerLineSpacing))
                    synced++;
            };

            var win = new Window { Content = page, Width = 800, Height = 900, Left = -10000 };
            win.Show();
            Pump(win.Dispatcher, 100);

            var paraSlider = (Slider)Fld(page, "_paraSpacingSlider")!;
            var lineSlider = (Slider)Fld(page, "_lineSpacingSlider")!;
            var lineLbl = (TextBlock)Fld(page, "_lineSpacingLbl")!;
            var resetBtn = (Button)Fld(page, "_lineSpacingResetBtn")!;

            Assert.Equal(0.8, paraSlider.Value, 2);
            Assert.Equal("標準", lineLbl.Text);
            Assert.False(resetBtn.IsEnabled);

            paraSlider.Value = 1.1;
            lineSlider.Value = 1.5;
            Pump(win.Dispatcher, 50);

            Assert.Equal(1.1, cfg.Settings.AnswerParaSpacing, 2);
            Assert.Equal(1.5, cfg.Settings.AnswerLineSpacing, 2);
            Assert.Equal("1.5", lineLbl.Text);
            Assert.True(resetBtn.IsEnabled);
            Assert.True(synced >= 2);

            resetBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(win.Dispatcher, 50);

            Assert.Equal(0.0, cfg.Settings.AnswerLineSpacing);
            Assert.Equal("標準", lineLbl.Text);
            Assert.False(resetBtn.IsEnabled);

            win.Close();
            return true;
        });
    }
}

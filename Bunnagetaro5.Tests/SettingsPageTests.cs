using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 設定ページ（AI接続と答えの表示）のその場保存の動き。
/// 窓に出さなくてもRaisedイベントで直接発火できるので、ページ単体でSTAで動かす。
/// 保存が300ms間引かれていることも、ここで確かめる。
/// </summary>
public class SettingsPageTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public SettingsPageTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_setpg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    /// <summary>名前で部品を探す（描画ツリー不要の論理ツリー歩き）。</summary>
    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (var ch in LogicalTreeHelper.GetChildren(root))
        {
            if (ch is T t && (t.Name == name || string.IsNullOrEmpty(name))) return t;
            if (ch is DependencyObject d)
            {
                var deeper = FindLogical<T>(d, name);
                if (deeper is not null) return deeper;
            }
        }
        return null;
    }

    private static void PressEnter(UIElement box)
    {
        // KeyEventArgsの presentation source が要るので、窓に出ている部品から取る
        var src = PresentationSource.FromVisual(box);
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Enter)
        { RoutedEvent = UIElement.KeyDownEvent };
        box.RaiseEvent(e);
    }

    private static void Pump(int ms)
    {
        var d = Application.Current!.Dispatcher;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

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
        th.Join(TimeSpan.FromSeconds(30));
        if (err is not null) throw err;
        return result;
    }

    private SettingsPage MakePage(out ConfigStore cfg, List<string> statusLog)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var page = new SettingsPage(cfg, () => { }, statusLog.Add);
        // 窓に出してからでないとKeyEventArgsのpresentation sourceが取れない
        var win = new Window { Content = page, Width = 760, Height = 820, ShowActivated = false };
        win.Show();
        Pump(200);
        return page;
    }

    private string ReadSavedConfig() => File.ReadAllText(_paths.ConfigJson, System.Text.Encoding.UTF8);

    // ---- tests ----

    [Fact]
    public void PageHasAiConnectionAndAnswerSections()
    {
        OnSta(() =>
        {
            var page = MakePage(out _, new List<string>());
            Assert.NotNull(FindLogical<ComboBox>(page, "UrlBox"));
            Assert.NotNull(FindLogical<Button>(page, "ConnTestButton"));
            Assert.NotNull(FindLogical<Button>(page, "FetchModelsButton"));
            Assert.NotNull(FindLogical<TextBox>(page, "BgBox"));
            Assert.NotNull(FindLogical<TextBox>(page, "FgBox"));
            Assert.NotNull(FindLogical<Slider>(page, "AlphaSlider"));
            Assert.NotNull(FindLogical<ComboBox>(page, "FontCombo"));
            Assert.NotNull(FindLogical<TextBox>(page, "FontSizeBox"));
            return true;
        });
    }

    /// <summary>キャプチャ窓の枠の欄（色・不透明度・太さ）は即書かれ、本体へ知らせ、「既定に戻す」で3つだけ戻る</summary>
    [Fact]
    public void CaptureBorderFieldsWriteNotifyAndReset()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            int sync = 0;
            cfg.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppSettings.CaptureBorderColor)
                    or nameof(AppSettings.CaptureBorderPx)
                    or nameof(AppSettings.CaptureBorderAlpha))
                    sync++;
            };
            var color = FindLogical<TextBox>(page, "BorderColorBox")!;
            var px = FindLogical<TextBox>(page, "BorderPxBox")!;
            var alpha = FindLogical<Slider>(page, "BorderAlphaSlider")!;
            var reset = FindLogical<Button>(page, "BorderReset")!;
            Assert.Equal("#0067C0", color.Text);
            Assert.Equal("4", px.Text);
            Assert.Equal(0.70, alpha.Value, 6);

            color.Text = "#ff8800"; PressEnter(color);
            Assert.Equal("#ff8800", cfg.Settings.CaptureBorderColor);
            px.Text = "9"; PressEnter(px);
            Assert.Equal(9, cfg.Settings.CaptureBorderPx);
            alpha.Value = 0.4;
            Assert.Equal(0.4, cfg.Settings.CaptureBorderAlpha, 6);
            Assert.True(sync >= 3);
            // 不正な色・範囲外の太さは保存しない
            color.Text = "red"; PressEnter(color);
            Assert.Equal("#ff8800", cfg.Settings.CaptureBorderColor);
            px.Text = "99"; PressEnter(px);
            Assert.Equal(9, cfg.Settings.CaptureBorderPx);

            // 背景の不透明度など別の設定は触らない
            double overlay = cfg.Settings.OverlayAlpha;
            reset.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(("#0067C0", 0.70, 4), (cfg.Settings.CaptureBorderColor, cfg.Settings.CaptureBorderAlpha, cfg.Settings.CaptureBorderPx));
            Assert.Equal("#0067C0", color.Text);
            Assert.Equal("4", px.Text);
            Assert.Equal(overlay, cfg.Settings.OverlayAlpha);
            return true;
        });
    }

    [Fact]
    public void UrlEnterSavesNormalizedAndDebounced()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            var url = FindLogical<ComboBox>(page, "UrlBox")!;
            Assert.Equal("http://localhost:11434", cfg.GetStr("base_url"));

            url.Text = "localhost:8080";
            PressEnter(url);
            Assert.Equal("http://localhost:8080", cfg.GetStr("base_url")); // settings即変更
            Assert.Equal("http://localhost:8080", url.Text);               // 正規化を欄にも戻す
            Assert.Contains("http://localhost:8080", url.Items.OfType<string>());

            // 保存は300ms間引く: すぐにはまだファイルに書けない
            Pump(80);
            Assert.DoesNotContain("8080", ReadSavedConfig());
            Pump(500); // タイクを待つ
            Assert.Contains("localhost:8080", ReadSavedConfig());

            // 履歴から元のURLを選び直すと即切り替わる
            url.SelectedItem = "http://localhost:11434";
            Pump(30);
            Assert.Equal("http://localhost:11434", cfg.GetStr("base_url"));

            // ドロップダウンを開いてもテンプレートエラーにならない
            url.IsDropDownOpen = true;
            Pump(50);
            url.IsDropDownOpen = false;
            Pump(20);

            // ドロップダウン行の削除で履歴から消える（既定URLは消えない）
            page.DeleteUrlFromHistory("http://localhost:11434");
            Assert.Contains("http://localhost:11434", url.Items.OfType<string>());
            page.DeleteUrlFromHistory("http://localhost:8080");
            Pump(30);
            Assert.DoesNotContain("http://localhost:8080", url.Items.OfType<string>());
            return true;
        });
    }

    [Fact]
    public void BadUrlIsNotSavedAndSaysWhy()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            var url = FindLogical<ComboBox>(page, "UrlBox")!;
            url.Text = "http://";
            PressEnter(url);
            Assert.Equal("http://localhost:11434", cfg.GetStr("base_url")); // 前のまま
            page.FlushPendingSave();
            Assert.Contains("localhost:11434", ReadSavedConfig());
            return true;
        });
    }

    [Fact]
    public void ColorInvalidKeepsOldAndValidSaves()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            var bg = FindLogical<TextBox>(page, "BgBox")!;
            Assert.Equal("#202020", cfg.GetStr("overlay_bg"));

            bg.Text = "zzz";
            PressEnter(bg);
            Assert.Equal("#202020", cfg.GetStr("overlay_bg")); // 不正は前の値のまま

            bg.Text = "#ff0000";
            PressEnter(bg);
            Assert.Equal("#ff0000", cfg.GetStr("overlay_bg"));
            page.FlushPendingSave();
            Assert.Contains("#ff0000", ReadSavedConfig());
            return true;
        });
    }

    [Fact]
    public void FontSizeOutOfRangeNotSavedInRangeSaved()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            var size = FindLogical<TextBox>(page, "FontSizeBox")!;
            Assert.Equal(12, cfg.GetInt("overlay_font_size", 12));

            size.Text = "7";
            PressEnter(size);
            Assert.Equal(12, cfg.GetInt("overlay_font_size", 12)); // 範囲外は保存しない

            size.Text = "30";
            PressEnter(size);
            Assert.Equal(30, cfg.GetInt("overlay_font_size", 12));
            return true;
        });
    }

    [Fact]
    public void AlphaSliderCommitsRightAway()
    {
        OnSta(() =>
        {
            var page = MakePage(out var cfg, new List<string>());
            var slider = FindLogical<Slider>(page, "AlphaSlider")!;
            slider.Value = 0.42;
            Assert.Equal(0.42, cfg.GetDouble("overlay_alpha", 0.7), 3);
            page.FlushPendingSave();
            Assert.Contains("0.42", ReadSavedConfig());
            return true;
        });
    }

    [Fact]
    public void F10_RecordOpenButtonIsNotPrimary()
    {
        OnSta(() =>
        {
            var page = MakePage(out _, new List<string>());
            var btn = FindLogical<Button>(page, "OpenConfigFolder")!;
            var primary = Application.Current?.TryFindResource("PrimaryButton") as Style;
            Assert.NotNull(primary);
            Assert.NotSame(primary, btn.Style);
            Assert.Null(btn.ReadLocalValue(FrameworkElement.StyleProperty) as Style);
            return true;
        });
    }
}

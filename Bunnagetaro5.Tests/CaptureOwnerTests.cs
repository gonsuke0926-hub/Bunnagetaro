using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>メイン窓を最小化してもキャプチャ窓・バー・解答欄が隠れない（Owner を持たない）。</summary>
public class CaptureOwnerTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public CaptureOwnerTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_f36_" + Guid.NewGuid().ToString("N"));
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

    private MainWindow NewWindow(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Left = -10000;
        w.Show();
        Pump(w.Dispatcher, 200);
        return w;
    }

    [Fact]
    public void キャプチャ窓とバーと解答欄のOwnerがnullでタスクバーとAltTabに出ずTopmostが有効()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            w.Session.OpenCapture(animate: false);
            Pump(w.Dispatcher, 150);

            var cap = (CaptureWindow)Fld(w.Session, "_capture")!;
            var bar = (ChromeBar)Fld(w.Session, "_bar")!;
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var strip = (AnswerStrip)Fld(w.Session, "_strip")!;

            Assert.Null(cap.Owner);
            Assert.Null(bar.Owner);
            Assert.Null(panel.Owner);
            Assert.Null(strip.Owner);

            Assert.False(cap.ShowInTaskbar);
            Assert.False(bar.ShowInTaskbar);
            Assert.False(panel.ShowInTaskbar);
            Assert.False(strip.ShowInTaskbar);

            Assert.True(cap.Topmost);
            Assert.True(bar.Topmost);
            Assert.True(panel.Topmost);
            Assert.True(strip.Topmost);

            const long WS_EX_TOOLWINDOW = 0x00000080;
            const long WS_EX_TOPMOST = 0x00000008;
            var capEx = Native.GetWindowLongPtrW(new WindowInteropHelper(cap).Handle, Native.GWL_EXSTYLE).ToInt64();
            var barEx = Native.GetWindowLongPtrW(new WindowInteropHelper(bar).Handle, Native.GWL_EXSTYLE).ToInt64();
            Assert.True((capEx & WS_EX_TOOLWINDOW) != 0);
            Assert.True((barEx & WS_EX_TOOLWINDOW) != 0);
            Assert.True((capEx & WS_EX_TOPMOST) != 0);
            Assert.True((barEx & WS_EX_TOPMOST) != 0);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void メイン窓を最小化してもキャプチャ窓とバーは表示されたままで自動トリガ条件も維持される()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            w.Session.OpenCapture(animate: false);
            w.Session.BarAutoTrig(true);
            Pump(w.Dispatcher, 150);

            var cap = (CaptureWindow)Fld(w.Session, "_capture")!;
            var bar = (ChromeBar)Fld(w.Session, "_bar")!;

            w.WindowState = WindowState.Minimized;
            Pump(w.Dispatcher, 150);

            Assert.Equal(WindowState.Minimized, w.WindowState);
            Assert.True(cap.IsVisible);
            Assert.True(bar.IsVisible);
            Assert.Equal(WindowState.Normal, cap.WindowState);
            Assert.Equal(WindowState.Normal, bar.WindowState);

            var trigEnabled = (bool)w.GetType().GetMethod("TriggerEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null)!;
            Assert.True(trigEnabled);

            w.Close();
            return true;
        });
    }

    [Fact]
    public void メイン窓を閉じるとOwnerなしでもキャプチャ窓とバーと解答欄がすべて閉じる()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            w.Session.OpenCapture(animate: false);
            Pump(w.Dispatcher, 150);

            var cap = (CaptureWindow)Fld(w.Session, "_capture")!;
            var bar = (ChromeBar)Fld(w.Session, "_bar")!;
            var panel = (AnswerPanel)Fld(w.Session, "_panel")!;
            var strip = (AnswerStrip)Fld(w.Session, "_strip")!;

            var capH = new WindowInteropHelper(cap).Handle;
            var barH = new WindowInteropHelper(bar).Handle;
            Assert.True(Native.IsWindow(capH));
            Assert.True(Native.IsWindow(barH));

            w.Close();
            Pump(w.Dispatcher, 100);

            Assert.False(Native.IsWindow(capH));
            Assert.False(Native.IsWindow(barH));
            Assert.Null(PresentationSource.FromVisual(panel));
            Assert.Null(PresentationSource.FromVisual(strip));
            return true;
        });
    }
}

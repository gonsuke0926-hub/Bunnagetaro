// テストで共通に使うお助け（窓のテスト用のスレッド・少し待つ・画面の部品の読み込み）
using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Bunnagetaro5.Tests;

internal static class TestUtil
{
    /// <summary>新しい STA スレッドで body を流す。timeoutSeconds 待っても終わらない時、throwOnTimeout なら TimeoutException。body の例外はそのまま投げ直す</summary>
    internal static void OnSta(Action body, int timeoutSeconds = 60, bool throwOnTimeout = true)
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { err = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(timeoutSeconds)) && throwOnTimeout) throw new TimeoutException();
        if (err is not null) throw err;
    }

    /// <summary>ms ミリ秒のあいだ、画面の仕事（メッセージ）を流しながら待つ</summary>
    internal static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Application が無ければ作り、色と部品の見た目（Colors / Controls）を読み込む</summary>
    internal static void EnsureAppResources()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Colors.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Bunnagetaro;component/Theme/Controls.xaml") });
    }

    /// <summary>cond() が true になるか timeoutMs が過ぎるまで、stepMs ずつ Pump して待つ。最後の cond() の値を返す</summary>
    internal static bool WaitUntil(Func<bool> cond, int timeoutMs, int stepMs = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds >= timeoutMs) return cond();
            Pump(stepMs);
        }
        return true;
    }
}

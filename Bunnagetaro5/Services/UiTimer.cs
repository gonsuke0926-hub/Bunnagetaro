using System.Windows.Threading;

namespace Bunnagetaro5.Services;

/// <summary>UIスレッドの小さな遅延実行</summary>
internal static class UiTimer
{
    /// <summary>ms ミリ秒後に1回だけ a を UI スレッドで呼ぶ</summary>
    public static void Later(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }
}

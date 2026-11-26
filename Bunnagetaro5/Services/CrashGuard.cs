using System.Windows;
using System.Windows.Threading;

namespace Bunnagetaro5.Services;

/// <summary>
/// 異常時の記録（指示書 7.10）。未処理の例外は crash.log に書き、ダイアログで知らせる。
/// フックは AppDomain.UnhandledException / DispatcherUnhandledException /
/// TaskScheduler.UnobservedTaskException の3種（指示書指定）。
/// Python版 translator_window.py の _excepthook（app.log へ "unhandled:" で全文残す）も踏襲。
/// </summary>
public static class CrashGuard
{
    public const string DialogCaption = "Bunnagetaro";

    private static bool _installed;
    private static bool _dialogShown;   // 連発時のダイアログ連打防止（記録は毎回残す）

    /// <summary>Application が生まれてから呼ぶ（OnStartup の頭）。二重登録しない</summary>
    public static void Install(Application app)
    {
        if (_installed) return;
        _installed = true;

        // UIスレッドの未処理例外。アプリは生かしたまま続ける
        // （Python版 Tk もコールバック内の例外で本体まで死なないのと同じ趣旨）
        app.DispatcherUnhandledException += (_, e) =>
        {
            HandleWithDialog("unhandled(UI)", e.Exception);
            e.Handled = true;
        };

        // 背景スレッド等の未処理例外。この後は終了するのが筋（握りつぶさない）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleWithDialog("unhandled(AppDomain)",
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "(null)"));

        // 誰からも待たれなかった Task の例外。GC 時に静かに起きてアプリは危険でないため
        // 記録だけ（ダイアログ連打防止）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("unobserved(TaskScheduler)", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>起動時に app.log へ環境情報（env_info）を1行ずつ</summary>
    public static void LogEnv()
    {
        foreach (string line in EnvInfo.Lines())
            AppLog.Info(line);
    }

    /// <summary>crash.log に残すブロック（時刻・区別タグ・例外全文＝内部例外とスタック込み）</summary>
    public static string Describe(string tag, Exception ex)
        => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {tag}\n{ex}\n\n";

    /// <summary>起動そのものが失敗した経路。記録とダイアログの後、呼び手は終了処理へ進む</summary>
    public static void Fatal(string tag, Exception ex)
    {
        Record(tag, ex);
        ShowDialog();
    }

    private static void HandleWithDialog(string tag, Exception ex)
    {
        Record(tag, ex);
        ShowDialog();
    }

    private static void Record(string tag, Exception ex)
    {
        AppLog.Error(tag, ex);      // app.log にも（Python版 excepthook の "unhandled:" 相当）
        AppLog.Crash(Describe(tag, ex));
    }

    private static void ShowDialog()
    {
        if (_dialogShown) return;
        _dialogShown = true;
        try
        {
            MessageBox.Show(
                "処理中に未解決のエラーが起きました。\n詳細は crash.log に記録しました。",
                DialogCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }   // 記録は済んでいる。表示の失敗で止めない
    }
}

using System.Linq;
using System.Windows;
using Bunnagetaro5.Services;

namespace Bunnagetaro5;

/// <summary>
/// 起動の組み立て: 置き場所 → ログ → config読み込み → メイン窓。
/// Python版 translator_window.py の起動順（load_config → _restore_main_geometry → UI）に対応。
/// </summary>
public partial class App : Application
{
    public static Paths Paths { get; private set; } = null!;
    public static ConfigStore Cfg { get; private set; } = null!;
    private SingleInstanceGuard? _instanceGuard;

    protected override void OnExit(ExitEventArgs e)
    {
        try { MathRenderer.Shutdown(); } catch { }   // 数式の WebView2 と見えない窓を解放（F0）
        try { _instanceGuard?.Dispose(); } catch { }
        base.OnExit(e);
    }

    /// <summary>修正J54: 起動引数に --tray があるか（Windows の起動時の登録が付ける）</summary>
    internal static bool IsTrayStart(string[]? args)
        => args is not null && args.Any(a => string.Equals(a, StartupRegistration.TrayArg, StringComparison.OrdinalIgnoreCase));

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceGuard = SingleInstanceGuard.TryAcquire();
        if (_instanceGuard is null)
        {
            if (!IsTrayStart(e.Args)) SingleInstanceGuard.ActivateExisting(Bunnagetaro5.MainWindow.WindowTitle);   // 修正J54: --tray の二重起動は何も出さずに終わる
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;   // 見えない窓（数式）等が残ってもメイン窓を閉じたら終わる（F0）
        base.OnStartup(e);
        CrashGuard.Install(this);   // 未処理例外 → crash.log + ダイアログ（指示書 7.10）
        try
        {
            Paths = new Paths();
            AppLog.Init(Paths);
            CrashGuard.LogEnv();    // 起動時にバージョン・OS・画面構成を app.log へ1行ずつ
            Cfg = new ConfigStore(Paths);
            Cfg.Load();   // 初回はここで config.json を書き出す（引っ越しがあっても書き戻す）
            var win = new MainWindow(Paths, Cfg);
            MainWindow = win;
            // 修正J54: Windows の起動時の登録がフォルダの移動で古くなっていたら黙って今の場所へ入れ直す（OFF なら触らない）
            StartupRegistration.SyncOnStartup(Cfg.Settings.StartWithWindows, Environment.ProcessPath);
            if (IsTrayStart(e.Args)) win.StartHidden();   // --tray: 窓は出さずトレイだけ（窓はトレイ・二重起動から出る）
            else win.Show();
            _ = MathRenderer.Start(Paths);   // 数式の KaTeX を裏で立ち上げる（ランタイムが無ければ WpfMath のまま）
        }
        catch (Exception ex)
        {
            CrashGuard.Fatal("unhandled(startup)", ex);
            Environment.ExitCode = 1;
            Shutdown();
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページの起動と終了（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    internal CheckBox CloseToTrayCheck { get; private set; } = null!;
    internal CheckBox StartWithWindowsCheck { get; private set; } = null!;
    private bool _startupRevert;   // 失敗してチェックを戻す間は変更の聞き手を働かせない

    private void BuildStartupSection()
    {
        var host = Pane(CatSystem);
        host.Children.Add(Section("起動と終了"));

        // ×で閉じた時にトレイへしまうか（既定 OFF）
        CloseToTrayCheck = MakeSwitch("CloseToTrayChk", _cfg.Settings.CloseToTray);
        CloseToTrayCheck.Checked += (_, _) => { _cfg.Settings.CloseToTray = true; SaveSoon(); };
        CloseToTrayCheck.Unchecked += (_, _) => { _cfg.Settings.CloseToTray = false; SaveSoon(); };
        var closeToTrayRow = new SettingRow("×で閉じた時はタスクトレイにしまう",
            "終了はトレイの右クリック「終了」から",
            CloseToTrayCheck);

        // Windows の起動時にトレイで起動するか（既定 OFF）。ON の間、現ユーザーの Run に「exe --tray」を入れる
        StartWithWindowsCheck = MakeSwitch("StartWithWindowsChk", _cfg.Settings.StartWithWindows);
        StartWithWindowsCheck.Checked += (_, _) => SetStartWithWindows(true);
        StartWithWindowsCheck.Unchecked += (_, _) => SetStartWithWindows(false);
        var startRow = new SettingRow("Windows の起動時にトレイで起動する",
            "サインイン時に窓を出さず起動します",
            StartWithWindowsCheck);

        host.Children.Add(MakeGroupCard(closeToTrayRow, startRow));
    }

    /// <summary>Run への登録／削除をして、うまくいったら設定を保存する。失敗は Warning にしてチェックを元へ戻す（落とさない）</summary>
    private void SetStartWithWindows(bool on)
    {
        if (_startupRevert) return;
        try
        {
            if (on)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("exe の場所が分からない");
                StartupRegistration.Register(exe);
                AppLog.Info("startup: Windows の起動時にトレイで起動する設定を ON にした: " + exe);
            }
            else
            {
                StartupRegistration.Unregister();
                AppLog.Info("startup: Windows の起動時にトレイで起動する設定を OFF にした");
            }
            _cfg.Settings.StartWithWindows = on;
            SaveSoon();
        }
        catch (Exception ex)
        {
            AppLog.Warning("startup: Run の" + (on ? "登録" : "削除") + "に失敗: " + ex.Message);
            _startupRevert = true;
            try { StartWithWindowsCheck.IsChecked = !on; }
            finally { _startupRevert = false; }
        }
    }
}

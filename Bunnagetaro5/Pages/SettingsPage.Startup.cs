using System;
using System.Windows;
using System.Windows.Controls;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページの起動と終了（SettingsPage の partial class。修正J54）。</summary>
public partial class SettingsPage
{
    internal CheckBox CloseToTrayCheck { get; private set; } = null!;
    internal CheckBox StartWithWindowsCheck { get; private set; } = null!;
    private bool _startupRevert;   // 失敗してチェックを戻す間は変更の聞き手を働かせない

    private void BuildStartupSection()
    {
        BodyPanel.Children.Add(Section("起動と終了"));

        // 修正J51: ×で閉じた時にトレイへしまうか（既定 OFF。修正J54 で「答えの表示」からここへ移した）
        CloseToTrayCheck = new CheckBox
        {
            Content = "×で閉じた時はタスクトレイにしまう",
            IsChecked = _cfg.Settings.CloseToTray,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "CloseToTrayChk",
        };
        CloseToTrayCheck.Checked += (_, _) => { _cfg.Settings.CloseToTray = true; SaveSoon(); };
        CloseToTrayCheck.Unchecked += (_, _) => { _cfg.Settings.CloseToTray = false; SaveSoon(); };
        var closeToTrayRow = new SettingRow("×で閉じた時",
            "終了はトレイの右クリック「終了」から",
            CloseToTrayCheck);

        // 修正J54: Windows の起動時にトレイで起動するか（既定 OFF）。ON の間、現ユーザーの Run に「exe --tray」を入れる
        StartWithWindowsCheck = new CheckBox
        {
            Content = "Windows の起動時にトレイで起動する",
            IsChecked = _cfg.Settings.StartWithWindows,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "StartWithWindowsChk",
        };
        StartWithWindowsCheck.Checked += (_, _) => SetStartWithWindows(true);
        StartWithWindowsCheck.Unchecked += (_, _) => SetStartWithWindows(false);
        var startRow = new SettingRow("Windows の起動時",
            "サインイン時に窓を出さず起動します",
            StartWithWindowsCheck);

        BodyPanel.Children.Add(MakeGroupCard(closeToTrayRow, startRow));
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

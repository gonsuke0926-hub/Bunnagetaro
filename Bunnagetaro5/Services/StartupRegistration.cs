using Microsoft.Win32;

namespace Bunnagetaro5.Services;

/// <summary>
/// 修正J54: Windows のサインイン時にトレイで起動する登録。現ユーザーの Run（HKCU）に
/// 値名 <see cref="ValueName"/>、データ `"exe のフルパス" --tray` を入れる／消す。
/// 管理者権限は要らない。キーの場所はテストが差し替えられる（本物の Run は触らない）。
/// </summary>
internal static class StartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Bunnagetaro";
    internal const string TrayArg = "--tray";

    /// <summary>Run キーの場所（HKCU の下）。テストが scratch のキーへ差し替える</summary>
    internal static string KeyPath { get; set; } = RunKeyPath;

    /// <summary>Run に入れるデータ（`"exe" --tray`）</summary>
    internal static string CommandFor(string exePath) => "\"" + exePath + "\" " + TrayArg;

    /// <summary>今の Run の値（無ければ null）</summary>
    internal static string? CurrentValue()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        return k?.GetValue(ValueName) as string;
    }

    /// <summary>登録されているか（値があるか）</summary>
    internal static bool IsRegistered() => CurrentValue() is not null;

    /// <summary>この exe の `"exe" --tray` が入っているか（大文字小文字は区別しない）</summary>
    internal static bool IsRegisteredFor(string exePath)
        => string.Equals(CurrentValue(), CommandFor(exePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>登録する（無ければキーも作る）。失敗は例外のまま（呼ぶ側が Warning に落とす）</summary>
    internal static void Register(string exePath)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new InvalidOperationException("Run キーを開けない");
        k.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
    }

    /// <summary>登録を消す（値が無くても何もしない）</summary>
    internal static void Unregister()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        k?.DeleteValue(ValueName, false);
    }

    /// <summary>起動時の追従。設定が ON で、Run の値が今の exe と違う（フォルダを動かした等）なら黙って入れ直す。OFF なら触らない。失敗は Warning</summary>
    internal static void SyncOnStartup(bool enabled, string? exePath)
    {
        if (!enabled || string.IsNullOrEmpty(exePath)) return;
        try
        {
            if (IsRegisteredFor(exePath)) return;
            Register(exePath);
            AppLog.Info("startup: Run を今の場所へ入れ直した: " + exePath);
        }
        catch (Exception ex) { AppLog.Warning("startup: Run の入れ直しに失敗: " + ex.Message); }
    }
}

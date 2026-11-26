using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Pages;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 設定ページの「登録」「解除」と、ぶん投げホットキー発火の配線。
/// 実際の押下はフックが掴む（それは QuickInputWatcherTests と e2e が確かめる）ので、
/// ここでは「掴まれた想定で BindDone を呼んだとき」の設定・欄・状態欄の動きを見る。
/// </summary>
public class BindTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public BindTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_bind_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- helpers（MenuDrawerTests と同じ形 + private メンバーへのリフレクション） ----

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

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t && t.Name == name) return t;
            var deeper = Find<T>(ch, name);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static void SetFld(object o, string name, object? v)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(o, v);

    private static void Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(o, args);

    private static string StatusOf(MainWindow w) => Find<TextBlock>(w, "StatusText")!.Text;

    private MainWindow NewWindow(out ConfigStore cfg)
    {
        EnsureAppResources();
        cfg = new ConfigStore(_paths);
        cfg.Load();
        var w = new MainWindow(_paths, cfg);
        w.Show();
        Pump(w.Dispatcher, 250);
        // 初期ページはログなので、設定欄をFindできるようにしておいてから使う
        w.ShowSettingsPage();
        Pump(w.Dispatcher, 150);
        return w;
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        Pump(w.Dispatcher, 100);
    }

    private static SettingsPage Page(MainWindow w) => (SettingsPage)Fld(w, "_settingsPage")!;
    private static QuickInputWatcher Watch(MainWindow w) => w.Quick.Input;
    private static string BindWhich(MainWindow w) => w.Binds.Which;
    private static void BindStart(MainWindow w, string which) => w.Binds.Start(which);
    private static void BindDone(MainWindow w, string tok) => w.Binds.Done(tok);
    private static void Apply(MainWindow w) => w.Quick.Apply(false);

    // ---- tests ----

    [Fact]
    public void HotkeyBindDoneWritesCfgFieldAndHook()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "hot");
            Assert.Equal("hot", BindWhich(w));
            Assert.Equal("登録するキー / ボタンを押してください", Find<TextBox>(w, "HotkeyBox")!.Text);
            Assert.Contains("8秒で自動中止", StatusOf(w));
            BindDone(w, "f9");
            Assert.Equal("", BindWhich(w));
            Assert.Equal("f9", cfg.GetStr("hotkey"));
            Assert.Equal(Binding.Label("f9"), Find<TextBox>(w, "HotkeyBox")!.Text);
            Assert.Contains("ぶん投げホットキー: F9 設定しました", StatusOf(w));
            Assert.Equal("f9", Watch(w).Hotkey());   // Applyで発火待ちにも載った
            Close(w);
            return true;
        });
    }

    [Fact]
    public void SameRegisterAgainAbortsAndKeepsValue()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetStr("hotkey", "f9");
            Apply(w);
            BindStart(w, "hot");
            BindStart(w, "hot");   // もう一度「登録」＝中止
            Assert.Equal("", BindWhich(w));
            Assert.Equal("ぶん投げホットキー: 登録をやめました", StatusOf(w));
            Assert.Equal("f9", cfg.GetStr("hotkey"));
            Assert.Equal(Binding.Label("f9"), Find<TextBox>(w, "HotkeyBox")!.Text);
            Assert.Equal("f9", Watch(w).Hotkey());   // 中止後も設定どおりへ戻っている
            Close(w);
            return true;
        });
    }

    [Fact]
    public void EscFromCapturedInputAborts()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "kquick");
            BindDone(w, "esc");
            Assert.Equal("", BindWhich(w));
            Assert.Equal("キー起動: 登録をやめました", StatusOf(w));
            Assert.Equal("", cfg.GetStr("quick_key"));
            Assert.Equal("（未設定）", Find<TextBox>(w, "QuickKeyBox")!.Text);
            Close(w);
            return true;
        });
    }

    [Fact]
    public void SwitchingRegisterMovesWaitBetweenFields()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            BindStart(w, "hot");
            Assert.Equal("登録するキー / ボタンを押してください", Find<TextBox>(w, "HotkeyBox")!.Text);
            BindStart(w, "kquick");   // 別の欄のを押したら乗り換え
            Assert.Equal("kquick", BindWhich(w));
            Assert.Equal("（未設定）", Find<TextBox>(w, "HotkeyBox")!.Text);   // 前の欄は表示を戻す
            Assert.Equal("登録するキーを押してください", Find<TextBox>(w, "QuickKeyBox")!.Text);
            Close(w);
            return true;
        });
    }

    [Fact]
    public void TimeoutAutoAbortsAndUnhooks()
    {
        OnSta(() =>
        {
            var w = NewWindow(out _);
            BindStart(w, "hot");
            Assert.Equal(true, Fld(Watch(w), "_catch"));
            // 8秒経った想定で巡回を1回回す（BindCatchTimeoutSec=8.0）
            w.Binds.CatchAt = DateTime.UtcNow - TimeSpan.FromSeconds(9);
            w.Quick.Poll();
            Assert.Equal("", BindWhich(w));
            Assert.Contains("登録する入力が押されないので中止しました", StatusOf(w));
            Assert.Equal(false, Fld(Watch(w), "_catch"));   // 掴みっぱなしにしない
            Close(w);
            return true;
        });
    }

    [Fact]
    public void KquickRejectsBareKeyAndMouseButton()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "kquick");
            BindDone(w, "a");   // 危険な単独キー
            Assert.Equal("", cfg.GetStr("quick_key"));
            Assert.StartsWith("キー起動: ", StatusOf(w));
            Assert.Contains("そのキー単独だと普通の打鍵を握ってしまうので設定できません", StatusOf(w));

            BindStart(w, "kquick");
            BindDone(w, "mbutton");   // マウスボタンはキー起動にできない
            Assert.Equal("", cfg.GetStr("quick_key"));
            Assert.Contains("キーで開始するにはキーを押してください", StatusOf(w));
            Close(w);
            return true;
        });
    }

    [Fact]
    public void J38_不正なキー設定の警告は同じ値なら1回だけapp_logに書く()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            int Count(string s) => File.Exists(_paths.AppLogFile)
                ? File.ReadAllLines(_paths.AppLogFile).Count(l => l.Contains(s)) : 0;
            const string badKey = "quick_key設定が無効のため無視: mbutton";
            const string badHot = "hotkey設定が無効のため無視: win+f9";
            int k0 = Count(badKey), h0 = Count(badHot);
            cfg.SetStr("quick_key", "mbutton");   // マウスボタンはキー起動にできない
            cfg.SetStr("hotkey", "win+f9");       // Windowsキー付きは使えない
            for (int i = 0; i < 4; i++) Apply(w);  // Poll の1秒ごとの読み直しと同じ
            Assert.Equal(k0 + 1, Count(badKey));
            Assert.Equal(h0 + 1, Count(badHot));
            cfg.SetStr("quick_key", "");          // いったん直してから、また同じ不正値にしたら書く
            cfg.SetStr("hotkey", "");
            Apply(w);
            cfg.SetStr("quick_key", "mbutton");
            cfg.SetStr("hotkey", "win+f9");
            Apply(w);
            Apply(w);
            Assert.Equal(k0 + 2, Count(badKey));
            Assert.Equal(h0 + 2, Count(badHot));
            cfg.SetStr("quick_key", "");
            cfg.SetStr("hotkey", "");
            Apply(w);
            Close(w);
            return true;
        });
    }

    [Fact]
    public void HotkeyRejectsWinComboAndCollisionWithQuickKey()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "hot");
            BindDone(w, "win+f9");
            Assert.Equal("", cfg.GetStr("hotkey"));
            Assert.Contains("Windowsキー付きはOSのショートカットを潰すため設定できません", StatusOf(w));

            cfg.SetStr("quick_key", "f8");
            Apply(w);
            BindStart(w, "hot");
            BindDone(w, "f8");   // キー起動に使用中
            Assert.Equal("", cfg.GetStr("hotkey"));
            Assert.Contains("クイックぶん投げのキー起動に使用中です", StatusOf(w));

            // 逆方向: ホットキーに使用中のキーはキー起動へ登録できない
            cfg.SetStr("quick_key", "");
            Apply(w);
            BindStart(w, "hot");
            BindDone(w, "f9");
            Assert.Equal("f9", cfg.GetStr("hotkey"));
            BindStart(w, "kquick");
            BindDone(w, "f9");
            Assert.Equal("", cfg.GetStr("quick_key"));
            Assert.Contains("そのキーはぶん投げホットキーに使用中です", StatusOf(w));
            Close(w);
            return true;
        });
    }

    [Fact]
    public void HotkeyMouseButtonOnlyWhenNotQuickLauncher()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            // クイックON+ホイール起動 → ホイールクリックの発火待ちにはできない
            cfg.SetBool("quick_enabled", true);
            Apply(w);
            BindStart(w, "hot");
            BindDone(w, "mbutton");
            Assert.Equal("", cfg.GetStr("hotkey"));
            Assert.Contains("そのボタンはクイックぶん投げの起動に使用中です", StatusOf(w));

            // 起動を「戻る」に変えればホイールクリックは発火待ちにできる
            cfg.SetStr("quick_button", "back");
            Apply(w);
            BindStart(w, "hot");
            BindDone(w, "mbutton");
            Assert.Equal("mbutton", cfg.GetStr("hotkey"));
            Assert.Equal("mbutton", Watch(w).Hotkey());
            Close(w);
            return true;
        });
    }

    [Fact]
    public void QuickOffStillKeepsHotkeyFiring()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetStr("hotkey", "f9");
            cfg.SetBool("quick_enabled", false);
            w.Quick.Apply(false);
            // Enabled() は「フックを張っている」意味（発火待ちだけでも張る）。
            // クイックぶん投げの長押し掴みは _mask が空になることで確かめる。
            var mask = Fld(Watch(w), "_mask");
            Assert.Equal(0, mask!.GetType().GetProperty("Count")!.GetValue(mask));
            Assert.Equal("f9", Watch(w).Hotkey());     // 発火待ちだけは残る（Pythonと同じ）
            Assert.True(Watch(w).Enabled());           // 発火待ちのあいだもキーボードは掴んだまま
            // クイックぶん投げ自体は切れている
            Assert.False(cfg.GetBool("quick_enabled", true));
            Close(w);
            return true;
        });
    }

    [Fact]
    public void HotkeyFireWithoutWindowOnlyShowsStatus()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetStr("hotkey", "f9");
            Apply(w);
            w.Binds.HotkeyFire("f9");
            Assert.Equal("ぶん投げホットキー(F9): キャプチャ窓が開いていません", StatusOf(w));
            Assert.False(w.Status.Busy);
            Close(w);
            return true;
        });
    }

    [Fact]
    public void ClearButtonThroughMainWindowRemovesCfgAndHook()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            cfg.SetStr("hotkey", "f9");
            Apply(w);
            var page = Page(w);
            Assert.NotNull(page.RegRequest);    // ctor で結線されている
            Assert.NotNull(page.ClearRequest);
            page.ClearRequest!.Invoke("hot");
            Assert.Equal("", cfg.GetStr("hotkey"));
            Assert.Equal("", Watch(w).Hotkey());
            Assert.Contains("ぶん投げホットキー: （未設定） 解除しました", StatusOf(w));

            // 登録待ちのあいだの解除: 待機を下ろしてから消す
            cfg.SetStr("hotkey", "f9");
            Apply(w);
            BindStart(w, "hot");
            page.ClearRequest!.Invoke("hot");
            Assert.Equal("", BindWhich(w));
            Assert.Equal("", cfg.GetStr("hotkey"));
            Assert.Equal(false, Fld(Watch(w), "_catch"));
            Close(w);
            return true;
        });
    }

    [Fact]
    public void RegisterWritesDiskImmediatelyEvenWith300msSavePending()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "kquick");
            BindDone(w, "ctrl+f8");
            Assert.Equal("ctrl+f8", cfg.GetStr("quick_key"));
            // 「登録」は保存ボタンを待たずディスクへ書く
            var disk = new ConfigStore(_paths);
            disk.Load();
            Assert.Equal("ctrl+f8", disk.GetStr("quick_key"));
            Assert.Equal(Binding.Label("ctrl+f8"), Find<TextBox>(w, "QuickKeyBox")!.Text);
            Close(w);
            return true;
        });
    }

    [Fact]
    public void RejectNoticeAppearsBelowFieldInWarningColorAndClearsOnNextAction()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            var hotNotice = Find<TextBlock>(w, "HotkeyNotice")!;
            var kqNotice = Find<TextBlock>(w, "QuickKeyNotice")!;
            Assert.Equal(Visibility.Collapsed, hotNotice.Visibility);
            Assert.Equal(Visibility.Collapsed, kqNotice.Visibility);

            // 1. 単独の通常キーは却下され、欄のすぐ下に注意色で表示される
            BindStart(w, "hot");
            BindDone(w, "a");
            Assert.Equal(Visibility.Visible, hotNotice.Visibility);
            Assert.Contains("単独", hotNotice.Text);
            Assert.NotEqual(Bunnagetaro5.Pages.SettingRow.DangerBrush(), hotNotice.Foreground);
            Assert.Contains("ぶん投げホットキー:", StatusOf(w));

            // 次の操作（別の登録開始）で消える
            BindStart(w, "hot");
            Assert.Equal(Visibility.Collapsed, hotNotice.Visibility);
            Assert.Equal("", hotNotice.Text);

            // 2. クイック起動中のホイールクリックをホットキーへ登録しようとした時も却下案内が出る
            Find<CheckBox>(w, "QuickOnChk")!.IsChecked = true;
            cfg.Settings.QuickEnabled = true;
            cfg.Settings.QuickButton = "wheel";
            BindDone(w, "mbutton");
            Assert.Equal(Visibility.Visible, hotNotice.Visibility);
            Assert.Contains("クイックぶん投げの起動に使用中", hotNotice.Text);

            // 次の操作（解除）で消える
            Page(w).ClearRequest!.Invoke("hot");
            Assert.Equal(Visibility.Collapsed, hotNotice.Visibility);

            // 3. キー起動の却下も欄の下に出て、クイック設定の操作で消える
            BindStart(w, "kquick");
            BindDone(w, "a");
            Assert.Equal(Visibility.Visible, kqNotice.Visibility);
            Assert.Contains("単独", kqNotice.Text);
            Find<CheckBox>(w, "QuickOnChk")!.IsChecked = false;
            Assert.Equal(Visibility.Collapsed, kqNotice.Visibility);

            Close(w);
            return true;
        });
    }

    // ---- 範囲ドラッグのキー ----

    [Fact]
    public void J58_範囲ドラッグのキーは既定が未設定で_登録すると保存されフックに載る()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            Assert.Equal("", cfg.GetStr("snip_hotkey"));
            Assert.Equal("（未設定）", Find<TextBox>(w, "SnipKeyBox")!.Text);
            BindStart(w, "snip");
            Assert.Equal("snip", BindWhich(w));
            Assert.Equal("登録するキー / ボタンを押してください", Find<TextBox>(w, "SnipKeyBox")!.Text);
            BindDone(w, "f7");
            Assert.Equal("", BindWhich(w));
            Assert.Equal("f7", cfg.GetStr("snip_hotkey"));
            Assert.Equal(Binding.Label("f7"), Find<TextBox>(w, "SnipKeyBox")!.Text);
            Assert.Contains("範囲ドラッグのキー: F7 設定しました", StatusOf(w));
            Assert.Equal("f7", Watch(w).SnipHotkey());
            Assert.True(Watch(w).Enabled());
            Page(w).ClearRequest!.Invoke("snip");
            Assert.Equal("", cfg.GetStr("snip_hotkey"));
            Assert.Equal("", Watch(w).SnipHotkey());
            Close(w);
            return true;
        });
    }

    [Fact]
    public void J58_範囲ドラッグのキーはぶん投げホットキー_キー起動と同じ入力を登録できない()
    {
        OnSta(() =>
        {
            var w = NewWindow(out var cfg);
            BindStart(w, "hot");
            BindDone(w, "f9");
            BindStart(w, "snip");
            BindDone(w, "f9");
            Assert.Equal("", cfg.GetStr("snip_hotkey"));
            Assert.Contains("ぶん投げホットキーに使用中です", StatusOf(w));
            BindStart(w, "kquick");
            BindDone(w, "f6");
            BindStart(w, "snip");
            BindDone(w, "f6");
            Assert.Equal("", cfg.GetStr("snip_hotkey"));
            Assert.Contains("キー起動に使用中です", StatusOf(w));
            BindStart(w, "snip");
            BindDone(w, "win+f5");   // 検証は同じ（Windowsキー付きは不可）
            Assert.Equal("", cfg.GetStr("snip_hotkey"));
            // 逆向き: 範囲ドラッグのキーと同じ入力は、ぶん投げホットキー・キー起動へ登録できない
            BindStart(w, "snip");
            BindDone(w, "f7");
            Assert.Equal("f7", cfg.GetStr("snip_hotkey"));
            BindStart(w, "hot");
            BindDone(w, "f7");
            Assert.Equal("f9", cfg.GetStr("hotkey"));
            Assert.Contains("範囲ドラッグのキーに使用中です", StatusOf(w));
            BindStart(w, "kquick");
            BindDone(w, "f7");
            Assert.Equal("f6", cfg.GetStr("quick_key"));
            Assert.Contains("範囲ドラッグのキーに使用中です", StatusOf(w));
            // クイックの起動ボタンと同じボタンも不可
            cfg.SetBool("quick_enabled", true);
            Apply(w);
            BindStart(w, "snip");
            BindDone(w, "mbutton");
            Assert.Equal("f7", cfg.GetStr("snip_hotkey"));
            Assert.Contains("そのボタンはクイックぶん投げの起動に使用中です", StatusOf(w));
            Close(w);
            return true;
        });
    }
}

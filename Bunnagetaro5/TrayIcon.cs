using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5;

/// <summary>
/// 修正J50: タスクトレイ（通知領域）のアイコンと右クリックのメニュー。メイン窓が持つ。
/// アプリが動いているあいだいつも出し、窓を閉じたら外す（残像を残さない）。
/// メニューの中身は新しく作らず、設定ページ・バーが使っている道をそのまま呼ぶ:
/// 解答欄の場所は SettingsPage.SetResultPlaceForActive、クイックは SettingsPage.SetQuickEnabled、
/// プリセットは CaptureSession.BarPresetSelect（バーのコンボと同じ）、終了は MainWindow.QuitApp。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainWindow _win;
    private readonly ConfigStore _cfg;
    private readonly Dispatcher _disp;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon? _drawIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _show = new("ログの表示");
    private readonly System.Windows.Forms.ToolStripMenuItem _place = new("解答欄の場所（このプリセット）");
    private readonly System.Windows.Forms.ToolStripMenuItem _placeIn = new("枠の中");
    private readonly System.Windows.Forms.ToolStripMenuItem _placeOut = new("枠の外");
    private readonly System.Windows.Forms.ToolStripMenuItem _quick = new("クイックぶん投げ");
    private readonly System.Windows.Forms.ToolStripMenuItem _preset = new("プリセット");
    private readonly System.Windows.Forms.ToolStripSeparator _sep = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _quit = new("終了");
    private readonly DispatcherTimer _clickTimer;
    private bool _disposed;

    public TrayIcon(MainWindow win, ConfigStore cfg)
    {
        _win = win;
        _cfg = cfg;
        _disp = win.Dispatcher;

        _place.DropDownItems.Add(_placeIn);
        _place.DropDownItems.Add(_placeOut);
        _menu.Items.Add(_show);
        _menu.Items.Add(_place);
        _menu.Items.Add(_quick);
        _menu.Items.Add(_preset);
        _menu.Items.Add(_sep);
        _menu.Items.Add(_quit);

        _show.Click += (_, _) => Post(ShowWindow);
        _placeIn.Click += (_, _) => Post(() => SetPlace("inside"));
        _placeOut.Click += (_, _) => Post(() => SetPlace("outside"));
        _quick.Click += (_, _) => Post(ToggleQuick);
        _quit.Click += (_, _) => Post(Quit);
        _menu.Opening += (_, _) => RefreshMenu();

        _drawIcon = LoadIcon();
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _drawIcon ?? System.Drawing.SystemIcons.Application,
            Text = Tip(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        // 修正J52: 左の1回押しは範囲ドラッグ（二重押しの時間だけ待ち、二重押しが来なければ始める）。二重押しは窓を出す
        _clickTimer = new DispatcherTimer(DispatcherPriority.Normal, _disp)
        { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            Run(OnLeftClick);
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) Post(PendLeftClick);
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) Post(OnDoubleClick);
        };
        RefreshMenu();
    }

    // ---------------- テストが見る入口 ----------------

    internal System.Windows.Forms.NotifyIcon Icon => _icon;
    internal System.Windows.Forms.ContextMenuStrip Menu => _menu;
    internal System.Windows.Forms.ToolStripMenuItem PresetItem => _preset;
    internal System.Windows.Forms.ToolStripMenuItem QuickItem => _quick;
    internal System.Windows.Forms.ToolStripMenuItem PlaceInItem => _placeIn;
    internal System.Windows.Forms.ToolStripMenuItem PlaceOutItem => _placeOut;
    internal bool IsDisposed => _disposed;

    /// <summary>メニューを今の設定へ合わせる（開く度に呼ぶ。プリセットは増減するので毎回作り直す）</summary>
    internal void RefreshMenu()
    {
        if (_disposed) return;
        bool outside = _cfg.EffectiveResultPlace() == "outside";
        _placeIn.Checked = !outside;
        _placeOut.Checked = outside;
        _quick.Checked = _cfg.Settings.QuickEnabled;

        _preset.DropDownItems.Clear();
        string active = _cfg.Settings.PromptActive;
        if (_cfg.Settings.Prompts is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject p) continue;
                string name = ConfigStore.NodeToStr(p["name"]);
                if (name.Length == 0) continue;
                var item = new System.Windows.Forms.ToolStripMenuItem(name) { Checked = name == active };
                item.Click += (_, _) => Post(() => SelectPreset(name));
                _preset.DropDownItems.Add(item);
            }
        }
        _preset.Enabled = _preset.DropDownItems.Count > 0;
    }

    // ---------------- 動き（メニューの項目と同じ。UI スレッドで呼ぶ） ----------------

    internal void ShowWindow() => _win.ShowAndActivate();

    /// <summary>左の1回押し（修正J52: 画面を少し暗くして範囲ドラッグ）</summary>
    internal void OnLeftClick() => _win.StartSnip();

    /// <summary>1回押しを預かる。二重押しの時間（システム設定）のうちに二重押しが来なければ OnLeftClick を始める</summary>
    internal void PendLeftClick()
    {
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    /// <summary>二重押し: 預かっていた1回押しを捨て、始まっていた範囲ドラッグも取り消して、窓を出す</summary>
    internal void OnDoubleClick()
    {
        _clickTimer.Stop();
        _win.Quick.SnipCancel("トレイの二重押し");
        ShowWindow();
    }

    internal void SetPlace(string place)
    {
        _win.SettingsPageForTray?.SetResultPlaceForActive(place);
        RefreshMenu();
    }

    internal void ToggleQuick()
    {
        _win.SettingsPageForTray?.SetQuickEnabled(!_cfg.Settings.QuickEnabled);
        RefreshMenu();
    }

    /// <summary>バーのコンボと同じ道（設定ページの書きかけの査読 → 切り替え → 保存 → 解答欄・バーの塗り直し）</summary>
    internal void SelectPreset(string name)
    {
        _win.CaptureSessionForTray.BarPresetSelect(name);
        RefreshMenu();
    }

    internal void Quit() => _win.QuitApp();

    /// <summary>吹き出しを出した回数（テスト用。OS の吹き出しは見ない）</summary>
    internal int HintCount { get; private set; }

    /// <summary>修正J55: しまった時の吹き出しは、これまでに1度も出していない時だけ出す（出したら `tray_hint_shown` を保存。音が毎回鳴ってうるさいため）。--tray の起動では出さない</summary>
    internal void MaybeShowHiddenHint(bool startedHidden)
    {
        if (_disposed || startedHidden || _cfg.Settings.TrayHintShown) return;
        _cfg.Settings.TrayHintShown = true;
        try { _cfg.Save(); } catch (Exception ex) { AppLog.Error("tray_hint_shown save failed", ex); }
        ShowHiddenHint();
    }

    /// <summary>修正J51: 吹き出しを出す（出すかどうかは MaybeShowHiddenHint が決める）</summary>
    internal void ShowHiddenHint()
    {
        if (_disposed) return;
        try
        {
            HintCount++;
            _icon.ShowBalloonTip(5000, Pages.AboutWindow.AppTitle,
                "タスクトレイにしまいました。右クリックで「ログの表示」「終了」が選べます",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { AppLog.Error("tray balloon failed", ex); }
    }

    // ---------------- 部品 ----------------

    private void Run(Action a)
    {
        if (_disposed) return;
        try { a(); }
        catch (Exception ex) { AppLog.Error("tray action failed", ex); }
    }

    private void Post(Action a)
    {
        if (_disposed) return;
        _disp.BeginInvoke(() =>
        {
            if (_disposed) return;
            try { a(); }
            catch (Exception ex) { AppLog.Error("tray action failed", ex); }
        });
    }

    private static string Tip()
    {
        string t = Pages.AboutWindow.AppTitle;
        return t.Length > 63 ? t[..63] : t;   // NotifyIcon.Text は63文字まで
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Bunnagetaro;component/Assets/app.ico"));
            if (res is null) return null;
            using var s = res.Stream;
            return new System.Drawing.Icon(s);
        }
        catch (Exception ex)
        {
            AppLog.Error("tray icon load failed", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clickTimer.Stop();
        try { _icon.Visible = false; } catch { }
        _icon.Dispose();
        _menu.Dispose();
        _drawIcon?.Dispose();
    }
}

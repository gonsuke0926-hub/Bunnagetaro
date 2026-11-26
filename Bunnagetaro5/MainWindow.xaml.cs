using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bunnagetaro5.Input;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Status;

namespace Bunnagetaro5;

/// <summary>
/// メイン窓。上の段（主ボタン・前面固定・ステータス・☰）とページ表示域。
/// 位置・大きさ・最大化の保存/復元と、ステータス文字のtickerを持つ。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>メイン窓の題名（版は csproj の &lt;Version&gt; から）。2つ目の起動が元の窓を探す時もこれで探す</summary>
    public static readonly string WindowTitle = Pages.AboutWindow.AppTitle + " " + Pages.AboutWindow.Version;
    private int _activateMsg;
    private readonly Paths _paths;
    private readonly ConfigStore _cfg;
    private readonly LogStore _store;
    private Pages.LogPage? _logPage;
    private Pages.SettingsPage? _settingsPage;

    // ---- ステータス（文言を決めるのは Status/StatusModel。ここは窓へ出すだけ。350msごとに回る） ----
    internal readonly StatusModel Status = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private TriggerWatcher? _trig;   // 自動トリガの背景巡回
    private readonly CancellationTokenSource _closeCts = new();
    private volatile bool _closing;
    /// <summary>巡回の撮影代わり。通常は ScreenShot.GrabFrame（検証ハーネスは合成画像へ差し替え）</summary>
    private Func<Native.RECT, ChangeDetector.Frame?> _trigGrab = r => ScreenShot.GrabFrame(r);

    public MainWindow(Paths paths, ConfigStore cfg)
    {
        _paths = paths;
        _cfg = cfg;
        InitializeComponent();
        Title = WindowTitle;

        // ページ表示域の既定はログページ（タブは使わない）。
        // entries.json の読み込み・pending後始末・孤児画像掃除はここで済む
        Icon = AppIcon.Window();   // タスクバー・Alt+Tab のアイコン
        _store = new LogStore(paths);
        _logPage = new Pages.LogPage(_store, SetStatus, () => Services.PanelStyle.From(_cfg));
        _session = new CaptureSession(_cfg, this, Status, SetStatus, () => StatusText.Text);
        _throw = new ThrowController(_cfg, _store, _logPage, Status, _session, SetStatus, Dispatcher);
        _quick = new QuickThrowController(_cfg, _session, SetStatus) { SetNote = n => Status.SetNote(n) };
        _session.Throw = _throw;
        _session.Quick = _quick;
        _binds = new BindingRegistrar(_cfg, _quick.Input, SetStatus, _quick);
        _quick.Binds = _binds;
        _settingsPage = new Pages.SettingsPage(_cfg, ShowLogPage, SetStatus);
        _settingsPage.AfterSave = _throw.AfterRunCfgChange;   // モデル・URL・載せ方が変わったら載せ直し
        _binds.Fields = _settingsPage;
        _session.SettingsView = _settingsPage;
        _throw.ModelList = _settingsPage;
        _settingsPage.RegRequest = _binds.Start;     // 「登録」→ 次に押された入力を掴まえる
        _settingsPage.ClearRequest = _binds.Clear;   // 「解除」も消す（Applyでフックも即追従）
        _session.AutoMode = _cfg.Settings.Mode == "auto";
        PageArea.Children.Add(_logPage);
        UpdatePageMarks();

        // アイコンフォント（Segoe Fluent Icons / MDL2）が無い環境では
        // アイコンを消して文字だけの項目にする
        if (!HasIconFont()) DrawerView.UseNoIconFont();
        DrawerView.ItemInvoked += DoDrawerAction;

        // 前面固定。チェック欄を作ったここで窓の実態へも適用する。
        // ここを忘れると main_topmost:true が残っていても窓は手前に出ない
        bool topmost = cfg.Settings.MainTopmost;
        TopmostCheck.IsChecked = topmost;
        Topmost = topmost;
        // 初期設定で保存を発火させないため、値を入れてから変更の聞き手をつける
        TopmostCheck.Checked += Topmost_Changed;
        TopmostCheck.Unchecked += Topmost_Changed;

        Status.TriggerEnabled = TriggerEnabled;
        _statusTimer.Tick += (_, _) => TickStatus();
        _loadedInit = () =>   // 窓を出す前（トレイ起動）でも同じ初期化を回せるよう、Loaded の中身はここへ置く
        {
            // 動き出し: まず500ms後、そのあと350msごと
            var first = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            first.Tick += (_, _) => { first.Stop(); _statusTimer.Start(); };
            first.Start();
            if (!string.IsNullOrEmpty(_cfg.LoadWarning))
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                t.Tick += (_, _) => { t.Stop(); SetStatus(_cfg.LoadWarning); };
                t.Start();
            }
            else if (_cfg.Migrations.Count > 0)
            {
                // 既定値の引っ越しは状態欄に1行でまとめる（窓ができてから）
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                t.Tick += (_, _) => { t.Stop(); SetStatus("設定: " + string.Join(" / ", _cfg.Migrations)); };
                t.Start();
            }
        };

        // 自動トリガの背景巡回。始めても窓が無ければ1周で何もしない。
        // trig_* は毎巡回 設定から読む（設定ページで変えればその場追いつく）
        _trig = new TriggerWatcher(ReadTrigParams, TriggerEnabled, () => Status.Busy,
            () => _throw.AutoRunInFlight,
            TrigRectNow, r => _trigGrab(r), TrigFire,
            v => { Status.TrigTicks++; Status.TrigDetect = v; });   // ticks は「監視中…」の点の進行
        _trig.Start();
        _throw.AutoAllowed = TriggerEnabled;   // 打ち切りの後の投げ直しも、自動トリガが有効な時だけ

        // クイックぶん投げ。フックスレッドは常時待機で、実際に
        // 割り当て入力を握るのは quick_enabled/quick_button がONのあいだだけ。
        // 設定ページ側の欄はApply(fromUi:true)を直接呼ぶ（上のコンストラクタ引数）。
        // 巡回が生きているあいだの取りこぼし対策としてQuickPoll内の1秒読み直しも残す
        _quick.Start();
        _binds.SyncFields();   // 欄の表示は検証後のメモリ側値へ（rawのまま見せない）
        // AIまわりの準備（モデルの一覧読み・温め・前提チェック）。窓ができた後の 900/1100/1400ms
        _throw.InitRunKey();
        _loadedInit += () => _throw.ScheduleEnvChecks();
        Loaded += (_, _) => RunLoadedInit();

        // タスクトレイのアイコン（部品が全部できてから。閉じる時に OnClosed で外す）
        _tray = new TrayIcon(this, _cfg);
        // ログオフ・シャットダウンの時は、トレイへしまう設定でも本当に終わる
        if (Application.Current is { } app && app.Dispatcher.CheckAccess()) app.SessionEnding += (_, _) => _sessionEnding = true;
    }

    // ---------------- タスクトレイ ----------------

    // Loaded で1回だけ回す初期化（ステータス巡回の始動・設定の読み込み注意の表示・AI の前提チェック）。
    // トレイ起動（窓を出さない）では Loaded が来ないので StartHidden が同じものを回す。2回目は何もしない
    private Action? _loadedInit;
    private void RunLoadedInit() { var a = _loadedInit; _loadedInit = null; a?.Invoke(); }
    internal bool StatusTimerEnabled => _statusTimer.IsEnabled;
    /// <summary>--tray で窓を出さずに起動した（この起動ではしまった時の吹き出しを出さない）</summary>
    internal bool StartedHidden { get; private set; }

    /// <summary>窓を出さずに起動する（Windows の起動時のトレイ起動）。窓のハンドルを作って位置の復元（OnSourceInitialized）を済ませ、Loaded 相当の初期化を回す。窓は ShowAndActivate で出る</summary>
    internal void StartHidden()
    {
        StartedHidden = true;
        new WindowInteropHelper(this).EnsureHandle();
        RunLoadedInit();
    }

    private TrayIcon? _tray;
    internal TrayIcon? Tray => _tray;
    internal ConfigStore Cfg => _cfg;
    internal CaptureSession CaptureSessionForTray => _session;
    internal Pages.SettingsPage? SettingsPageForTray => _settingsPage;

    /// <summary>窓を出して前へ。最小化なら元へ戻し、隠れていれば Show する</summary>
    internal void ShowAndActivate()
    {
        if (_closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>終了の印。トレイの「終了」から。Close() で OnClosing の後始末へ進む
    /// （「×でトレイへ」が ON の時の×は隠すだけ。この印が立っていれば本当に終わる）</summary>
    internal bool Quitting { get; private set; }

    internal void QuitApp()
    {
        Quitting = true;
        Close();
    }

    // ---------------- 位置・大きさの保存と復元 ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _activateMsg = Native.RegisterWindowMessage(SingleInstanceGuard.ActivateMessageName);
        HwndSource.FromHwnd(hwnd)?.AddHook(MainWndProc);
        // 前回の位置がどのモニタにも掛からない（モニタを外した等）ときは
        // 大きさだけ戻して位置はOS任せ
        if (WindowGeometry.TryParse(_cfg.Settings.MainGeometry, out var g))
        {
            if (WindowGeometry.OnMonitor(g.X, g.Y, g.W, g.H))
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, g.X, g.Y, g.W, g.H,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            else if (Native.GetWindowRect(hwnd, out var r))
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, g.W, g.H,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
        }
        if (_cfg.Settings.MainZoomed)
            WindowState = WindowState.Maximized;
    }

    private IntPtr MainWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_activateMsg != 0 && msg == _activateMsg)
        {
            ShowAndActivate();   // トレイにしまわれている時も出す
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool _sessionEnding;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // 「×でトレイへ」が ON で、トレイの「終了」・ログオフ/シャットダウン・アプリ終了のどれでもない時は、
        // 閉じずに窓だけ隠す（捕捉窓・バー・クイック・自動トリガは動いたまま。後始末も走らせない）
        if (!e.Cancel && _cfg.Settings.CloseToTray && !Quitting && !_sessionEnding
            && !Dispatcher.HasShutdownStarted && !_closing && _tray is { IsDisposed: false })
        {
            e.Cancel = true;
            SaveGeometry();
            WindowMotion.HideFade(this, 1.0, WindowMotion.PanelFadeMs);   // 透過しない窓なので待たずにすぐ隠す（Windows の消える動きに任せる）
            _tray.MaybeShowHiddenHint(StartedHidden);   // 初めての1回だけ（--tray の起動では出さない）
            return;
        }
        _closing = true;
        _tray?.Dispose();   // 後始末で数秒かかっても、アイコンは先に外す
        _closeCts.Cancel();   // 巡回側の UI 待ちを即座にほどいて Join(3000) で固まらないようにする
        _trig?.Stop();   // 巡回を止めてから片付ける（撮影だけ走ったまま終わらない）
        _quick.Stop();  // 巡回を止め、低レベルフックを剥がして終わる（他アプリへ割り込みを残さない）
        // 設定ページが300ms間引いている保存を、ここでもらい忘れて落とさない
        _settingsPage?.FlushPendingSave();
        SaveGeometry();
        // 見えている窓を全部いっしょに薄れさせてから（約0.2秒）、後の後始末へ進む（前は即 Hide）
        FadeOutForQuit();
        // モデル降ろしの最大3秒待ちで窓が固まって見えないよう、窓を全部先に隠す
        Hide();
        _session.HideAllForQuit();
        _throw.CancelOnQuit();   // ぶん投げ中ならやめるを呼んでログを「やめました」で確定
        _throw.UnloadOnQuit();   // 載せたモデルを降ろす（終了後に数GBが居残らない）
    }

    /// <summary>終わる時に窓が薄れる長さ（ms）。0 なら薄れずに即隠す（テストは TestInit で 0 にする）</summary>
    internal static int QuitFadeDefaultMs = WindowMotion.CloseMs;
    internal int QuitFadeMs = QuitFadeDefaultMs;
    /// <summary>ログの窓を、ほかの窓が薄れ終わる何 ms 前に隠すか（Windows の消える動きの長さ。この PC で約 90ms と測った）</summary>
    private const int MainHideLeadMs = 90;

    /// <summary>ログの窓・キャプチャ窓・バー・解答欄をいっしょに薄れさせ、薄れ終わるまでこの場で待つ
    /// （その後の Hide・モデル降ろしは今まで通り。ログオフ・シャットダウンの時は待たない）</summary>
    private void FadeOutForQuit()
    {
        int ms = QuitFadeMs;
        if (ms <= 0 || _sessionEnding || Dispatcher.HasShutdownStarted) return;
        bool any = _session.FadeAllForQuit(ms);
        // ログの窓は透過しない普通の窓で、WPF が WS_EX_LAYERED を付けさせないので、窓全体の濃さでは薄れない。
        // 代わりに、ほかの窓が薄れ終わる少し前に隠し、Windows 自身の消える動き（約 90ms）がほかの窓といっしょに終わるようにする
        bool main = IsVisible;
        if (!any && !main) return;
        try
        {
            var frame = new DispatcherFrame();
            if (main)
            {
                var hideMain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms - MainHideLeadMs)) };
                hideMain.Tick += (_, _) => { hideMain.Stop(); Hide(); };
                hideMain.Start();
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 30) };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }
        catch (Exception ex) { AppLog.Error("quit fade", ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _tray?.Dispose();   // アイコンを残さない
        _tray = null;
        // Owner を外した関連窓もメイン窓を閉じたら確実に全部閉じる
        _session.CloseAllNow();
        // 設定の購読とアクティブの見張り（TaskbarFront）を外す。前は呼んでおらず、テストで窓を作るたびに見張りが残った
        _session.Dispose();
    }

    /// <summary>
    /// 終了時にメイン窓の大きさ・位置を覚える。最大化中は「最大化だった」だけ
    /// 覚え、元の大きさは前回の値を残す（最大化したままの大きさを保存すると
    /// 戻したときに画面いっぱいの普通の窓になる）。最小化中は何も変えない
    /// </summary>
    private void SaveGeometry()
    {
        if (WindowState == WindowState.Minimized) return;
        if (WindowState == WindowState.Maximized)
        {
            _cfg.Settings.MainZoomed = true;
        }
        else
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r))
            {
                _cfg.Settings.MainZoomed = false;
                _cfg.Settings.MainGeometry =
                    WindowGeometry.Format(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        _cfg.Save();
    }

    // ---------------- キャプチャ窓 ----------------

    private void CaptureButton_Click(object sender, RoutedEventArgs e) => _session.OpenCapture();

    private void SnipButton_Click(object sender, RoutedEventArgs e) => StartSnip();

    /// <summary>画面を少し暗くして範囲ドラッグ（メイン窓の「範囲ドラッグ」・トレイの左1回押し）。メイン窓は動かさない</summary>
    internal void StartSnip() => _quick.SnipStart();

    // ---------------- 前面固定 ----------------

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        bool on = TopmostCheck.IsChecked == true;
        Topmost = on;
        _cfg.Settings.MainTopmost = on;
        // v4.4: 前面固定は押した瞬間に保存する。
        // 終了時の保存だけに頼むと、アプリを落としかたで次回OFFへ戻る
        _cfg.Save();
    }

    // ---------------- ステータス ----------------

    /// <summary>状態欄へ一言出す。12秒たつと監視中/待機中へ戻る。バーの2行目にも同じ言葉を出す</summary>
    public void SetStatus(string msg)
    {
        Status.Set(msg);
        StatusText.Text = msg;
        _session.SetBarStatus(msg);
    }

    private void TickStatus()
    {
        string msg = Status.Tick();
        StatusText.Text = msg;
        _session.SetBarStatus(msg);   // 2行目は同じ言葉が並ぶ（bar_refresh_status と同じ）
    }

    /// <summary>自動トリガが有効か。バーのトグルで mode が切り替わる</summary>
    private bool TriggerEnabled() => !_closing && _session.AutoMode && Status.CaptureOpen;

    /// <summary>trig_* を毎巡回読み直す（Python の mtime ホットリロードの代わり。
    /// 設定ページと同じ値をメモリから読むだけ）。範囲外は TrigParams.From でclamp</summary>
    private TrigParams ReadTrigParams() => TrigParams.From(
        _cfg.Settings.TrigPollInterval,
        _cfg.Settings.TrigChangeRatio,
        _cfg.Settings.TrigStableRatio,
        _cfg.Settings.TrigStableSamples,
        _cfg.Settings.TrigCooldown);

    private T InvokeOrCancelOnClose<T>(Func<T> func, T fallback)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return fallback;
        try
        {
            var op = Dispatcher.InvokeAsync(func);
            op.Task.Wait(_closeCts.Token);
            return op.Task.Result;
        }
        catch (OperationCanceledException) { return fallback; }
        catch (AggregateException) { return fallback; }
    }

    /// <summary>巡回用の撮る矩形（UIスレッドで）。最小化・窓消滅は null</summary>
    private Native.RECT? TrigRectNow() => InvokeOrCancelOnClose<Native.RECT?>(() =>
    {
        var cap = _session.CaptureWin;
        if (cap is null) return null;
        IntPtr h = new WindowInteropHelper(cap).Handle;
        return h == IntPtr.Zero ? null : Native.ClientRectScreen(h);
    }, null);

    /// <summary>発火頼み: UIスレッドで busy を予約してからぶん投げ開始。
    /// 始められなければ false（watcher は指紋を控えず次の巡回でまた狙う）</summary>
    private bool TrigFire() => InvokeOrCancelOnClose(() => _throw.AutoFire(), false);

    // ---------------- 部品（組み立ては上のコンストラクタ） ----------------

    private readonly ThrowController _throw;
    internal ThrowController Throw => _throw;
    private readonly QuickThrowController _quick;
    private readonly BindingRegistrar _binds;
    internal QuickThrowController Quick => _quick;
    internal BindingRegistrar Binds => _binds;
    private readonly CaptureSession _session;
    internal CaptureSession Session => _session;

    // ---------------- ページ表示域 ----------------

    /// <summary>ログページを開き直す（☰のログ・設定ページから←）。</summary>
    internal void ShowLogPage()
    {
        _settingsPage?.FlushPendingSave();
        PageArea.Children.Clear();
        PageArea.Children.Add(_logPage!);
        _current = "log";
        UpdatePageMarks();
        _logPage!.ShowAtBottom();
    }

    /// <summary>設定ページへ切り替える（見出し行付き）。</summary>
    internal void ShowSettingsPage()
    {
        PageArea.Children.Clear();
        PageArea.Children.Add(_settingsPage!);
        _current = "cfg";
        UpdatePageMarks();
    }

    private string _current = "log";

    /// <summary>今いるページの項目にだけ左端のAccent縦線を出す（Drawer へ伝える）。</summary>
    private void UpdatePageMarks() => DrawerView.SetCurrentPage(_current);

    // ---------------- ☰メニュー（本体は Views/Drawer） ----------------

    private void MenuButton_Click(object sender, RoutedEventArgs e) => DrawerView.Toggle();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DrawerView.IsOpen)
        {
            DrawerView.Close();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void DoDrawerAction(string tag)
    {
        switch (tag)
        {
            case "log": ShowLogPage(); break;
            case "cfg": ShowSettingsPage(); break;
            case "help":
                // 設定ページの中「バーの使い方」節（注釈図）までスクロール（_show_bar_help）
                ShowSettingsPage();
                UiTimer.Later(30, () => _settingsPage?.ScrollToBarHelp());
                break;
            case "folder": OpenLogFolder(); break;
            case "about": ShowAbout(); break;
        }
    }

    internal string LogFolderTarget() => _paths.Root;

    private void OpenLogFolder()
    {
        try
        {
            string dir = LogFolderTarget();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("open log folder failed", ex);
            SetStatus("ログのフォルダを開けませんでした");
        }
    }

    private void ShowAbout()
    {
        // 版は csproj の <Version>（αの印は付けない）。アイコンを出すため MessageBox ではなく専用の窓
        new Pages.AboutWindow { Owner = this }.ShowDialog();
    }

    // ---------------- 小さな共通 helpers ----------------

    private static bool HasIconFont()
    {
        foreach (var f in Fonts.SystemFontFamilies)
        {
            if (f.Source == "Segoe Fluent Icons" || f.Source == "Segoe MDL2 Assets")
                return true;
        }
        return false;
    }
}

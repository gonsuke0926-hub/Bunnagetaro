using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;

namespace Bunnagetaro5;

/// <summary>
/// 解答欄 inside（7.5）。キャプチャ窓のクライアント矩形にぴったり重なる別窓。
/// 地色 overlay_bg を overlay_alpha の不透明度で塗り、文字は不透明のまま
/// （AllowsTransparency の窓に半透明 Border を置く WPF 版の作り。Tk版の
/// -alpha と違い文字まで薄くならない。指示書7.5の方針）。
/// 本文は読み取り専用の RichTextBox: 選択・Ctrl+C・右クリック
/// 「選択部分をコピー」「全文をコピー」。文字の上は何も無い場所を押すと
/// キャプチャ窓ごとにつかんで動かせる（Tk版の余白ドラッグ fleur 相当）。
/// 思考欄は本文の下。思考が無い間は畳み、有る間だけ出る。文字は薄色
/// （overlay_fg を overlay_bg 側へ45%寄せ）で最後の4000字だけ。
/// 表示中はキャプチャ窓の移動・サイズ変更・最小化に毎回ついて回る。
/// 撮影の直前には必ず隠す（消え方は即。人が閉じるときのフェードは host 側）。
/// 本文には LaTeX 式を混ぜられる（手順25）: $...$ は文中、$$..$$・\[..\] は
/// 独立行で中央。WpfMath で解答欄の色・大きさ合わせ表示し、表示できない式は
/// 元の文字のまま。ストリーミング中は文字で出し、閉じ記号で式に差し替え。
/// </summary>
public class AnswerPanel : Window
{
    /// <summary>おまけの窓なので Alt+Tab に出さない。WS_EX_TOOLWINDOW</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly IAnswerHost _host;
    private readonly CaptureWindow _cap;
    private readonly ConfigStore _cfg;

    /// <summary>解答欄の中身（本文・思考・追加の問い・数式・コピー・見た目）。窓は枠だけ</summary>
    internal AnswerView View { get; }

    /// <summary>解答欄に触れた最後の時刻（手順19の巡回が「自分の表示」と見なす材料）</summary>
    public DateTime LastInteractUtc => _lastWinInteract > View.LastInteractUtc ? _lastWinInteract : View.LastInteractUtc;
    private DateTime _lastWinInteract = DateTime.MinValue;

    internal AnswerPanel(ConfigStore cfg, IAnswerHost host, CaptureWindow cap)
    {
        _cfg = cfg;
        _host = host;
        _cap = cap;

        // Tkの overrideredirect 相当: 枠なし・完全透明地・タスクバーに出さない。
        // 大きさ・位置は Win32 物理pxでクライアント矩形へ合わせる（Show前にDIP概算）
        Title = "解答欄";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;

        View = new AnswerView(host, followRightMargin: 18, thinkMaxHeight: 90);
        Content = View;
        // 余白ドラッグでキャプチャ窓ごと動かす（窓としての仕事。見えている文字の上は選択に任せる。修正J115: 位置の反映中も正しく判定）
        View.PreviewMouseLeftButtonDown += Body_Down;
        View.PreviewMouseMove += Body_Hover;
        ApplyStyle();
    }

    /// <summary>解答欄自身が見えているか（Python版 overlay_visible 相当）</summary>
    public bool ShownUp => IsVisible;

    private IntPtr RootHwnd
    {
        get
        {
            var h = new WindowInteropHelper(this).Handle;
            return h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
        }
    }

    private IntPtr CapRootHwnd
    {
        get
        {
            var h = new WindowInteropHelper(_cap).Handle;
            return h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
        }
    }

    // ---------------- 見た目 ----------------

    /// <summary>設定（色・透明度・フォント）を実窓へ反映（tw_panel._apply_panel_style）。
    /// 保存時と表示直前に host から呼ばれる。中身の適用は AnswerView（地色は overlay_alpha で透ける）</summary>
    public void ApplyStyle()
    {
        var st = PanelStyle.From(_cfg);
        byte a = (byte)Math.Round(Math.Clamp(st.Alpha, 0.15, 1.0) * 255.0);
        View.ApplyStyle(st, a, (byte)(a / 2));
    }

    // ---------------- 配置（クライアント矩形へぴったり） ----------------

    /// <summary>解答欄をキャプチャ窓のクライアント矩形（物理px）へ合わせ直す（置き場所は WindowPlacer が決める）</summary>
    public void PositionPanel() => PositionPanel(null);

    /// <summary>target が非 null なら「これから行く矩形」へ合わせる</summary>
    public void PositionPanel(Native.RECT? target) => _host.PlacePanel(target);

    internal void UpdateThinkMaxHeight()
    {
        // 思考欄の高さ: 窓の高さの3割ぶんまで（それ以上は中をスクロール）
        if (ActualHeight > 40) View.ThinkMaxHeight = Math.Max(30, ActualHeight * 0.35);
    }

    // ---------------- 表示の中身（ストリーミング。中身・表示判断は AnswerView / WindowVisibility） ----------------

    /// <summary>新しいぶん投げの開始。本文・思考を空にして上から（tw_panelのcard開始 yview 0）</summary>
    public void ResetRun()
    {
        _host.ResetPanelRun();
        View.ResetRun();
    }

    /// <summary>本文のdelta（これまでの全文）。出ていなければ出してから、中身へ渡す</summary>
    public void ApplyBody(string partial)
    {
        if (partial.Length == 0 || partial == View.ModelText) return;
        if (_host.DeferPanelWhileDragging(() => ApplyBody(partial))) return;
        if ((!IsVisible || _host.IsPanelFadingOut()) && !_host.RequestPanelShow()) return;
        View.ApplyBody(partial);
    }

    /// <summary>思考のdelta。思考が届いた時点で窓を出す（本文より長く思考するモデルで、
    /// 本文が来るまで思考が見えないのを防ぐ。tw_panelは本文deltaでだけ出していた）</summary>
    public void ApplyThink(string think)
    {
        if (think.Length > 0 && _host.DeferPanelWhileDragging(() => ApplyThink(think))) return;
        if (think.Length > 0 && (!IsVisible || _host.IsPanelFadingOut()) && !_host.RequestPanelShow()) return;
        View.ApplyThink(think);
    }

    /// <summary>修正J112: 区画（マスク）が渡された時、文字を待たずに窓を出す（出ていなければ。出し方は ApplyThink と同じ）</summary>
    public void RevealForLayout()
    {
        if (_host.DeferPanelWhileDragging(RevealForLayout)) return;
        if (!IsVisible || _host.IsPanelFadingOut()) _host.RequestPanelShow();
    }

    /// <summary>完成の仕上げ（done ok・keep_view=True）</summary>
    public void ShowFinal(string text)
    {
        if (_host.DeferPanelWhileDragging(() => ShowFinal(text))) return;
        if (!_host.RequestPanelShow()) return;
        View.ShowFinal(text);
    }

    /// <summary>設定で置き場所が切り替わった時、前の側の答えを写して出す（F6）</summary>
    internal bool AdoptFrom(AnswerView other)
    {
        if (_host.DeferPanelWhileDragging(() => AdoptFrom(other))) return true;
        if (!_host.RequestPanelShow()) return false;
        View.AdoptFrom(other);
        return true;
    }

    /// <summary>ぶん投げ完了後の本文（テスト・診断用）</summary>
    public string ModelText => View.ModelText;

    /// <summary>撮影の直前: 即隠す（フェード無し。_hide_overlay相当。人は WindowVisibility の HidePanelFade）</summary>
    public void HideForCapture() => _host.HidePanelForCapture();

    /// <summary>E3/E4: 薄れてから隠す（モード切替・最小化・案内の自動消し）。撮影前の隠しはこちらでなく
    /// <see cref="HideForCapture"/>（即）。onHidden は隠れた直後（打ち切られたら呼ばれない）</summary>
    public void HideFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null)
        => _host.HidePanelFade(ms, onHidden);

    // ---------------- 余白ドラッグでキャプチャ窓ごと動かす ----------------

    private void Body_Down(object sender, MouseButtonEventArgs e)
    {
        _lastWinInteract = DateTime.UtcNow;
        // 本文エリア外（思考欄・たずねる欄）や、見えている文字の上なら選択・操作に任せる（修正J115）
        if (!View.IsInBodyArea(e) || View.IsOverText(e)) return;
        var cap = CapRootHwnd;
        if (cap == IntPtr.Zero || !Native.IsWindow(cap)) return;
        e.Handled = true;
        Native.ReleaseCapture();
        Native.SendMessage(cap, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
    }

    private void Body_Hover(object sender, MouseEventArgs e)
    {
        if (!View.IsInBodyArea(e)) return;
        View.SetBodyCursor(View.IsOverText(e) ? Cursors.IBeam : Cursors.SizeAll);
    }

    // ---------------- 素通し・作成時 ----------------

    /// <summary>透過（7.7のバーと同じ切換）。解答欄も下地操作を通す（_sync_panel_passthru）</summary>
    public void ApplyPassthru(bool on)
    {
        var h = RootHwnd;
        if (h == IntPtr.Zero) return;
        if (!Native.SetClickThrough(h, on))
            AppLog.Error("解答欄の素通し切替に失敗（set_click_through）");
    }

    /// <summary>修正J30: 配信・録画に映す設定を反映する</summary>
    internal void ApplyCaptureAffinity()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero && !Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("解答欄の SetWindowDisplayAffinity 失敗");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        Native.NoTransitions(h);
        // 7.2: 自分の窓は作成時に撮影へ写らない指定を付ける（配信・録画に映す ON なら WDA_NONE。修正J30）
        if (!Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("解答欄の SetWindowDisplayAffinity 失敗");

        // 保存済み透過が ON なら開いた瞬間に素通しを適用（バーと同じ流れ）
        if (_cfg.Settings.ChromePassthru) ApplyPassthru(true);
        HwndSource.FromHwnd(h)?.AddHook(PanelWndProc);
    }

    /// <summary>
    /// 解答欄は窓の中いっぱいに重なるので、そのままだとキャプチャ窓の縁（リサイズ・上端の移動）を
    /// 覆ってしまい、答えが出ている間は縁を掴めなくなる。縁の帯の上は HTTRANSPARENT を返して
    /// 下のキャプチャ窓へ渡す（Python版 tw_band の _panel_resize_subclass_proc と同じ）。
    /// 中（文字の余白の掴み移動・文字の選択）はこれまでどおり解答欄が受ける
    /// </summary>
    private IntPtr PanelWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_NCHITTEST) return IntPtr.Zero;
        var cap = CapRootHwnd;
        if (cap == IntPtr.Zero || !Native.GetWindowRect(cap, out var r)) return IntPtr.Zero;
        long p = lParam.ToInt64();
        int mx = (short)(p & 0xFFFF);
        int my = (short)((p >> 16) & 0xFFFF);
        int hit = CaptureWindow.HitTest(mx - r.Left, my - r.Top,
                                        r.Right - r.Left, r.Bottom - r.Top, CaptureWindow.HitBand);
        if (hit == 0) return IntPtr.Zero;
        handled = true;
        return new IntPtr(Native.HTTRANSPARENT);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5;

/// <summary>
/// キャプチャ窓（7.1）。デスクトップに重ねる透明の枠。中は透けて、
/// 縁（4物理px・Accent半透明）だけが見える。縁ドラッグでリサイズ、
/// 中ドラッグで移動（WM_NCHITTEST で HT* を返す）。
/// 中は Python版と同じ不透明度1/255の見えない膜（BandAlpha）で塗ってあり、
/// それで初めてクリックを受け取れる（完全に透明な画素は OS が窓に届けない）。
/// 膜の画素は目に見えないうえ、窓全体が WDA_EXCLUDEFROMCAPTURE で撮影から
/// 除外されているので撮影画素へは影響しない。位置・大きさは物理ピクセルで保存・復元。
/// バー・解答欄・透過・枠固定は手順14以降（ここでは窓そのものだけ）。
/// </summary>
public class CaptureWindow : Window
{
    // Python版 CAPTURE_MIN_W/H（クイックぶん投げの最小四角も将来ここを参照）
    public const int MinW = 30;
    public const int MinH = 30;

    /// <summary>見える縁の厚さ（物理px、7.1）。窓のDPIでDIPへ換算して描く</summary>
    public const int BorderPhys = 4;   // 既定値（実際の太さは設定 capture_border_px。CaptureBorder.Px）

    /// <summary>いまの縁の太さ（物理px。設定から。撮る範囲の削り幅・掴める幅・ダミー枠もこれを使う）</summary>
    public static int BorderPx => CaptureBorder.Px;

    /// <summary>
    /// リサイズ・移動の掴み判定をする帯（物理px）。縁は4pxだが、
    /// 200%窓で4pxは掴みにくいので内側へ見えない余裕を足した（DECISIONS）
    /// </summary>
    internal static int HitBand => CaptureBorder.HitBand;

    /// <summary>
    /// 中を掴めるようにする膜の不透明度（Python版 tw_band BAND_ALPHA = 1/255）。
    /// 1/255は目には見えないが、完全に透明だと OS がクリックを窓へ届けない
    /// </summary>
    internal const byte BandAlpha = 1;

    /// <summary>位置が未保存・モニタ外だった時の既定位置（Python版と同じ 200,200）</summary>
    internal const int DefaultX = 200;
    internal const int DefaultY = 200;

    private readonly ConfigStore _cfg;
    private readonly Action<string> _setStatus;
    private readonly Border _frame = new();
    private readonly DispatcherTimer _saveTimer;

    /// <summary>
    /// 解答欄が窓の上へ出ている間、中を素通し（HTTRANSPARENT）へ戻すための問い合わせ。
    /// 設定しない＝常に中を掴める。Python版 tw_band 338行の overlay_visible 相当。
    /// WPFの解答欄は別の窓なので通常は噛み合わないが、上の窓が奪ったクリックを
    /// 下の解答欄へ返すために付ける
    /// </summary>
    internal Func<bool>? InteriorBlocked;

    public CaptureWindow(ConfigStore cfg, Action<string> setStatus)
    {
        _cfg = cfg;
        _setStatus = setStatus;

        Title = "キャプチャ窓 — ぶん投げたい領域に重ねる";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.CanResize;   // WM_NCHITTEST で縁を返しても WS_THICKFRAME が無いと OS はリサイズしない（A2点検で判明）。枠なし・透明窓なので太枠は見えない
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = null;                  // 窓自体は何も塗らない。描くのは膜と縁だけ
        Left = DefaultX;
        Top = DefaultY;
        Width = 640;                        // 仮。実寸は OnSourceInitialized で物理px指定
        Height = 400;

        CaptureBorder.Update(cfg.Settings);
        _frame.BorderBrush = CaptureBorder.Brush();
        _frame.BorderThickness = new Thickness(CaptureBorder.Px);
        _frame.SnapsToDevicePixels = true;
        _frame.UseLayoutRounding = true;   // 125%/175%は1物理pxが端数DIP。右下の縁のにじみ・欠けを防ぐ
        // 中を見えない膜（A=1/255）で塗る（Python版 BAND_ALPHA=1・tw_band 36行）。
        // これを塗らないと中のクリックが OS で取りこぼされ、窓が動かない（A2）
        _frame.Background = MembraneBrush();
        Content = _frame;

        // 保存は500msの取りまとめ（Python版 root.after(500, _save_capture_size) と同じ）
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveGeometryNow(); };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();
        DpiChanged += (_, _) => ApplyBorder();
    }

    /// <summary>設定が変わった時に縁の色・不透明度・太さを今の設定へ（SyncBarFromCfg から）</summary>
    public void ApplyBorderSettings(AppSettings s)
    {
        CaptureBorder.Update(s);
        _frame.BorderBrush = CaptureBorder.Brush();
        ApplyBorder();
    }

    /// <summary>中を掴めるようにする膜（#01000000）。Freeze して使い回す</summary>
    private static SolidColorBrush MembraneBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(BandAlpha, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>縁を「物理4px」に保つためのDIP変換（DPI違いで掛け間違えない）</summary>
    private void ApplyBorder()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double dpi = hwnd == IntPtr.Zero ? 0 : Native.GetDpiForWindow(hwnd);
        if (dpi < 96) dpi = 96;
        _frame.BorderThickness = new Thickness(CaptureBorder.Px * 96.0 / dpi);
    }

    private const int WS_EX_TOOLWINDOW = 0x00000080;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // F36: Owner をメイン窓にしないため、Alt+Tab に出ないよう WS_EX_TOOLWINDOW を付ける
        long ex = Native.GetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        // J1: 最大化できない窓にする（画面の上端へ寄せた時・縁をダブルクリックした時の Windows の最大化・スナップを出さない）
        long st = Native.GetWindowLongPtrW(hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtrW(hwnd, Native.GWL_STYLE, new IntPtr(st & ~WS_MAXIMIZEBOX));
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        // 出す・消す時のアニメーション消えかけを残さない（7.1）。
        // Tkは map のたびに外枠を作り直すので毎回付けていたが、
        // WPFのHWNDは作り直されないのでこの1回で足りる（DECISIONS）
        Native.NoTransitions(hwnd);
        // 自分の窓はスクショに写らない（7.2）。配信・録画に映す設定が ON なら WDA_NONE（修正J30）
        ApplyCaptureAffinity();
        ApplyBorder();

        int w = ResolveOpenSize(_cfg.Settings.CaptureWidth, MinW,
                                Native.GetSystemMetrics(Native.SM_CXSCREEN));
        int h = ResolveOpenSize(_cfg.Settings.CaptureHeight, MinH,
                                Native.GetSystemMetrics(Native.SM_CYSCREEN));
        var (x, y) = ResolveOpenPos(_cfg.Settings.CaptureX,
                                    _cfg.Settings.CaptureY, w, h);
        // 物理pxでそのまま置く（DIPを通さない: 7.1「掛け間違えないこと」）
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>修正J30: 配信・録画に映す設定を反映する</summary>
    internal void ApplyCaptureAffinity()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.ApplyCaptureAffinity(hwnd, _cfg.Settings.AllowCaptureOverlay);
    }

    /// <summary>保存された大きさの範囲切り。Python版 min(max(v,MIN), max(MIN,screen)) と同じ</summary>
    internal static int ResolveOpenSize(int v, int min, int screen)
        => Math.Min(Math.Max(v, min), Math.Max(min, screen));

    /// <summary>前回の位置が今もモニタ上ならそこへ、無ければ既定位置へ（7.1）</summary>
    internal static (int X, int Y) ResolveOpenPos(int? sx, int? sy, int w, int h)
    {
        if (sx is int x && sy is int y && WindowGeometry.OnMonitor(x, y, w, h))
            return (x, y);
        return (DefaultX, DefaultY);
    }

    /// <summary>窓が動く／大きさが変わる「直前」に、これから行く矩形（物理px）を渡す。
    /// バー・解答欄を同じフレームで動かして、1フレーム遅れて付いてくるのを防ぐ（C4）</summary>
    internal Action<Native.RECT>? Moving;

    // H6: 上・左を含む縁を引いている間は、本物の窓を動かさず、ダミー枠（QuickMarker の外から矩形を渡す形）を動かす。
    // 本物の窓は透過窓で、位置が先に動いて中の絵が1フレーム遅れ、反対の辺がうねって見えるため。
    private bool _inSizeMove;
    private Native.RECT _moveStartRect;
    private Native.POINT _moveStartCursor;
    private bool _dummyDrag;                   // ダミー枠を使って引いている最中
    private bool _dragCancelled;               // WM_CANCELMODE（フォーカス喪失など）が来た
    private Native.RECT _dragStartRect;
    private int _dragEdge;
    private Input.QuickMarker? _dummy;
    private int _dummyGen;
    private Brush? _savedBorderBrush;

    /// <summary>引いている間の「いまの矩形」（ダミー枠の矩形）。null なら引いていない。
    /// バー・外の解答欄・窓の中の解答欄は、本物の窓の代わりにこれを基準に追従する</summary>
    internal Native.RECT? DragRect { get; private set; }

    /// <summary>H9: ダミー枠で引き始めた時 true、片付け（離した・取り消した）の後 false。
    /// 窓の中の解答欄は引いている間だけ隠れる（透過窓は位置と大きさが同時に変わるとうねるため）</summary>
    internal event Action<bool>? DummyDragChanged;

    // H11: ダミーを使わないサイズ変更（右・下・右下）の間、解答欄を隠している
    private bool _sizeHidden;
    /// <summary>縁でサイズを変えている間（ダミー枠でも OS のサイズ変更でも）true。窓の中の解答欄が隠れている間</summary>
    internal bool SizeDragging => DragRect is not null || _sizeHidden;

    /// <summary>テスト用: カーソル位置の取得（既定は GetCursorPos）</summary>
    internal Func<Native.POINT> CursorProvider = static () => Native.GetCursorPos(out var p) ? p : default;
    /// <summary>テスト用: Esc が押されているか</summary>
    internal Func<bool> EscapeDown = static () => (GetAsyncKeyState(0x1B) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    internal static bool IsTopOrLeftSizingEdge(int edge) =>
        edge is Native.WMSZ_LEFT or Native.WMSZ_TOP or Native.WMSZ_TOPLEFT
             or Native.WMSZ_TOPRIGHT or Native.WMSZ_BOTTOMLEFT;

    /// <summary>引き始めの矩形・掴んだ辺・カーソルの動き量(dx,dy)から、引いている矩形を出す（物理px）。
    /// 掴んでいない辺は動かさず、最小サイズ（minW×minH）より小さくなる時は反対側の辺を固定して寄せる</summary>
    internal static Native.RECT DragRectFor(Native.RECT start, int edge, int dx, int dy, int minW, int minH)
    {
        bool l = edge is Native.WMSZ_LEFT or Native.WMSZ_TOPLEFT or Native.WMSZ_BOTTOMLEFT;
        bool r = edge is Native.WMSZ_RIGHT or Native.WMSZ_TOPRIGHT or Native.WMSZ_BOTTOMRIGHT;
        bool t = edge is Native.WMSZ_TOP or Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT;
        bool b = edge is Native.WMSZ_BOTTOM or Native.WMSZ_BOTTOMLEFT or Native.WMSZ_BOTTOMRIGHT;
        var o = start;
        if (l) o.Left = Math.Min(start.Left + dx, start.Right - minW);
        if (r) o.Right = Math.Max(start.Right + dx, start.Left + minW);
        if (t) o.Top = Math.Min(start.Top + dy, start.Bottom - minH);
        if (b) o.Bottom = Math.Max(start.Bottom + dy, start.Top + minH);
        return o;
    }

    /// <summary>ダミー枠を作って出す。作れなかった（Failed）時は false＝本物の窓をそのまま引かせる</summary>
    private bool BeginDummyDrag(Native.RECT start)
    {
        try
        {
            _dummy ??= new Input.QuickMarker(0x00, 0x67, 0xC0, CaptureBorder.DefaultPx);
            var c = CaptureBorder.Color;
            _dummy.SetStyle(c.R, c.G, c.B, CaptureBorder.AlphaByte(CaptureBorder.Alpha), CaptureBorder.Px);
            _dummy.SetAllowCapture(_cfg.Settings.AllowCaptureOverlay);
            if (!_dummy.StartExternal(start.Left, start.Top, start.Right - start.Left, start.Bottom - start.Top))
                return false;
        }
        catch (Exception ex) { AppLog.Error("capture dummy start failed", ex); return false; }
        _dummyGen++;
        _dummyDrag = true;
        _dragStartRect = start;
        DragRect = start;
        // 本物の窓の縁を消す（膜は残す。引いている間の見た目はダミー枠だけ）。
        // 修正J66: 知らせる（DummyDragChanged）より先に消す。知らせた先で解答欄が隠れてマウスの掴みが外れると、
        // その場で EndDummyDrag が走って縁を戻すので、後から消すと縁が消えたまま残った
        // 修正J87: すぐには消さず、ダミー枠が画面に出てから消す（ダミー枠は別のスレッドが次のフレームで描くので、
        // すぐ消すと縁がどこにも無い瞬間ができ、ちらついて見えた）。先に終わっていたら消さない（世代で見分ける）
        _savedBorderBrush = _frame.BorderBrush;
        HideRealBorderWhenDummyShown(_dummyGen);
        try { DummyDragChanged?.Invoke(true); }
        catch (Exception ex) { AppLog.Error("capture dummy drag hide", ex); }
        return _dummyDrag;   // 知らせた先で終わっていたら false（呼び側は本物の窓のまま）
    }

    /// <summary>修正J87: ダミー枠が画面に出たのを待ってから（最大4フレーム）、本物の窓の縁を消す。
    /// 2枚が重なる1〜2フレームは縁が濃く見えるが、縁が消える瞬間（ちらつき）よりは目立たない（H10 の終わりと同じ考え）</summary>
    private void HideRealBorderWhenDummyShown(int gen)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            for (int i = 0; i < 4 && _dummy is { IsShown: false, Failed: false } && gen == _dummyGen && _dummyDrag; i++)
            {
                try { Native.DwmFlush(); } catch { break; }
            }
            if (gen == _dummyGen && _dummyDrag) _frame.BorderBrush = Brushes.Transparent;
        }));
    }

    /// <summary>引き終わり・取り消し・フォーカス喪失の後片付け。commit なら本物の窓を最後の矩形へ1回で置く</summary>
    private void EndDummyDrag(IntPtr hwnd, bool commit)
    {
        if (!_dummyDrag) return;
        _dummyDrag = false;
        var last = DragRect ?? _dragStartRect;
        DragRect = null;
        if (commit)
        {
            // 位置と大きさを1回で（Moving が最後の矩形でバー・解答欄を合わせる。保存もここから）
            Native.SetWindowPos(hwnd, IntPtr.Zero, last.Left, last.Top, last.Right - last.Left, last.Bottom - last.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        else
        {
            // 取り消し: 本物の窓は動いていないので、バー・解答欄を元の位置へ戻すだけ
            try { if (Native.GetWindowRect(hwnd, out var cur)) Moving?.Invoke(cur); }
            catch (Exception ex) { AppLog.Error("capture dummy cancel", ex); }
        }
        _frame.BorderBrush = _savedBorderBrush ?? CaptureBorder.Brush();
        _savedBorderBrush = null;
        try { DummyDragChanged?.Invoke(false); }   // H9: 新しい（元の）矩形が決まってから解答欄を出し直す
        catch (Exception ex) { AppLog.Error("capture dummy drag show", ex); }
        // 本物の縁が描かれてからダミーを消す（一瞬縁が無い状態を作らない）。H10: 2枚が重なっている間は
        // 半透明どうしが重なって濃く（太く）見えるので、重なりを最短（WPF の描画が画面へ出る1〜2フレーム）にする。
        // Render 優先度で本物の縁が積まれた後に DwmFlush を2回待ってから Stop（以前は60ms待って約4フレーム重なっていた）
        int gen = _dummyGen;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try { Native.DwmFlush(); Native.DwmFlush(); } catch { }
            if (gen == _dummyGen && !_dummyDrag) _dummy?.Stop();
        }));
    }

    // ---------------- J1: 中を掴んで動かすのは自前で（Windows のスナップ・最大化のアニメーションを出さない） ----------------
    // 前は WM_NCHITTEST の HTCAPTION を OS の移動に任せていたので、画面の上端・左右の端へ持っていくと
    // Windows のスナップ（最大化・半分）の枠やアニメーションが出た。バーのハンドル（ChromeBar.Handle_*）と同じく、
    // マウスを掴んで SetWindowPos で動かす。大きさは変えない。

    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;

    private bool _ownMove;
    private Native.RECT _ownMoveStart;
    private Native.POINT _ownMoveCursor;

    /// <summary>自前で動かしている最中（中を掴んでいる間）</summary>
    internal bool OwnMoving => _ownMove;

    /// <summary>掴み始めの左上・カーソルから、いまのカーソルでの左上（物理px）</summary>
    internal static (int X, int Y) OwnMovePos(Native.RECT start, Native.POINT startCursor, Native.POINT cursor)
        => (start.Left + cursor.X - startCursor.X, start.Top + cursor.Y - startCursor.Y);

    private bool BeginOwnMove(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out _ownMoveStart)) return false;
        _ownMoveCursor = CursorProvider();
        _ownMove = true;
        if (!CaptureMouse()) { _ownMove = false; return false; }
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
        BeginMoveDrag();   // 修正J65: 動かす間はダミー枠（作れなければ本物の窓をそのまま動かす）
        return true;
    }

    /// <summary>いまのカーソル位置へ窓を動かす（大きさはそのまま）。修正J65: 動かすのはダミー枠で、本物の窓は離した時に1回だけ。
    /// バー・解答欄は Moving で付いてくる</summary>
    internal void OwnMoveStep()
    {
        if (!_ownMove) return;
        var (x, y) = OwnMovePos(_ownMoveStart, _ownMoveCursor, CursorProvider());
        MoveDragTo(x, y);
    }

    internal void EndOwnMove()
    {
        if (!_ownMove) return;
        _ownMove = false;
        System.Windows.Input.Mouse.OverrideCursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        EndMoveDrag();
    }

    // ---------------- 修正J65: 窓ごと動かす間はダミー枠（中を掴む・バーのハンドル） ----------------
    // 大きい窓を毎フレーム SetWindowPos で動かすと 1 回 6〜10ms かかってカクつく（BLOCKED の E5 項目4）。
    // 動かす間はダミー枠だけを動かし、窓の中の解答欄は隠す（大きさを変える時と同じ）。離した時に本物の窓を1回で置く。

    /// <summary>窓ごと動かし始める。修正J87: ダミー枠はまだ出さず、実際に動いた時（<see cref="MoveDragTo"/>）に出す
    /// （掴んだだけ・クリックしただけで縁が消えたり窓の中の解答欄が隠れたりして、ちらつかないように）。準備できたら true</summary>
    internal bool BeginMoveDrag()
    {
        if (_dummyDrag || _moveArmed) return true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return false;
        _moveArmed = true;
        _moveArmRect = r;
        return true;
    }

    /// <summary>修正J87: BeginMoveDrag の後、まだ動いていない（ダミー枠を出していない）</summary>
    private bool _moveArmed;
    private Native.RECT _moveArmRect;
    /// <summary>修正J87: ダミー枠を出す途中（知らせの中）で EndMoveDrag が呼ばれた</summary>
    private bool _moveEndedInStart;

    /// <summary>窓の左上を (x, y)（物理px）へ。大きさは動かし始めのまま</summary>
    internal void MoveDragTo(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!_dummyDrag && _moveArmed)
        {
            // 修正J87: まだ掴んだ場所から動いていなければ何もしない。動いたら、ここでダミー枠を出す
            if (x == _moveArmRect.Left && y == _moveArmRect.Top) return;
            _moveArmed = false;
            _moveEndedInStart = false;
            bool started = BeginDummyDrag(_moveArmRect);   // 出せなければ下で本物の窓を動かす
            // 動かし始めの知らせの中で終わった（J66: マウスの掴みが外れた）なら、本物の窓も動かさない
            if (!started && _moveEndedInStart) return;
        }
        if (!_dummyDrag)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }
        var s = _dragStartRect;
        var rc = new Native.RECT { Left = x, Top = y, Right = x + (s.Right - s.Left), Bottom = y + (s.Bottom - s.Top) };
        if (DragRect is { } cur && cur.Left == rc.Left && cur.Top == rc.Top && cur.Right == rc.Right && cur.Bottom == rc.Bottom) return;
        DragRect = rc;
        try
        {
            _dummy!.Place(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            Moving?.Invoke(rc);   // バー・解答欄はダミー枠に付いてくる
        }
        catch (Exception ex) { AppLog.Error("move drag", ex); }
    }

    /// <summary>窓ごと動かし終わり。commit なら本物の窓を最後の場所へ1回で置く（取り消しなら元の場所のまま）</summary>
    internal void EndMoveDrag(bool commit = true)
    {
        _moveArmed = false;   // 修正J87: 動かずに離した（クリックだけ）なら何も起きない
        _moveEndedInStart = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        EndDummyDrag(hwnd, commit);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_ownMove) { OwnMoveStep(); e.Handled = true; }
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_ownMove) { OwnMoveStep(); EndOwnMove(); e.Handled = true; }
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndOwnMove();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCLBUTTONDOWN && wParam.ToInt64() == Native.HTCAPTION)
        {
            // 中を掴んだ（窓の中・解答欄の余白ドラッグ E1 からの SendMessage も同じ）。OS の移動には渡さない
            if (BeginOwnMove(hwnd)) { handled = true; return IntPtr.Zero; }
            return IntPtr.Zero;
        }
        if (msg == WM_NCLBUTTONDBLCLK)
        {
            // 中・縁のダブルクリックで最大化・縦いっぱいにしない
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MAXIMIZE)
        {
            handled = true;   // Win+↑ なども最大化させない
            return IntPtr.Zero;
        }
        if (msg == Native.WM_ENTERSIZEMOVE)
        {
            _inSizeMove = true;
            _dragCancelled = false;
            _moveStartCursor = CursorProvider();
            Native.GetWindowRect(hwnd, out _moveStartRect);
            return IntPtr.Zero;
        }
        if (msg == Native.WM_CANCELMODE)
        {
            if (_dummyDrag) _dragCancelled = true;
            return IntPtr.Zero;
        }
        if (msg == Native.WM_EXITSIZEMOVE)
        {
            _inSizeMove = false;
            if (_dummyDrag) EndDummyDrag(hwnd, commit: !_dragCancelled && !EscapeDown());
            else if (_sizeHidden)
            {
                // H11: 右・下・右下（OS のサイズ変更）の終わり。窓の矩形が決まってから解答欄を出し直す
                _sizeHidden = false;
                try { DummyDragChanged?.Invoke(false); }
                catch (Exception ex) { AppLog.Error("capture size drag show", ex); }
            }
            return IntPtr.Zero;
        }
        if (msg == Native.WM_SIZING && _inSizeMove)
        {
            int edge = (int)wParam.ToInt64();
            if (!_dummyDrag)
            {
                // 修正J87: 縁を掴んだだけ（大きさがまだ変わらない）の間はダミー枠を出さない（クリックだけでちらつかない）
                var p0 = CursorProvider();
                var rc0 = DragRectFor(_moveStartRect, edge, p0.X - _moveStartCursor.X, p0.Y - _moveStartCursor.Y, MinW, MinH);
                if (rc0.Left == _moveStartRect.Left && rc0.Top == _moveStartRect.Top &&
                    rc0.Right == _moveStartRect.Right && rc0.Bottom == _moveStartRect.Bottom)
                {
                    if (Native.GetWindowRect(hwnd, out var cur0)) Marshal.StructureToPtr(cur0, lParam, false);
                    handled = true;
                    return new IntPtr(1);
                }
                // 修正J65: 右・下・右下もダミー枠で引く（大きい窓を OS のサイズ変更で毎回 SetWindowPos するとカクつく）
                if (!BeginDummyDrag(_moveStartRect))
                {
                    // ダミー枠が作れない時だけ今まで通り OS のサイズ変更。H11: ただし窓の中の解答欄は隠す
                    // （WM_SIZING が来た＝移動ではなくサイズ変更。移動では来ないので隠れない）
                    if (!_sizeHidden)
                    {
                        _sizeHidden = true;
                        try { DummyDragChanged?.Invoke(true); }
                        catch (Exception ex) { AppLog.Error("capture size drag hide", ex); }
                    }
                    return IntPtr.Zero;
                }
                _dragEdge = edge;
            }
            try
            {
                var p = CursorProvider();
                var rc = DragRectFor(_dragStartRect, _dragEdge, p.X - _moveStartCursor.X, p.Y - _moveStartCursor.Y, MinW, MinH);
                DragRect = rc;
                _dummy!.Place(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
                Moving?.Invoke(rc);   // バー・解答欄はダミー枠に付いてくる
                // 本物の窓は動かさない（OS へは「いまの矩形」を返す）
                if (Native.GetWindowRect(hwnd, out var cur))
                    Marshal.StructureToPtr(cur, lParam, false);
                handled = true;
                return new IntPtr(1);
            }
            catch (Exception ex) { AppLog.Error("sizing", ex); }
            return IntPtr.Zero;
        }
        if (msg == Native.WM_WINDOWPOSCHANGING && Moving is not null && !_dummyDrag)
        {
            try
            {
                var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if (Native.GetWindowRect(hwnd, out var cur) && Native.TargetRect(wp, cur) is { } tr)
                    Moving(tr);
            }
            catch (Exception ex) { AppLog.Error("windowposchanging", ex); }
            return IntPtr.Zero;   // handled にしない（通常の処理を続ける）
        }
        if (msg == Native.WM_GETMINMAXINFO)
        {
            // 最小 30×30 物理px。MinWidth(DIP)だとDPIで値が変わるので
            // マウスドラッグ限界を直接物理pxで決める（DECISIONS）
            var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = new Native.POINT { X = MinW, Y = MinH };
            mmi.ptMaxTrackSize = new Native.POINT { X = 0x7FFF, Y = 0x7FFF };
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == Native.WM_NCHITTEST)
        {
            // バーの「透過」がONの間は中も縁も素通し（Python版 tw_band 246行付近と同じ）
            long ex = Native.GetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TRANSPARENT) != 0)
            {
                handled = true;
                return new IntPtr(Native.HTTRANSPARENT);
            }
            long p = lParam.ToInt64();
            int mx = (short)(p & 0xFFFF);
            int my = (short)((p >> 16) & 0xFFFF);
            if (Native.GetWindowRect(hwnd, out var r))
            {
                int hit = HitTest(mx - r.Left, my - r.Top,
                                  r.Right - r.Left, r.Bottom - r.Top, HitBand);
                if (hit == 0)
                    // 中はどこを掴んでも窓が動く（A2）。膜を塗ったのでクリックは届く。
                    // 解答欄が出ている時だけ、下の窓（解答欄）へ返す
                    hit = InteriorHit(InteriorBlocked?.Invoke() == true);
                handled = true;
                return new IntPtr(hit);
            }
            return IntPtr.Zero;
        }
        if (msg == Native.WM_SETCURSOR)
        {
            // lParamの下16ビットはヒットテストの結果。中（HTCAPTION）は四方向矢印、
            // 縁はそれぞれのリサイズ矢印（Python版 tw_band CURSORS と同じ形）
            IntPtr cur = CursorForHit((int)(lParam.ToInt64() & 0xFFFF));
            if (cur != IntPtr.Zero)
            {
                Native.SetCursor(cur);
                handled = true;
                return new IntPtr(1);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>ヒットテストの結果に応じるカーソル（標準カーソルをIDで引く）</summary>
    internal static IntPtr CursorForHit(int ht)
    {
        int id = CursorIdForHit(ht);
        return id == 0 ? IntPtr.Zero : Native.LoadCursor(IntPtr.Zero, new IntPtr(id));
    }

    /// <summary>ヒットテストの結果 → 標準カーソルID（0は形を変えない）</summary>
    internal static int CursorIdForHit(int ht) => ht switch
    {
        Native.HTCAPTION => Native.IDC_SIZEALL,
        Native.HTLEFT or Native.HTRIGHT => Native.IDC_SIZEWE,
        Native.HTTOP or Native.HTBOTTOM => Native.IDC_SIZENS,
        Native.HTTOPLEFT or Native.HTBOTTOMRIGHT => Native.IDC_SIZENWSE,
        Native.HTTOPRIGHT or Native.HTBOTTOMLEFT => Native.IDC_SIZENESW,
        _ => 0,
    };

    /// <summary>
    /// 掴みなし域（HitTest が 0 を返した所）の扱い。膜（BandAlpha）を塗ったので
    /// 中は掴んで移動（HTCAPTION）。解答欄が出ている間は素通しへ戻し、余白ドラッグ（E1）に任せる
    /// （Python版 tw_band 338行の overlay_visible 相当）
    /// </summary>
    internal static int InteriorHit(bool panelShowing)
        => panelShowing ? Native.HTTRANSPARENT : Native.HTCAPTION;

    /// <summary>
    /// 窓の左上基準の物理px座標を縁判定へ。四辺と四隅はリサイズ（上端も HTTOP、E3）、
    /// 中は 0（掴みなし域）。呼び手側で中は HTCAPTION へ
    /// 置き換える（下の WndProc）。隅は端より優先
    /// </summary>
    internal static int HitTest(int x, int y, int w, int h, int band)
    {
        bool l = x < band;
        bool rt = x >= w - band;
        bool t = y < band;
        bool b = y >= h - band;
        if (t && l) return Native.HTTOPLEFT;
        if (t && rt) return Native.HTTOPRIGHT;
        if (b && l) return Native.HTBOTTOMLEFT;
        if (b && rt) return Native.HTBOTTOMRIGHT;
        if (l) return Native.HTLEFT;
        if (rt) return Native.HTRIGHT;
        if (b) return Native.HTBOTTOM;
        if (t) return Native.HTTOP;
        return 0;
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// capture_x/y/width/height（物理px）へ書き出して保存。Python版
    /// tw_drag._save_capture_size と同じガード: 壊れた大きさ（最小未満）は
    /// 保存しない。隠している間の座標は本当の置き場所ではないので位置だけ触らない
    /// </summary>
    internal void SaveGeometryNow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left;
        int intH = r.Bottom - r.Top;
        if (w < MinW || intH < MinH) return;

        var upd = new Dictionary<string, int>
        {
            ["capture_width"] = w,
            ["capture_height"] = intH,
        };
        bool hidden = WindowState != WindowState.Normal || !IsVisible;
        if (!hidden)
        {
            upd["capture_x"] = r.Left;
            upd["capture_y"] = r.Top;
        }
        bool changed = false;
        foreach (var (key, val) in upd)
        {
            if (_cfg.Settings.ReadNode(key)?.ToJsonString() != val.ToString(System.Globalization.CultureInfo.InvariantCulture)) { changed = true; break; }
        }
        if (!changed) return;
        foreach (var (key, val) in upd)
            _cfg.Settings.WriteNode(key, val);
        _cfg.Save();
    }

    /// <summary>
    /// 同じ判定をHWNDだけで行う版。ぶん投げのワーカーは窓そのものへ触れない（触ると
    /// 「別のスレッドが所有」エラーになる）ので、こっちを呼ぶ
    /// </summary>
    public static bool TryGetCaptureAreaFor(IntPtr hwnd, out Native.RECT inner)
    {
        inner = default;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return false;
        // 描く縁は ApplyBorder で物理px固定（既定4px）なので、削る幅も同じ物理px（DPIで掛けない）
        int b = CaptureBorder.Px;
        inner = new Native.RECT
        {
            Left = r.Left + b,
            Top = r.Top + b,
            Right = r.Right - b,
            Bottom = r.Bottom - b,
        };
        return inner.Right > inner.Left && inner.Bottom > inner.Top;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _saveTimer.Stop();
        SaveGeometryNow();   // 閉じる時に今日の位置・大きさをもらい忘れない
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _dummy?.Dispose(); } catch { }
        try { _setStatus("キャプチャ窓を閉じました"); } catch { /* 終了済み窓への報告は捨ててよい */ }
    }
}

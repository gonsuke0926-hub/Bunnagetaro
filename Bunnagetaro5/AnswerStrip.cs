using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using Bunnagetaro5.Views;

namespace Bunnagetaro5;

/// <summary>
/// 解答欄（outside）。キャプチャ窓の外側（バーの下、バーが無い時は窓の下。窓の上へは回らない）に
/// 付く帯。strip_width×strip_height（既定520×140）で、左端はいつもバーの左端に揃う
/// （strip_offset_x は読み捨て）。端をドラッグしてリサイズでき、離した瞬間の大きさが
/// 設定に保存される。
/// リサイズは OS標準（WS_THICKFRAME + WM_NCHITTEST で HT* を返す）。
/// 最小 260×100 物理pxは WM_GETMINMAXINFO で決める（SetWindowPos にも効く）。
/// 保存は WM_EXITSIZEMOVE の中でやらない（クラッシュ防止のため、印だけ立てて host の250ms巡回で
/// 保存する）。
/// 撮影client矩形・バーとは絶対に重ねない（fits不成立なら黙って隠れ、
/// 収まる場所が戻れば巡回で再表示）。
/// 地色は overlay_bg を不透明で塗る（窓外なので写り込みの心配なし）。
/// DWMの枠色と見かけのタイトルバー色も同じ色に塗る（Win11で上辺だけ塗られない対策）。
/// </summary>
public class AnswerStrip : Window
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>最小寸法（物理px。result_strip.minsize(260,100) と配置側のclamp）</summary>
    internal const int MinW = 260;
    internal const int MinH = 100;

    /// <summary>画面にこれだけpx見えていれば置き続ける。
    /// 普通の窓と同じく画面の外へはみ出してよい。反対側へ跳ばさない</summary>
    internal const int StripMinVisible = 40;

    /// <summary>バーとの縦のすきあい（物理px、+4）</summary>
    private const int Gap = 4;

    private readonly IAnswerHost _host;
    private readonly ConfigStore _cfg;

    /// <summary>解答欄の中身（本文・思考・追加の問い・数式・コピー・見た目）。窓は枠だけ</summary>
    internal AnswerView View { get; }

    /// <summary>WM_EXITSIZEMOVEが立てた「大きさを保存して」の印（host巡回で消費）</summary>
    private bool _sizePending;

    /// <summary>解答欄に触れた最後の時刻（panelと共通の扱い）</summary>
    public DateTime LastInteractUtc => View.LastInteractUtc;

    internal AnswerStrip(ConfigStore cfg, IAnswerHost host)
    {
        _cfg = cfg;
        _host = host;

        // WS_THICKFRAME により枠なしだが OS のリサイズは効かせる。
        // 大きさ・位置は Win32 物理px（DIP混在禁止）
        Title = "解答欄（窓外）";
        WindowStyle = WindowStyle.None;
        // 透過する窓にする（キャプチャ窓・バー・窓の中の解答欄と同じ）。透過しない窓では WPF が WS_EX_LAYERED を
        // 付けさせず、薄れ（窓全体の濃さ）が効かずに最後に一気に消えていた。地は ApplyStyle で塗るので見た目は変わらない
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;   // リサイズは WM_NCHITTEST 側
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        View = new AnswerView(host, followRightMargin: 6, thinkMaxHeight: 60) { LayoutAllowed = false };   // 外窓は位置配置せず読む順で上から並べる
        Content = View;
        ApplyStyle();
    }

    /// <summary>自分の縁を掴んでいる間（WM_ENTERSIZEMOVE〜EXITSIZEMOVE）は追従で動かさない</summary>
    private bool _selfSizing;

    internal bool SelfSizing => _selfSizing;

    /// <summary>今表示しているか</summary>
    public bool ShownUp => IsVisible;

    /// <summary>ぶん投げ完了後の本文（テスト・診断用）</summary>
    public string ModelText => View.ModelText;

    private IntPtr OwnHwnd
    {
        get
        {
            var h = new WindowInteropHelper(this).Handle;
            return h == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(h, Native.GA_ROOT);
        }
    }

    // ---------------- 見た目 ----------------

    /// <summary>設定（外窓の色・不透明度・フォント）を帯へ反映。DWMの枠色も同じ地色へ塗る。
    /// 色と不透明度は外窓の設定（strip_bg・strip_fg・strip_alpha。既定は不透明）</summary>
    public void ApplyStyle()
    {
        var st = PanelStyle.ForStrip(_cfg);
        byte a = (byte)Math.Round(st.Alpha * 255.0);
        View.ApplyStyle(st, a, a == 255 ? (byte)255 : (byte)(a / 2));
        Color bgc = AnswerView.ColorOf(st.Bg, 0x20, 0x20, 0x20);
        // 窓そのものの地も同じ色に（既定は白。開く・閉じる時に中身を縮めると、見える範囲との 1px のすき間に白が出た）。
        // 透かす時は窓の地は透明にする（中身の地が透ける色を持つので、窓の地も塗ると二重に濃くなる）
        var bgBrush = a == 255 ? new SolidColorBrush(bgc) : new SolidColorBrush(Colors.Transparent);
        bgBrush.Freeze();
        Background = bgBrush;
        // Win11のDWMが帯の上辺だけ別色に塗る対策: 枠と見かけのタイトルバーを
        // いっしょの色へ（translator_window._strip_frame_color）
        var h = OwnHwnd;
        if (h != IntPtr.Zero)
            Native.SetFrameColors(h, ColorRef(bgc), ColorRef(bgc));
        // 描き終わる前の下地の色（HwndTarget.BackgroundColor）を地の色にするのはやめた。
        // 透過する窓にしたので下地は透明のままでよく、不透明にすると窓全体の濃さを下げても下地が残って薄れなかった
    }

    /// <summary>COLORREF（0x00BBGGRR）へ。DWMの塗り色指定はこの順だった</summary>
    private static int ColorRef(Color c) => (c.B << 16) | (c.G << 8) | c.R;

    // ---------------- 表示の中身（ストリーミング。中身は AnswerView、出し入れは WindowVisibility） ----------------

    /// <summary>本文のdelta。中身を更新して出し入れを WindowVisibility へ頼む</summary>
    public void ApplyBody(string partial)
    {
        if (partial.Length == 0 || partial == View.ModelText) return;
        View.ApplyBody(partial);
        _host.RequestStripShow();
    }

    /// <summary>区画（マスク）が渡された時、文字を待たずに帯を出す</summary>
    public void RevealForLayout() => _host.RequestStripShow();

    /// <summary>思考のdelta（最後の4000字だけ。窓内と同じ作法）。空なら欄を畳む</summary>
    public void ApplyThink(string think)
    {
        View.ApplyThink(think);
        if (think.Length > 0) _host.RequestStripShow();
    }

    /// <summary>done ok の仕上げ（_show_strip(text, keep_view=True)）</summary>
    public void ShowFinal(string text)
    {
        View.ShowFinal(text);
        _host.RequestStripShow();
    }

    /// <summary>設定で置き場所が切り替わった時、前の側の答えを写して出す</summary>
    internal void AdoptFrom(AnswerView other)
    {
        View.AdoptFrom(other);
        _host.RequestStripShow();
    }

    /// <summary>案内だけを消して帯を下ろす。本文も空へ戻す（次の答えを続きと見ない）</summary>
    public void ClearAndHide()
    {
        View.ClearBody();
        HideStrip();
    }

    /// <summary>案内の自動消し。文字は残したまま薄れて、隠れた時にまだ案内のままなら本文を空へ戻す
    /// （薄れている最中に本文が空になって見えるのを避ける。その間に答えが来ていれば消さない）</summary>
    public void ClearAndHideFade(string hintText, int ms)
    {
        HideStripFade(ms, () =>
        {
            if (View.ModelText.Trim() == hintText)
                View.ClearBody();
        });
    }

    /// <summary>薄れてから下ろす。意向は捨てる（<see cref="HideStrip"/> と同じ）</summary>
    public void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null)
        => _host.HideStripFade(ms, onHidden);

    /// <summary>ぶん投げ失敗・やめ・空応答・place切替: 帯を即下ろす。意向も捨てる</summary>
    public void HideStrip() => _host.HideStripNow();

    // ---------------- 配置と保存（WindowPlacer 経由） ----------------

    public void Sync(Native.RECT? barRect) => _host.PlaceStrip(barRect);

    internal void FlushPendingSizeSave()
    {
        if (!_sizePending) return;
        _sizePending = false;
        SaveStripSize();
    }

    internal void UpdateThinkMaxHeight()
    {
        // 思考欄の高さ: 窓の高さの3割ぶんまで（insideと同じ）
        if (ActualHeight > 40) View.ThinkMaxHeight = Math.Max(30, ActualHeight * 0.35);
    }

    /// <summary>
    /// 帯の置き場所を計算する（テストから呼べるように切り出した純粋処理）。
    /// バーがある時はバーの外側（バーが窓の上なら帯はバーの上）。バーが無い時は窓の下。
    /// 窓の上へは回り込まない。下に収まらない時は画面の中へずらすだけ（窓に重なってよい。バーには重ならない）
    /// </summary>
    internal static Native.RECT? Pick(Native.RECT wr, Native.RECT client,
                                      Native.RECT? ch, Native.RECT vd,
                                      int sw, int sh)
    {
        int vw = vd.Right - vd.Left, vh = vd.Bottom - vd.Top;
        sw = Math.Min(Math.Max(sw, MinW), Math.Max(MinW, vw));
        sh = Math.Min(Math.Max(sh, MinH), Math.Max(MinH, vh));

        int sx = ch is { } c ? c.Left : client.Left;   // いつもバーの左端に揃える（strip_offset_x は使わない）
        sx = Math.Min(Math.Max(sx, vd.Left), vd.Left + vw - sw);

        // 縦はバーの下（バーが無ければ窓の下）に固定し、画面の下へはみ出させてよい
        // （画面の中へ縦にずらしたり、窓と重ねたりしない）
        Native.RECT Below(int anchorBottom)
        {
            int sy = anchorBottom + Gap;
            return new Native.RECT { Left = sx, Top = sy, Right = sx + sw, Bottom = sy + sh };
        }

        if (ch is { } bar)
        {
            // 外の解答欄はいつもバーの下（バーと窓の間が狭ければ窓と重なってよい）
            return Below(bar.Bottom);
        }
        return Below(wr.Bottom);
    }

    private static bool Intersects(int x, int y, int w, int h, Native.RECT o) =>
        x < o.Right && x + w > o.Left && y < o.Bottom && y + h > o.Top;

    /// <summary>帯の矩形→ strip_width/strip_height と、バーからの横偏移
    /// （横偏移 strip_offset_x は保存しない）</summary>
    private void SaveStripSize()
    {
        var own = OwnHwnd;
        if (own == IntPtr.Zero || !Native.GetWindowRect(own, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < MinW || h < MinH) return;   // 壊れた寸法は保存しない
        _cfg.Settings.StripWidth = w;
        _cfg.Settings.StripHeight = h;
        // 横位置はいつもバーの左端。strip_offset_x は読み捨て（保存しない）
        _cfg.Save();
        _host.TouchPanelGen();
    }

    // ---------------- 窓の縁リサイズ ----------------

    /// <summary>hostの250ms巡回が呼ぶ: 縁を掴んで離したら保存印だけ立てる
    /// （コールバック内での重い処理によるクラッシュを避ける構成）</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // 窓全体を中身にする（縁の枠を作らない）。WS_THICKFRAME の窓には、Windows 10/11 で左・右・下に
            // 見えない縁（150% で 8px）と、中身の外に枠の帯（上 10px・左右 2px）が付き、見えている左端がバーより右へずれていた。
            // 開く・閉じる時に見える範囲を絞る（SetWindowRgn）と、その枠の帯が白く描かれた。
            // 縁をつかんで大きさを変えるのは WM_NCHITTEST が返す HT* のまま効く
            // 0 を返すと、Windows は古い中身を新しい窓の左上にそろえて写すので、左・上の縁で大きさを変える間、
            // 反対側（右・下）の中身が描き直されるまでずれて、うねうね動いて見えた。残してよい所（前と今の中身が重なる所）を
            // 画面の同じ場所のまま残す（WVR_VALIDRECTS）
            handled = true;
            var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            var newClient = p.rgrc0;              // 窓全体が中身
            var keep = KeepRect(p.rgrc2, newClient);
            if (keep is null) return IntPtr.Zero;
            p.rgrc0 = newClient;
            p.rgrc1 = keep.Value;                  // 写し先
            p.rgrc2 = keep.Value;                  // 写し元（同じ場所）
            Marshal.StructureToPtr(p, lParam, false);
            return new IntPtr(WVR_VALIDRECTS);
        }
        if (msg == WM_NCACTIVATE)
        {
            // 枠の無い窓でも、アクティブが変わると Windows が昔の形のタイトルバー・枠を描きに来て、つかんだ時にちらついた。
            // lParam = -1 で既定の処理に渡すと、枠を描き直さずにアクティブの切り替えだけをする
            handled = true;
            return DefWindowProc(hwnd, msg, wParam, new IntPtr(-1));
        }
        if (msg == Native.WM_GETMINMAXINFO)
        {
            // 最小 260×100 物理px。
            // MinWidth(DIP)だとDPIで化けるので直接物理pxで指定する
            var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
            mmi.ptMinTrackSize = new Native.POINT { X = MinW, Y = MinH };
            mmi.ptMaxTrackSize = new Native.POINT { X = 0x7FFF, Y = 0x7FFF };
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == Native.WM_NCHITTEST)
        {
            long p = lParam.ToInt64();
            int mx = (short)(p & 0xFFFF);
            int my = (short)((p >> 16) & 0xFFFF);
            if (Native.GetWindowRect(hwnd, out var r))
            {
                // 掴み帯は max(6, SM_CXSIZEFRAME)
                int edge = Math.Max(6, Native.GetSystemMetrics(Native.SM_CXSIZEFRAME));
                int hit = HitTest(mx - r.Left, my - r.Top, r.Right - r.Left, r.Bottom - r.Top, edge);
                if (hit != 0)
                {
                    handled = true;
                    return new IntPtr(hit);
                }
            }
            return IntPtr.Zero;   // 中は通常扱い
        }
        if (msg == Native.WM_ENTERSIZEMOVE)
        {
            _selfSizing = true;
            return IntPtr.Zero;
        }
        if (msg == Native.WM_EXITSIZEMOVE)
        {
            _selfSizing = false;
            // ここでは保存しない。印だけ立てて、
            // 実際の保存はhost巡回の Sync 側でやる
            _sizePending = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private const int WM_NCACTIVATE = 0x0086;
    private const int WVR_VALIDRECTS = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public Native.RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>大きさが変わる時に、古い中身をそのまま残してよい所（前と今の中身の重なり。画面の同じ場所）。
    /// 重なりが無ければ null</summary>
    internal static Native.RECT? KeepRect(Native.RECT oldClient, Native.RECT newClient)
    {
        int l = Math.Max(oldClient.Left, newClient.Left), t = Math.Max(oldClient.Top, newClient.Top);
        int r = Math.Min(oldClient.Right, newClient.Right), b = Math.Min(oldClient.Bottom, newClient.Bottom);
        if (l >= r || t >= b) return null;
        return new Native.RECT { Left = l, Top = t, Right = r, Bottom = b };
    }

    /// <summary>窓の左上基準の物理px座標を縁判定へ。右下の角→右・下の辺→中（0）。
    /// 帯はバーが位置を決めるので HTCAPTION（移動）は無い。
    /// 大きさを変えられるのは右・下・右下だけ。左端はいつもバーの左端、
    /// 上端はいつもバーのすぐ下に戻されるうえ、左・上の縁で引く間は反対側がうねうね動くため</summary>
    internal static int HitTest(int x, int y, int w, int h, int band)
    {
        bool rt = x >= w - band;
        bool b = y >= h - band;
        if (b && rt) return Native.HTBOTTOMRIGHT;
        if (rt) return Native.HTRIGHT;
        if (b) return Native.HTBOTTOM;
        return 0;
    }

    /// <summary>配信・録画に映す設定を反映する</summary>
    internal void ApplyCaptureAffinity()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero && !Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("解答欄（窓外）の SetWindowDisplayAffinity 失敗");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        // OS標準の縁リサイズを効かせる
        // WM_NCCALCSIZE を受けられるよう、枠を作り直す（SWP_FRAMECHANGED）より先にフックを付ける
        HwndSource.FromHwnd(h)?.AddHook(WndProc);
        long st = Native.GetWindowLongPtrW(h, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtrW(h, Native.GWL_STYLE, new IntPtr(st | Native.WS_THICKFRAME));
        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        Native.NoTransitions(h);
        // 自分の窓は作成時に撮影へ写らない指定を付ける（配信・録画に映す ON なら WDA_NONE）
        if (!Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("解答欄（窓外）の SetWindowDisplayAffinity 失敗");
        // 枠・見かけのタイトルバーを地色へ（ApplyStyleがHWND生成前に呼ばれるのでここで再度）
        var st0 = PanelStyle.From(_cfg);
        if (ColorUtil.ToBrush(st0.Bg) is { } br)
        {
            Native.SetFrameColors(h, ColorRef(br.Color), ColorRef(br.Color));
        }
    }
}

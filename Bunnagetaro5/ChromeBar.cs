using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;

namespace Bunnagetaro5;

/// <summary>
/// バー（chrome / 7.7）。キャプチャ窓に添える高さ36pxほどの横帯。
/// 左: 移動ハンドル・ぶん投げる・くっつける / 右: 自動トリガ・ぶん投げ方・
/// 透過・プリセット・閉じる。2行目にステータス（Subtle 12、長い … ）。
///
/// 位置計算はWin32物理pxに統一（Tk版 tw_place._win_phys と同じ趣旨。
/// WPFのDIPと混在させると125%環境で1.25倍ずれて窓に寄らない、の実測対策）。
/// 収まる場所が無ければバーは隠れる（Python版の「重ね配置は絶対しない」）。
/// </summary>
public class ChromeBar : Window
{
    /// <summary>バーの角丸（7.7）</summary>
    private const int CornerRad = 6;

    /// <summary>出し終わりの透明度（7.7）。開く・閉じる・クイックの動きの基準</summary>
    internal const double RestOpacity = 0.92;

    /// <summary>おまけの帯なので Alt+Tab に出さない。WS_EX_TOOLWINDOW</summary>
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly IBarActions _host;
    private readonly CaptureWindow _cap;
    private readonly ConfigStore _cfg;

    // ---- 各部品 ----
    private readonly Canvas _handle = new();
    private readonly Line _hl1 = new(), _hl2 = new();
    private Button _translateBtn = new();
    private TextBlock _translateLabel = new();
    private ToggleButton _linkTgl = new(), _autoTgl = new(), _passTgl = new();
    private ComboBox _presetCb = new();
    private TextBlock _statusText = new();
    private StatusFitBox _statusBox = new();

    /// <summary>修正J18: 2行目のステータスの入れ物。バーの大きさを決める時は BaseWidth（48文字ぶん）だけを
    /// 求め、並べる時は渡された幅（＝バーの幅）いっぱいで文字を測る。長い文はその幅で … になる。
    /// 修正J18b: 測る幅と並べる幅を同じにする。前は測る時 48 文字ぶん・並べる時バーの幅で中身を測り直していたので、
    /// 48 文字ぶんより長い文の間は中身の大きさが毎回食い違い、並べ直しが止まらなかった（1秒に約1800回。
    /// 画面のスレッドが塞がり、長い一言が出ている12秒の間、窓がかくかくした）</summary>
    internal sealed class StatusFitBox : Decorator
    {
        public double BaseWidth { get; set; }

        /// <summary>前に並べた幅（まだなら 0）。測る時もこの幅で中身を測る</summary>
        private double _arrangedWidth;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return new Size(BaseWidth, 0);
            double w = _arrangedWidth > 0 ? _arrangedWidth : BaseWidth;
            Child.Measure(new Size(Math.Min(w, constraint.Width), constraint.Height));
            return new Size(BaseWidth, Child.DesiredSize.Height);
        }

        /// <summary>テスト用: 並べた回数（修正J18b）</summary>
        internal int ArrangeCount { get; private set; }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            ArrangeCount++;
            if (Child is null) return arrangeSize;
            // 幅が変わった時だけ測り直す（この1回で中身の大きさが変わっても、次に測る時は同じ幅なので落ち着く）
            if (Math.Abs(arrangeSize.Width - _arrangedWidth) > 0.5)
            {
                _arrangedWidth = arrangeSize.Width;
                Child.Measure(arrangeSize);
            }
            Child.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }

    // ---- 状態（Python版の _chrome_* 変数に対応） ----
    private bool _linked = true;
    private bool _passthru;
    private bool _filling;             // 塗り直し中に発火したハンドラを止める
    private (int X, int Y)? _barPos;   // _chrome_pos（バー左上・物理px）
    private (int X, int Y)? _offset;   // _chrome_offset（バー−窓の左上差・物理px）
    private string _curPreset = "";    // コンボの直前選択（拒否時の差し戻し用）
    private DragState? _drag;

    private sealed record DragState(
        int BarX, int BarY, int BarW, int BarH,
        int CapX, int CapY,
        int CurX, int CurY,
        bool Group);

    internal ChromeBar(ConfigStore cfg, IBarActions host, CaptureWindow cap)
    {
        _cfg = cfg;
        _host = host;
        _cap = cap;

        // Tkの overrideredirect 相当: 枠なし・透明地・タスクバーに出さない
        Title = "バー";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = RestOpacity;   // 7.7 の指定（Python版 alpha 0.82 より指示書を優先）

        _linked = _cfg.Settings.ChromeLinked;
        _passthru = _cfg.Settings.ChromePassthru;
        int? ox = _cfg.Settings.ChromeOffsetX;
        int? oy = _cfg.Settings.ChromeOffsetY;
        if (ox is not null && oy is not null) _offset = (ox.Value, oy.Value);

        BuildLayout();
    }

    /// <summary>バー自身のルートHWND（Tkの _win_phys と同じ GetAncestor(GA_ROOT) 済み）</summary>
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

    private static object? Res(string key) => Application.Current?.TryFindResource(key);

    private static Brush BrushOf(string key) =>
        (Brush)(Application.Current?.TryFindResource(key) ?? Brushes.Gray);

    private static TextBlock BarLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>文字幅（DIP）。ボタンやコンボの「○文字ぶん」の幅決め用</summary>
    private static double TextW(string s, double size)
    {
        var ff = Application.Current?.TryFindResource("UiFont") as FontFamily
                 ?? new FontFamily("Yu Gothic UI");
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private void BuildLayout()
    {
        var root = new Border
        {
            Background = BrushOf("BarBg"),
            CornerRadius = new CornerRadius(CornerRad),
            Padding = new Thickness(6, 2, 6, 1),
            SnapsToDevicePixels = true,
        };

        // 1行目: 左の塊と右寄せの塊を並べる（Tkの pack LEFT/RIGHT と同じ発想）
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 移動ハンドル（2本線）。ここを掴んで引く（tw_drag のハンドル）
        _handle.Width = 14;
        _handle.Height = 20;
        _handle.Background = Brushes.Transparent;   // からでも掴めるように当たりだけ置く
        _handle.Cursor = Cursors.SizeAll;
        foreach (var (ln, x) in new[] { (_hl1, 4.0), (_hl2, 9.0) })
        {
            ln.X1 = ln.X2 = x;
            ln.Y1 = 3;
            ln.Y2 = 17;
            ln.Stroke = BrushOf("Subtle");
            ln.StrokeThickness = 2;
            _handle.Children.Add(ln);
        }
        _handle.MouseEnter += (_, _) => { _hl1.Stroke = _hl2.Stroke = BrushOf("Text"); };
        _handle.MouseLeave += (_, _) => { _hl1.Stroke = _hl2.Stroke = BrushOf("Subtle"); };
        _handle.MouseLeftButtonDown += Handle_Down;
        _handle.MouseMove += Handle_Move;
        _handle.MouseLeftButtonUp += Handle_Up;
        _handle.LostMouseCapture += Handle_Up;
        left.Children.Add(_handle);

        // ぶん投げる（主ボタン）。ぶん投げ中は「やめる」。文字が変わっても幅は動かさない
        _translateLabel = BarLabel("ぶん投げる");
        _translateBtn = new Button
        {
            Style = (Style)Res("PrimaryButton")!,
            Content = _translateLabel,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _translateBtn.Width = Math.Max(TextW("ぶん投げる", 13), TextW("やめる", 13)) + 18;
        _translateBtn.Click += Translate_Click;
        left.Children.Add(_translateBtn);

        // くっつける（トグル）
        _linkTgl = MakeToggle("くっつける", _linked);
        _linkTgl.Checked += (_, _) => LinkChanged();
        _linkTgl.Unchecked += (_, _) => LinkChanged();
        left.Children.Add(_linkTgl);

        // ---- 右寄せ（Tkの pack RIGHT と同じ並び。視覚的に左→右は 自動トリガ…閉じる） ----
        _autoTgl = MakeToggle("自動トリガ", _cfg.Settings.Mode == "auto");
        _autoTgl.Checked += (_, _) => { if (!_filling) _host.BarAutoTrig(true); };
        _autoTgl.Unchecked += (_, _) => { if (!_filling) _host.BarAutoTrig(false); };
        right.Children.Add(_autoTgl);

        _passTgl = MakeToggle("透過", _passthru);
        _passTgl.Checked += (_, _) => PassthruChanged(true);
        _passTgl.Unchecked += (_, _) => PassthruChanged(false);
        right.Children.Add(_passTgl);

        // プリセット（選んだ瞬間に切り替え・12文字ぶん）
        _presetCb = new ComboBox
        {
            Width = 12 * TextW("0", 13) + 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
        };
        _presetCb.SelectionChanged += Preset_Changed;
        right.Children.Add(_presetCb);

        var closeBtn = new Button
        {
            Content = BarLabel("閉じる"),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.Width = 6 * TextW("0", 13) + 12;
        closeBtn.Click += (_, _) => _host.CloseCaptureAll();
        right.Children.Add(closeBtn);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        rowGrid.Children.Add(left);
        rowGrid.Children.Add(right);

        // 2行目: ステータス（Subtle 12。48文字ぶん幅で … 切り捨て）。
        // 幅固定でバー自体が伸び縮みしないようにする（Tkの label width=48 と同じ狙い）
        _statusText = new TextBlock
        {
            Text = "待機中",
            FontSize = 12,
            Foreground = BrushOf("Subtle"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
        };
        // 修正J18: 幅は固定にせず、バーの幅いっぱいまで使う（48文字ぶんの固定では、全角の字が
        // 約24字で切れ、1行目のボタンの幅があっても途中で … になっていた）。
        // バーの大きさを決めるのは今まで通り 1行目と「48文字ぶん」で、ステータスの長さではバーは伸びない
        _statusBox = new StatusFitBox { BaseWidth = 48 * TextW("0", 12), Child = _statusText };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(rowGrid, 0);
        Grid.SetRow(_statusBox, 1);
        grid.Children.Add(rowGrid);
        grid.Children.Add(_statusBox);

        root.Child = grid;
        Content = root;

        RefreshPresetCombo();
    }

    private ToggleButton MakeToggle(string text, bool isChecked) => new()
    {
        Style = (Style)Res("ToolToggle")!,
        Content = BarLabel(text),
        IsChecked = isChecked,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---------------- 配置（tw_place._position_chrome） ----------------

    /// <summary>バーを窓の外側へ即再配置。収まる場所が無ければ隠す（置き場所・表示は WindowPlacer / WindowVisibility が決める）</summary>
    public void PositionChrome() => PositionChrome(null);

    /// <summary>target が非 null なら「これから行く矩形」で配置を計算する</summary>
    public void PositionChrome(Native.RECT? target) => _host.PlaceBar(target);

    internal (int X, int Y)? PickPosition(Native.RECT wr, Native.RECT client, Native.RECT br)
    {
        (int X, int Y)? dragPos = _drag is not null && !_drag.Group ? (br.Left, br.Top) : null;
        return PredictPos(wr, client, br.Right - br.Left, br.Bottom - br.Top,
            _barPos, _offset, _linked, Native.VirtualDesktop(), dragPos);
    }

    internal void RememberPos(int x, int y)
    {
        if (_linked || _barPos is null) _barPos = (x, y);
    }

    /// <summary>純関数版（テスト用）: 窓の矩形 → バーの左上</summary>
    internal static (int X, int Y)? PredictPos(Native.RECT wr, Native.RECT client, int bw, int bh,
        (int X, int Y)? barPos, (int X, int Y)? offset, bool linked, Native.RECT vd,
        (int X, int Y)? dragPos)
        => ChromePlace.Pick(wr.Left, wr.Top, wr.Right, wr.Bottom, client,
                            bw, bh, barPos, offset, linked, vd, dragPos);

    internal static Native.RECT RectAt(int x, int y, int w, int h)
        => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };

    /// <summary>バーの実窓の物理矩形。見えていなければ null（帯の配置は
    /// 「バーが無い時」の分岐へ落ちる。tw_place の chrome 非表示扱いと同じ）</summary>
    internal Native.RECT? BarRectNow()
    {
        if (!IsVisible) return null;
        var b = RootHwnd;
        if (b == IntPtr.Zero || !Native.IsWindow(b)) return null;
        return Native.GetWindowRect(b, out var r) ? r : (Native.RECT?)null;
    }

    // ---------------- ハンドル・ドラッグ（tw_drag） ----------------

    private void Handle_Down(object sender, MouseButtonEventArgs e)
    {
        var bar = RootHwnd;
        var cap = CapRootHwnd;
        if (bar == IntPtr.Zero || cap == IntPtr.Zero || !Native.IsWindow(cap)) return;
        if (!Native.GetWindowRect(bar, out var br) || !Native.GetWindowRect(cap, out var wr)) return;
        if (!Native.GetCursorPos(out var p)) return;

        // 連結ONかつ透過OFFのときだけ窓ごと動く（locked は 7.8 でUI廃止＝常に false）
        bool group = _linked && !_passthru;
        _drag = new DragState(br.Left, br.Top, br.Right - br.Left, br.Bottom - br.Top,
            wr.Left, wr.Top, p.X, p.Y, group);
        _handle.CaptureMouse();
        if (group) _cap.BeginMoveDrag();   // 修正J65: 窓ごと動かす間はダミー枠（離した時に本物の窓を1回で置く）
        e.Handled = true;
    }

    private void Handle_Move(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d) return;
        if (!Native.GetCursorPos(out var p)) return;
        var cap = CapRootHwnd;
        if (cap == IntPtr.Zero || !Native.IsWindow(cap)) return;

        if (d.Group)
        {
            // 窓ごと動かす。修正J65: 動くのはダミー枠で、バーは Moving→PositionChrome でついてくる
            _cap.MoveDragTo(d.CapX + p.X - d.CurX, d.CapY + p.Y - d.CurY);
        }
        else
        {
            // バー単独: 収まる場所のときだけ動かす（tw_drag._move_toolbar_one と同じ）
            var client = Native.ClientRectScreen(cap);
            if (client is null) return;
            int nx = d.BarX + p.X - d.CurX, ny = d.BarY + p.Y - d.CurY;
            if (ChromePlace.Fits(nx, ny, d.BarW, d.BarH, Native.VirtualDesktop(), client.Value))
            {
                _barPos = (nx, ny);
                // F3: バーが動く直前に外の解答欄も同じフレームで置く（D1 と同じ。巡回を待たない）
                _host.BarMovingTo(RectAt(nx, ny, d.BarW, d.BarH));
                Native.SetWindowPos(RootHwnd, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
        }
    }

    private void Handle_Up(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        if (_handle.IsMouseCaptured) _handle.ReleaseMouseCapture();
        if (d.Group) _cap.EndMoveDrag();   // 修正J65: 本物の窓を最後の場所へ1回で置いてから、差を保存する
        SyncOffsetAfterMove();
    }

    /// <summary>ドラッグ終了: バーと窓の左上の差を chrome_offset_x/y に保存（tw_place._sync_chrome_offset_after_move）</summary>
    private void SyncOffsetAfterMove()
    {
        var bar = RootHwnd;
        var cap = CapRootHwnd;
        if (bar == IntPtr.Zero || cap == IntPtr.Zero || !Native.IsWindow(cap)) return;
        if (!Native.GetWindowRect(bar, out var br) || !Native.GetWindowRect(cap, out var wr)) return;
        _barPos = (br.Left, br.Top);
        _offset = (br.Left - wr.Left, br.Top - wr.Top);
        _cfg.Settings.ChromeOffsetX = _offset.Value.X;
        _cfg.Settings.ChromeOffsetY = _offset.Value.Y;
        _cfg.Save();
        PositionChrome();
    }

    // ---------------- 各部品の振る舞い ----------------

    private void Translate_Click(object sender, RoutedEventArgs e) => _host.BarTranslatePressed();

    /// <summary>ぶん投げ中は「やめる」。やめ要求中は押させない（_sync_translate_btn）</summary>
    public void SetBusy(bool busy, bool cancelling)
    {
        _translateLabel.Text = busy ? "やめる" : "ぶん投げる";
        _translateBtn.IsEnabled = !cancelling;
    }

    public void SetStatusText(string msg) => _statusText.Text = msg;

    /// <summary>
    /// くっつける ON/OFF（tw_place._chrome_linked_changed）。
    /// ONは位置記憶を捨てて窓直下へ吸着 → 現在の位置からオフセットを計算して保存。
    /// OFFは現在位置を記憶したままオフセットだけ保存（離れても元の場所を維持）
    /// </summary>
    private void LinkChanged()
    {
        if (_filling) return;
        _linked = _linkTgl.IsChecked == true;
        if (_linked)
        {
            _offset = null;
            _barPos = null;
            PositionChrome();
        }
        var bar = RootHwnd;
        var cap = CapRootHwnd;
        if (bar != IntPtr.Zero && cap != IntPtr.Zero &&
            Native.GetWindowRect(bar, out var br) && Native.GetWindowRect(cap, out var wr))
        {
            _barPos = (br.Left, br.Top);
            _offset = (br.Left - wr.Left, br.Top - wr.Top);
        }
        _cfg.Settings.ChromeLinked = _linked;
        if (_offset is null)
        {
            _cfg.SetNull("chrome_offset_x");
            _cfg.SetNull("chrome_offset_y");
        }
        else
        {
            _cfg.Settings.ChromeOffsetX = _offset.Value.X;
            _cfg.Settings.ChromeOffsetY = _offset.Value.Y;
        }
        _cfg.Save();
    }

    /// <summary>いま透過が効いているか（解答欄の素通し同期用。_passthru の読み出し）</summary>
    public bool PassthruActive => _passthru;

    /// <summary>透過（_passthru_changed）。失敗したら OFF のままに戻して保存＋状態欄に理由。
    /// 切换成功時は解答欄の素通しもいっしょに揃える（_sync_panel_passthru）</summary>
    private void PassthruChanged(bool on)
    {
        if (_filling) return;
        var cap = CapRootHwnd;
        if (cap == IntPtr.Zero || !Native.IsWindow(cap))
        {
            SetToggle(_passTgl, false);
            _passthru = false;
            _cfg.Settings.ChromePassthru = false;
            _cfg.Save();
            _host.SyncPanelPassthru(false);
            return;
        }
        if (!Native.SetClickThrough(cap, on))
        {
            AppLog.Error("set_click_through 失敗（透過の反映が確認できない）");
            SetToggle(_passTgl, false);
            _passthru = false;
            _cfg.Settings.ChromePassthru = false;
            _cfg.Save();
            _host.SetStatus("透過切替に失敗（ログ参照）");
            _host.SyncPanelPassthru(false);
            return;
        }
        _passthru = on;
        _cfg.Settings.ChromePassthru = on;
        _cfg.Save();
        _host.SyncPanelPassthru(on);
        _host.SetStatus(on
            ? "透過ON: キャプチャ窓は移動・サイズ変更できません（操作にはOFFに）"
            : "透過OFF: 窓内をドラッグして移動・訳文をドラッグして選択コピーできます");
    }

    private void SetToggle(ToggleButton t, bool v)
    {
        _filling = true;
        t.IsChecked = v;
        _filling = false;
    }

    // ---------------- プリセット（_preset_select / _preset_bar_refresh） ----------------

    private void RefreshPresetCombo()
    {
        var names = new List<string>();
        if (_cfg.Settings.Prompts is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject p)
                {
                    string s = ConfigStore.NodeToStr(p["name"]);
                    if (s.Length > 0) names.Add(s);
                }
        _filling = true;
        try
        {
            _presetCb.ItemsSource = names;
            string active = _cfg.Settings.PromptActive;
            _presetCb.SelectedItem = names.Contains(active) ? active : null;
            _curPreset = (string?)_presetCb.SelectedItem ?? "";
        }
        finally
        {
            _filling = false;
        }
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        string name = (string?)_presetCb.SelectedItem ?? "";
        if (name.Length == 0)
        {
            RefreshPresetCombo();
            return;
        }
        if (!_host.BarPresetSelect(name))
        {
            // 設定ページ側の受け入れ拒否 → コンボだけ直前の値へ戻す
            _filling = true;
            _presetCb.SelectedItem = _curPreset.Length > 0 ? _curPreset : null;
            _filling = false;
            return;
        }
        _curPreset = name;
    }

    /// <summary>設定ページ側でプリセット・ぶん投げ方・自動トリガが変わったとき、バーの表示を cfg から塗り直す</summary>
    public void SyncWidgets()
    {
        _filling = true;
        try
        {
            SetToggle(_autoTgl, _cfg.Settings.Mode == "auto");
            SetToggle(_passTgl, _passthru);
            RefreshPresetCombo();
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>修正J30: 配信・録画に映す設定を反映する</summary>
    internal void ApplyCaptureAffinity()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero && !Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("バーの SetWindowDisplayAffinity 失敗");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // おまけの帯なので Alt+Tab には出さない
        var h = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        // 開閉のシステムアニメを出さない＋自分の窓は撮影に写らない指定（7.2。
        // バーが撮る範囲に重なることがあっても写らない）
        Native.NoTransitions(h);
        if (!Native.ApplyCaptureAffinity(h, _cfg.Settings.AllowCaptureOverlay))
            AppLog.Error("バーの SetWindowDisplayAffinity 失敗");

        // 保存済み透過が ON なら開いた瞬間に素通しを適用（失敗時は OFF に戻して保存）
        if (_passthru)
        {
            var cap = CapRootHwnd;
            if (cap == IntPtr.Zero || !Native.SetClickThrough(cap, true))
            {
                AppLog.Error("set_click_through 失敗（開いたときの透過適用）");
                SetToggle(_passTgl, false);
                _passthru = false;
                _cfg.Settings.ChromePassthru = false;
                _cfg.Save();
            }
        }
    }

    /// <summary>Show 前のちらつき防止: 窓の下側（DIP概算）に置いてから表示する。正確な配置は PositionChrome</summary>
    public void PlaceNearCapture()
    {
        var cap = CapRootHwnd;
        if (cap == IntPtr.Zero || !Native.GetWindowRect(cap, out var wr)) return;
        int dpi = (int)Native.GetDpiForWindow(cap);
        double k = dpi <= 0 ? 1.0 : 96.0 / dpi;
        Left = (wr.Left + 8) * k;
        Top = (wr.Bottom + 2) * k;
    }
}

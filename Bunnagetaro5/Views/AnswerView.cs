using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;
using Bunnagetaro5.Session;
using WpfMath.Controls;

namespace Bunnagetaro5.Views;

/// <summary>
/// 解答欄の中身。解答欄(中)の AnswerPanel と帯(外)の AnswerStrip が
/// 同じ部品を載せる。本文（式込み）・思考欄・追加の問いの欄・ストリーミングの反映・コピー・見た目の適用。
/// 窓としての仕事（位置合わせ・リサイズ・透過・影・出し入れ）は持たない。
/// 本文は読み取り専用の RichTextBox: 選択・Ctrl+C・右クリック「選択部分をコピー」「全文をコピー」。
/// 本文には LaTeX 式を混ぜられる: $...$ は文中、$$..$$・\[..\] は独立行で中央。
/// ストリーミング中は文字で出し、閉じ記号で式に差し替え。表示できない式は元の文字のまま。
/// 思考欄は本文の下。思考が無い間は畳み、有る間だけ出る。
/// </summary>
public partial class AnswerView : UserControl
{
    private readonly IAnswerHost _host;

    private readonly Border _root = new();
    private readonly Border _thinkSep = new();
    private readonly ScrollViewer _thinkSv = new();
    private readonly TextBlock _think = new();
    private readonly RichTextBox _body = new();

    // 追加の問いの1行欄。枠の子なので窓の出し入れ・透明度・色へついてくる
    private readonly TextBlock _followLbl = new() { Text = "たずねる" };
    private readonly CheckBox _followChk = new() { Content = "画像も", IsChecked = false };
    private readonly TextBox _followEnt = new();
    private readonly DockPanel _followRow = new();

    /// <summary>今表示している本文の全文（差分追加の判定に使う）</summary>
    private string _model = "";

    /// <summary>答えの文（コピー・追加の問いの対手）。画面の _model は、吸い込み中の
    /// 書き起こし文の残りを付けた分だけ長いことがある</summary>
    private string _target = "";
    private readonly List<SuckText.Ghost> _ghosts = new();
    private readonly SuckText.RateMeter _rate = new();
    private bool _tablesAfterSuck;

    /// <summary>今の文書に載っている式込みの区切れ目（末尾だけの伸びなら
    /// 組み直さずAppendTextで速くする）と、式が1つでも載っているかの印</summary>
    private List<MathSeg> _segs = new();
    private bool _docMath;

    // 段落間・行間とストリーミング追記時の末尾改行状態
    private const string DisplayMathTag = "dp";
    private double _paraSpacingPx = PanelStyle.DefaultSize * PanelStyle.DefaultParaSpacing;
    private double _displayMathMarginPx = PanelStyle.DefaultSize * PanelStyle.DisplayMathParaSpacing;
    private bool _needNewPara;
    private bool _tailSingleNewline;

    /// <summary>出し終わった答えの表を WPF の表に組み替えた文書か（流している間・組み直しの後は false）</summary>
    private bool _tablesShown;
    private List<AnswerPart>? _tableParts;

    /// <summary>思考欄の自動追従。上へスクロールで止まる</summary>
    private bool _thinkFollow = true;

    /// <summary>解答欄に触れた最後の時刻（巡回が「自分の表示」と見なす材料）</summary>
    public DateTime LastInteractUtc { get; private set; } = DateTime.MinValue;

    /// <summary>「たずねる」欄の右の余白。窓内パネルの右端には細バーが重なるので広め（18）、帯は6</summary>
    public double FollowRightMargin { get; }

    /// <summary>本文（窓側が余白ドラッグなどの入力を足すため）</summary>
    internal RichTextBox Body => _body;

    /// <summary>テスト用: 「たずねる」欄の書きかけ（隠しても消えないことの確認）</summary>
    internal string AskDraftForTest() => _followEnt.Text;
    internal void SetAskDraftForTest(string t) => _followEnt.Text = t;

    /// <summary>思考欄の高さの上限（窓の高さに合わせて窓側が変える）</summary>
    public double ThinkMaxHeight
    {
        get => _thinkSv.MaxHeight;
        set => _thinkSv.MaxHeight = value;
    }

    internal AnswerView(IAnswerHost host, double followRightMargin, double thinkMaxHeight)
    {
        _host = host;
        FollowRightMargin = followRightMargin;
        BuildLayout();
        _thinkSv.MaxHeight = thinkMaxHeight;
    }

    // ---------------- ログ用 ----------------

    /// <summary>ログのカードの本文に使う時の入口（XAML から作る）。思考欄・たずねる欄は無く、
    /// 色はログの画面に合わせ、高さは中身に伸びる（スクロールバーは出さない）</summary>
    public AnswerView() : this(LogHost.Instance, 0, 0) { }

    /// <summary>ログのカードの本文の見た目（文字の大きさ・段落の間・行間・LaTeX のコピーの形は解答欄の設定と同じ）。
    /// LogPage が差し替える。差し替えたら使い回しの時に付け直す</summary>
    internal static PanelStyle? LogStyle { get; set; }

    /// <summary>ログのカードのコピーの結果を状態欄へ出す先（LogPage が設定）</summary>
    internal static Action<string>? LogStatus { get; set; }

    private sealed class LogHost : IAnswerHost
    {
        public static readonly LogHost Instance = new();
        public bool FollowupSend(string question, bool withImage) => false;
        public void TouchPanelGen() { }
        public void SetStatus(string msg) => LogStatus?.Invoke(msg);
        public Native.RECT? BarRectNow() => null;
        public void PlacePanel(Native.RECT? target = null) { }
        public void PlaceStrip(Native.RECT? barRect) { }
        public bool DeferPanelWhileDragging(Action again) => false;
        public void ResetPanelRun() { }
        public bool RequestPanelShow() => false;
        public bool IsPanelFadingOut() => false;
        public void HidePanelForCapture() { }
        public void HidePanelFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
        public void RequestStripShow() { }
        public void HideStripNow() { }
        public void HideStripFade(int ms = WindowMotion.PanelFadeMs, Action? onHidden = null) { }
    }

    private bool _logMode;
    private bool _layoutItemMode;
    private PanelStyle? _lastStyle;
    private PanelStyle? _logStyleApplied;
    private bool _logCopyEnabled = true;

    public static readonly DependencyProperty LogTextProperty = DependencyProperty.Register(
        nameof(LogText), typeof(string), typeof(AnswerView),
        new PropertyMetadata("", (d, e) => ((AnswerView)d).OnLogText((string?)e.NewValue ?? "")));

    /// <summary>ログのカードの本文（カードの使い回しで DataContext が変わると、ここが変わって本文が組み直される）</summary>
    public string LogText { get => (string)GetValue(LogTextProperty); set => SetValue(LogTextProperty, value); }

    public static readonly DependencyProperty LogCopyEnabledProperty = DependencyProperty.Register(
        nameof(LogCopyEnabled), typeof(bool), typeof(AnswerView),
        new PropertyMetadata(true, (d, e) => ((AnswerView)d)._logCopyEnabled = (bool)e.NewValue));

    /// <summary>失敗・ぶん投げ中のカードは「答えをすべてコピー」を無効にする</summary>
    public bool LogCopyEnabled { get => (bool)GetValue(LogCopyEnabledProperty); set => SetValue(LogCopyEnabledProperty, value); }

    /// <summary>ログ用にする（XAML で最初に付ける）</summary>
    public bool LogMode
    {
        get => _logMode;
        set
        {
            if (_logMode == value || !value) return;
            _logMode = true;
            _thinkSep.Visibility = Visibility.Collapsed;
            _thinkSv.Visibility = Visibility.Collapsed;
            _followRow.Visibility = Visibility.Collapsed;
            _body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _body.Padding = new Thickness(0);
            _body.FontFamily = Application.Current?.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Yu Gothic UI");
            _body.Foreground = Application.Current?.TryFindResource("Text") as Brush ?? Brushes.Black;
            BuildLogCopyMenu();
            if (LogText.Length > 0) OnLogText(LogText);   // 先に本文が入っていた時
        }
    }

    /// <summary>ログ用の本文の右クリック。選んだ所があれば「コピー」（選んだ所だけ）と「答えをすべてコピー」。
    /// 式の上は式の画像に付けた「LaTeXでコピー」「画像でコピー」が出る</summary>
    private void BuildLogCopyMenu()
    {
        var miSel = new MenuItem { Header = "コピー" };
        miSel.Click += (_, _) => CopyAnswer(SelectedCopy());
        var miAll = new MenuItem { Header = "答えをすべてコピー" };
        miAll.Click += (_, _) => CopyAnswer(FullCopy());
        var menu = new ContextMenu();
        menu.Items.Add(miSel);
        menu.Items.Add(miAll);
        menu.Opened += (_, _) =>
        {
            miSel.Visibility = SelectedText().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            miAll.IsEnabled = _logCopyEnabled;
        };
        _body.ContextMenu = menu;
    }

    private void ApplyLogStyle(PanelStyle st)
    {
        _logStyleApplied = st;
        _copyForm = MathText.ParseCopyForm(st.LatexCopy);
        _convertOtslTable = st.ConvertOtslTable;
        _convertHtmlTable = st.ConvertHtmlTable;
        _convertLatexMath = st.ConvertLatexMath;
        _convertMarkdown = st.ConvertMarkdown;
        _body.FontSize = st.FontSize;
        _paraSpacingPx = st.ParaMarginPx;
        _displayMathMarginPx = st.DisplayMathMarginPx;
        _body.Document.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        _body.Document.LineHeight = st.EffectiveLineHeight;
    }

    private void OnLogText(string text)
    {
        if (!_logMode) return;
        if (LogStyle is { } st && !ReferenceEquals(st, _logStyleApplied)) ApplyLogStyle(st);
        _model = text;
        _target = text;
        ReplaceDoc(text);   // 前のカードの式・順番待ちはここで外れる（NewDocGen）
        ApplyTables();      // ログは最初から出来上がった答えなので最初から表で出す
    }

    /// <summary>本文を組み直すたびに新しい世代にする。前の世代の式の順番待ちは取り消す
    /// （使い回されたカード・画面から外れたカードの式を描く順番待ちに残さない）</summary>
    private CancellationTokenSource _docCts = new();

    private void NewDocGen()
    {
        _tailMark = null;   // 組み直したら前の文書の印は使えない
        _mdRendered = null;
        _mathImgs.Clear();
        var old = _docCts;
        _docCts = new CancellationTokenSource();
        try { old.Cancel(); } catch { }
        old.Dispose();
    }

    /// <summary>細スクロールバー。Track地は背景と同化、棒は文字色から作る。
    /// 既定の17px幅のバーの代わりにこのテンプレを解答欄の中のバー全部へ適用する</summary>
    internal const string SlimSbXaml =
        "<Style TargetType='ScrollBar' " +
        "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
        "xmlns:l='clr-namespace:Bunnagetaro5;assembly=Bunnagetaro'>" +
        "<Setter Property='Background' Value='{DynamicResource PanelSbBg}'/>" +
        // 既定テーマの ScrollBar は MinWidth/MinHeight=17 を持つので、8 にするには 0 へ戻す必要がある
        "<Setter Property='MinWidth' Value='0'/>" +
        "<Setter Property='MinHeight' Value='0'/>" +
        // 縦は幅だけ・横は高さだけ8（両方固定すると縦バーが8px四方になって右端の中央に潰れて出る）
        "<Setter Property='Template'>" +
        "<Setter.Value>" +
        "<ControlTemplate TargetType='ScrollBar'>" +
        "<Grid Background='{TemplateBinding Background}'>" +
        "<Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'>" +
        // Track は棒の最小長を約8px（システム値）に固定し Thumb.MinHeight を見ない。長い答えで棒が点になるので、
        // 棒が最小長(24px)に満たない分だけ ViewportSize を水増しして棒を伸ばす（位置の対応は Track が同じ値で計算するので崩れない）
        "<Track.ViewportSize>" +
        "<MultiBinding Converter='{x:Static l:ThumbMin.Instance}'>" +
        "<Binding Path='ViewportSize' RelativeSource='{RelativeSource TemplatedParent}'/>" +
        "<Binding Path='Minimum' RelativeSource='{RelativeSource TemplatedParent}'/>" +
        "<Binding Path='Maximum' RelativeSource='{RelativeSource TemplatedParent}'/>" +
        "<Binding Path='Orientation' RelativeSource='{RelativeSource TemplatedParent}'/>" +
        "<Binding Path='ActualHeight' RelativeSource='{RelativeSource Self}'/>" +
        "<Binding Path='ActualWidth' RelativeSource='{RelativeSource Self}'/>" +
        "</MultiBinding>" +
        "</Track.ViewportSize>" +
        "<Track.DecreaseRepeatButton>" +
        "<RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Opacity='0' Focusable='False'/>" +
        "</Track.DecreaseRepeatButton>" +
        "<Track.Thumb>" +
        "<Thumb>" +
        "<Thumb.Template>" +
        "<ControlTemplate TargetType='Thumb'>" +
        "<Border x:Name='Tb' Background='{DynamicResource PanelSbThumb}' CornerRadius='3'/>" +
        "</ControlTemplate>" +
        "</Thumb.Template>" +
        "</Thumb>" +
        "</Track.Thumb>" +
        "<Track.IncreaseRepeatButton>" +
        "<RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Opacity='0' Focusable='False'/>" +
        "</Track.IncreaseRepeatButton>" +
        "</Track>" +
        "</Grid>" +
        "</ControlTemplate>" +
        "</Setter.Value>" +
        "</Setter>" +
        "<Style.Triggers>" +
        "<Trigger Property='Orientation' Value='Vertical'>" +
        "<Setter Property='Width' Value='8'/>" +
        "</Trigger>" +
        "<Trigger Property='Orientation' Value='Horizontal'>" +
        "<Setter Property='Height' Value='8'/>" +
        "</Trigger>" +
        "</Style.Triggers>" +
        "</Style>";

    private void BuildLayout()
    {
        // 細バーのテンプレ（解答欄の中の ScrollBar 全部に効く暗黙スタイル）
        Resources[typeof(ScrollBar)] = XamlReader.Parse(SlimSbXaml);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 本文: 読み取り専用でも選択・コピーは効く（文字の上＝xterm/IBeam、
        // 何もない場所＝SizeAllで窓ドラッグ）。折り返しは char 相当（Wrap）
        _body.IsReadOnly = true;
        _body.IsDocumentEnabled = false;
        // 式の組み直しでBlocks.Clear()する度、Undo履歴が式の中身をXAMLへ
        // シリアライズしに行くが、WpfMathのFormulaControlを持つと壊れた
        // エラーテンプレート（AdornedElementPlaceholder）を無理に作って例外に
        // なる（v2.1.0の作りの問題）。読み取り専用でUndoは一切使わないので0で切る
        _body.UndoLimit = 0;
        _body.BorderThickness = new Thickness(0);
        _body.Background = Brushes.Transparent;
        _body.Padding = new Thickness(8, 6, 8, 6);
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _body.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _body.IsTabStop = false;
        _body.PreviewMouseLeftButtonDown += (_, _) => LastInteractUtc = DateTime.UtcNow;   // 触れた印（余白ドラッグは窓側が足す）
        // Ctrl+ホイールで富文が拡大縮小する既定は止める（ホイールはスクロールに使う）
        _body.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) e.Handled = true;
        };
        // 式が載っているときネイティブのCtrl+Cは選択範囲をXAML/RTFでも書こうと
        // して式の部分で同じシリアライズの地雷を踏む。素の文字としてコピーする
        _body.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && (_docMath || _tablesShown))
            {
                e.Handled = true;
                CopyAnswer(SelectedCopy());
            }
        };
        // 欄の ActualWidth は縦スクロールバーが出ても変わらず、SizeChanged の時点では見える幅がまだ古い。
        // ScrollViewer の見える幅の変化で付け直す
        _body.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnBodyScrollChanged));
        BuildCopyMenu();
        Grid.SetRow(_body, 0);
        grid.Children.Add(_body);
        BuildLayoutCanvas(grid);   // 位置の反映の描き板（本文の上に重ねる）

        // 思考欄: 本文の下。無い間は畳む。それ自身の内でスクロールし、
        // 上へスクロールすると自動追従が止まる
        _thinkSep.Height = 1;
        _thinkSep.Margin = new Thickness(8, 0, 8, 0);
        _thinkSep.Visibility = Visibility.Collapsed;
        Grid.SetRow(_thinkSep, 1);
        grid.Children.Add(_thinkSep);

        _think.TextWrapping = TextWrapping.Wrap;
        _think.Padding = new Thickness(8, 2, 8, 6);
        _thinkSv.Content = _think;
        _thinkSv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _thinkSv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _thinkSv.MaxHeight = 90;   // 窓側が高さに合わせて変える（ThinkMaxHeight）
        _thinkSv.Visibility = Visibility.Collapsed;
        _thinkSv.PreviewMouseWheel += (_, e) => { if (e.Delta > 0) _thinkFollow = false; };
        _thinkSv.ScrollChanged += (_, e) =>
        {
            // 一番下まで来たら追従やり直し（ExtentHeightが伸びただけでは止めない）
            if (e.ExtentHeight <= e.ViewportHeight + 2 ||
                e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) <= 2.0)
                _thinkFollow = true;
        };
        Grid.SetRow(_thinkSv, 2);
        grid.Children.Add(_thinkSv);

        // 追加の問い: 枠の中の一番下。Enterで送り、Escで欄を空にする。
        // 窓内パネルの右端には細バーが重なるので右の余白は広めに取る
        BuildFollowRow();
        Grid.SetRow(_followRow, 3);
        grid.Children.Add(_followRow);

        _root.Child = grid;
        Content = _root;
    }


    /// <summary>追加の問いの1行欄を組み立てる。
    /// 左に「たずねる」、右に □「画像も」（既定OFF＝前の答えの文字だけでたずねる）、
    /// 真ん中の欄に書いた文をそのままAIへ送る。決まり文句は付かない。</summary>
    private void BuildFollowRow()
    {
        if (Application.Current?.TryFindResource("QuietCheckBox") is Style quiet) _followChk.Style = quiet;
        _followRow.Margin = new Thickness(6, 1, FollowRightMargin, 3);
        DockPanel.SetDock(_followChk, Dock.Right);
        _followChk.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(_followLbl, Dock.Left);
        _followLbl.Margin = new Thickness(0, 0, 4, 0);
        _followLbl.VerticalAlignment = VerticalAlignment.Center;
        _followEnt.BorderThickness = new Thickness(1);
        _followEnt.Padding = new Thickness(2, 1, 2, 1);
        _followRow.Children.Add(_followChk);
        _followRow.Children.Add(_followLbl);
        _followRow.Children.Add(_followEnt);

        // 欄の操作は解答欄に触れたこととして数える（自動トリガの自己発火防止の手がかり）
        _followEnt.PreviewKeyDown += Follow_Ent_KeyDown;
        // 日本語入力の未確定文字は WPF の TextBox が標準で欄の中へ出す
        // （色と字の大きさは ApplyStyle で揃える）
    }

    private void Follow_Ent_KeyDown(object sender, KeyEventArgs e)
    {
        LastInteractUtc = DateTime.UtcNow;
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            // Enter（テンキーのEnterも同じ。IME確定直後のEnterはここへ来ない）
            if (_host.FollowupSend(_followEnt.Text, _followChk.IsChecked == true))
                _followEnt.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _followEnt.Clear();
            e.Handled = true;
        }
    }

    /// <summary>右クリックメニュー。選択無しは上だけ出ない</summary>
    private void BuildCopyMenu()
    {
        var miSel = new MenuItem { Header = "選択部分をコピー (Ctrl+C)" };
        miSel.Click += (_, _) => CopyAnswer(SelectedCopy());
        var miAll = new MenuItem { Header = "全文をコピー" };
        miAll.Click += (_, _) => CopyAnswer(FullCopy());
        var menu = new ContextMenu();
        menu.Items.Add(miSel);
        menu.Items.Add(miAll);
        menu.Opened += (_, _) => miSel.IsEnabled = SelectedText().Length > 0;
        _body.ContextMenu = menu;
    }

    /// <summary>設定（色・透明度・フォント）を反映。
    /// alpha は枠と「たずねる」欄の地色の不透明度、sbAlpha は細バーの走（Track）の地色。
    /// 解答欄(中)は透けた地色（alpha=overlay_alpha、sbAlpha=その半分）、帯(外)は不透明（どちらも255）。
    /// 適用も自分の表示変化（genを足す）</summary>
    public void ApplyStyle(PanelStyle st, byte alpha, byte sbAlpha)
    {
        _lastStyle = st;
        _copyForm = MathText.ParseCopyForm(st.LatexCopy);
        bool convChanged = _convertOtslTable != st.ConvertOtslTable
            || _convertHtmlTable != st.ConvertHtmlTable
            || _convertLatexMath != st.ConvertLatexMath
            || _convertMarkdown != st.ConvertMarkdown;
        _convertOtslTable = st.ConvertOtslTable;
        _convertHtmlTable = st.ConvertHtmlTable;
        _convertLatexMath = st.ConvertLatexMath;
        _convertMarkdown = st.ConvertMarkdown;
        if (!_logMode && !_layoutItemMode)
            _followRow.Visibility = st.ShowFollowRow ? Visibility.Visible : Visibility.Collapsed;
        Color bgc = ColorOf(st.Bg, 0x20, 0x20, 0x20);
        Color fgc = ColorOf(st.Fg, 0xF5, 0xF5, 0xF5);
        Color thc = ColorOf(st.ThinkHex, 0x8A, 0x8A, 0x8A);

        _rootBg = _layoutItemMode ? Brushes.Transparent : Frozen(Color.FromArgb(alpha, bgc.R, bgc.G, bgc.B));
        _layoutTextBg = st.LayoutTextBg;
        ApplyRootBg();   // 位置の反映で「文字の所だけ地」の時は全体の地を透明に
        _body.Foreground = Frozen(fgc);
        _think.Foreground = Frozen(thc);
        // 枠の区切り: 文字色を薄めた1pxの線（TkのRIDGE枠の代わり）
        _thinkSep.Background = Frozen(Color.FromArgb(90, fgc.R, fgc.G, fgc.B));
        // 細バー: 走（Track）は地色と同化、棒は文字色から作った半透明
        Resources["PanelSbBg"] = Frozen(Color.FromArgb(sbAlpha, bgc.R, bgc.G, bgc.B));
        Resources["PanelSbThumb"] = Frozen(Color.FromArgb(170, thc.R, thc.G, thc.B));

        FontFamily ff = st.FontFamily.Length > 0
            ? new FontFamily(st.FontFamily)
            : Application.Current?.TryFindResource("UiFont") as FontFamily
              ?? new FontFamily("Yu Gothic UI");
        _body.FontFamily = ff;
        _think.FontFamily = ff;
        _body.FontSize = st.FontSize;
        _think.FontSize = st.FontSize;
        _paraSpacingPx = st.ParaMarginPx;
        _displayMathMarginPx = st.DisplayMathMarginPx;
        double lh = st.EffectiveLineHeight;
        _body.Document.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        _body.Document.LineHeight = lh;
        _think.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        _think.LineHeight = lh;
        ApplyParagraphMargins();
        ApplyLayoutItemsStyle();   // 区画ごとの RichTextBox へも色・字体・変換設定を反映
        PlaceLayout();   // 字体・大きさ・色が変わったら位置の反映も置き直す
        // 追加の問い: 本文よりひとまわり小さく、
        // ラベルと□は思考欄と同じ薄色、打つ文字は本文と同じ色・地色は枠と同じ
        double small = Math.Max(8, st.FontSize - 2);
        _followLbl.FontFamily = ff;
        _followChk.FontFamily = ff;
        _followEnt.FontFamily = ff;
        _followLbl.FontSize = small;
        _followChk.FontSize = small;
        _followEnt.FontSize = small;
        _followLbl.Foreground = Frozen(thc);
        // 「画像も」: 「たずねる」と同じ薄色。枠・チェックは QuietCheckBox が Foreground で描く
        _followChk.Foreground = Frozen(thc);
        _followEnt.Background = Frozen(Color.FromArgb(alpha, bgc.R, bgc.G, bgc.B));
        _followEnt.Foreground = Frozen(fgc);
        _followEnt.CaretBrush = Frozen(fgc);
        _followEnt.BorderBrush = Frozen(thc);
        // 文字色・大きさ・変換設定が変わったら式/表を再構築。
        // 見た目に効く値が前と同じなら組み直さない。プリセットの切り替えなどで、何も変わらないのに
        // 解答欄・外の解答欄とも何度も呼ばれ、式の多い答えを毎回組み直して画面のスレッドが10秒ほど塞がっていた
        string key = string.Join("|", ff.Source, st.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fgc.ToString(), bgc.ToString(), alpha.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lh.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _paraSpacingPx.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _displayMathMarginPx.ToString(System.Globalization.CultureInfo.InvariantCulture), _copyForm.ToString());
        bool lookChanged = key != _lastStyleKey;
        _lastStyleKey = key;
        if (convChanged && _model.Length > 0)
        {
            ReplaceDoc(_model);
            ApplyTables();
        }
        else if (lookChanged && (_docMath || _tablesShown || (_convertMarkdown && _model.Length > 0)))
        {
            RebuildCurrent();
            StyleRebuilds++;
        }
        _host.TouchPanelGen();
    }

    /// <summary>前に式・表を組み直した時の見た目の値（同じなら組み直さない）</summary>
    private string? _lastStyleKey;

    /// <summary>テスト用: ApplyStyle で式・表を組み直した回数</summary>
    internal int StyleRebuilds { get; private set; }

    internal static Color ColorOf(string hex, byte r, byte g, byte b) =>
        ColorUtil.ToBrush(hex) is { } br ? br.Color : Color.FromRgb(r, g, b);

    internal static SolidColorBrush Frozen(Color c)
    {
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }

    /// <summary>新しいぶん投げの開始。本文・思考を空にして上から</summary>
    public void ResetRun()
    {
        StopSuck();
        _target = "";
        _rate.Reset();
        _model = "";
        ReplaceDoc("");
        SetLayout(null);   // 新しいぶん投げは位置の反映なしから
        _think.Text = "";
        _thinkSv.Visibility = Visibility.Collapsed;
        _thinkSep.Visibility = Visibility.Collapsed;
        _thinkFollow = true;
        if (IsVisible) _body.ScrollToVerticalOffset(0);
    }

    /// <summary>本文のdelta（これまでの全文）。前回の続きなら末尾だけ足す、
    /// 食い違ったら見ている位置を保ったまま全文置き換</summary>
    public void ApplyBody(string partial)
    {
        if (partial.Length == 0 || partial == _target) return;
        SetTarget(partial);
        _host.TouchPanelGen();
    }

    /// <summary>答えの文を今の文にする。行の後ろの英文が外れた所は、画面では吸い込ませながら消す</summary>
    private void SetTarget(string next)
    {
        long now = WindowMotion.Clock();
        if (!_logMode && !_layoutItemMode && IsVisible)
        {
            _rate.Observe(_target, next, now);
            // 前の文の続き（末尾に足しただけ）なら外れた行は無いので、全文を行に分けて比べない
            var removed = StreamText.IsContinuation(_target, next)
                ? new List<(int Line, string Base, string Suffix)>() : SuckText.Removed(_target, next);
            foreach (var (line, bas, suffix) in removed)
            {
                // 吸い込み中の行がまた外れたら、残っていた分も続けて食う
                var shownLine = SuckText.Compose(_target, _ghosts, now).Split('\n');
                string tail = line < shownLine.Length && shownLine[line].StartsWith(bas, StringComparison.Ordinal)
                    ? shownLine[line][bas.Length..] : suffix;
                _ghosts.RemoveAll(g => g.Line == line);
                if (tail.Length == 0) continue;
                var (stepMs, stepChars) = SuckText.EffectiveStep(_rate.Step, tail.Length);
                _ghosts.Add(new SuckText.Ghost
                {
                    Line = line, Base = bas, Suffix = tail, StartMs = now,
                    StepMs = stepMs, StepChars = stepChars,
                });
            }
        }
        else _ghosts.Clear();
        _target = next;
        RenderShown(now);
        if (_ghosts.Count > 0)
        {
            long end = 0;
            foreach (var g in _ghosts) end = Math.Max(end, g.EndMs);
            WindowMotion.Run(_ghosts, (int)Math.Max(1, end - now), t => t, _ => RenderShown(WindowMotion.Clock()), OnSuckDone);
        }
        else WindowMotion.Cancel(_ghosts);
    }

    /// <summary>位置の反映表示中に、見えない _body（RichTextBox）の更新を省いた印。
    /// 位置の反映を外す時に _model から同期する</summary>
    private bool _bodyDirty;

    /// <summary>今の文＋吸い込み中の残りを画面に出す（見えている文字 _model だけが変わる）</summary>
    private void RenderShown(long now)
    {
        string shown = SuckText.Compose(_target, _ghosts, now);
        if (shown == _model) return;
        // 位置の反映で表示できる間は、見えない _body の全文組み直し（ReplaceDoc）を省く
        if (CanShowLayoutFor(shown))
        {
            _model = shown;
            _bodyDirty = true;
            RefreshLayout();
            return;
        }
        bool wasBottom = AtBottom();
        if (!_bodyDirty && StreamText.IsContinuation(_model, shown))
        {
            string add = StreamText.Tail(_model, shown);
            _model = shown;
            AppendBody(add);
        }
        else
        {
            double keep = _body.VerticalOffset;
            _bodyDirty = false;
            ReplaceDoc(shown);
            _model = shown;
            _body.ScrollToVerticalOffset(keep);   // 途中→同じ行
        }
        if (wasBottom) _body.ScrollToEnd();        // 一番下→一番下追従
        RefreshLayout();
    }

    private void OnSuckDone()
    {
        _ghosts.Clear();
        RenderShown(WindowMotion.Clock());
        if (_tablesAfterSuck)
        {
            _tablesAfterSuck = false;
            if (LayoutShown) ShowFinalLayoutItems();   // 吸い込み完了後に各区画の表変換等を適用
            else ApplyTables();
        }
        _host.TouchPanelGen();
    }

    /// <summary>吸い込みを打ち切って、今の文だけにする</summary>
    private void StopSuck()
    {
        WindowMotion.Cancel(_ghosts);
        _tablesAfterSuck = false;
        if (_ghosts.Count == 0) return;
        _ghosts.Clear();
        RenderShown(WindowMotion.Clock());
    }

    /// <summary>吸い込み中の行がある（テスト用）</summary>
    internal bool Sucking => _ghosts.Count > 0;
    /// <summary>画面に見えている本文（吸い込み中の残りを含む。テスト用）</summary>
    internal string ShownText => _model;

    /// <summary>思考のdelta（最後の4000字だけ出す）。空なら欄を畳む。
    /// 表示そのものは本文deltaが出すので、ここは窓を出さない</summary>
    public void ApplyThink(string think)
    {
        string tail = StreamText.ThinkTail(think);
        if (_think.Text != tail)
        {
            _think.Text = tail;
            if (_thinkFollow) _thinkSv.ScrollToEnd();
        }
        var vis = tail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_thinkSv.Visibility != vis)
        {
            _thinkSv.Visibility = vis;
            _thinkSep.Visibility = vis;
        }
        _host.TouchPanelGen();
    }

    /// <summary>完成の仕上げ（done ok・keep_view=True）。ストリーの続きなら末尾だけ、
    /// 違う文なら見ていた位置を保って置き換。思考欄は畳む。
    /// 書き起こし文の残りが外れる時は吸い込ませ、表の組み替えは吸い込みの後</summary>
    public void ShowFinal(string text)
    {
        if (text.Length > 0 && text != _target) SetTarget(text);
        if (_ghosts.Count > 0) _tablesAfterSuck = true;
        else if (LayoutShown) ShowFinalLayoutItems();   // 吸い込み中なら完了後、終わっていればすぐ各区画へ適用
        else ApplyTables();    // 出し終わった今だけ、答えの中の表を表に組み替える
        ApplyThink("");   // doneには思考が無いので欄を畳む
        _host.TouchPanelGen();
    }

    /// <summary>ぶん投げ完了後の本文（テスト・診断用）。吸い込み中でも答えの文</summary>
    public string ModelText => _target;

    /// <summary>思考欄の今の文字（空なら畳まれている）</summary>
    public string ThinkText => _think.Text;

    /// <summary>別の解答欄（窓に重ねる⇔外の解答欄の切替前の側）に出ていた答えをそのまま写す。
    /// 本文・思考・追加の問いの入力</summary>
    internal void AdoptFrom(AnswerView o)
    {
        SetLayout(o._layout);   // 位置の反映も写す
        if (o.ModelText.Length > 0 && o.ModelText != _target) ApplyBody(o.ModelText);
        if (o._tablesShown || o._otslHtmlShown) ApplyTables();   // 出し終わっていた答えは表（か HTML の文字）のまま写す
        ApplyThink(o.ThinkText);
        _followEnt.Text = o._followEnt.Text;
        _followChk.IsChecked = o._followChk.IsChecked;
    }

    /// <summary>本文だけを空にする（案内を下ろす時。思考欄などは触らない）</summary>
    public void ClearBody()
    {
        WindowMotion.Cancel(_ghosts);
        _ghosts.Clear();
        _tablesAfterSuck = false;
        _target = "";
        _model = "";
        _bodyDirty = false;
        ReplaceDoc("");
        SetLayout(null);
    }

    private bool AtBottom() =>
        _body.ExtentHeight <= _body.ViewportHeight ||
        _body.ExtentHeight - (_body.VerticalOffset + _body.ViewportHeight) <= 2.0;

    private void ReplaceDoc(string text)
    {
        _tablesShown = false;
        _otslHtmlShown = false;
        _tableParts = null;
        NewDocGen();
        _body.Document.Blocks.Clear();
        _needNewPara = false;
        _tailSingleNewline = false;
        _segs = SplitMath(text);
        _docMath = _segs.Exists(x => x.Kind != MathSegKind.Text);
        // 式が載った文書だけ、埋め込みUI（FormulaControl）への入力を生かす。
        // 式の右クリックメニューに必要。無い間は従来通り完全に文字扱い
        _body.IsDocumentEnabled = _docMath;
        if (_convertMarkdown)
        {
            MdRender(text, _segs);
            return;
        }
        if (!_docMath)
        {
            AppendPlainText(text);
            ApplyParagraphMargins();
            return;
        }
        BuildMathDoc();
    }

    /// <summary>ストリーの末尾足し。閉じ記号待ちの文字列が続くだけ
    /// （区切れ目の末尾Textが伸びただけ）なら文書を組み直さず追記。
    /// 閉じ記号が来て式に差し替わる段だけ一度組み直す</summary>
    private void AppendBody(string add)
    {
        if (_tablesShown)
        {
            ReplaceDoc(_model);   // 表に組み替えた後に続きが来たら文字の文書へ戻す（_model は全文）
            return;
        }
        if (_convertMarkdown)
        {
            MdRender(_model, SplitMath(_model));   // 変わった塊から後ろだけ組み直す
            return;
        }
        var ns = SplitMath(_model);
        if (TailOnlyGrew(_segs, ns))
        {
            AppendPlainText(add);
            ApplyParagraphMargins();
            _segs = ns;
            return;
        }
        // 閉じ記号が来て区切れ目が変わっても、前の方が同じなら末尾だけ組み直す
        if (_docMath && TryRebuildFromMark(ns)) return;
        NewDocGen();
        _body.Document.Blocks.Clear();
        _needNewPara = false;
        _tailSingleNewline = false;
        _segs = ns;
        _docMath = ns.Exists(x => x.Kind != MathSegKind.Text);
        _body.IsDocumentEnabled = _docMath;   // 式の右クリック入力
        if (!_docMath)
        {
            AppendPlainText(_model);
            ApplyParagraphMargins();
        }
        else BuildMathDoc();
    }

    private void AppendPlainText(string text)
    {
        var curPara = _body.Document.Blocks.LastBlock as Paragraph;
        AppendTextSegment(text, ref curPara, ref _needNewPara, ref _tailSingleNewline);
    }

    /// <summary>改行1つ（\n）は同じ段落内の LineBreak、空行（\n\n 以上）だけを段落の切れ目にする。
    /// 空の段落は作らない。ストリーミング中の書き足しでも同じ規則になる</summary>
    private void AppendTextSegment(string text, ref Paragraph? curPara, ref bool needNewPara, ref bool tailSingleNewline)
    {
        var doc = _body.Document;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                if (!needNewPara)
                {
                    if (tailSingleNewline)
                    {
                        if (curPara is not null && curPara.Inlines.LastInline is LineBreak lb)
                            curPara.Inlines.Remove(lb);
                        tailSingleNewline = false;
                        needNewPara = true;
                    }
                    else if (curPara is not null && curPara.Inlines.Count > 0)
                    {
                        curPara.Inlines.Add(new LineBreak());
                        tailSingleNewline = true;
                    }
                }
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            tailSingleNewline = false;
            if (needNewPara || curPara is null)
            {
                curPara = new Paragraph();
                doc.Blocks.Add(curPara);
                needNewPara = false;
            }
            curPara.Inlines.Add(new Run(text.Substring(start, i - start)));
        }
    }

    private void ApplyParagraphMargins()
    {
        var blocks = _body.Document.Blocks;
        for (var b = blocks.FirstBlock; b is not null; b = b.NextBlock)
        {
            if (b is Table tbl)
            {
                tbl.Margin = b.NextBlock is null ? new Thickness(0) : new Thickness(0, 0, 0, _paraSpacingPx);
                continue;
            }
            if (b is not Paragraph p) continue;
            if (p.Tag is MdTag mt)
            {
                p.Margin = MdMargin(mt, b);
                continue;
            }
            if (p.Tag is string tag && tag == DisplayMathTag)
            {
                p.Margin = new Thickness(0, _displayMathMarginPx, 0, _displayMathMarginPx);
                continue;
            }
            bool lastOrBeforeDp = b.NextBlock is null ||
                                  (b.NextBlock is Paragraph np && np.Tag is string nt && nt == DisplayMathTag);
            p.Margin = lastOrBeforeDp ? new Thickness(0) : new Thickness(0, 0, 0, _paraSpacingPx);
        }
    }

    private static bool TailOnlyGrew(List<MathSeg> oldS, List<MathSeg> newS)
    {
        if (newS.Count == 0) return false;
        if (oldS.Count == 0) return newS is [{ Kind: MathSegKind.Text }];
        if (newS.Count != oldS.Count) return false;
        for (int i = 0; i < oldS.Count - 1; i++)
            if (oldS[i] != newS[i]) return false;
        var (o, nw) = (oldS[^1], newS[^1]);
        return o.Kind == MathSegKind.Text && nw.Kind == MathSegKind.Text &&
               nw.Raw.StartsWith(o.Raw, StringComparison.Ordinal);
    }

    /// <summary>式込みの本文を組み直す。文中の式は本文の行の中へ InlineUIContainer、
    /// 独立行の式（$$..$$・\[..\]）は行を分けて中央寄せ。色・字の大きさは
    /// 解答欄に合わせる。表示できない式はRunで原文字のまま</summary>
    private void BuildMathDoc()
    {
        Paragraph? curPara = null;
        _needNewPara = false;
        _tailSingleNewline = false;
        AddSegs(_segs, ref curPara, markBase: 0);
        ApplyParagraphMargins();
    }

    /// <summary>区切れ目の列の「最後の Text の手前」（最後が式なら末尾）の文書の状態。
    /// ストリーミングで閉じ記号が来て区切れ目が変わっても、そこまでの区切れ目が同じなら、
    /// 文書を全部組み直さず、ここまで戻して残りだけ組み足す（式・文が多い長い答えで1回ごとに全部作り直していた）</summary>
    private sealed class TailMark
    {
        public int SegIndex;              // この番号の区切れ目から先を組み足す
        public Block? LastBlock;          // 印を付けた時の最後の段落（無ければ null）
        public int BlockCount;
        public int LastInlines;           // LastBlock が段落なら、その中の数
        public Paragraph? CurPara;
        public bool NeedNewPara, TailSingleNewline;
    }

    private TailMark? _tailMark;

    private void MarkTail(int segIndex, Paragraph? curPara)
    {
        var blocks = _body.Document.Blocks;
        var last = blocks.LastBlock;
        _tailMark = new TailMark
        {
            SegIndex = segIndex,
            LastBlock = last,
            BlockCount = blocks.Count,
            LastInlines = (last as Paragraph)?.Inlines.Count ?? 0,
            CurPara = curPara,
            NeedNewPara = _needNewPara,
            TailSingleNewline = _tailSingleNewline,
        };
    }

    /// <summary>印の所まで文書を戻し、ns の残りだけ組み足す。使えない時は false（呼び手が全部組み直す。
    /// 途中まで戻した後に false でも、呼び手が Blocks.Clear() から作り直すので見た目は壊れない）</summary>
    private bool TryRebuildFromMark(List<MathSeg> ns)
    {
        var m = _tailMark;
        if (m is null || m.SegIndex > _segs.Count || m.SegIndex > ns.Count) return false;
        for (int i = 0; i < m.SegIndex; i++)
            if (_segs[i] != ns[i]) return false;
        var doc = _body.Document;
        if (m.LastBlock is not null && !ReferenceEquals(m.LastBlock.Parent, doc)) return false;
        _tailMark = null;
        while (doc.Blocks.LastBlock is { } lb && !ReferenceEquals(lb, m.LastBlock)) doc.Blocks.Remove(lb);
        if (doc.Blocks.Count != m.BlockCount) return false;
        if (m.LastBlock is Paragraph p)
        {
            while (p.Inlines.Count > m.LastInlines) p.Inlines.Remove(p.Inlines.LastInline);
            // 続きの「\n」や独立行の式が、印の前の段落の末尾の改行を外していたら戻す
            if (m.TailSingleNewline && p.Inlines.Count == m.LastInlines - 1) p.Inlines.Add(new LineBreak());
            if (p.Inlines.Count != m.LastInlines) return false;
        }
        var curPara = m.CurPara;
        _needNewPara = m.NeedNewPara;
        _tailSingleNewline = m.TailSingleNewline;
        AddSegs(ns.GetRange(m.SegIndex, ns.Count - m.SegIndex), ref curPara, markBase: m.SegIndex);
        _segs = ns;
        _docMath = ns.Exists(x => x.Kind != MathSegKind.Text);
        _body.IsDocumentEnabled = _docMath;
        ApplyParagraphMargins();
        return true;
    }

    /// <summary>markBase: 0 以上なら、最後の Text の手前（最後が式なら末尾）に印を付ける（segs[0] の番号）</summary>
    private void AddSegs(List<MathSeg> segs, ref Paragraph? curPara, int markBase = -1)
    {
        var doc = _body.Document;
        for (int si = 0; si < segs.Count; si++)
        {
            var seg = segs[si];
            if (seg.Kind == MathSegKind.Text)
            {
                if (markBase >= 0 && si == segs.Count - 1) MarkTail(markBase + si, curPara);
                AppendTextSegment(seg.Raw, ref curPara, ref _needNewPara, ref _tailSingleNewline);
                continue;
            }
            Inline item;
            if (MathRenderer.Active)
            {
                item = MakeKatexInline(seg);   // 画像があれば画像、無ければ原文字で出して描き終わったら差し替え
            }
            else
            {
                var fc = MakeFormula(seg);
                if (fc is null)
                {
                    _tailSingleNewline = false;
                    if (_needNewPara || curPara is null)
                    {
                        curPara = new Paragraph();
                        doc.Blocks.Add(curPara);
                        _needNewPara = false;
                    }
                    curPara.Inlines.Add(new Run(seg.Raw));   // 表示できない式は元の文字のまま
                    continue;
                }
                item = new InlineUIContainer(fc);
            }
            if (seg.Kind == MathSegKind.Display)
            {
                if (_tailSingleNewline && curPara is not null && curPara.Inlines.LastInline is LineBreak lb)
                    curPara.Inlines.Remove(lb);
                _tailSingleNewline = false;
                _needNewPara = false;
                curPara = null;
                var dp = new Paragraph { TextAlignment = TextAlignment.Center, Tag = DisplayMathTag };
                dp.Inlines.Add(item);
                doc.Blocks.Add(dp);
            }
            else
            {
                _tailSingleNewline = false;
                if (_needNewPara || curPara is null)
                {
                    curPara = new Paragraph();
                    doc.Blocks.Add(curPara);
                    _needNewPara = false;
                }
                curPara.Inlines.Add(item);
                if (MathRenderer.Active)
                {
                    // 文中の式の直後に、行の下を広げるつっかえ棒（描き終わる前は高さ0。差し替え時に合わせる）
                    curPara.Inlines.Add(MakeMathSpacer());
                    if (item is InlineUIContainer { Child: SnapImage sim } && _natSizes.TryGetValue(sim, out var n)) ApplyFit(sim, n);
                }
            }
        }
        if (markBase >= 0 && (segs.Count == 0 || segs[^1].Kind != MathSegKind.Text))
            MarkTail(markBase + segs.Count, curPara);
    }

}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>
/// 設定ページ（指示書6.4）。見出し行は「← 設定」（ログへ戻る）。
/// 節: AI接続・プリセット/生成・答えの表示・自動トリガ・クイックぶん投げ・ホットキー・バーの使い方・記録。
/// 値は変えたらその場でsettingsへ書き、保存は300ms間引く（6.4）。
/// </summary>
public partial class SettingsPage : UserControl, Bunnagetaro5.Input.IBindFields, Bunnagetaro5.Session.IModelListView, Bunnagetaro5.Session.IBarSettingsView
{
    private readonly Action _showLog;
    private readonly ConfigStore _cfg;
    private readonly Action<string> _setStatus;

    // ---- AI接続 ----
    private SettingRow _connRow = null!;
    private ComboBox _urlBox = null!;
    private Button _testBtn = null!;
    private Button _fetchBtn = null!;
    private bool _fillingUrl;
    private System.Threading.CancellationTokenSource? _connCts;

    // ---- キャプチャ窓の枠 ----
    private TextBox _borderColorBox = null!;
    private Border _borderColorSwatch = null!;
    private SettingRow _borderColorRow = null!;
    private Slider _borderAlphaSlider = null!;
    private TextBlock _borderAlphaLbl = null!;
    private NumCtl _borderPxNum = null!;

    // ---- 答えの表示 ----
    private RadioButton _placeInside = null!, _placeOutside = null!;
    private TextBox _bgBox = null!, _fgBox = null!;
    private Border _bgSwatch = null!, _fgSwatch = null!;
    private SettingRow _bgRow = null!, _fgRow = null!;
    private Slider _alphaSlider = null!;
    private TextBlock _alphaLbl = null!;
    private ComboBox _fontCb = null!;
    private SettingRow _fontRow = null!;
    private TextBox _sizeBox = null!;
    private SettingRow _sizeRow = null!;
    private Border _previewBg = null!;
    private TextBlock _previewText = null!;
    // 修正J101: 外窓（枠の外に出す解答欄）の色・不透明度・見本
    private TextBox _stripBgBox = null!, _stripFgBox = null!;
    private Border _stripBgSwatch = null!, _stripFgSwatch = null!;
    private SettingRow _stripBgRow = null!, _stripFgRow = null!;
    private Slider _stripAlphaSlider = null!;
    private TextBlock _stripAlphaLbl = null!;
    private Border _previewStripBg = null!;
    private TextBlock _previewStripText = null!;
    private Slider _paraSpacingSlider = null!;
    private TextBlock _paraSpacingLbl = null!;
    private Slider _lineSpacingSlider = null!;
    private TextBlock _lineSpacingLbl = null!;
    private Button _lineSpacingResetBtn = null!;
    private bool _suppressLineSpacing;

    // ---- プリセット・生成 ----
    /// <summary>使うプリセットではなく、共通の設定を編集するための入口（tw_uicfg.GLOBAL_ENTRY）。
    /// 括弧つきなので普通に付けた名前とはぶつからない。</summary>
    public const string GlobalEntry = "［グローバル設定］";

    private List<JsonObject> _pitems = new();   // prompts の作業コピー
    private int _pcur;                          // 選んでいる位置＝使っているプリセット
    private bool _editingGlobal;                // 「［グローバル設定］」の画面か
    private bool _filling;                      // 欄を programme 側で書き換えている（commit を止める）
    private bool _presetRejected;               // 欄を直すところがあり切り替えを取り消した
    private string _lastNote = "";              // Note() が最後に出した文言（バーからの拒否理由取り出し用）
    private int _selfWriteDepth;                // 設定ページ自身の書き込み中は PropertyChanged での再読込を止める
    private bool _suppressBarRefresh;           // バー側で拒否された時は RefreshFromBar を止める

    /// <summary>設定を保存した後（300ms間引き・窓を閉じる/ページを離れる時）に呼ぶ。
    /// モデル・URL・載せ方が変わっていれば載せ直す（A4。_after_run_cfg_change）</summary>
    public Action? AfterSave;

    private ComboBox _presetCb = null!;
    private Button _addBtn = null!, _delBtn = null!, _resetBtn = null!;
    private TextBox _nameBox = null!, _instrBox = null!;
    private CheckBox _useGlobalChk = null!;
    private SettingRow _rowPreset = null!, _rowUseGlobal = null!;
    private StackPanel _genBox = null!;
    private TextBlock _genHeading = null!;
    private ComboBox _modelCb = null!, _thinkCb = null!;
    private RadioButton _ocrOn = null!, _ocrOff = null!, _ocrBuiltinOnly = null!;
    private StackPanel _ocrSubBox = null!, _ocrEnginePanel = null!, _builtinSizePanel = null!, _ocrVlmPanel = null!;
    private RadioButton _ocrEngBuiltin = null!, _ocrEngVlm = null!;
    private ComboBox _builtinSizeCb = null!;
    /// <summary>修正J96: 内蔵 OCR の時「読み取った位置に並べる」（プリセットごと builtin_ocr_layout）</summary>
    private CheckBox _layoutChk = null!;
    private ComboBox _ocrVlmModelCb = null!;
    private TextBox _ocrVlmPromptBox = null!;
    private DispatcherTimer? _ocrVlmPromptTimer;
    private CheckBox _clipChk = null!;
    private readonly List<NumCtl> _genNums = new();

    /// <summary>「けす」の確認。テストではモーダルを避けるため差し替える（本番は MessageBox）。</summary>
    public Func<string, bool>? ConfirmOverride;

    // ---- 自動トリガ / クイック / ホットキー / 記録 ----
    private CheckBox _modeChk = null!, _quickOnChk = null!, _saveShotsChk = null!;
    private ComboBox _quickBtnCb = null!;
    private TextBox _quickKeyBox = null!, _hotkeyBox = null!, _snipKeyBox = null!, _shotsBox = null!;
    private TextBlock _quickKeyNotice = null!, _hotkeyNotice = null!, _snipKeyNotice = null!;
    private readonly List<NumCtl> _trigNums = new(), _quickNums = new();

    // ---- 保存の間引き（300ms） ----
    private DispatcherTimer? _saveTimer;
    private bool _savePending;

    public SettingsPage(ConfigStore cfg, Action showLog, Action<string> setStatus)
    {
        _cfg = cfg;
        _showLog = showLog;
        _setStatus = setStatus;
        InitializeComponent();
        // アイコンフォントが無い環境では「←」の文字に戻す（Python版のfallbackと同じ）
        if (!HasIconFont())
            BackButton.Content = "←";
        BuildBody();
        ApplyPreview();
        InitExpertToggle();
        _cfg.Settings.PropertyChanged += OnSettingsPropertyChanged;
        // 空白部分や別ウィンドウへ移った時も、編集中のテキストボックスからフォーカスを外して即確定する
        PreviewMouseDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase focusedTb
                && e.OriginalSource is DependencyObject src)
            {
                DependencyObject scopeRoot = focusedTb.TemplatedParent is ComboBox cb ? cb : focusedTb;
                if (!IsDescendantOf(src, scopeRoot))
                    Keyboard.ClearFocus();
            }
        };
    }

    /// <summary>AppSettings.PropertyChanged の購読を外す</summary>
    public void Detach()
    {
        _cfg.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _connCts?.Cancel();
        _connCts?.Dispose();
        _connCts = null;
    }

    private static void BindCommitOnFocusLoss(UIElement el, Action commit)
    {
        el.LostFocus += (_, _) => commit();
        el.LostKeyboardFocus += (_, _) => commit();
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? cur = node; cur is not null;)
        {
            if (ReferenceEquals(cur, ancestor)) return true;
            cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void WriteSelf(Action body)
    {
        _selfWriteDepth++;
        try { body(); }
        finally { _selfWriteDepth--; }
    }

    public void SetBarRefreshSuppressed(bool on) => _suppressBarRefresh = on;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selfWriteDepth > 0) return;
        switch (e.PropertyName)
        {
            case "":
                RefreshFromBar();
                ApplyExpertMode(false);
                break;
            case nameof(AppSettings.ExpertMode):
                ApplyExpertMode(IsLoaded);
                break;
            case nameof(AppSettings.Mode):
                SyncModeFromBar(_cfg.Settings.Mode == "auto");
                break;
            case nameof(AppSettings.PromptActive):
            case nameof(AppSettings.Prompts):
            case nameof(AppSettings.OcrSplitMode):
                if (!_suppressBarRefresh) RefreshFromBar();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _showLog();

    private static bool HasIconFont()
    {
        foreach (var f in Fonts.SystemFontFamilies)
        {
            if (f.Source == "Segoe Fluent Icons" || f.Source == "Segoe MDL2 Assets")
                return true;
        }
        return false;
    }

    // ================= 組み立て =================

    private void BuildBody()
    {
        BuildAiSection();
        BuildPresetSection();     // プリセット＋生成（1組の欄でスコープを切り替える）
        BuildCaptureBorderSection();   // キャプチャ窓の枠（E11）
        BuildAnswerSection();
        BuildLayoutParamsSection();   // 位置の反映（内蔵OCR）の細かい値（修正J100。上級者モードだけ）
        BuildTrigSection();
        BuildQuickSection();
        BuildHotkeySection();
        BuildBarHelpSection();    // バーの使い方（注釈図。☰メニューの「バーの使い方」がここへ飛ぶ）
        BuildRecordSection();
        BuildStartupSection();    // 起動と終了（修正J54。いちばん下）
    }

    // ---------------- バーの使い方（tw_uicfg の bar_help 節） ----------------

    private TextBlock _barHelpHeading = null!;
    private Image _barHelpImage = null!;

    private void BuildBarHelpSection()
    {
        _barHelpHeading = Section("使い方");
        _barHelpHeading.Name = "BarHelpHeading";
        BodyPanel.Children.Add(_barHelpHeading);
        var col = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Bunnagetaro;component/Assets/bar_help.png");
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _barHelpImage = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 780,
                HorizontalAlignment = HorizontalAlignment.Left,
                Name = "BarHelpImage",
            };
            RenderOptions.SetBitmapScalingMode(_barHelpImage, BitmapScalingMode.HighQuality);
            col.Children.Add(_barHelpImage);   // 図だけで分かるので下の説明文は置かない（人の判断 2026-11-15）
        }
        catch (Exception ex)
        {
            AppLog.Error("bar_help 読み込み失敗", ex);
            _barHelpImage = new Image { Name = "BarHelpImage" };
            col.Children.Add(new TextBlock { Text = "注釈図（bar_help.png）を読み込めませんでした" });
        }
        BodyPanel.Children.Add(MakeGroupCard(col));
    }

    /// <summary>☰メニューの「バーの使い方」: 設定ページのこの節の頭までスクロールする
    /// （tw_uicfg の cfg_barhelp_frame へ yview_moveto）</summary>
    internal void ScrollToBarHelp()
    {
        BodyScroll.UpdateLayout();
        var p = _barHelpHeading.TranslatePoint(new Point(0, 0), BodyPanel);
        BodyScroll.ScrollToVerticalOffset(Math.Max(0, p.Y - 8));
    }

    // ================= グループカード・上級者モード（I12） =================

    private readonly List<Border> _expertWraps = new();
    private bool _syncingExpert;

    private void InitExpertToggle()
    {
        _syncingExpert = true;
        try { ExpertModeChk.IsChecked = _cfg.Settings.ExpertMode; }
        finally { _syncingExpert = false; }
        ExpertModeChk.Checked += (_, _) => OnExpertToggle(true);
        ExpertModeChk.Unchecked += (_, _) => OnExpertToggle(false);
        ApplyExpertMode(false);
    }

    private void OnExpertToggle(bool on)
    {
        if (_syncingExpert) return;
        WriteSelf(() => _cfg.Settings.ExpertMode = on);
        SaveSoon();
        ApplyExpertMode(IsLoaded);
    }

    private void ApplyExpertMode(bool animate)
    {
        bool on = _cfg.Settings.ExpertMode;
        if (ExpertModeChk.IsChecked != on)
        {
            _syncingExpert = true;
            try { ExpertModeChk.IsChecked = on; }
            finally { _syncingExpert = false; }
        }
        foreach (var wrap in _expertWraps)
            SetExpertWrapState(wrap, on, animate);
    }

    private static void SetExpertWrapState(Border wrap, bool on, bool animate)
    {
        if (wrap.LayoutTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, on ? 1 : 0);
            wrap.LayoutTransform = st;
        }
        if (!animate)
        {
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            wrap.BeginAnimation(OpacityProperty, null);
            st.ScaleY = on ? 1.0 : 0.0;
            wrap.Opacity = on ? 1.0 : 0.0;
            wrap.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (on)
        {
            wrap.Visibility = Visibility.Visible;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            var opAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            wrap.BeginAnimation(OpacityProperty, opAnim);
        }
        else
        {
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
            var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            var opAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160));
            scaleAnim.Completed += (_, _) =>
            {
                if (st.ScaleY <= 0.01) wrap.Visibility = Visibility.Collapsed;
            };
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            wrap.BeginAnimation(OpacityProperty, opAnim);
        }
    }

    private Border MakeExpertWrap(FrameworkElement content, bool withTopDivider = false)
    {
        FrameworkElement inner = content;
        if (withTopDivider)
        {
            var sp = new StackPanel();
            sp.Children.Add(MakeDivider());
            sp.Children.Add(content);
            inner = sp;
        }
        bool on = _cfg.Settings.ExpertMode;
        var wrap = new Border
        {
            ClipToBounds = true,
            Child = inner,
            LayoutTransform = new ScaleTransform(1, on ? 1.0 : 0.0),
            Opacity = on ? 1.0 : 0.0,
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
        };
        _expertWraps.Add(wrap);
        return wrap;
    }

    private static Border MakeDivider() => new()
    {
        Height = 1,
        Background = SettingRow.NormalBorderBrush(),
        Margin = new Thickness(14, 0, 14, 0),
        SnapsToDevicePixels = true,
    };

    private Border MakeGroupCard(params UIElement[] items)
    {
        var card = new Border { Margin = new Thickness(0, 0, 0, 10) };
        if (Application.Current?.TryFindResource("Card") is Style st)
            card.Style = st;
        else
        {
            card.Background = Brushes.White;
            card.BorderBrush = SettingRow.NormalBorderBrush();
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(6);
        }
        card.Padding = new Thickness(0);

        var stack = new StackPanel();
        bool needDividerBeforeNext = false;
        foreach (var item in items)
        {
            if (item is SettingRow sr) sr.AsGrouped();
            bool isExpert = item is Border b && _expertWraps.Contains(b);
            if (!isExpert && needDividerBeforeNext)
                stack.Children.Add(MakeDivider());
            stack.Children.Add(item);
            // 修正J101: 欄の中の小見出しの直後には区切り線を入れない
            if (!isExpert) needDividerBeforeNext = !(item is Border hb && Equals(hb.Tag, GroupHeadingTag));
        }
        card.Child = stack;
        return card;
    }

    private TextBlock Section(string title)
    {
        var tb = new TextBlock { Text = title, Margin = new Thickness(0, 14, 0, 6) };
        if (Application.Current?.TryFindResource("SectionHeading") is Style st)
            tb.Style = st;
        else
        {
            tb.FontSize = 16;
            tb.FontWeight = FontWeights.SemiBold;
        }
        return tb;
    }

    // ================= 共通の小さな部品 =================

    private static string S(JsonNode? n) => ConfigStore.NodeToStr(n);

    private static readonly (string Val, string Lab)[] ThinkChoices =
    {
        ("auto", "モデル任せ"), ("on", "考える"), ("off", "考えない（速い）"),
    };

    private static readonly (string Lab, string Val)[] QuickButtonLabels =
    {
        ("ホイールクリック（中央）", "wheel"),
        ("マウス「戻る」ボタン", "back"),
        ("マウス「進む」ボタン", "forward"),
        ("戻る・進む 両方", "both"),
        ("使わない（OFF）", "off"),
    };

    private static string CollapseWs(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private TextBlock Subtle(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 8) };
        if (Application.Current?.TryFindResource("SubtleText") is Style st)
            tb.Style = st;
        else
        {
            tb.FontSize = 12;
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61));
        }
        return tb;
    }

    private TextBlock DangerNote(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = SettingRow.DangerBrush(),
            Margin = new Thickness(2, 4, 0, 8),
        };
    }

    /// <summary>数値欄（範囲外は保存せず Danger 縁＋「○〜○の数値」）。
    /// 書き込み先は Root（グローバル）か presets の settings（プリセット専用）。</summary>
    private sealed class NumCtl
    {
        public TextBox Box = null!;
        public SettingRow Row = null!;
        public double Lo, Hi;
        public bool IsInt;
        public string Key = "";
        public string RangeText = "";
        public double Def;                       // config に無い時の表示（既定値）
        public Func<JsonObject?> Scope = null!;  // 書き込み先（触れない状態なら null）
        public Action? AfterWrite;               // プリセット専用なら WritePresets
        public bool Invalid;
        public bool Clamp;                       // 範囲外は赤にせず範囲へ寄せて表示する

        public void Load(JsonObject src)
        {
            JsonNode? n = src.TryGetPropertyValue(Key, out var v) ? v : null;
            if (!TryGet(n, out _)) n = JsonValue.Create(Def);
            Box.Text = Fmt(n);
        }

        private string Fmt(JsonNode? n)
        {
            if (!TryGet(n, out double d)) return "";
            if (IsInt) return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            string s = d.ToString(CultureInfo.InvariantCulture);
            return s.Contains('.') ? s : s + ".0";
        }

        private static bool TryGet(JsonNode? n, out double d)
        {
            d = 0;
            if (n is not JsonValue jv) return false;
            var k = jv.GetValueKind();
            if (k is not (JsonValueKind.Number or JsonValueKind.String)) return false;
            return double.TryParse(jv.ToJsonString().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        public void Commit(SettingsPage page)
        {
            string t = Box.Text.Trim();
            if (Clamp && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double cv)
                && (cv < Lo || cv > Hi))
            {
                cv = Math.Min(Hi, Math.Max(Lo, cv));
                Box.Text = Fmt(JsonValue.Create(cv));
                t = Box.Text;
            }
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < Lo || v > Hi)
            {
                Invalid = true;
                Box.BorderBrush = SettingRow.DangerBrush();
                Row.SetDesc(RangeText + "の数値");
                return;
            }
            if (Box.BorderBrush != null && !Box.BorderBrush.Equals(SettingRow.NormalBorderBrush()))
                Box.BorderBrush = SettingRow.NormalBorderBrush();
            Row.SetDesc(RangeText);
            Invalid = false;
            JsonObject? scope = Scope();
            if (scope is null) return;
            JsonNode? cur = scope.TryGetPropertyValue(Key, out var x) ? x : null;
            if (IsInt)
            {
                long r = (long)Math.Round(v, MidpointRounding.AwayFromZero);
                if (cur is JsonValue cj && cj.GetValueKind() == JsonValueKind.Number && cj.ToString() == r.ToString(CultureInfo.InvariantCulture))
                    return;
                if (ReferenceEquals(scope, page._cfg.Root))
                    page.WriteSelf(() => page._cfg.Settings.WriteNode(Key, JsonValue.Create((int)r)));
                else
                    scope[Key] = (int)r;
            }
            else
            {
                if (TryGet(cur, out double old) && old == v) return;
                if (ReferenceEquals(scope, page._cfg.Root))
                    page.WriteSelf(() => page._cfg.Settings.WriteNode(Key, JsonValue.Create(v)));
                else
                    scope[Key] = v;
            }
            AfterWrite?.Invoke();
            page.SaveSoon();
        }
    }

    private NumCtl AddNum(Panel? into, string name, string key, double lo, double hi, bool isInt, double def,
                          Func<JsonObject?> scope, Action? afterWrite, string? boxName = null)
    {
        string range = isInt
            ? ((long)lo).ToString(CultureInfo.InvariantCulture) + "〜" + ((long)hi).ToString(CultureInfo.InvariantCulture)
            : lo.ToString(CultureInfo.InvariantCulture) + "〜" + hi.ToString(CultureInfo.InvariantCulture);
        var box = new TextBox { Width = 110 };
        if (boxName is not null) box.Name = boxName;
        var n = new NumCtl { Box = box, Key = key, Lo = lo, Hi = hi, IsInt = isInt, RangeText = range, Scope = scope, AfterWrite = afterWrite };
        var row = new SettingRow(name, range, box);
        if (boxName is not null) row.Name = boxName + "Row";
        n.Row = row;
        n.Def = def;
        var s0 = scope();
        if (s0 is not null) n.Load(s0);   // 生成の欄はShowPrompt→FillGenで埋まる
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) n.Commit(this); };
        BindCommitOnFocusLoss(box, () => n.Commit(this));
        if (into is not null) into.Children.Add(row);
        return n;
    }

    // ================= 入力まわりの即反映（A1） =================
    // クイックぶん投げのON/OFF・起動ボタン・最小四角の変更は AppSettings.PropertyChanged 経由で
    // QuickThrowController.Apply(fromUi: true) へ伝わる。ここでは登録まわりの通知文だけ消す。
    private void NotifyInputChanged() => SetBindNotice("", "");

    // ================= 保存の間引き =================

    /// <summary>settingsは変えたが書き込めていない、の状態。300msおきにまとめて書く。</summary>
    private void SaveSoon()
    {
        _savePending = true;
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                _savePending = false;
                _cfg.Save();
                AfterSave?.Invoke();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>ページを離れる時・窓を閉じる時に、間引き待ちの保存を今すぐやる。</summary>
    public void FlushPendingSave()
    {
        _saveTimer?.Stop();
        if (_savePending)
        {
            _savePending = false;
            _cfg.Save();
            AfterSave?.Invoke();
        }
    }
}

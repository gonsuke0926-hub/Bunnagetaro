using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bunnagetaro5.Services;
using System.Text.Json.Nodes;

namespace Bunnagetaro5.Pages;

/// <summary>履歴カード1枚分の表示状態（entries.jsonの条目をそのまま映す）。</summary>
public sealed class LogCardVm : INotifyPropertyChanged
{
    public JsonObject Entry { get; }
    public string Id { get; }

    private string _thumbText = "";
    private ImageSource? _thumbSource;

    public LogCardVm(JsonObject entry)
    {
        Entry = entry;
        Id = LogStore.GetStr(entry, "id") ?? "";
    }

    public bool PendingNow => Entry["pending"] is JsonNode p && p.GetValueKind() != JsonValueKind.False;
    public bool IsFailedNow => LogStore.GetStr(Entry, "status") == "failed";

    public string SourceLabel => LogStore.GetStr(Entry, "source") switch
    {
        "auto" => "自動",
        "follow" => "追加",
        _ => "手動",
    };

    public string Ts => LogStore.GetStr(Entry, "ts") ?? "";
    public string ModelOrDash => LogStore.GetStr(Entry, "model") is { Length: > 0 } m ? m : "—";

    // python版 _card_header と同じ組み立て（✗とぶん投げ中…は色を変えるため別の runs へ出す）
    public string Header => PendingNow
        ? $"{Ts}  ({SourceLabel})"
        : $"{Ts}  {ModelOrDash}  ({SourceLabel})";

    public bool IsPending => PendingNow;
    public bool IsFailed => !PendingNow && IsFailedNow;
    public string Body => PendingNow ? "ぶん投げ中…" : LogStore.GetStr(Entry, "text") ?? "";

    // 失敗・ぶん投げ中はコピー無効（python版と同じ決まり）
    public bool CopyEnabled => !PendingNow && !IsFailed;

    public string ThumbText { get => _thumbText; private set { _thumbText = value; Raise(nameof(ThumbText)); } }
    public ImageSource? ThumbSource { get => _thumbSource; private set { _thumbSource = value; Raise(nameof(ThumbSource)); } }
    public bool HasImageFile { get; set; }
    public string? ImageFile { get; set; }

    /// <summary>たたんでいる時の本文の高さ（10行分。LogPage が設定から決める）。サムネイルの高さの上限も同じ</summary>
    public double BodyHeight { get; set; } = 160;

    private bool _isExpanded;
    /// <summary>「▼ 続きを表示」で開いているか（カードが使い回されても、その答えの開き具合を覚えておく）</summary>
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); } }

    public void SetThumb(ImageSource? src, string text)
    {
        ThumbSource = src;
        ThumbText = text;
    }

    /// <summary>ぶん投げ確定後に同じカードへ訳文を流し込む。</summary>
    public void RefreshAfterFinish()
    {
        Raise(nameof(Header)); Raise(nameof(IsPending)); Raise(nameof(IsFailed));
        Raise(nameof(Body)); Raise(nameof(CopyEnabled));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class LogPage : UserControl
{
    // 台帳（LogStore.MaxEntries 件まで）を全部カードにする（仮想化で描くのは見えている分だけ）。
    // 起動中に新しいカードが足されて台帳の上限を超えたら、いちばん古いカードから外す。
    private const int LogRenderCap = LogStore.MaxEntries;
    private const int ThumbW = 180;

    private readonly LogStore _store;
    private readonly Action<string> _setStatus;

    // 表示リストは古い→新しい順（python版 pack の並びと同じで、上が古い・下が新しい）
    private readonly ObservableCollection<object> _items = new();
    private readonly HashSet<string> _displayedIds = new();

    // ListBox のテンプレートの中にあるスクロール実体（スクロール位置の読み書きに使う）
    private ScrollViewer? _sc;
    private ScrollViewer? Sc
    {
        get
        {
            if (_sc is null && FindVisual<ScrollViewer>(Lst) is { } found)
            {
                _sc = found;
                _sc.ScrollChanged += OnScrollChanged;
                _sc.PreviewMouseWheel += (_, _) => _stickUntil = 0;   // 利用者が自分で動かしたら、下への追従は止める
                HookThumbDrag(_sc);
            }
            return _sc;
        }
    }

    // スクロールバーのつまみを引いている間は、中身をつまみの動きごとに動かさない（ScrollViewer の
    // 後回しスクロール）。カードを並べ直すのが1回 数十ms かかるので、動きごとに動かすと入力が溜まり、
    // 中身がつまみから遅れてついてきていた。
    // 代わりに、つまみが動いたら「溜まった入力を片付けた後に1回だけ」中身をつまみの所へ動かす
    // （Background の順位で1つだけ積む）。60ms ごとの追いつきでは中身がつまみとずれて見えるため。
    // ホイールは通常通り（後回しになるのはつまみを引いている間だけ）。
    private System.Windows.Controls.Primitives.ScrollBar? _vbar;
    private bool _catchUpQueued;

    private void HookThumbDrag(ScrollViewer sc)
    {
        sc.IsDeferredScrollingEnabled = true;
        sc.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, e) =>
            {
                if (IsVBarThumb(sc, e.OriginalSource)) _stickUntil = 0;   // 利用者が自分で動かした
            }), handledEventsToo: true);
        sc.AddHandler(System.Windows.Controls.Primitives.Thumb.DragDeltaEvent,
            new System.Windows.Controls.Primitives.DragDeltaEventHandler((_, e) =>
            {
                if (!IsVBarThumb(sc, e.OriginalSource) || _catchUpQueued) return;
                _catchUpQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _catchUpQueued = false;
                    CatchUpToThumb();
                });
            }), handledEventsToo: true);
        // 離した所へは ScrollViewer が動かす
    }

    private bool IsVBarThumb(ScrollViewer sc, object? src)
    {
        _vbar ??= sc.Template?.FindName("PART_VerticalScrollBar", sc) as System.Windows.Controls.Primitives.ScrollBar;
        return _vbar is not null && ReferenceEquals(src, _vbar.Track?.Thumb);
    }

    private void CatchUpToThumb()
    {
        if (_sc is null || _vbar is null) return;
        if (Math.Abs(_vbar.Value - _sc.VerticalOffset) > 0.5) _sc.ScrollToVerticalOffset(_vbar.Value);
    }

    // 一番下に居る時に新しいカードが来たら、配置（サムネイル・本文の高さ）が決まっていく間も下へ寄せ続ける
    private const int StickMs = 1500;
    private long _stickUntil;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_stickUntil == 0) return;
        if (Environment.TickCount64 > _stickUntil) { _stickUntil = 0; return; }
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) _sc?.ScrollToEnd();
    }

    /// <summary>「読んでいる位置」の目印: 見えている先頭のカードと、その上端の窓内の位置</summary>
    internal readonly record struct Anchor(object Item, double Top);

    internal Anchor? CaptureAnchor()
    {
        var sc = Sc;
        if (sc is null) return null;
        for (int i = 0; i < _items.Count; i++)
        {
            if (Lst.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            double top;
            try { top = c.TranslatePoint(new Point(0, 0), sc).Y; } catch (InvalidOperationException) { continue; }
            if (top + c.ActualHeight <= 0) continue;   // もう上に出ている
            return new Anchor(_items[i], top);
        }
        return null;
    }

    /// <summary>目印のカードを、覚えた位置へ戻す。仮想化の見積りで1回では合わないので数回寄せる</summary>
    internal void RestoreAnchor(Anchor a)
    {
        var sc = Sc;
        if (sc is null) return;
        for (int pass = 0; pass < 4; pass++)
        {
            Lst.UpdateLayout();
            var c = Lst.ItemContainerGenerator.ContainerFromItem(a.Item) as FrameworkElement;
            if (c is null)
            {
                if (!_items.Contains(a.Item)) return;   // 捨てられた（上限で落ちた）
                Lst.ScrollIntoView(a.Item);
                Lst.UpdateLayout();
                c = Lst.ItemContainerGenerator.ContainerFromItem(a.Item) as FrameworkElement;
                if (c is null) return;
            }
            double top = c.TranslatePoint(new Point(0, 0), sc).Y;
            double d = top - a.Top;
            if (Math.Abs(d) < 0.5) return;
            sc.ScrollToVerticalOffset(sc.VerticalOffset + d);
        }
    }

    private void StickToBottom()
    {
        _stickUntil = Environment.TickCount64 + StickMs;
        Sc?.ScrollToEnd();
        // 配置が終わってからもう一度（カードの高さが決まる前の位置で止まらないように）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_stickUntil != 0) Sc?.ScrollToEnd(); });
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { if (_stickUntil != 0) Sc?.ScrollToEnd(); });
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t) return t;
            if (FindVisual<T>(ch) is { } deep) return deep;
        }
        return null;
    }

    private readonly Func<PanelStyle>? _styleSource;

    /// <summary>ログの本文の見た目を今の設定へ合わせる。変わっていたら true（カードを作り直す必要がある）</summary>
    private bool RefreshStyle()
    {
        if (_styleSource is null) return false;
        var st = _styleSource();
        if (Equals(st, Views.AnswerView.LogStyle)) return false;
        Views.AnswerView.LogStyle = st;
        return true;
    }

    public LogPage(LogStore store, Action<string> setStatus, Func<PanelStyle>? styleSource = null)
    {
        _store = store;
        _setStatus = setStatus;
        _styleSource = styleSource;
        Views.AnswerView.LogStatus = setStatus;
        RefreshStyle();
        InitializeComponent();
        Lst.ItemsSource = _items;
        Rebuild();
        // 開いた時は最新の（下の）位置へ
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Sc?.ScrollToEnd());
    }

    /// <summary>ページを開き直した時: 最新の（下の）位置へ。上へスクロールして
    /// 読んでいる途中の「引き戻さない」は新しいカードの追加時にだけ効く。</summary>
    public void ShowAtBottom()
    {
        if (RefreshStyle()) Rebuild();   // 解答欄の文字の大きさ・間などの設定が変わっていたら本文を付け直す
        ShowAtBottomCore();
    }

    private void ShowAtBottomCore()
        => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Sc?.ScrollToEnd());

    private void Rebuild()
    {
        _items.Clear();
        _displayedIds.Clear();
        var snap = _store.Snapshot();                       // 新しい→古い
        foreach (JsonObject e in Enumerable.Reverse(snap.Take(LogRenderCap)))
            AddCardVm(e, startThumb: true);
        EmptyText.Visibility = _displayedIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>カードの本文をたたんだ時の行数</summary>
    internal const int BodyLines = 10;

    /// <summary>たたんだ本文の高さ（px）= 10行分。行間の指定が無い時は、ログの本文の字（UiFont）の行の高さ</summary>
    internal static double BodyHeightFor(PanelStyle? st)
    {
        double size = st?.FontSize ?? PanelStyle.DefaultSize;
        double lh = st?.EffectiveLineHeight ?? double.NaN;
        if (double.IsNaN(lh) || lh <= 0)
        {
            var ff = Application.Current?.TryFindResource("UiFont") as FontFamily ?? new FontFamily("Yu Gothic UI");
            lh = ff.LineSpacing * size;
        }
        return Math.Ceiling(lh * BodyLines);
    }

    private void AddCardVm(JsonObject entry, bool startThumb, int? insertAt = null)
    {
        var vm = new LogCardVm(entry) { BodyHeight = BodyHeightFor(Views.AnswerView.LogStyle) };
        string? file = _store.ImagePath(entry);
        vm.ImageFile = file;
        vm.HasImageFile = file is not null;
        if (insertAt is null) _items.Add(vm);
        else _items.Insert(Math.Min(insertAt.Value, _items.Count), vm);
        if (!string.IsNullOrEmpty(vm.Id)) _displayedIds.Add(vm.Id);
        if (startThumb) StartThumbLoad(vm, file);
    }

    /// <summary>worker（バックグラウンド）から呼ぶ想定の入口。
    /// 新しいカードは末尾（下）へ。利用者が上へスクロールして読んでいる間は引き戻さない。</summary>
    public void AppendLiveCard(JsonObject entry)
    {
        var sc = Sc;
        bool atBottom = sc is null || sc.ScrollableHeight - sc.VerticalOffset <= 48;
        Anchor? anchor = atBottom ? null : CaptureAnchor();   // 上を読んでいる時は、見ているカードの位置を保つ
        AddCardVm(entry, startThumb: true);
        TrimView();
        EmptyText.Visibility = _displayedIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (atBottom) StickToBottom();
        else if (anchor is { } a) RestoreAnchor(a);
    }

    /// <summary>表示中の条目を台帳の現在値で更新する（ぶん投げ確定時）。カードが無ければ末尾に足す。</summary>
    public void RefreshOrAppend(JsonObject entry)
    {
        string id = LogStore.GetStr(entry, "id") ?? "";
        LogCardVm? vm = _items.OfType<LogCardVm>().FirstOrDefault(c => c.Id == id);
        if (vm is null) { AppendLiveCard(entry); return; }
        string? file = _store.ImagePath(entry);
        if (file is not null && vm.ImageFile is null) StartThumbLoad(vm, file);
        vm.ImageFile ??= file;
        vm.HasImageFile = file is not null;
        vm.RefreshAfterFinish();
    }

    private bool DropOldest()
    {
        LogCardVm? oldest = _items.OfType<LogCardVm>().FirstOrDefault(); // 先頭 = 最古
        if (oldest is null) return false;
        _items.Remove(oldest);
        if (!string.IsNullOrEmpty(oldest.Id)) _displayedIds.Remove(oldest.Id);
        return true;
    }

    private void TrimView()
    {
        while (_displayedIds.Count > LogRenderCap)
            if (!DropOldest()) break;
    }

    // ---- サムネイル: DecodePixelWidth=180 でバックグラウンド読み込み ----

    private void StartThumbLoad(LogCardVm vm, string? file)
    {
        if (file is null) { vm.SetThumb(null, "画像\nなし"); return; }
        vm.SetThumb(null, "画像\n読み込み中…");
        Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = ThumbW;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();   // UIスレッドへ渡す前に凍結
                vm.SetThumb(bmp, "");
            }
            catch (Exception ex)
            {
                AppLog.Error("log image load failed: " + file, ex);
                vm.SetThumb(null, "画像\n読めず");
            }
        });
    }

    // ---- カード上の操作 ----

    /// <summary>「▼ 続きを表示」（下の薄くなった所）を押すと、そのカードだけ開く</summary>
    private void Expand_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogCardVm vm) return;
        _stickUntil = 0;
        vm.IsExpanded = true;
        e.Handled = true;
    }

    /// <summary>「▲ たたむ」</summary>
    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogCardVm vm) return;
        _stickUntil = 0;
        vm.IsExpanded = false;
    }

    private void Thumb_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogCardVm vm) return;
        if (vm.ImageFile is null) return;
        new ScreenshotPopup(vm.ImageFile, vm.Ts, _setStatus, vm.CopyEnabled ? vm.Body : null)
        {
            Owner = Window.GetWindow(this),
        }.ShowAnimated();
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogCardVm vm) return;
        CopyShotToClipboard(vm.ImageFile, _setStatus);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogCardVm vm) return;
        CopyTextToClipboard(vm.Body, _setStatus);
    }

    internal static void CopyTextToClipboard(string text, Action<string>? setStatus = null)
    {
        try
        {
            Clipboard.SetText(text);
            setStatus?.Invoke("答えをコピーしました");
        }
        catch (Exception ex)
        {
            AppLog.Error("clipboard copy failed", ex);
            setStatus?.Invoke("コピーに失敗しました（app.log参照）");
        }
    }

    /// <summary>スクショ用クリップボード内容（PNG + Bitmap の2形式。スクショは透明が無いので Bitmap 版はそのまま）。</summary>
    internal static DataObject BuildShotData(BitmapSource bmp)
    {
        var data = new DataObject();
        var pngMs = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(pngMs);
        pngMs.Position = 0;
        data.SetData("PNG", pngMs);
        data.SetData(DataFormats.Bitmap, bmp);
        return data;
    }

    /// <summary>ディスク上の元画像をそのままクリップボードへ（貼った先では元の大きさ。PNG + Bitmap の2形式）。</summary>
    internal static void CopyShotToClipboard(string? file, Action<string>? setStatus = null)
    {
        if (file is null) return;
        bool ok = false;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            Clipboard.SetDataObject(BuildShotData(bmp), true);
            ok = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("clipboard image copy failed: " + file, ex);
        }
        setStatus?.Invoke(ok ? "画像をコピーしました" : "画像のコピーに失敗（app.log参照）");
    }
}

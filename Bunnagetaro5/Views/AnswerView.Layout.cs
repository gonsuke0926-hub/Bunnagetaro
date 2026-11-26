using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Views;

/// <summary>修正J97・J115: 位置の反映（枠の中の解答欄）。答えが「1行＝1段落」で位置が渡された時、
/// 本文と同じ場所に重ねた描き板（Canvas）に、n 行目を n 番目の位置（解答欄の本文の広さ × 割合）へ置く。
/// 修正J115: 各区画の中身を TextBlock から軽量な AnswerView（RichTextBox）へ変更し、
/// 通常の解答欄の数式（KaTeX/WpfMath）・表・文字ドラッグ選択・コピーをそのまま使い回す。
/// 位置の反映中は裏の _body を IsHitTestVisible=false にして、見えている区画の文字上だけで選択／余白は窓移動を判定する</summary>
public partial class AnswerView
{
    private readonly Canvas _layoutCanvas = new() { ClipToBounds = true, Background = null, Visibility = Visibility.Collapsed };
    private IReadOnlyList<OcrBlock>? _layout;
    private readonly List<AnswerView> _layoutViews = new();
    private readonly List<string> _layoutRawTexts = new();
    /// <summary>修正J98: 段落ごとの今の字の大きさ（訳の途中は小さくなる向きだけにするため）</summary>
    private readonly List<double> _layoutFonts = new();
    /// <summary>修正J115: 字の大きさ計算に使い回す非表示の測定用 TextBlock</summary>
    private readonly TextBlock _measureTb = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>字の大きさの上限・下限（DIP）</summary>
    internal const double LayoutMaxFont = 48, LayoutMinFont = 8;

    private void BuildLayoutCanvas(Grid grid)
    {
        Grid.SetRow(_layoutCanvas, 0);
        grid.Children.Add(_layoutCanvas);
        _layoutCanvas.SizeChanged += (_, _) => PlaceLayout();
    }

    /// <summary>位置どおりに並べて見せている（テスト用）</summary>
    internal bool LayoutShown => _layoutCanvas.Visibility == Visibility.Visible;
    /// <summary>並べている区画の部品（テスト用）</summary>
    internal IReadOnlyList<AnswerView> LayoutBlocks => _layoutViews;
    internal Canvas LayoutCanvas => _layoutCanvas;
    /// <summary>並べている文（テスト用）</summary>
    internal IReadOnlyList<string> LayoutTexts => _layoutRawTexts;

    /// <summary>修正J114: この解答欄で位置の反映（座標配置）を使うか。外窓（AnswerStrip）は false（読む順で上から並べる）</summary>
    public bool LayoutAllowed { get; set; } = true;

    /// <summary>修正J115: 位置の反映の 1 区画として初期化する（思考・たずねる・スクロールバーなし、背景透明）</summary>
    internal void InitLayoutItem(AnswerView parent)
    {
        _layoutItemMode = true;
        LayoutAllowed = false;
        _thinkSep.Visibility = Visibility.Collapsed;
        _thinkSv.Visibility = Visibility.Collapsed;
        _followRow.Visibility = Visibility.Collapsed;
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _body.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _body.Padding = new Thickness(0);
        _body.Document.PagePadding = new Thickness(0);
        _body.Margin = new Thickness(0);
        _root.Background = Brushes.Transparent;
        _body.Background = Brushes.Transparent;
        Background = null;
        BuildLayoutItemMenu(parent);
    }

    private void BuildLayoutItemMenu(AnswerView parent)
    {
        var miSel = new MenuItem { Header = "選択部分をコピー (Ctrl+C)" };
        miSel.Click += (_, _) => CopyAnswer(SelectedCopy());
        var miOne = new MenuItem { Header = "このまとまりをコピー" };
        miOne.Click += (_, _) => CopyAnswer(FullCopy());
        var miAll = new MenuItem { Header = "答えをすべてコピー" };
        miAll.Click += (_, _) => parent.CopyAnswer(parent.FullCopy());
        var menu = new ContextMenu();
        menu.Items.Add(miSel);
        menu.Items.Add(miOne);
        menu.Items.Add(miAll);
        menu.Opened += (_, _) => miSel.IsEnabled = SelectedText().Length > 0;
        _body.ContextMenu = menu;
    }

    /// <summary>修正J115: 区画の文字サイズを更新する（数式や表がある場合も再計算に追従）</summary>
    internal void SetLayoutItemFontSize(double fs)
    {
        FontSize = fs;
        if (Math.Abs(_body.FontSize - fs) < 0.05) return;
        _body.FontSize = fs;
        double ls = _lastStyle?.LineSpacing ?? PanelStyle.DefaultLineSpacing;
        _body.Document.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        _body.Document.LineHeight = ls > 0 ? fs * ls : double.NaN;
        if (_docMath && !_tablesShown && _model.Length > 0) ReplaceDoc(_model);
    }

    private void ApplyLayoutItemsStyle()
    {
        if (_layoutViews.Count == 0) return;
        var st = _lastStyle ?? new PanelStyle(PanelStyle.DefaultFg, PanelStyle.DefaultBg, PanelStyle.DefaultAlpha, "", (int)Math.Round(_body.FontSize));
        for (int i = 0; i < _layoutViews.Count; i++)
        {
            double fs = i < _layoutFonts.Count && _layoutFonts[i] > 0 ? _layoutFonts[i] : st.FontSize;
            _layoutViews[i].ApplyStyle(st with { FontSize = Math.Clamp((int)Math.Round(fs), 8, 72) }, 0, 0);
            _layoutViews[i].SetLayoutItemFontSize(fs);
        }
    }

    private void ShowFinalLayoutItems()
    {
        if (_layout is null || _layoutViews.Count == 0) return;
        var lines = _model.Length == 0 ? new string[_layout.Count] : _model.Split('\n');
        if (lines.Length != _layoutViews.Count) return;
        for (int i = 0; i < _layoutViews.Count; i++)
        {
            if (!string.IsNullOrEmpty(lines[i])) _layoutViews[i].ShowFinal(lines[i]);
        }
    }

    /// <summary>段落の位置を渡す（null・空なら位置の反映をやめて、本文をそのまま見せる）。
    /// 修正J107: 段落ごとに、文の後ろへ「マスク」（解答欄の背景色で塗った四角）を置く。文がまだ無くても位置が分かった時点で出す</summary>
    public void SetLayout(IReadOnlyList<OcrBlock>? blocks)
    {
        if (_logMode || !LayoutAllowed) blocks = null;
        foreach (var old in _layoutViews) old.ClearBody();   // 描き途中の KaTeX タスク等を確実にキャンセル
        _layout = blocks is { Count: > 0 } ? blocks : null;
        _layoutCanvas.Children.Clear();
        _layoutViews.Clear();
        _layoutRawTexts.Clear();
        _layoutMasks.Clear();
        _layoutFonts.Clear();
        if (_layout is not null)
        {
            foreach (var b in _layout)
            {
                var mask = new Border { IsHitTestVisible = false, Tag = b };
                _layoutMasks.Add(mask);
                _layoutCanvas.Children.Add(mask);   // マスクは文より奥
            }
            var st = _lastStyle ?? new PanelStyle(PanelStyle.DefaultFg, PanelStyle.DefaultBg, PanelStyle.DefaultAlpha, "", (int)Math.Round(_body.FontSize));
            for (int idx = 0; idx < _layout.Count; idx++)
            {
                var b = _layout[idx];
                int capIdx = idx;
                var item = new AnswerView(_host, 0, 0) { Tag = b };
                item.InitLayoutItem(this);
                item.ApplyStyle(st, 0, 0);
                item.FontSize = st.FontSize;
                item.SizeChanged += (_, _) => SyncMaskFromActual(capIdx);
                _layoutViews.Add(item);
                _layoutRawTexts.Add("");
                _layoutCanvas.Children.Add(item);
            }
        }
        RefreshLayout();
    }

    /// <summary>修正J113: この文を位置の反映で出せるか（行数が区画数と一致、または空）</summary>
    private bool CanShowLayoutFor(string shown)
    {
        if (_layout is null) return false;
        if (shown.Length == 0) return true;
        int lines = 1;
        for (int i = 0; i < shown.Length; i++) if (shown[i] == '\n') lines++;
        return lines == _layout.Count;
    }

    /// <summary>見えている文（_model）を段落ごとに描き板へ写す。文がまだ無い時はマスクだけ出す（修正J107）。
    /// 行の数が段落の数と合わない時は本文をそのまま見せる</summary>
    private void RefreshLayout()
    {
        if (_layout is null)
        {
            HideLayout();
            return;
        }
        var lines = _model.Length == 0 ? new string[_layout.Count] : _model.Split('\n');
        if (lines.Length != _layout.Count)
        {
            HideLayout();
            return;
        }
        bool wasVisible = _layoutCanvas.Visibility == Visibility.Visible;
        List<int>? changed = wasVisible ? new List<int>() : null;
        for (int i = 0; i < lines.Length; i++)
        {
            string txt = lines[i] ?? "";
            if (_layoutRawTexts[i] != txt)
            {
                _layoutRawTexts[i] = txt;
                if (txt.Length == 0) _layoutViews[i].ClearBody();
                else _layoutViews[i].ApplyBody(txt);
                changed?.Add(i);
            }
        }
        _layoutCanvas.Visibility = Visibility.Visible;
        _body.Opacity = 0;
        _body.IsHitTestVisible = false;   // 修正J115: 裏の見えない _body にヒットさせない
        ApplyRootBg();
        // 修正J113: すでに表示中なら、文が変わった区画だけ再計測・配置する
        PlaceLayout(remeasure: !wasVisible, onlyIndices: changed);   // 修正J98: 文が変わった時は字を小さくする向きだけ
    }

    private void HideLayout()
    {
        _layoutCanvas.Visibility = Visibility.Collapsed;
        _body.Opacity = 1;
        _body.IsHitTestVisible = true;
        if (_bodyDirty)
        {
            _bodyDirty = false;
            ReplaceDoc(_model);
        }
        ApplyRootBg();
    }

    // ---------------- 修正J115: 余白ドラッグ・文字選択のヒット判定 ----------------

    /// <summary>マウスが本文エリア（思考欄や「たずねる」欄以外の領域）の上にあるか</summary>
    internal bool IsInBodyArea(MouseEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        while (d is not null)
        {
            if (ReferenceEquals(d, _thinkSv) || ReferenceEquals(d, _thinkSep) || ReferenceEquals(d, _followRow))
                return false;
            if (ReferenceEquals(d, _body) || ReferenceEquals(d, _layoutCanvas) || ReferenceEquals(d, _root))
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return true;
    }

    /// <summary>マウスが「見えている文字（または数式・表）」の上にあるか。
    /// 位置の反映中は裏の見えない _body ではなく、実際に見えている各区画の RichTextBox で判定する</summary>
    internal bool IsOverText(MouseEventArgs e)
    {
        if (!LayoutShown)
            return _body.GetPositionFromPoint(e.GetPosition(_body), false) is not null;
        return IsOverLayoutTextAt(e.GetPosition(_layoutCanvas));
    }

    internal bool IsOverLayoutTextAt(Point ptInCanvas)
    {
        for (int i = 0; i < _layoutViews.Count; i++)
        {
            var item = _layoutViews[i];
            var b = item.Body;
            var p = _layoutCanvas.TranslatePoint(ptInCanvas, b);
            if (p.X < 0 || p.Y < 0 || p.X > b.ActualWidth || p.Y > b.ActualHeight) continue;
            if (b.GetPositionFromPoint(p, false) is not null) return true;
        }
        return false;
    }

    /// <summary>本文エリアのカーソル（文字の上＝IBeam、余白＝SizeAll）を切り替える</summary>
    internal void SetBodyCursor(Cursor cur)
    {
        if (!LayoutShown)
        {
            _body.Cursor = cur;
            return;
        }
        _root.Cursor = cur;
        _layoutCanvas.Cursor = cur;
        foreach (var item in _layoutViews) item.Body.Cursor = cur;
    }

    /// <summary>修正J103: 解答欄の地（設定の背景色・不透明度）。ApplyStyle で作る</summary>
    private Brush? _rootBg;
    /// <summary>修正J103・J107: 位置の反映の時、段落（区画）の所だけに背景（マスク）を敷く（設定 layout_text_bg。既定 ON）</summary>
    private bool _layoutTextBg = true;
    private readonly List<Border> _layoutMasks = new();

    /// <summary>位置の反映を見せている時の「区画の所だけ背景（マスク）」か</summary>
    internal bool LayoutTextOnlyBg => _layoutTextBg && LayoutShown;
    /// <summary>段落ごとのマスク（テスト用）</summary>
    internal IReadOnlyList<Border> LayoutMasks => _layoutMasks;

    /// <summary>地の塗り分け。区画の所だけの時は、全体の地をほぼ透明にし、区画のマスクに解答欄の地を塗る。
    /// 修正J104: 全体の地は完全な透明（アルファ 0）にしない。透ける窓はアルファ 0 の所のクリックを後ろの窓へ通すので、
    /// 空いている所をつかんで窓を動かせなかった。
    /// 修正J107: 背景は J104 の「行ごとに文字の後ろだけ」をやめて、区画の四角のマスクにした（人の判断 2026-11-26）</summary>
    private void ApplyRootBg()
    {
        if (_rootBg is null) return;
        bool textOnly = LayoutTextOnlyBg;
        _root.Background = textOnly ? NearlyClear(_rootBg) : _rootBg;
        foreach (var m in _layoutMasks) m.Background = textOnly ? _rootBg : null;
    }

    /// <summary>修正J104: 見えないがクリックは受ける地（同じ色のアルファ 1）</summary>
    private static Brush NearlyClear(Brush b)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Black;
        var nb = new SolidColorBrush(Color.FromArgb(1, c.R, c.G, c.B));
        nb.Freeze();
        return nb;
    }

    /// <summary>段落を置き、字の大きさを決める（修正J98。人の判断 2026-11-24）。字はまず解答欄の文字サイズ。
    /// 自分の矩形の幅で、下の段落（無ければ欄の下の端）にぶつからずに入ればそのまま。入らなければ右の空き
    /// （右にある別の段落か欄の端まで）に広げてみる。それでもぶつかる時だけ字を小さくし、下限でも入らなければ下へはみ出してよい。
    /// 訳の途中（文が変わった時）は小さくなる向きだけにする（揺れないように）。remeasure（欄の大きさ・字の設定が変わった）なら測り直す</summary>
    private void PlaceLayout() => PlaceLayout(remeasure: true, onlyIndices: null);

    private void PlaceLayout(bool remeasure, IReadOnlyList<int>? onlyIndices = null)
    {
        if (_layout is null || _layoutCanvas.Visibility != Visibility.Visible) return;
        double cw = _layoutCanvas.ActualWidth, ch = _layoutCanvas.ActualHeight;
        if (cw <= 1 || ch <= 1) return;
        double baseFont = Math.Clamp(_body.FontSize, LayoutMinFont, LayoutMaxFont);
        if (onlyIndices is not null)
        {
            foreach (int i in onlyIndices) PlaceLayoutOne(i, cw, ch, baseFont, remeasure);
            return;
        }
        for (int i = 0; i < _layout.Count; i++) PlaceLayoutOne(i, cw, ch, baseFont, remeasure);
    }

    private void PlaceLayoutOne(int i, double cw, double ch, double baseFont, bool remeasure)
    {
        var b = _layout![i];
        var item = _layoutViews[i];
        string txt = _layoutRawTexts[i];
        double x = b.X * cw, y = b.Y * ch;
        double w = Math.Max(LayoutMinFont, b.W * cw);
        double right = RightLimit(i);
        double wide = Math.Max(w, right * cw - x - 2);
        double hOwn = Math.Max(Math.Max(LayoutMinFont, b.H * ch), BottomLimit(i, b.X + b.W) * ch - y - 2);
        double hWide = Math.Max(Math.Max(LayoutMinFont, b.H * ch), BottomLimit(i, right) * ch - y - 2);
        double start = baseFont;
        if (!remeasure && i < _layoutFonts.Count && _layoutFonts[i] > 0) start = Math.Min(start, _layoutFonts[i]);

        _measureTb.FontFamily = _body.FontFamily;
        _measureTb.Text = txt;
        var (fs, width) = FitLayoutFont(_measureTb, start, w, hOwn, wide, hWide);
        while (_layoutFonts.Count <= i) _layoutFonts.Add(0);
        _layoutFonts[i] = fs;
        item.SetLayoutItemFontSize(fs);
        item.Width = width;
        Canvas.SetLeft(item, x);
        Canvas.SetTop(item, y);

        // 修正J107: マスクは区画の四角。文がはみ出した時（右・下へ広げた時）は、文の所まで広げて文の後ろを必ず覆う
        var mask = _layoutMasks[i];
        _measureTb.FontSize = fs;
        _measureTb.Measure(new Size(width, double.PositiveInfinity));
        double mw = Math.Max(b.W * cw, string.IsNullOrEmpty(txt) ? 0 : Math.Max(_measureTb.DesiredSize.Width, item.ActualWidth));
        double mh = Math.Max(b.H * ch, string.IsNullOrEmpty(txt) ? 0 : Math.Max(_measureTb.DesiredSize.Height, item.ActualHeight));
        mask.Width = Math.Max(1, mw);
        mask.Height = Math.Max(1, mh);
        Canvas.SetLeft(mask, x);
        Canvas.SetTop(mask, y);
    }

    /// <summary>修正J115: 数式画像や表が後から描かれて区画の高さ・幅が伸びた時も、マスクを追従させる</summary>
    private void SyncMaskFromActual(int i)
    {
        if (_layout is null || i < 0 || i >= _layout.Count || i >= _layoutMasks.Count || i >= _layoutViews.Count) return;
        double cw = _layoutCanvas.ActualWidth, ch = _layoutCanvas.ActualHeight;
        if (cw <= 1 || ch <= 1 || string.IsNullOrEmpty(_layoutRawTexts[i])) return;
        var b = _layout[i];
        var item = _layoutViews[i];
        var mask = _layoutMasks[i];
        mask.Width = Math.Max(1, Math.Max(b.W * cw, item.ActualWidth));
        mask.Height = Math.Max(1, Math.Max(b.H * ch, item.ActualHeight));
    }

    /// <summary>i 番目の段落の右の空きの端（割合）。右にあって高さが重なる段落の左端、無ければ欄の右端（1）</summary>
    private double RightLimit(int i)
    {
        var a = _layout![i];
        double limit = 1.0;
        for (int j = 0; j < _layout.Count; j++)
        {
            if (j == i) continue;
            var b = _layout[j];
            bool rightSide = b.X >= a.X + a.W - 0.005;
            bool overlapY = b.Y < a.Y + a.H && a.Y < b.Y + b.H;
            if (rightSide && overlapY) limit = Math.Min(limit, b.X);
        }
        return limit;
    }

    /// <summary>i 番目の段落の下の空きの端（割合）。左端から xRight までの横の範囲で、下にある段落の上端、無ければ欄の下の端（1）</summary>
    private double BottomLimit(int i, double xRight)
    {
        var a = _layout![i];
        double limit = 1.0;
        for (int j = 0; j < _layout.Count; j++)
        {
            if (j == i) continue;
            var b = _layout[j];
            bool below = b.Y >= a.Y + a.H - 0.005;
            bool overlapX = b.X < xRight && a.X < b.X + b.W;
            if (below && overlapX) limit = Math.Min(limit, b.Y);
        }
        return limit;
    }

    /// <summary>字の大きさと幅を決める（start の大きさから試し、矩形の幅→右の空きの幅の順に、下の空きに収まるかを見る）</summary>
    internal static (double Font, double Width) FitLayoutFont(TextBlock tb, double start, double w, double hOwn, double wide, double hWide)
    {
        tb.Width = double.NaN;   // 修正J103: 前に決めた幅が残っていると、試す幅で測れない（Measure が決まった幅を使う）
        double fs = Math.Clamp(start, LayoutMinFont, LayoutMaxFont);
        while (true)
        {
            tb.FontSize = fs;
            if (Fits(tb, w, hOwn)) return (fs, w);
            if (wide > w + 0.5 && Fits(tb, wide, hWide)) return (fs, wide);
            if (fs <= LayoutMinFont) return (LayoutMinFont, wide);
            fs = Math.Max(LayoutMinFont, Math.Floor(fs * 0.9 * 2) / 2);
        }
    }

    private static bool Fits(TextBlock tb, double width, double h)
    {
        // 修正J115: RichTextBox の内部余白（約 10px）分を引いた幅で測り、実表示が下の段落にぶつからないようにする
        double w = Math.Max(LayoutMinFont, width - 10);
        tb.Measure(new Size(w, double.PositiveInfinity));
        return tb.DesiredSize.Height <= h + 0.5 && tb.DesiredSize.Width <= w + 0.5;
    }
}

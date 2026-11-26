using System.Windows;
using System.Windows.Controls;

namespace Bunnagetaro5.Pages;

/// <summary>ログのカードの本文の枠。たたんでいる間は、中身の長さによらず高さを BodyHeight（10行分）に
/// そろえ、はみ出した分は切る。開く（IsExpanded）と中身の高さまで伸びる。
/// カードの高さがそろうと、仮想化の見積りが外れなくなり、つまみを引いた時にスクロールバーが揺れない。</summary>
public sealed class LogClipBox : Decorator
{
    public static readonly DependencyProperty BodyHeightProperty = DependencyProperty.Register(
        nameof(BodyHeight), typeof(double), typeof(LogClipBox),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>たたんでいる時の高さ（px）</summary>
    public double BodyHeight { get => (double)GetValue(BodyHeightProperty); set => SetValue(BodyHeightProperty, value); }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(LogClipBox),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>開いているか（カードの使い回しでも残るよう、カードの VM に結ぶ）</summary>
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private static readonly DependencyPropertyKey IsOverflowingKey = DependencyProperty.RegisterReadOnly(
        nameof(IsOverflowing), typeof(bool), typeof(LogClipBox), new PropertyMetadata(false, OnFlagsChanged));
    public static readonly DependencyProperty IsOverflowingProperty = IsOverflowingKey.DependencyProperty;

    /// <summary>中身が BodyHeight に収まらないか</summary>
    public bool IsOverflowing => (bool)GetValue(IsOverflowingProperty);

    private static readonly DependencyPropertyKey ShowMoreKey = DependencyProperty.RegisterReadOnly(
        nameof(ShowMore), typeof(bool), typeof(LogClipBox), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowMoreProperty = ShowMoreKey.DependencyProperty;

    /// <summary>「▼ 続きを表示」を出すか（はみ出していて、たたんでいる）</summary>
    public bool ShowMore => (bool)GetValue(ShowMoreProperty);

    private static readonly DependencyPropertyKey ShowLessKey = DependencyProperty.RegisterReadOnly(
        nameof(ShowLess), typeof(bool), typeof(LogClipBox), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowLessProperty = ShowLessKey.DependencyProperty;

    /// <summary>「▲ たたむ」を出すか（はみ出していて、開いている）</summary>
    public bool ShowLess => (bool)GetValue(ShowLessProperty);

    /// <summary>中身の本当の高さ（テスト用）</summary>
    internal double FullHeight { get; private set; }

    public LogClipBox()
    {
        ClipToBounds = true;
    }

    private static void OnFlagsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LogClipBox)d).UpdateFlags();

    private void UpdateFlags()
    {
        SetValue(ShowMoreKey, IsOverflowing && !IsExpanded);
        SetValue(ShowLessKey, IsOverflowing && IsExpanded);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsExpandedProperty) UpdateFlags();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return new Size(0, BodyHeight);
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));   // 中身は高さの制限なしで測る
        FullHeight = Child.DesiredSize.Height;
        bool over = FullHeight > BodyHeight + 0.5;
        if (over != IsOverflowing) SetValue(IsOverflowingKey, over);
        double h = IsExpanded && over ? FullHeight : BodyHeight;   // 短い答えも BodyHeight（高さをそろえる）
        return new Size(Child.DesiredSize.Width, h);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(FullHeight, arrangeSize.Height)));
        return arrangeSize;
    }
}

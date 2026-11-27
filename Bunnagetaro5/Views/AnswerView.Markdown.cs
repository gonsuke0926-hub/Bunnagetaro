using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Bunnagetaro5.Services;
using WpfMath.Controls;

namespace Bunnagetaro5.Views;

/// <summary>解答欄の Markdown（見出し・太字・箇条書き・引用・コード・区切り線）の表示（AnswerView を分けたもの）。
/// 式は MathText の区切れ目を先に印に置き換えてから読むので、式の中は Markdown として触らない。
/// 流している間は、前と同じ塊はそのまま残し、変わった塊から後ろだけを組み直す。
/// Markdown の表は出し終わってから表にする（ApplyTables）</summary>
public partial class AnswerView
{
    /// <summary>Markdown を見た目にするか（設定 convert_markdown。ApplyStyle で更新）</summary>
    private bool _convertMarkdown;

    /// <summary>Markdown の塊の印（段落の Tag）。余白は ApplyParagraphMargins がこれを見て付ける</summary>
    private sealed class MdTag
    {
        public MdBlockKind Kind;
        public double Left;
    }

    /// <summary>組んだ塊（比べるための文字と、文書に置いた段落）。null は「今の文書は塊ごとに組んでいない」</summary>
    private List<(string Key, MdBlock Block, List<Block> Blocks)>? _mdRendered;

    /// <summary>Markdown で組んだ時の元の文字（見た目が変わった時に組み直す）</summary>
    private string _mdText = "";

    private static readonly FontFamily MonoFont = new("Consolas, BIZ UDGothic, MS Gothic");

    /// <summary>text を Markdown として文書にする。前に組んだ塊と同じ所はそのまま、変わった塊から後ろを組み直す</summary>
    private void MdRender(string text, List<MathSeg> segs)
    {
        var blocks = MarkdownText.ParseBlocks(MarkdownText.Protect(segs));
        var doc = _body.Document;
        if (_mdRendered is null)
        {
            doc.Blocks.Clear();
            _mdRendered = new List<(string, MdBlock, List<Block>)>();
        }
        var keys = blocks.Select(b => MdKey(b, segs)).ToList();
        int k = 0;
        while (k < _mdRendered.Count && k < keys.Count && _mdRendered[k].Key == keys[k] &&
               _mdRendered[k].Blocks.All(x => ReferenceEquals(x.Parent, doc)))
            k++;
        // 最後の塊の末尾に記号の無い文字が足されただけなら、組み直さずに末尾へ足す（長い段落を流す時に毎回作り直さない）
        if (k == _mdRendered.Count - 1 && blocks.Count == _mdRendered.Count &&
            MdTryAppendTail(_mdRendered[k], blocks[k]))
        {
            _mdRendered[k] = (keys[k], blocks[k], _mdRendered[k].Blocks);
            k = blocks.Count;
        }
        for (int i = _mdRendered.Count - 1; i >= k; i--)
            foreach (var wb in _mdRendered[i].Blocks)
                if (ReferenceEquals(wb.Parent, doc)) doc.Blocks.Remove(wb);
        _mdRendered.RemoveRange(k, _mdRendered.Count - k);
        for (int i = k; i < blocks.Count; i++)
            _mdRendered.Add((keys[i], blocks[i], MdBuildBlock(blocks[i], segs)));
        _segs = segs;
        _mdText = text;
        _docMath = segs.Exists(x => x.Kind != MathSegKind.Text);
        _body.IsDocumentEnabled = _docMath;
        ApplyParagraphMargins();
    }

    /// <summary>行の中の読み方を変えうる文字（飾り・コード・リンク・エスケープ・式の印）</summary>
    private const string MdSpecial = "*_~`[]()!\\<";

    /// <summary>前に組んだ塊の文字の後ろに、記号の無い文字だけが足された時は、段落の末尾へ文字を足して true。
    /// 前の文字の最後が記号なら（閉じたばかりの飾りの直後に英数字が来ると読み方が変わる）使わない</summary>
    private bool MdTryAppendTail((string Key, MdBlock Block, List<Block> Blocks) old, MdBlock nb)
    {
        var ob = old.Block;
        if (nb.Kind != ob.Kind || nb.Level != ob.Level || nb.Marker != ob.Marker) return false;
        if (nb.Kind is not (MdBlockKind.Para or MdBlockKind.Quote or MdBlockKind.ListItem)) return false;
        if (ob.Text.Length == 0 || nb.Text.Length <= ob.Text.Length || !nb.Text.StartsWith(ob.Text, StringComparison.Ordinal)) return false;
        if (MdSpecial.Contains(ob.Text[^1])) return false;
        string add = nb.Text[ob.Text.Length..];
        if (add.IndexOfAny(MdSpecial.ToCharArray()) >= 0) return false;
        if (old.Blocks.Count != 1 || old.Blocks[0] is not Paragraph p || !ReferenceEquals(p.Parent, _body.Document)) return false;
        var lines = add.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
        }
        return true;
    }

    private static string MdKey(MdBlock b, List<MathSeg> segs) =>
        $"{(int)b.Kind}|{b.Level}|{b.Marker}|{MarkdownText.Restore(b.Text, segs)}";

    /// <summary>表の前後の文字など、塊を覚えずに文書の末尾へ足す（表の組み替え用）</summary>
    private void MdAppend(string text)
    {
        var segs = SplitMath(text);
        foreach (var b in MarkdownText.ParseBlocks(MarkdownText.Protect(segs)))
            MdBuildBlock(b, segs);
    }

    /// <summary>塊1つを文書の末尾へ置き、置いた段落を返す</summary>
    private List<Block> MdBuildBlock(MdBlock b, List<MathSeg> segs)
    {
        var doc = _body.Document;
        double fs = Math.Max(1.0, _body.FontSize);
        Color fg = (_body.Foreground as SolidColorBrush)?.Color ?? Color.FromRgb(0xF5, 0xF5, 0xF5);
        var made = new List<Block>();
        Paragraph P(MdBlockKind kind, double left = 0)
        {
            var p = new Paragraph { Tag = new MdTag { Kind = kind, Left = left } };
            doc.Blocks.Add(p);
            made.Add(p);
            return p;
        }
        switch (b.Kind)
        {
            case MdBlockKind.Math:
            {
                int idx = MarkdownText.DisplayIndex(b.Text);
                if (idx < 0 || idx >= segs.Count) { MdInlines(P(MdBlockKind.Para), b.Text, segs, fg); break; }
                var seg = segs[idx];
                var item = MdMathItem(seg);
                var dp = new Paragraph { TextAlignment = TextAlignment.Center, Tag = DisplayMathTag };
                dp.Inlines.Add(item ?? new Run(seg.Raw));
                doc.Blocks.Add(dp);
                made.Add(dp);
                break;
            }
            case MdBlockKind.Heading:
            {
                var p = P(MdBlockKind.Heading);
                p.FontSize = fs * (b.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 });
                p.FontWeight = FontWeights.Bold;
                MdInlines(p, b.Text, segs, fg);
                break;
            }
            case MdBlockKind.ListItem:
            {
                // ぶら下げ: 2行目からは印の後ろにそろえる
                double mark = b.Marker.Length <= 1 ? fs * 1.2 : fs * (0.55 * b.Marker.Length + 0.6);
                var p = P(MdBlockKind.ListItem, fs * 1.2 * b.Level);
                p.Padding = new Thickness(mark, 0, 0, 0);
                p.TextIndent = -mark;
                p.Inlines.Add(new Run(b.Marker + (b.Marker.Length <= 1 ? " " : " ")));
                MdInlines(p, b.Text, segs, fg);
                break;
            }
            case MdBlockKind.Quote:
            {
                var p = P(MdBlockKind.Quote, fs * 0.2 + fs * 0.6 * (b.Level - 1));
                p.BorderBrush = Frozen(Color.FromArgb(0x90, fg.R, fg.G, fg.B));
                p.BorderThickness = new Thickness(Math.Max(2, fs * 0.2), 0, 0, 0);
                p.Padding = new Thickness(fs * 0.6, 0, 0, 0);
                p.Foreground = Frozen(Color.FromArgb(0xD0, fg.R, fg.G, fg.B));
                MdInlines(p, b.Text, segs, fg);
                break;
            }
            case MdBlockKind.Code:
            {
                var p = P(MdBlockKind.Code);
                p.FontFamily = MonoFont;
                p.Background = Frozen(Color.FromArgb(0x26, fg.R, fg.G, fg.B));
                p.Padding = new Thickness(fs * 0.5, fs * 0.3, fs * 0.5, fs * 0.3);
                var lines = MarkdownText.Restore(b.Text, segs).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.Inlines.Add(new LineBreak());
                    if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
                }
                break;
            }
            case MdBlockKind.Rule:
            {
                var p = P(MdBlockKind.Rule);
                p.BorderBrush = Frozen(Color.FromArgb(0x70, fg.R, fg.G, fg.B));
                p.BorderThickness = new Thickness(0, 0, 0, 1);
                p.FontSize = Math.Max(1, fs * 0.3);
                p.LineHeight = Math.Max(1, fs * 0.3);
                p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                break;
            }
            default:
                MdInlines(P(MdBlockKind.Para), b.Text, segs, fg);
                break;
        }
        return made;
    }

    /// <summary>行の中（太字・斜体・取り消し線・コード・リンクの文字・式・改行）を段落へ足す。
    /// 飾りは Span で包まず Run の性質にする（選んだ所のコピーが式を段落の直下で拾えるように）</summary>
    private void MdInlines(Paragraph p, string text, List<MathSeg> segs, Color fg)
    {
        Brush? codeBg = null;
        foreach (var sp in MarkdownText.ParseInline(text))
        {
            if (sp.IsBreak) { p.Inlines.Add(new LineBreak()); continue; }
            if (sp.Math >= 0)
            {
                if (sp.Math >= segs.Count) continue;
                var seg = segs[sp.Math];
                var inlineSeg = seg.Kind == MathSegKind.Display ? seg with { Kind = MathSegKind.Inline } : seg;
                var item = MdMathItem(inlineSeg);
                if (item is null) { p.Inlines.Add(new Run(seg.Raw)); continue; }
                p.Inlines.Add(item);
                if (MathRenderer.Active)
                {
                    p.Inlines.Add(MakeMathSpacer());
                    if (item is InlineUIContainer { Child: SnapImage sim } && _natSizes.TryGetValue(sim, out var n)) ApplyFit(sim, n);
                }
                continue;
            }
            var run = new Run(sp.Style.HasFlag(MdStyle.Code) ? MarkdownText.Restore(sp.Text, segs) : sp.Text);
            if (sp.Style.HasFlag(MdStyle.Bold)) run.FontWeight = FontWeights.Bold;
            if (sp.Style.HasFlag(MdStyle.Italic)) run.FontStyle = FontStyles.Italic;
            if (sp.Style.HasFlag(MdStyle.Strike)) run.TextDecorations = TextDecorations.Strikethrough;
            if (sp.Style.HasFlag(MdStyle.Code))
            {
                run.FontFamily = MonoFont;
                run.Background = codeBg ??= Frozen(Color.FromArgb(0x26, fg.R, fg.G, fg.B));
            }
            p.Inlines.Add(run);
        }
    }

    /// <summary>式1つの表示部品（KaTeX の画像か原文字の Run、WpfMath）。描けない式は null</summary>
    private Inline? MdMathItem(MathSeg seg)
    {
        if (MathRenderer.Active) return MakeKatexInline(seg);
        var fc = MakeFormula(seg);
        return fc is null ? null : new InlineUIContainer(fc);
    }

    /// <summary>Markdown の段落の余白（ApplyParagraphMargins から）。箇条書きが続く所は詰める</summary>
    private Thickness MdMargin(MdTag t, Block b)
    {
        var next = b.NextBlock;
        double fs = Math.Max(1.0, _body.FontSize);
        double bottom;
        if (next is null || (next is Paragraph np && np.Tag is string nt && nt == DisplayMathTag)) bottom = 0;
        else if (t.Kind == MdBlockKind.ListItem && next is Paragraph { Tag: MdTag { Kind: MdBlockKind.ListItem } }) bottom = fs * 0.15;
        else if (t.Kind == MdBlockKind.Heading) bottom = _paraSpacingPx * 0.5;
        else bottom = _paraSpacingPx;
        double top = t.Kind is MdBlockKind.Heading or MdBlockKind.Rule && b.PreviousBlock is not null ? _paraSpacingPx * 0.5 : 0;
        if (t.Kind == MdBlockKind.Rule && next is not null) bottom = Math.Max(bottom, _paraSpacingPx * 0.5);
        return new Thickness(t.Left, top, 0, bottom);
    }

    /// <summary>Markdown が ON なら記号を外した文字（$ などの式の中は触らない）</summary>
    private string MdPlainIfEnabled(string text) =>
        _convertMarkdown ? MarkdownText.ToPlain(text, _convertLatexMath) : text;
}

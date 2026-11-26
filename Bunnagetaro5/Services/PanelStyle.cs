using System.Globalization;

namespace Bunnagetaro5.Services;

/// <summary>
/// 解答欄の見た目。overlay_fg / overlay_bg / overlay_alpha /
/// overlay_font_family / overlay_font_size から作る純粋な値。
/// 正規化ルール:
/// 色は #rrggbb で読めなければ既定、alpha は 0.15〜1.0、大きさ 8〜72。
/// 薄い色（思考欄・スクロールバー）は設定の色から毎回作り直す
/// （適用のたびに薄くなっていく病気の防止）。
/// </summary>
public sealed record PanelStyle(
    string Fg,
    string Bg,
    double Alpha,
    string FontFamily,
    int FontSize,
    string LatexCopy = "plain",
    double ParaSpacing = PanelStyle.DefaultParaSpacing,
    double LineSpacing = PanelStyle.DefaultLineSpacing,
    bool ConvertOtslTable = true,
    bool ConvertHtmlTable = true,
    bool ConvertLatexMath = true,
    bool ShowFollowRow = false,
    bool LayoutTextBg = true)   // 位置の反映の時、文字の所だけ地を敷く（全体の地は透明）
{
    public const string DefaultFg = "#f5f5f5";
    public const string DefaultBg = "#202020";
    public const double DefaultAlpha = 0.65;   // 解答欄の不透明度の既定
    public const double DefaultStripAlpha = 1.0;   // 外窓の不透明度の既定（不透明）
    public const int DefaultSize = 12;

    // 段落間・行間・独立式段落の定数（1か所にまとめる）
    public const double DefaultParaSpacing = 0.8;
    public const double MinParaSpacing = 0.0;
    public const double MaxParaSpacing = 1.5;
    public const double DisplayMathParaSpacing = 0.3;
    public const double DefaultLineSpacing = 0.0;   // 0 は指定なし相当（Double.NaN）
    public const double MinLineSpacing = 1.0;
    public const double MaxLineSpacing = 2.0;

    public double ParaMarginPx => FontSize * ParaSpacing;
    public double DisplayMathMarginPx => FontSize * DisplayMathParaSpacing;
    public double EffectiveLineHeight => LineSpacing > 0 ? FontSize * LineSpacing : double.NaN;

    public static PanelStyle From(ConfigStore cfg)
    {
        string fg = cfg.Settings.OverlayFg;
        if (!ColorUtil.IsHexColor(fg)) fg = DefaultFg;
        string bg = cfg.Settings.OverlayBg;
        if (!ColorUtil.IsHexColor(bg)) bg = DefaultBg;

        double a = cfg.Settings.OverlayAlpha;
        if (double.IsNaN(a) || double.IsInfinity(a)) a = DefaultAlpha;
        a = Math.Clamp(a, 0.15, 1.0);

        int sz = Math.Clamp(cfg.Settings.OverlayFontSize, 8, 72);
        return new PanelStyle(fg.ToLowerInvariant(), bg.ToLowerInvariant(),
                              a, cfg.Settings.OverlayFontFamily, sz, cfg.Settings.LatexCopy,
                              cfg.Settings.AnswerParaSpacing, cfg.Settings.AnswerLineSpacing,
                              cfg.EffectiveBool("convert_otsl_table", true),
                              cfg.EffectiveBool("convert_html_table", true),
                              cfg.EffectiveBool("convert_latex_math", true),
                              cfg.EffectiveBool("show_follow_row", false),
                              cfg.Settings.LayoutTextBg);
    }

    /// <summary>外窓（枠の外に出す解答欄）の見た目。色と不透明度は外窓の設定、ほかは枠の中と同じ</summary>
    public static PanelStyle ForStrip(ConfigStore cfg)
    {
        var st = From(cfg);
        string fg = cfg.Settings.StripFg;
        if (!ColorUtil.IsHexColor(fg)) fg = DefaultFg;
        string bg = cfg.Settings.StripBg;
        if (!ColorUtil.IsHexColor(bg)) bg = DefaultBg;
        double a = cfg.Settings.StripAlpha;
        if (double.IsNaN(a) || double.IsInfinity(a)) a = DefaultStripAlpha;
        return st with { Fg = fg.ToLowerInvariant(), Bg = bg.ToLowerInvariant(), Alpha = Math.Clamp(a, 0.15, 1.0) };
    }

    /// <summary>思考欄の薄い文字色（本文の文字色を背景色側へ45%寄せ）</summary>
    public string ThinkHex => MixToward(Fg, Bg, 0.45);

    /// <summary>細スクロールバーの棒（文字色から作る。Tk版 _slim_sb_colors の趣旨）</summary>
    public string ScrollThumbHex => MixToward(Fg, Bg, 0.45);

    /// <summary>色aを色b側へt（0〜1）だけ寄せた #rrggbb。
    /// 読めない値は既定（a=#f0f0f0, b=#202020）で混ぜる。例外じみたら #8a8a8a</summary>
    public static string MixToward(string a, string b, double t)
    {
        try
        {
            var ca = RgbOf(a) ?? (240, 240, 240);
            var cb = RgbOf(b) ?? (32, 32, 32);
            double tt = Math.Clamp(t, 0.0, 1.0);
            int Mix((int x, int y) p) => (int)Math.Round(p.x * (1.0 - tt) + p.y * tt);
            return "#"
                + Mix((ca.Item1, cb.Item1)).ToString("x2", CultureInfo.InvariantCulture)
                + Mix((ca.Item2, cb.Item2)).ToString("x2", CultureInfo.InvariantCulture)
                + Mix((ca.Item3, cb.Item3)).ToString("x2", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "#8a8a8a";
        }
    }

    private static (int, int, int)? RgbOf(string? s)
    {
        if (!ColorUtil.IsHexColor(s)) return null;
        return (
            int.Parse(s!.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(s.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// ストリーミング表示の純粋な道具。
/// delta は毎回「これまでの全文」で来る。
/// 前回表示分の続きなら末尾だけ足す、食い違ったら全文置き換え。
/// 思考欄に出すのは最後の4000字だけ。
/// </summary>
public static class StreamText
{
    public const int ThinkMaxChars = 4000;

    /// <summary>next が prev の続き（同じ文頭で長い）か。同文字列は false（更新なし）</summary>
    public static bool IsContinuation(string prev, string next) =>
        next.Length > prev.Length && next.StartsWith(prev, StringComparison.Ordinal);

    /// <summary>続きのときに末尾へ足す分（IsContinuation=true のときだけ使う）</summary>
    public static string Tail(string prev, string next) => next[prev.Length..];

    /// <summary>思考の長い文は最後の4000字だけ残す</summary>
    public static string ThinkTail(string s) =>
        s.Length <= ThinkMaxChars ? s : s[^ThinkMaxChars..];
}

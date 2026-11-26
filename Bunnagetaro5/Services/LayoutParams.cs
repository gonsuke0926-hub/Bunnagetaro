using System.Text.Json.Nodes;

namespace Bunnagetaro5.Services;

/// <summary>修正J100: 位置の反映（「読み取った位置に並べる」）の細かい値。全体で1つ（人の判断 2026-11-25）。
/// 設定ページの「位置の反映」（上級者モード）で変える。手で書き換えて範囲を外れても、ここで範囲に収める。
/// 修正J109: 段落の分け方・つなぎ方の値（J99〜J106。左右に分ける間・上下につなぐ決まり・行間・字の大きさ）は、
/// 区画をレイアウトのモデルに任せたので消した。残るのは訳の頼み方だけ（OCR_LAYOUT_ROUND2）</summary>
public sealed record LayoutParams(
    int BatchChars,        // layout_batch_chars: 1回にまとめて頼む文字数（0 なら1段落ずつ）
    int LabelWords,        // layout_label_words: この語数以下は短いラベルとして1つずつ頼む
    int LabelChars,        // layout_label_chars: 日本語などはこの文字数以下を短いラベルとする
    double ScoreThresh = LayoutModel.ScoreThreshold)   // 修正J116: layout_score_thresh: 区画判定のしきい値（0.1〜0.95、既定 0.5）
{
    /// <summary>既定（頼み方は sonoba の README の値、しきい値は PP-DocLayoutV3 の既定 0.5）</summary>
    public static readonly LayoutParams Default = new(240, 3, 8, LayoutModel.ScoreThreshold);

    /// <summary>キー・範囲・整数か（設定ページと From で同じ物を使う）</summary>
    public static readonly (string Key, string Name, double Lo, double Hi, bool IsInt, double Def)[] Specs =
    {
        ("layout_score_thresh", "区画判定のしきい値", 0.1, 0.95, false, Default.ScoreThresh),
        ("layout_batch_chars", "1回に頼む文字数", 0, 2000, true, Default.BatchChars),
        ("layout_label_words", "1つずつ頼む短いラベルの語数", 0, 10, true, Default.LabelWords),
        ("layout_label_chars", "1つずつ頼む短いラベルの文字数（日本語など）", 0, 40, true, Default.LabelChars),
    };

    public static LayoutParams From(JsonObject? cfg)
    {
        double V(int i)
        {
            var s = Specs[i];
            double v = s.Def;
            try
            {
                if (cfg?[s.Key] is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    && double.TryParse(jv.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    && !double.IsNaN(d)) v = d;
            }
            catch { }
            v = Math.Clamp(v, s.Lo, s.Hi);
            return s.IsInt ? Math.Round(v) : v;
        }
        return new LayoutParams((int)V(1), (int)V(2), (int)V(3), V(0));
    }
}

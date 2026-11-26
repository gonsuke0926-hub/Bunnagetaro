using System.Drawing;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bunnagetaro5.Services;

/// <summary>レイアウトのモデルが見つけた区画（画素の四角・種類・読む順番）</summary>
public sealed record LayoutRegion(Rectangle R, string Label, float Score, float Order)
{
    /// <summary>行を1つの段落につながない区画（表・図・グラフなど。行ごとに別のまとまりにする）</summary>
    public bool KeepLines => Label is "table" or "chart" or "image" or "header_image" or "footer_image" or "seal" or "algorithm";
}

/// <summary>
/// 文書の区画を見つけるモデル PP-DocLayoutV3（PaddlePaddle・Apache-2.0）。
/// 配る物は有志が ONNX に変換した物（AlexTransformer/PP-DocLayoutV3-onnx）を SHA256 で確かめて同梱する。
/// 入力は画像を 800×800 に縮めた物（BGR・0〜1。モデルの inference.yml のとおり）と、縮めた割合。
/// 出力は区画ごとに「種類の番号・確からしさ・左上・右下（元の画像の画素）・読む順番」。確からしさ 0.5 以上（inference.yml の draw_threshold）だけ使う。
/// GPU（DirectML）で読み込み、だめなら CPU（内蔵 OCR の rec と同じ）。
/// </summary>
public sealed class LayoutModel : IDisposable
{
    public const int Size = 800;
    public const float ScoreThreshold = 0.5f;

    /// <summary>inference.yml の label_list の順</summary>
    internal static readonly string[] Labels =
    {
        "abstract", "algorithm", "aside_text", "chart", "content", "display_formula", "doc_title", "figure_title",
        "footer", "footer_image", "footnote", "formula_number", "header", "header_image", "image", "inline_formula",
        "number", "paragraph_title", "reference", "reference_content", "seal", "table", "text", "vertical_text",
        "vision_footnote",
    };

    private readonly InferenceSession _s;
    public string Backend { get; }

    public LayoutModel(string path, bool failGpuForTest = false)
    {
        if (!failGpuForTest)
        {
            try
            {
                using var gso = PpOcrEngine.GpuOptions();
                _s = new InferenceSession(path, gso);
                Backend = PpOcrEngine.BackendGpu;
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warning("レイアウトのモデル: GPU（DirectML）が使えないので CPU で動かします（" + ex.GetType().Name + ": " + ex.Message + "）");
            }
        }
        using var cso = PpOcrEngine.CpuOptions();
        _s = new InferenceSession(path, cso);
        Backend = PpOcrEngine.BackendCpu;
    }

    /// <summary>exe の横の Models\pp-doclayoutv3\layout.onnx</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Models", "pp-doclayoutv3", "layout.onnx");

    /// <summary>区画を見つける（bgr は w×h の BGR の画素）。読む順番に並べて返す</summary>
    public List<LayoutRegion> Detect(byte[] bgr, int w, int h, float scoreThreshold = ScoreThreshold)
    {
        var rs = PpOcrPost.ResizeBilinear(bgr, w, h, Size, Size);
        int plane = Size * Size;
        var x = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var span = x.Buffer.Span;
        for (int i = 0, j = 0; i < plane; i++, j += 3)
        {
            span[i] = rs[j] / 255f;                 // B
            span[plane + i] = rs[j + 1] / 255f;     // G
            span[plane * 2 + i] = rs[j + 2] / 255f; // R
        }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image", x),
            NamedOnnxValue.CreateFromTensor("im_shape", new DenseTensor<float>(new[] { (float)Size, Size }, new[] { 1, 2 })),
            NamedOnnxValue.CreateFromTensor("scale_factor", new DenseTensor<float>(new[] { (float)Size / h, (float)Size / w }, new[] { 1, 2 })),
        };
        using var res = _s.Run(inputs);
        var o = res.First().AsTensor<float>();
        var rows = new List<LayoutRegion>();
        int n = o.Dimensions[0];
        for (int r = 0; r < n; r++)
        {
            float score = o[r, 1];
            if (score < scoreThreshold) continue;
            int cls = (int)o[r, 0];
            string label = cls >= 0 && cls < Labels.Length ? Labels[cls] : "text";
            int x0 = Math.Clamp((int)Math.Floor(o[r, 2]), 0, w), y0 = Math.Clamp((int)Math.Floor(o[r, 3]), 0, h);
            int x1 = Math.Clamp((int)Math.Ceiling(o[r, 4]), 0, w), y1 = Math.Clamp((int)Math.Ceiling(o[r, 5]), 0, h);
            if (x1 - x0 < 2 || y1 - y0 < 2) continue;
            rows.Add(new LayoutRegion(Rectangle.FromLTRB(x0, y0, x1, y1), label, score, o.Dimensions[1] > 6 ? o[r, 6] : r));
        }
        return Dedupe(rows).OrderBy(q => q.Order).ToList();
    }

    /// <summary>同じ所を2つの種類で出した物（ほぼ重なる）は、確からしい方だけ残す</summary>
    internal static List<LayoutRegion> Dedupe(List<LayoutRegion> rows)
    {
        var keep = new List<LayoutRegion>();
        foreach (var q in rows.OrderByDescending(q => q.Score))
            if (!keep.Any(k => IoU(k.R, q.R) > 0.9)) keep.Add(q);
        return keep;
    }

    internal static double IoU(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = (double)i.Width * i.Height;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter);
    }

    public void Dispose() => _s.Dispose();
}

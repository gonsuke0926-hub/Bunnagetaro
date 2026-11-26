namespace Bunnagetaro5.Services;

/// <summary>
/// 画面変化検知の純粋関数。
/// Display affinity（WDA_EXCLUDEFROMCAPTURE）により解答欄・バーは撮影に
/// 画素差ゼロで写らないため、半透明パネルによる下地の薄れ補正は行わない。
/// BGRA の生バイトから指紋（96x54 の灰度）を作り、バイト列同士を比べる。
/// WPF / GDI 型に依存しないので xunit で合成画像をそのまま渡せる。
/// </summary>
public static class ChangeDetector
{
    /// <summary>1ピクセルを「変わった」とみなす明るさ差（v1.2からの既定値）</summary>
    public const int ChangeDelta = 40;
    /// <summary>将来 閾値を下げる補正を入れるときの下限（DWM合成や動画の微噪雑を数えない）。この移植では未使用</summary>
    public const int MinChangeDelta = 8;
    public const int FpW = 96, FpH = 54;

    /// <summary>撮影1枚。Bgra は 1ピクセル4バイト（青,緑,赤,不使用）を行優先で並べたもの</summary>
    public readonly record struct Frame(byte[] Bgra, int W, int H);

    /// <summary>
    /// 撮影1枚の指紋（Python の convert("L") → resize((96,54), BILINEAR) と同じ順序）。
    /// 明るさは PIL の L と同じ 299/587/114。縮小は各出力マス目が覆う元領域の
    /// 加重平均（BILINEAR 相当。文字スクロール等の細かい要素を取りこぼさない
    /// ことのほうが、ピタリ同一より大事なので面積で集める）。
    /// </summary>
    public static byte[] Fingerprint(Frame f)
    {
        int w = f.W, h = f.H;
        var acc = new double[FpW * FpH];
        var wsum = new double[FpW * FpH];
        for (int y = 0; y < h; y++)
        {
            double y0 = (double)y * FpH / h, y1 = (double)(y + 1) * FpH / h;
            int cy0 = (int)Math.Floor(y0), cy1 = (int)Math.Ceiling(y1);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int luma = (f.Bgra[i + 2] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i] * 114 + 500) / 1000;
                double x0 = (double)x * FpW / w, x1 = (double)(x + 1) * FpW / w;
                int cx0 = (int)Math.Floor(x0), cx1 = (int)Math.Ceiling(x1);
                for (int cy = cy0; cy < cy1; cy++)
                {
                    double oy = Math.Min(y1, cy + 1) - Math.Max(y0, cy);
                    if (oy <= 0) continue;
                    for (int cx = cx0; cx < cx1; cx++)
                    {
                        double ox = Math.Min(x1, cx + 1) - Math.Max(x0, cx);
                        if (ox <= 0) continue;
                        int k = cy * FpW + cx;
                        acc[k] += luma * ox * oy;
                        wsum[k] += ox * oy;
                    }
                }
            }
        }
        var fp = new byte[FpW * FpH];
        for (int k = 0; k < fp.Length; k++)
            fp[k] = wsum[k] > 0 ? (byte)Math.Clamp((int)Math.Round(acc[k] / wsum[k]), 0, 255) : (byte)0;
        return fp;
    }

    /// <summary>指紋同士の「変わったピクセル率」（0〜1。仕様4: 閾値は率で取る）。
    /// delta は「変わった」とみなす明るさ差。Python の getcolors 集計と同じ意味</summary>
    public static double ChangeRatio(byte[] a, byte[] b, int delta = ChangeDelta)
    {
        int n = Math.Min(a.Length, b.Length);
        int changed = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(a[i] - b[i]) > delta) changed++;
        return (double)changed / (FpW * FpH);
    }

    /// <summary>真っ黒枠か（PIL convert("L").getextrema()[1] &lt; 10 と同じ）。黒でないと分かったら即抜ける</summary>
    public static bool IsBlackFrame(Frame f)
    {
        long n = (long)f.W * f.H;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            int luma = (f.Bgra[p + 2] * 299 + f.Bgra[p + 1] * 587 + f.Bgra[p] * 114 + 500) / 1000;
            if (luma >= 10) return false;
        }
        return true;
    }
}

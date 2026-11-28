using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bunnagetaro5.Services;

/// <summary>内蔵 OCR で読んだ段落1つ。X・Y・W・H は画像の幅・高さに対する割合（0〜1）</summary>
public sealed record OcrBlock(string Text, double X, double Y, double W, double H)
{
    /// <summary>画素の矩形 r（画像 w×h の中）から割合を作る。画像の外へはみ出した分は切る</summary>
    public static OcrBlock FromPixels(string text, Rectangle r, int w, int h)
    {
        if (w <= 0 || h <= 0) return new OcrBlock(text, 0, 0, 0, 0);
        double x0 = Math.Clamp(r.Left / (double)w, 0, 1), y0 = Math.Clamp(r.Top / (double)h, 0, 1);
        double x1 = Math.Clamp(r.Right / (double)w, 0, 1), y1 = Math.Clamp(r.Bottom / (double)h, 0, 1);
        return new OcrBlock(text, x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}

/// <summary>
/// 内蔵の OCR（PP-OCRv6 medium / small を ONNX Runtime で動かす）。
/// プリセットの「モデル」に <see cref="ModelName"/> または <see cref="SmallModelName"/> を選ぶと、Ollama を使わずにここで文字を読む。
/// モデルは exe の横の Models\ppocrv6 と Models\ppocrv6-small（ビルドの時に落として同梱する）。初めて使う時に読み込み、
/// 終わるまで置いておく（約0.5GB）。読むのは1度に1枚ずつ（CPU を取り合わない）。
/// 読むのは GPU（DirectML）で、使えない PC では ONNX Runtime の CPU の作りへ戻す。
/// </summary>
public static class BuiltinOcr
{
    /// <summary>プリセットのモデル欄に出す名前（config.json にもこの名前で残る）。
    /// Ollama のモデル名は空白・全角かっこを含まないので重ならない。</summary>
    public const string ModelName = "PP-OCRv6（内蔵）";
    /// <summary>軽量・高速版の内蔵 OCR</summary>
    public const string SmallModelName = "PP-OCRv6 small（内蔵）";
    public static readonly string[] ModelNames = { ModelName, SmallModelName };
    public const string SizeSmall = "small", SizeMedium = "medium";

    public static string ModelForSize(string? size) => (size ?? "").Trim() == SizeMedium ? ModelName : SmallModelName;
    public static string SizeOfModel(string? model) => (model ?? "").Trim() == ModelName ? SizeMedium : SizeSmall;

    public static bool IsBuiltin(string? model)
    {
        var m = (model ?? "").Trim();
        return m == ModelName || m == SmallModelName;
    }

    internal static string NormalizeModel(string? model) => (model ?? "").Trim() == SmallModelName ? SmallModelName : ModelName;

    /// <summary>テストが別の場所のモデルを使う時だけ差し替える</summary>
    internal static string ModelDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Models", "ppocrv6");
    internal static string SmallModelDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Models", "ppocrv6-small");

    internal static string DirOf(string? model) => NormalizeModel(model) == SmallModelName ? SmallModelDir : ModelDir;

    private static readonly string[] Files = { "det.onnx", "rec.onnx", "rec.yml" };

    public static bool ModelsPresent(string? model = null)
    {
        var dir = DirOf(model);
        return Files.All(f => File.Exists(Path.Combine(dir, f)));
    }

    private static readonly object Gate = new();
    private static PpOcrEngine? _engine;
    private static string _engineModel = "";
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private static PpOcrEngine Engine(string? model = null)
    {
        string m = NormalizeModel(model);
        lock (Gate)
        {
            if (_engine is not null && _engineModel == m) return _engine;
            _engine?.Dispose();
            _engine = null;
            _engineModel = "";
            string dir = DirOf(m);
            if (!ModelsPresent(m))
                throw new OllamaException("内蔵の OCR のモデルが見つかりません（" + dir + "）。配布フォルダごと置き直してください");
            var sw = Stopwatch.StartNew();
            _engine = new PpOcrEngine(Path.Combine(dir, "det.onnx"), Path.Combine(dir, "rec.onnx"),
                                      Path.Combine(dir, "rec.yml"));
            _engineModel = m;
            AppLog.Info("内蔵の OCR を読み込みました（" + m + "・" + _engine.Backend + "・" + sw.ElapsedMilliseconds + "ms）");
            return _engine;
        }
    }

    /// <summary>裏で読み込んでおく（起動時・プリセットを切り替えた時。失敗は app.log だけ）。
    /// GPU の時はよく使う幅の段も 1 段ずつ空回ししておき、初回から遅くならないようにする</summary>
    public static Task WarmAsync(string? model = null) => Task.Run(async () =>
    {
        try
        {
            string m = NormalizeModel(model);
            PpOcrEngine eng;
            await RunLock.WaitAsync().ConfigureAwait(false);
            try { eng = Engine(m); }
            finally { RunLock.Release(); }
            if (eng.Backend != PpOcrEngine.BackendGpu) return;
            foreach (int w in PpOcrEngine.WarmWidthSteps)
            {
                await RunLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    PpOcrEngine? cur;
                    lock (Gate) cur = _engineModel == m ? _engine : null;
                    if (cur is null || cur.Backend != PpOcrEngine.BackendGpu) return;
                    cur.WarmWidth(w);
                }
                finally { RunLock.Release(); }
            }
        }
        catch (Exception ex) { AppLog.Warning("内蔵の OCR の読み込みに失敗: " + ex.Message); }
    });

    internal static int[] WarmedWidths(string? model = null) => Engine(model).RecWidthsSeen();

    public static bool Loaded { get { lock (Gate) return _engine is not null; } }

    /// <summary>読み込んだモデルを外す（アプリの終了時）。
    /// 読んでいる最中（ReadAsync・WarmAsync が RunLock を持っている間）に外すと推論中の物を壊すので、
    /// RunLock を待ってから外す。待っても空かない時は外さない（安全な方。終了ならプロセスごと片づく）</summary>
    public static bool Unload(int timeoutMs = 5000)
    {
        if (!RunLock.Wait(timeoutMs))
        {
            AppLog.Warning("内蔵の OCR: 読んでいる最中なのでモデルを外さなかった");
            return false;
        }
        try
        {
            lock (Gate)
            {
                _engine?.Dispose();
                _engine = null;
                _engineModel = "";
            }
            return true;
        }
        finally { RunLock.Release(); }
    }

    /// <summary>PNG（base64）の文字を読み、行ごとに改行でつないで返す。やめる時は OllamaCancelledException。
    /// paragraphs: 画面の折り返しで切れた行を段落の1行につなぐ（訳す前の書き起こし用）</summary>
    public static Task<string> ReadAsync(string imageB64, CancellationToken ct = default,
        Dictionary<string, object?>? diag = null, bool paragraphs = false, string? model = null) =>
        RunAsync(imageB64, ct, diag, model, (r, _, _) =>
        {
            var lines = paragraphs ? PpOcrPost.JoinParagraphs(r.Items) : r.Lines;
            if (diag is not null && paragraphs) diag["builtin_ocr_paragraphs"] = lines.Count;
            return string.Join("\n", lines);
        });

    /// <summary>段落ごとの文と位置。位置は画像の幅・高さに対する割合（0〜1）。
    /// 段落（区画）はレイアウトのモデル（PP-DocLayoutV3）が決める。
    /// まず区画を見つけて onRegions に渡し（文はまだ空。解答欄はその所をマスクする＝今そこを処理していると分かる）、
    /// 次に内蔵 OCR で行を読み、行を「いちばん重なる区画」に入れて区画ごとにつなぐ（AssignToRegions）。
    /// モデルが無い・動かない時は従来の段落のつなぎを使う。
    /// 送る前の画像の加工（ImagePrep.SendPrep）は縦横を同じ割合で変えるだけなので、割合なら撮った枠の中の位置と同じになる。
    /// やめる時は OllamaCancelledException</summary>
    public static Task<List<OcrBlock>> ReadBlocksAsync(string imageB64, CancellationToken ct = default,
        Dictionary<string, object?>? diag = null, string? model = null, Action<IReadOnlyList<OcrBlock>>? onRegions = null,
        float scoreThreshold = LayoutModel.ScoreThreshold, bool dropInside = true) =>
        RunCoreAsync(imageB64, ct, diag, model, (eng, bgr, w, h) =>
        {
            var regions = DetectRegions(bgr, w, h, diag, scoreThreshold, dropInside);
            if (regions is { Count: > 0 })
                onRegions?.Invoke(regions.Select(q => OcrBlock.FromPixels("", q.R, w, h)).ToList());
            ct.ThrowIfCancellationRequested();
            var r = ReadWith(eng, bgr, w, h, ct, diag, model);
            List<(Rectangle R, string S)> paras = regions is { Count: > 0 } && r.WordBoxes is not null && r.WordTexts is not null
                ? PpOcrPost.AssignToRegions(r.WordBoxes, r.WordTexts, regions)
                : PpOcrPost.JoinParagraphItems(r.Items);
            if (diag is not null) diag["builtin_ocr_paragraphs"] = paras.Count;
            return paras.Select(p => OcrBlock.FromPixels(p.S, p.R, w, h)).ToList();
        });

    /// <summary>読む本体（1度に1枚・やめる・diag は ReadAsync と ReadBlocksAsync で同じ）。post で結果の形を決める</summary>
    private static Task<T> RunAsync<T>(string imageB64, CancellationToken ct, Dictionary<string, object?>? diag,
        string? model, Func<PpOcrEngine.Result, int, int, T> post) =>
        RunCoreAsync(imageB64, ct, diag, model, (eng, bgr, w, h) => post(ReadWith(eng, bgr, w, h, ct, diag, model), w, h));

    private static PpOcrEngine.Result ReadWith(PpOcrEngine eng, byte[] bgr, int w, int h, CancellationToken ct,
        Dictionary<string, object?>? diag, string? model)
    {
        var r = eng.Read(bgr, w, h, ct);
        if (diag is not null)
        {
            diag["builtin_ocr_model"] = NormalizeModel(model);
            diag["builtin_ocr_det_ms"] = Math.Round(r.DetMs, 1);
            diag["builtin_ocr_rec_ms"] = Math.Round(r.RecMs, 1);
            diag["builtin_ocr_boxes"] = r.Boxes;
            diag["builtin_ocr_backend"] = eng.Backend;
        }
        return r;
    }

    /// <summary>1度に1枚だけ（RunLock）、画像を BGR にして body を動かす</summary>
    private static async Task<T> RunCoreAsync<T>(string imageB64, CancellationToken ct, Dictionary<string, object?>? diag,
        string? model, Func<PpOcrEngine, byte[], int, int, T> body)
    {
        byte[] png = Convert.FromBase64String(imageB64);
        string m = NormalizeModel(model);
        return await Task.Run(async () =>
        {
            try { await RunLock.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new OllamaCancelledException(); }
            try
            {
                var eng = Engine(m);
                var (bgr, w, h) = DecodeBgr(png);
                return body(eng, bgr, w, h);
            }
            catch (OperationCanceledException) { throw new OllamaCancelledException(); }
            finally { RunLock.Release(); }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // ---- レイアウトのモデル（初めて使う時に読み込む。無い・読めない時は null のまま、従来のつなぎ方） ----
    private static LayoutModel? _layoutModel;
    private static bool _layoutTried;
    internal static string LayoutModelPath = LayoutModel.DefaultPath;

    /// <summary>区画を見つける（RunLock の中で呼ぶ）。モデルが使えなければ null</summary>
    internal static List<LayoutRegion>? DetectRegions(byte[] bgr, int w, int h, Dictionary<string, object?>? diag,
        float scoreThreshold = LayoutModel.ScoreThreshold, bool dropInside = true)
    {
        lock (Gate)
        {
            if (!_layoutTried)
            {
                _layoutTried = true;
                try
                {
                    if (File.Exists(LayoutModelPath))
                    {
                        var sw = Stopwatch.StartNew();
                        _layoutModel = new LayoutModel(LayoutModelPath);
                        AppLog.Info("レイアウトのモデルを読み込みました（" + _layoutModel.Backend + "・" + sw.ElapsedMilliseconds + "ms）");
                    }
                    else AppLog.Warning("レイアウトのモデルがありません: " + LayoutModelPath);
                }
                catch (Exception ex) { AppLog.Warning("レイアウトのモデルを読み込めません: " + ex.Message); }
            }
        }
        if (_layoutModel is null) return null;
        try
        {
            var sw = Stopwatch.StartNew();
            var regions = _layoutModel.Detect(bgr, w, h, scoreThreshold, dropInside);
            if (diag is not null)
            {
                diag["layout_model_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                diag["layout_model_backend"] = _layoutModel.Backend;
                diag["layout_regions"] = regions.Count;
                diag["layout_labels"] = string.Join(",", regions.Select(q => q.Label));
            }
            return regions;
        }
        catch (Exception ex)
        {
            AppLog.Warning("レイアウトのモデルで区画を探せません: " + ex.Message);
            return null;
        }
    }

    /// <summary>VLM で位置の反映をする時に使う。区画を見つけ、区画ごとに切り抜いた画像（PNG の base64）を返す。
    /// 区画が無い・モデルが使えない時は空。内蔵 OCR は使わない（読み込まない）</summary>
    public static Task<(List<OcrBlock> Blocks, List<string> Crops)> DetectRegionCropsAsync(string imageB64,
        CancellationToken ct = default, Dictionary<string, object?>? diag = null,
        float scoreThreshold = LayoutModel.ScoreThreshold, bool dropInside = true)
    {
        byte[] png = Convert.FromBase64String(imageB64);
        return Task.Run(async () =>
        {
            try { await RunLock.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new OllamaCancelledException(); }
            try
            {
                var (bgr, w, h) = DecodeBgr(png);
                var regions = DetectRegions(bgr, w, h, diag, scoreThreshold, dropInside);
                var blocks = new List<OcrBlock>();
                var crops = new List<string>();
                if (regions is null) return (blocks, crops);
                foreach (var q in regions)
                {
                    // 字の端が切れないよう少しだけ広げて切り抜く
                    var r = Rectangle.Intersect(Rectangle.Inflate(q.R, 4, 4), new Rectangle(0, 0, w, h));
                    if (r.Width < 4 || r.Height < 4) continue;
                    blocks.Add(OcrBlock.FromPixels("", q.R, w, h));
                    crops.Add(EncodePngB64(bgr, w, r));
                }
                return (blocks, crops);
            }
            finally { RunLock.Release(); }
        }, CancellationToken.None);
    }

    /// <summary>BGR の画像の r の所を PNG（base64）にする</summary>
    internal static string EncodePngB64(byte[] bgr, int w, Rectangle r)
    {
        var crop = PpOcrPost.Crop(bgr, w, r);
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < r.Height; y++)
                Marshal.Copy(crop, y * r.Width * 3, data.Scan0 + y * data.Stride, r.Width * 3);
        }
        finally { bmp.UnlockBits(data); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>PNG を BGR の並び（1画素3バイト・詰めた行）にする。WPF の画像はスレッドを選ぶので System.Drawing で読む。</summary>
    internal static (byte[] Bgr, int W, int H) DecodeBgr(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var src = new Bitmap(ms);
        int w = src.Width, h = src.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);   // 透過は白の上に置く
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var o = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, o, y * w * 3, w * 3);
            return (o, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }
}

/// <summary>PP-OCRv6 の検出（det）と認識（rec）。前処理・後処理は PaddleOCR の既定に合わせた（横書きの画面向けの作り）。</summary>
public sealed class PpOcrEngine : IDisposable
{
    private readonly InferenceSession _det, _rec;
    private readonly string _detIn, _recIn;
    internal readonly string[] Dict;

    // PaddleOCR の既定（det の inference.yml・PaddleOCR 3.x の既定値）
    internal const int DetLimitMin = 64, DetLimitMax = 4000;
    internal const float DetThresh = 0.2f, BoxThresh = 0.45f;
    internal const double UnclipRatio = 1.4;
    internal const int RecH = 48, RecMinW = 320, RecMaxW = 3200;

    /// <summary>rec（行の読み取り）に使っている仕組み（"DirectML" か "CPU"）。det はいつも CPU</summary>
    public string Backend { get; }

    public const string BackendGpu = "DirectML", BackendCpu = "CPU";

    /// <summary>rec を GPU で動かしている間は、入れる幅を決まった段に切り上げる</summary>
    private readonly bool _bucketRec;

    /// <summary>GPU 推論（DirectML）は入力サイズが変わるたびに、そのサイズ用に作った内部リソース（1つ 35〜100MB）を保持し続ける。
    /// 行の幅はばらばらなので、そのままでは使うほどメモリが増える（60枚で 約9GB）。
    /// rec の幅をこの段に切り上げれば、保持するのは最大でこの数だけになる（右の余りは 0 で埋める）</summary>
    internal static readonly int[] RecWidthSteps = { 320, 640, 960, 1280, 1920, 2560, 3200 };

    /// <summary>起動時の裏温めで先に通しておくよく使う幅の段</summary>
    internal static readonly int[] WarmWidthSteps = { 320, 640, 960, 1280, 1920 };

    private static readonly float[] DetLut0 = MakeLut(0.485f, 0.229f);
    private static readonly float[] DetLut1 = MakeLut(0.456f, 0.224f);
    private static readonly float[] DetLut2 = MakeLut(0.406f, 0.225f);
    private static readonly float[] RecLut = MakeLut(0.5f, 0.5f);

    private static float[] MakeLut(float mean, float std)
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = (i / 255f - mean) / std;
        return t;
    }

    /// <summary>テスト用: rec に入れた幅（GPU なら段の数より増えない）</summary>
    private readonly HashSet<int> _recWidths = new();
    internal int[] RecWidthsSeen() { lock (_recWidths) return _recWidths.Where(w => w > 64).OrderBy(w => w).ToArray(); }

    internal static int BucketRecWidth(int w)
    {
        foreach (var s in RecWidthSteps)
            if (w <= s) return s;
        return RecWidthSteps[^1];
    }

    /// <summary>指定した幅の段を GPU 用に 1 回だけ空回ししてコンパイルしておく</summary>
    internal void WarmWidth(int w)
    {
        if (!_bucketRec) return;
        int W = BucketRecWidth(w);
        lock (_recWidths) { if (_recWidths.Contains(W)) return; }
        var input = new DenseTensor<float>(new[] { 1, 3, RecH, W });
        using var _ = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recIn, input) });
        lock (_recWidths) _recWidths.Add(W);
    }

    public PpOcrEngine(string detPath, string recPath, string dictYml) : this(detPath, recPath, dictYml, failGpuForTest: false) { }

    /// <summary>failGpuForTest: テスト用。GPU（DirectML）の用意がうまくいかなかったことにして、CPU へ戻る道を通す</summary>
    internal PpOcrEngine(string detPath, string recPath, string dictYml, bool failGpuForTest)
    {
        Dict = PpOcrPost.ParseDict(File.ReadLines(dictYml));
        // det（文字の場所を探す）はいつも CPU 実行。det に入れる大きさは窓の大きさで毎回変わり、
        // GPU だと大きさごとに約100MBずつ増え続ける（切り上げても組み合わせが多すぎる）
        using (var detSo = CpuOptions()) _det = new InferenceSession(detPath, detSo);
        // ここから後で失敗した時（rec のモデルが壊れているなど）に、先に作った det を残さない
        try
        {
            _detIn = _det.InputMetadata.Keys.First();
            // rec（行を読む。時間の大半）は、まず GPU（DirectML）で読む。幅は RecWidthSteps に切り上げる。
            // 用意か試しの1回がうまくいかなければ（古い CPU・足りない DLL など）、CPU 実行へ黙って戻す
            InferenceSession? rec = null;
            try
            {
                if (failGpuForTest) throw new InvalidOperationException("テストで GPU（DirectML）を止めた");
                using (var gpuSo = GpuOptions()) rec = new InferenceSession(recPath, gpuSo);
                (_rec, _recIn, _bucketRec) = (rec, rec.InputMetadata.Keys.First(), true);
                TryRun();
                Backend = BackendGpu;
                return;
            }
            catch (Exception ex)
            {
                rec?.Dispose();
                AppLog.Warning("内蔵の OCR: GPU（DirectML）が使えないので CPU で読みます（" + ex.GetType().Name + ": " + ex.Message + "）");
            }
            using (var recSo = CpuOptions()) _rec = new InferenceSession(recPath, recSo);
            try { _recIn = _rec.InputMetadata.Keys.First(); }
            catch { _rec.Dispose(); throw; }
            _bucketRec = false;
            Backend = BackendCpu;
        }
        catch
        {
            _det.Dispose();
            throw;
        }
    }

    /// <summary>OCR の推論・前処理に使うスレッドの数。全コアを使い切ると、クイックぶん投げのマウス・キーの
    /// フック（QuickInputWatcher）の応答が遅れ、OS にフックを黙って外されることがあるので、1コアは空けておく</summary>
    internal static int WorkerThreads { get; } = Math.Max(1, Environment.ProcessorCount - 1);

    // 1行ずつ・アリーナ無しが速さと使うメモリの釣り合いが良い
    internal static SessionOptions CpuOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Clamp(WorkerThreads, 1, 8),
        EnableCpuMemArena = false,
        EnableMemoryPattern = false,
    };

    /// <summary>GPU（DirectML。Windows の DirectX 12 が使える GPU なら NVIDIA・AMD・Intel のどれでも）。
    /// DirectML は「メモリの使い方の先読み」と「並べて動かす」に対応しないので切る（ONNX Runtime の決まり）</summary>
    internal static SessionOptions GpuOptions()
    {
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        so.AppendExecutionProvider_DML(0);
        return so;
    }

    /// <summary>白い小さな画像で det と rec を1回ずつ動かす（GPU は動かして初めて失敗することがあるので、用意の時に確かめる）</summary>
    private void TryRun()
    {
        var white = Enumerable.Repeat((byte)255, 64 * 64 * 3).ToArray();
        Detect(white, 64, 64);
        Recognize(white, 64, 64, new Rectangle(0, 16, 64, 32));
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }

    /// <summary>Items は行の箱と文（段落につなぐ時に使う）</summary>
    /// WordBoxes・WordTexts は行にまとめる前の箱と文（位置の反映で行を分け直す時に使う）
    public sealed record Result(List<string> Lines, double DetMs, double RecMs, int Boxes, List<(Rectangle R, string S)> Items,
        List<Rectangle>? WordBoxes = null, List<string>? WordTexts = null);

    public Result Read(byte[] bgr, int w, int h, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var boxes = Detect(bgr, w, h);
        double detMs = sw.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        sw.Restart();
        // 全行ぶんの rec の入力（1行 184KB〜1.8MB。どれも大きな物の置き場 LOH）を先に一度に作らず、
        // WorkerThreads 行ずつ並べて作っては読む。入れ物は ArrayPool から借りて使い回す（Gen2 の GC を減らす）
        var texts = new List<string>(boxes.Count);
        int chunk = Math.Max(1, WorkerThreads);
        var batch = new RecInput[Math.Min(chunk, boxes.Count)];
        var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = WorkerThreads };
        for (int start = 0; start < boxes.Count; start += chunk)
        {
            int n = Math.Min(chunk, boxes.Count - start);
            try
            {
                if (n > 1) Parallel.For(0, n, opts, i => batch[i] = PrepareRecInput(bgr, w, h, boxes[start + i]));
                else batch[0] = PrepareRecInput(bgr, w, h, boxes[start]);
                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    texts.Add(RunRec(batch[i].Tensor));
                }
            }
            finally
            {
                for (int i = 0; i < n; i++) { batch[i]?.Dispose(); batch[i] = null!; }
            }
        }
        double recMs = sw.Elapsed.TotalMilliseconds;
        var items = PpOcrPost.GroupLineItems(boxes, texts);
        return new Result(items.Select(l => l.S).ToList(), detMs, recMs, boxes.Count, items, boxes, texts);
    }

    private List<Rectangle> Detect(byte[] bgr, int w, int h)
    {
        var (rw, rh) = PpOcrPost.DetSize(w, h);
        var rs = PpOcrPost.ResizeBilinear(bgr, w, h, rw, rh);
        int plane = rw * rh;
        var input = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        var span = input.Buffer.Span;
        var p0 = span.Slice(0, plane);
        var p1 = span.Slice(plane, plane);
        var p2 = span.Slice(plane * 2, plane);
        for (int i = 0, j = 0; i < plane; i++, j += 3)
        {
            p0[i] = DetLut0[rs[j]];
            p1[i] = DetLut1[rs[j + 1]];
            p2[i] = DetLut2[rs[j + 2]];
        }
        using var res = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detIn, input) });
        // 出力を配列へ写さず、そのまま読む（res を片づける前に箱を取り終える）
        var prob = ((DenseTensor<float>)res.First().AsTensor<float>()).Buffer.Span;
        return PpOcrPost.Boxes(prob, rw, rh, w, h);
    }

    private string Recognize(byte[] bgr, int w, int h, Rectangle r)
    {
        using var input = PrepareRecInput(bgr, w, h, r);
        return RunRec(input.Tensor);
    }

    /// <summary>rec の入力1行分。中身は ArrayPool から借りた配列で、Dispose で返す</summary>
    internal sealed class RecInput : IDisposable
    {
        private float[]? _arr;
        public DenseTensor<float> Tensor { get; }

        public RecInput(int w)
        {
            int len = 3 * RecH * w;
            _arr = System.Buffers.ArrayPool<float>.Shared.Rent(len);
            var mem = new Memory<float>(_arr, 0, len);
            mem.Span.Clear();   // 借りた配列は前の中身が残っている。右の余りは 0（PaddleOCR と同じ）
            Tensor = new DenseTensor<float>(mem, new[] { 1, 3, RecH, w });
        }

        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _arr, null);
            if (a is not null) System.Buffers.ArrayPool<float>.Shared.Return(a);
        }
    }

    private RecInput PrepareRecInput(byte[] bgr, int w, int h, Rectangle r)
    {
        var crop = PpOcrPost.Crop(bgr, w, r);
        int cw = r.Width, chh = r.Height;
        if (chh >= cw * 1.5) (crop, cw, chh) = PpOcrPost.Rotate90Ccw(crop, cw, chh); // 縦長は横にして読む（PaddleOCR と同じ）
        int tw = Math.Clamp((int)Math.Ceiling(RecH * (double)cw / chh), 1, RecMaxW);
        int W = Math.Max(RecMinW, tw);
        if (_bucketRec) W = BucketRecWidth(W);   // GPU の覚える大きさを決まった数にする
        lock (_recWidths) _recWidths.Add(W);
        var rs = PpOcrPost.ResizeBilinear(crop, cw, chh, tw, RecH);
        var rec = new RecInput(W);   // 右の余りは 0（PaddleOCR と同じ）
        var span = rec.Tensor.Buffer.Span;
        int hw = RecH * W;
        var p0 = span.Slice(0, hw);
        var p1 = span.Slice(hw, hw);
        var p2 = span.Slice(hw * 2, hw);
        for (int y = 0; y < RecH; y++)
        {
            int dstRow = y * W;
            int srcRow = y * tw * 3;
            for (int x = 0; x < tw; x++)
            {
                int s = srcRow + x * 3;
                int d = dstRow + x;
                p0[d] = RecLut[rs[s]];
                p1[d] = RecLut[rs[s + 1]];
                p2[d] = RecLut[rs[s + 2]];
            }
        }
        return rec;
    }

    private string RunRec(DenseTensor<float> input)
    {
        using var res = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recIn, input) });
        var o = (DenseTensor<float>)res.First().AsTensor<float>();
        // 辞書とモデルの文字の数がずれると、番号がずれた所から先の字が抜ける。黙って読み違えない
        if (o.Dimensions[2] != Dict.Length)
            throw new OllamaException($"内蔵の OCR の辞書（{Dict.Length}字）とモデル（{o.Dimensions[2]}字）が合いません");
        return PpOcrPost.CtcDecode(o.Buffer.Span, o.Dimensions[1], o.Dimensions[2], Dict);
    }

}

/// <summary>前処理・後処理の決まった計算（モデル無しでテストできるよう分けた）</summary>
internal static class PpOcrPost
{
    /// <summary>rec.yml の character_dict を読む。[0] は CTC の空き（blank）、末尾に空白を足す（PaddleOCR の use_space_char）。
    /// 比べ方は必ず Ordinal（言語の規則で比べると、半角の濁点「ﾞ」などの行を「  - 」で始まらないと見て
    /// 一覧をそこで打ち切り、後ろの約900字と空白が抜ける）</summary>
    internal static string[] ParseDict(IEnumerable<string> ymlLines)
    {
        var chars = new List<string> { "" };
        bool inDict = false;
        foreach (var raw in ymlLines)
        {
            if (raw.StartsWith("  character_dict:", StringComparison.Ordinal)) { inDict = true; continue; }
            if (!inDict) continue;
            if (!raw.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (!raw.StartsWith("   ", StringComparison.Ordinal)) inDict = false;
                continue;
            }
            var v = raw.Substring(4);
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') v = v[1..^1].Replace("''", "'");
            else if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = Regex.Unescape(v[1..^1]);
            chars.Add(v);
        }
        chars.Add(" ");
        return chars.ToArray();
    }

    /// <summary>det に入れる大きさ（短い辺が 64 未満なら広げ、長い辺は 4000 まで。32 の倍数）</summary>
    internal static (int W, int H) DetSize(int w, int h)
    {
        double ratio = 1.0;
        int mn = Math.Min(w, h), mx = Math.Max(w, h);
        if (mn < PpOcrEngine.DetLimitMin) ratio = (double)PpOcrEngine.DetLimitMin / mn;
        if (mx * ratio > PpOcrEngine.DetLimitMax) ratio = (double)PpOcrEngine.DetLimitMax / mx;
        int rw = Math.Max(32, (int)Math.Round(w * ratio / 32) * 32);
        int rh = Math.Max(32, (int)Math.Round(h * ratio / 32) * 32);
        return (rw, rh);
    }

    /// <summary>
    /// DB の後処理（横書きの画面向けに簡単にした形）: 確率が DetThresh を超える画素のつながり（8近傍）ごとに
    /// 外接矩形を取り、平均の確率が BoxThresh 未満なら捨て、UnclipRatio で広げて元の画像の座標へ戻す。
    /// </summary>
    internal static List<Rectangle> Boxes(ReadOnlySpan<float> prob, int rw, int rh, int w, int h)
    {
        int plane = rw * rh;
        var seen = new bool[plane];
        var outBoxes = new List<Rectangle>();
        var q = new Queue<int>();
        double fx = (double)w / rw, fy = (double)h / rh;
        for (int i = 0; i < plane; i++)
        {
            if (seen[i] || prob[i] <= PpOcrEngine.DetThresh) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            q.Enqueue(i);
            seen[i] = true;
            while (q.Count > 0)
            {
                int p = q.Dequeue(), x = p % rw, y = p / rw;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= rw || ny >= rh) continue;
                        int ni = ny * rw + nx;
                        if (!seen[ni] && prob[ni] > PpOcrEngine.DetThresh) { seen[ni] = true; q.Enqueue(ni); }
                    }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (Math.Min(bw, bh) < 3) continue;
            double s = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) s += prob[y * rw + x];
            if (s / (bw * bh) < PpOcrEngine.BoxThresh) continue;
            double d = bw * bh * PpOcrEngine.UnclipRatio / (2.0 * (bw + bh));
            int X0 = Math.Clamp((int)Math.Floor((x0 - d) * fx), 0, w - 1);
            int Y0 = Math.Clamp((int)Math.Floor((y0 - d) * fy), 0, h - 1);
            int X1 = Math.Clamp((int)Math.Ceiling((x1 + 1 + d) * fx), X0 + 1, w);
            int Y1 = Math.Clamp((int)Math.Ceiling((y1 + 1 + d) * fy), Y0 + 1, h);
            outBoxes.Add(Rectangle.FromLTRB(X0, Y0, X1, Y1));
        }
        return outBoxes;
    }

    /// <summary>CTC の読み取り（毎コマいちばん確からしい文字を取り、blank と続けて同じ文字を落とす）</summary>
    internal static string CtcDecode(ReadOnlySpan<float> o, int T, int C, string[] dict)
    {
        var sb = new StringBuilder();
        int prev = -1;
        for (int t = 0; t < T; t++)
        {
            int best = ArgMax(o.Slice(t * C, C));
            if (best != 0 && best != prev && best < dict.Length) sb.Append(dict[best]);
            prev = best;
        }
        return sb.ToString().Trim();
    }

    private static int ArgMax(ReadOnlySpan<float> row)
    {
        int n = row.Length;
        if (n == 0) return 0;
        int best = 0;
        float bv = row[0];
        int c = 1;
        int vecLen = Vector<float>.Count;
        if (n >= vecLen)
        {
            var vBest = new Vector<float>(bv);
            int limit = n - vecLen;
            for (c = 0; c <= limit; c += vecLen)
            {
                var v = new Vector<float>(row.Slice(c, vecLen));
                if (!Vector.GreaterThan(v, vBest).Equals(Vector<int>.Zero))
                {
                    for (int k = 0; k < vecLen; k++)
                    {
                        float val = row[c + k];
                        if (val > bv) { bv = val; best = c + k; }
                    }
                    vBest = new Vector<float>(bv);
                }
            }
        }
        for (; c < n; c++)
        {
            float val = row[c];
            if (val > bv) { bv = val; best = c; }
        }
        return best;
    }

    /// <summary>箱を行にまとめる: 上から順に、前の行の最初の箱と高さが半分以上重なれば同じ行。行の中は左から、空白でつなぐ</summary>
    internal static List<string> GroupLines(List<Rectangle> boxes, List<string> texts) =>
        GroupLineItems(boxes, texts).Select(l => l.S).ToList();

    /// <summary>GroupLines と同じまとめ方で、行の文と、行の箱を全部囲む矩形を返す</summary>
    internal static List<(Rectangle R, string S)> GroupLineItems(List<Rectangle> boxes, List<string> texts)
    {
        var items = boxes.Zip(texts).Where(p => p.Second.Length > 0)
            .OrderBy(p => p.First.Y + p.First.Height / 2.0).ThenBy(p => p.First.X).ToList();
        var lines = new List<List<(Rectangle R, string S)>>();
        foreach (var (r, s) in items)
        {
            var last = lines.LastOrDefault();
            if (last is not null)
            {
                var lr = last[0].R;
                int ov = Math.Min(lr.Bottom, r.Bottom) - Math.Max(lr.Top, r.Top);
                if (ov > Math.Min(lr.Height, r.Height) / 2) { last.Add((r, s)); continue; }
            }
            lines.Add(new() { (r, s) });
        }
        return lines.Select(l => (
            l.Select(x => x.R).Aggregate(Rectangle.Union),
            string.Join(" ", l.OrderBy(x => x.R.X).Select(x => x.S)))).ToList();
    }

    /// <summary>検出した文字の四角を、レイアウトのモデルの区画に入れて段落にする（位置の反映の時だけ）。
    /// 決まりはこれだけ: 四角は「いちばん重なる区画」に入れる（四角の半分以上が重なる時。どこにも入らない四角は、従来の段落のつなぎで後ろに足す）。
    /// 区画の中は同じ高さの四角を1行にし（GroupLineItems）、上から順につなぐ（英字どうしは空白、ほかは何も挟まない）。
    /// 表・図などの区画（LayoutRegion.KeepLines）は行をつながず、1行ずつ別のまとまりにする（表の行・列を1つの文にしない）。
    /// 文の無い区画（絵だけなど）は使わない。並びは区画の読む順番（モデルが出す）</summary>
    internal static List<(Rectangle R, string S)> AssignToRegions(List<Rectangle> boxes, List<string> texts, IReadOnlyList<LayoutRegion> regions)
    {
        var inRegion = regions.Select(_ => (B: new List<Rectangle>(), T: new List<string>())).ToList();
        var restB = new List<Rectangle>();
        var restT = new List<string>();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (texts[i].Length == 0) continue;
            var b = boxes[i];
            double area = Math.Max(1.0, (double)b.Width * b.Height);
            int best = -1;
            double bestOv = 0;
            for (int k = 0; k < regions.Count; k++)
            {
                var it = Rectangle.Intersect(b, regions[k].R);
                double ov = it.IsEmpty ? 0 : (double)it.Width * it.Height;
                if (ov > bestOv) { bestOv = ov; best = k; }
            }
            if (best >= 0 && bestOv >= area * 0.5)
            {
                inRegion[best].B.Add(b);
                inRegion[best].T.Add(texts[i]);
            }
            else
            {
                restB.Add(b);
                restT.Add(texts[i]);
            }
        }
        var outList = new List<(Rectangle R, string S)>();
        for (int k = 0; k < regions.Count; k++)
        {
            if (inRegion[k].B.Count == 0) continue;
            var lines = GroupLineItems(inRegion[k].B, inRegion[k].T);
            if (regions[k].KeepLines)
            {
                outList.AddRange(lines);
                continue;
            }
            var sb = new StringBuilder();
            foreach (var (_, s) in lines)
            {
                if (sb.Length > 0) sb.Append(JoinChar(sb[^1].ToString(), s.TrimStart()[..1]));
                sb.Append(s.Trim());
            }
            outList.Add((regions[k].R, sb.ToString()));
        }
        if (restB.Count > 0) outList.AddRange(JoinParagraphItems(GroupLineItems(restB, restT)));
        return outList;
    }

    /// <summary>区画の中で行をつなぐ時に挟む文字（英字どうしは空白、ほかは何も挟まない）</summary>
    private static string JoinChar(string ae, string bs) => Latin.IsMatch(ae) && Latin.IsMatch(bs) ? " " : "";

    // 文の終わりの印（これで終わる行は、次の行とつなげない）
    private static readonly Regex SentenceEnd = new(@"[.!?:;。！？：；…」』）)\]]$", RegexOptions.Compiled);
    // 箇条書き・番号の始まり（これで始まる行は、前の行とつなげない）
    private static readonly Regex ListStart = new(@"^([-–—•・*●○■□▪◦‣⁃※]|\d{1,3}[.)、]|[(（]\d{1,3}[)）])", RegexOptions.Compiled);
    private static readonly Regex Cjk = new(@"[　-ヿ㐀-鿿豈-﫿＀-￯]", RegexOptions.Compiled);
    private static readonly Regex Latin = new(@"[A-Za-z0-9À-ɏ,'""’”-]", RegexOptions.Compiled);

    /// <summary>
    /// 画面の折り返しで切れた行を、段落の1行につなぐ（訳す前の書き起こしだけに使う）。
    /// 内蔵の OCR は画面の見た目の1行を1行で返すので、1つの文が何行にも分かれ、訳（ふつう段落で1行）で上書きすると
    /// 原文の行が下に残っていた。つなぐのは、次の行がすぐ下・同じくらいの高さと左端で、前の行が右端まで届き、
    /// 文の終わりの印で終わらず、次の行が箇条書きで始まらず、つなぎ目の両側が同じ文字の種類（英字どうし・日本語どうし）の時だけ。
    /// 英字どうしは空白、日本語どうしは何も挟まずにつなぐ。
    /// </summary>
    internal static List<string> JoinParagraphs(List<(Rectangle R, string S)> lines) =>
        JoinParagraphItems(lines).Select(p => p.S).ToList();

    /// <summary>JoinParagraphs と同じつなぎ方で、段落の文と、段落の行を全部囲む矩形を返す</summary>
    internal static List<(Rectangle R, string S)> JoinParagraphItems(List<(Rectangle R, string S)> lines)
    {
        var outLines = new List<(Rectangle R, string S)>();
        if (lines.Count == 0) return outLines;
        string cur = lines[0].S;
        var prev = lines[0].R;
        var box = lines[0].R;
        for (int i = 1; i < lines.Count; i++)
        {
            var (r, s) = lines[i];
            string? glue = Glue(prev, cur, r, s);
            if (glue is not null)
            {
                cur = cur + glue + s;
                box = Rectangle.Union(box, r);
            }
            else
            {
                outLines.Add((box, cur));
                cur = s;
                box = r;
            }
            prev = r;
        }
        outLines.Add((box, cur));
        return outLines;
    }

    /// <summary>a（今までの段落の最後の行）と b をつなぐなら間に挟む文字（"" か " "）、つながないなら null</summary>
    private static string? Glue(Rectangle a, string aText, Rectangle b, string bText)
    {
        if (aText.Length == 0 || bText.Length == 0) return null;
        int h = Math.Min(a.Height, b.Height);
        if (h <= 0 || Math.Max(a.Height, b.Height) > h * 1.4) return null;   // 字の大きさが違う（見出しなど）
        int gap = b.Top - a.Bottom;
        if (gap > h * 0.8 || gap < -h / 2) return null;                       // 段落の間が空いている・重なっている
        if (Math.Abs(a.Left - b.Left) > h * 2) return null;                   // 左端がそろっていない
        if (a.Right + h * 3 < b.Right) return null;                           // 前の行が次の行より短い（見出し・段落の最後の行）
        if (a.Width < h * 8) return null;                                     // 前の行がそもそも短い（ラベル・ボタンの字）
        if (SentenceEnd.IsMatch(aText.TrimEnd())) return null;
        if (ListStart.IsMatch(bText.TrimStart())) return null;
        string ae = aText.TrimEnd()[^1..], bs = bText.TrimStart()[..1];
        if (Latin.IsMatch(ae) && Latin.IsMatch(bs)) return " ";
        if (Cjk.IsMatch(ae) && Cjk.IsMatch(bs)) return "";
        return null;
    }

    internal static byte[] Crop(byte[] bgr, int w, Rectangle r)
    {
        var o = new byte[r.Width * r.Height * 3];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(bgr, ((r.Y + y) * w + r.X) * 3, o, y * r.Width * 3, r.Width * 3);
        return o;
    }

    /// <summary>反時計回りに 90 度（numpy の rot90 と同じ向き）</summary>
    internal static (byte[] Px, int W, int H) Rotate90Ccw(byte[] px, int w, int h)
    {
        var o = new byte[px.Length];
        // 新しい画像は幅 h・高さ w。新(x', y') = 元(x = w-1-y', y = x')
        for (int y2 = 0; y2 < w; y2++)
            for (int x2 = 0; x2 < h; x2++)
            {
                int sx = w - 1 - y2, sy = x2;
                Buffer.BlockCopy(px, (sy * w + sx) * 3, o, (y2 * h + x2) * 3, 3);
            }
        return (o, h, w);
    }

    /// <summary>双線形の拡大縮小（OpenCV の INTER_LINEAR と同じ画素の中心合わせ）</summary>
    internal static byte[] ResizeBilinear(byte[] src, int w, int h, int nw, int nh)
    {
        var o = new byte[nw * nh * 3];
        double sx = (double)w / nw, sy = (double)h / nh;
        var xa3 = new int[nw];
        var xb3 = new int[nw];
        var axArr = new double[nw];
        for (int x = 0; x < nw; x++)
        {
            double fx = (x + 0.5) * sx - 0.5;
            int x0 = (int)Math.Floor(fx);
            axArr[x] = fx - x0;
            xa3[x] = Math.Clamp(x0, 0, w - 1) * 3;
            xb3[x] = Math.Clamp(x0 + 1, 0, w - 1) * 3;
        }
        int w3 = w * 3, nw3 = nw * 3;
        void Row(int y)
        {
            double fy = (y + 0.5) * sy - 0.5;
            int y0 = (int)Math.Floor(fy);
            double ay = fy - y0;
            double ay1 = 1 - ay;
            int ya3 = Math.Clamp(y0, 0, h - 1) * w3;
            int yb3 = Math.Clamp(y0 + 1, 0, h - 1) * w3;
            int dstRow = y * nw3;
            for (int x = 0; x < nw; x++)
            {
                double ax = axArr[x], ax1 = 1 - ax;
                int i00 = ya3 + xa3[x], i01 = ya3 + xb3[x];
                int i10 = yb3 + xa3[x], i11 = yb3 + xb3[x];
                int dst = dstRow + x * 3;
                for (int c = 0; c < 3; c++)
                {
                    double v = ay1 * (ax1 * src[i00 + c] + ax * src[i01 + c])
                             + ay * (ax1 * src[i10 + c] + ax * src[i11 + c]);
                    o[dst + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }
        if (nw * nh >= 16384) Parallel.For(0, nh, new ParallelOptions { MaxDegreeOfParallelism = PpOcrEngine.WorkerThreads }, Row);
        else for (int y = 0; y < nh; y++) Row(y);
        return o;
    }
}

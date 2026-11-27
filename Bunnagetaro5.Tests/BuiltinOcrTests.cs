using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>内蔵の OCR（PP-OCRv6 medium）。前処理・後処理の計算と、本物のモデルでの読み取り、送る流れの分かれ方。</summary>
public class BuiltinOcrTests : IDisposable
{
    private readonly List<HttpClient> _clients = new();

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _clients.Clear();
    }

    // ---------------- 前処理・後処理（モデル無し） ----------------

    [Fact]
    public void J17_辞書は先頭がblankで末尾に空白_引用符の書き方を戻す()
    {
        var yml = new[]
        {
            "PostProcess:", "  name: CTCLabelDecode", "  character_dict:",
            "  - '!'", "  - ''''", "  - \\", "  - A", "  - あ", "  - '#'", "PreProcess:", "  - 無関係",
        };
        var d = PpOcrPost.ParseDict(yml);
        Assert.Equal(new[] { "", "!", "'", "\\", "A", "あ", "#", " " }, d);
    }

    [Fact]
    public void J17b_半角の濁点などの行でも辞書を打ち切らない()
    {
        // 言語の規則で比べると「  - ﾞ」は「  - 」で始まらないと見なされ、ここで一覧が切れていた
        var yml = new[] { "  character_dict:", "  - a", "  - 〽", "  - ﾞ", "  - ﾟ", "  - b" };
        Assert.Equal(new[] { "", "a", "〽", "ﾞ", "ﾟ", "b", " " }, PpOcrPost.ParseDict(yml));
    }

    [Fact]
    public void J17b_本物の辞書はモデルの出す文字の数と同じ()
    {
        var d = PpOcrPost.ParseDict(File.ReadLines(Path.Combine(BuiltinOcr.ModelDir, "rec.yml")));
        Assert.Equal(18710, d.Length);   // rec.onnx の出力の幅（blank＋18708字＋空白）
        Assert.Equal(" ", d[^1]);
    }

    [Fact]
    public void J17_detの大きさは32の倍数_小さい画像は短い辺64まで広げ_大きい画像は4000まで()
    {
        Assert.Equal((480, 224), PpOcrPost.DetSize(490, 224));
        var (w, h) = PpOcrPost.DetSize(300, 20);   // 短い辺 20 → 64 へ（3.2 倍）
        Assert.True(h >= 64 && h % 32 == 0 && w % 32 == 0);
        Assert.Equal(960, w);
        var (w2, h2) = PpOcrPost.DetSize(8000, 1000);
        Assert.Equal(4000, w2);
        Assert.Equal(512, h2);
    }

    [Fact]
    public void J17_CTCはblankと続けて同じ文字を落とす()
    {
        var dict = new[] { "", "a", "b", " " };
        // コマごとの一番: a a blank a b b blank
        int[] best = { 1, 1, 0, 1, 2, 2, 0 };
        var o = new float[best.Length * dict.Length];
        for (int t = 0; t < best.Length; t++) o[t * dict.Length + best[t]] = 1f;
        Assert.Equal("aab", PpOcrPost.CtcDecode(o, best.Length, dict.Length, dict));
    }

    [Fact]
    public void J17_箱は上から行にまとめ_行の中は左から空白でつなぐ_空の読みは捨てる()
    {
        var boxes = new List<Rectangle>
        {
            new(200, 10, 80, 20), new(10, 12, 100, 20), new(10, 50, 100, 20), new(10, 90, 50, 20),
        };
        var texts = new List<string> { "右", "左", "二行目", "" };
        Assert.Equal(new[] { "左 右", "二行目" }, PpOcrPost.GroupLines(boxes, texts));
    }

    [Fact]
    public void J23_折り返しで切れた行だけ段落につなぐ()
    {
        // 高さ 20・行の間 6。右端 500 まで届く行は折り返し
        (Rectangle, string) L(int y, int right, string s, int left = 10, int h = 20) => (new Rectangle(left, y, right - left, h), s);
        var lines = new List<(Rectangle R, string S)>
        {
            L(0, 500, "The quick brown fox jumps"),            // 英字どうしは空白でつなぐ
            L(26, 498, "over the lazy dog and keeps"),
            L(52, 200, "running."),
            L(110, 500, "Second paragraph starts here"),       // 段落の間が空いている
            L(136, 300, "and ends."),
            L(162, 500, "内蔵の OCR は Ollama を使わずに、アプリの中で"),   // 日本語どうしは何も挟まない
            L(188, 260, "文字を読みます。"),
            L(214, 120, "Settings"),                           // 短い行（ラベル）はつながない
            L(240, 140, "Connection"),
            L(266, 500, "This line ends with a period."),      // 文の終わりの印で終わる
            L(292, 500, "Next sentence on its own line"),
            L(318, 500, "・最初の項目がとても長くて右端まで届いている"),
            L(344, 200, "・次の項目"),                          // 箇条書きの始まりはつながない
            L(370, 500, "日本語の行が右端まで届いて"),
            L(396, 300, "English starts here"),                // 字の種類が違うとつながない
            L(422, 500, "Big heading text", h: 40),            // 字の大きさが違う
        };
        Assert.Equal(new[]
        {
            "The quick brown fox jumps over the lazy dog and keeps running.",
            "Second paragraph starts here and ends.",
            "内蔵の OCR は Ollama を使わずに、アプリの中で文字を読みます。",
            "Settings",
            "Connection",
            "This line ends with a period.",
            "Next sentence on its own line",
            "・最初の項目がとても長くて右端まで届いている",
            "・次の項目",
            "日本語の行が右端まで届いて",
            "English starts here",
            "Big heading text",
        }, PpOcrPost.JoinParagraphs(lines));
    }

    [Fact]
    public void J95_段落の位置は段落の行を全部囲み_文はJoinParagraphsと同じ()
    {
        (Rectangle, string) L(int y, int right, string s, int left = 10, int h = 20) => (new Rectangle(left, y, right - left, h), s);
        var lines = new List<(Rectangle R, string S)>
        {
            L(0, 500, "The quick brown fox jumps"),
            L(26, 498, "over the lazy dog and keeps"),
            L(52, 200, "running."),
            L(110, 300, "Settings", left: 40),
        };
        var items = PpOcrPost.JoinParagraphItems(lines);
        Assert.Equal(PpOcrPost.JoinParagraphs(lines), items.Select(i => i.S).ToList());
        Assert.Equal(2, items.Count);
        Assert.Equal(Rectangle.FromLTRB(10, 0, 500, 72), items[0].R);    // 3行を全部囲む
        Assert.Equal(Rectangle.FromLTRB(40, 110, 300, 130), items[1].R);
        Assert.Empty(PpOcrPost.JoinParagraphItems(new List<(Rectangle R, string S)>()));
    }

    [Fact]
    public void J95_位置は画像の幅と高さに対する割合_はみ出しは切る()
    {
        var b = OcrBlock.FromPixels("a", Rectangle.FromLTRB(50, 20, 150, 60), 200, 100);
        Assert.Equal("a", b.Text);
        Assert.Equal(0.25, b.X, 6); Assert.Equal(0.2, b.Y, 6);
        Assert.Equal(0.5, b.W, 6); Assert.Equal(0.4, b.H, 6);
        var c = OcrBlock.FromPixels("b", Rectangle.FromLTRB(-10, 90, 250, 120), 200, 100);
        Assert.Equal(0, c.X);
        Assert.Equal(1, c.W);
        Assert.Equal(0.9, c.Y, 6);
        Assert.Equal(0.1, c.H, 6);
        Assert.Equal(new OcrBlock("c", 0, 0, 0, 0), OcrBlock.FromPixels("c", new Rectangle(1, 1, 1, 1), 0, 0));
    }

    [Fact]
    public void J17_確率の地図から箱を取り_薄い所は捨て_元の座標へ戻す()
    {
        int rw = 64, rh = 32;
        var prob = new float[rw * rh];
        void Fill(int x0, int y0, int x1, int y1, float v)
        {
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) prob[y * rw + x] = v;
        }
        Fill(4, 4, 30, 10, 0.9f);    // 濃い → 残る
        Fill(40, 20, 60, 26, 0.3f);  // 閾値は超えるが平均が BoxThresh 未満 → 捨てる
        Fill(50, 2, 51, 3, 0.9f);    // 小さすぎる → 捨てる
        var boxes = PpOcrPost.Boxes(prob, rw, rh, 128, 64);   // 元の画像は2倍
        var b = Assert.Single(boxes);
        // 外接矩形 (4,4)-(31,11) を広げてから2倍
        Assert.True(b.Left < 8 && b.Top < 8 && b.Right > 62 && b.Bottom > 22);
        Assert.True(b.Left >= 0 && b.Top >= 0 && b.Right <= 128 && b.Bottom <= 64);
    }

    [Fact]
    public void J17_縦長は反時計回りに90度回す()
    {
        // 幅2・高さ3。画素の値 = 番号（1画素3バイトとも同じ値）
        int w = 2, h = 3;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) px[i * 3] = px[i * 3 + 1] = px[i * 3 + 2] = (byte)i;
        var (o, nw, nh) = PpOcrPost.Rotate90Ccw(px, w, h);
        Assert.Equal((3, 2), (nw, nh));
        // 元: [0 1 / 2 3 / 4 5] → 反時計回り: [1 3 5 / 0 2 4]
        Assert.Equal(new byte[] { 1, 3, 5, 0, 2, 4 }, Enumerable.Range(0, 6).Select(i => o[i * 3]).ToArray());
    }

    [Fact]
    public void J17_双線形の縮小は一様な色を変えない()
    {
        var px = Enumerable.Repeat((byte)123, 10 * 7 * 3).ToArray();
        var o = PpOcrPost.ResizeBilinear(px, 10, 7, 4, 3);
        Assert.All(o, v => Assert.Equal(123, v));
    }

    [Fact]
    public void J25_SIMDのCTC読み取りと並列リサイズは端数や同点でも元の計算と同じ()
    {
        // クラス数 37（SIMD 幅 8 の倍数でない）、負の値・同点・SIMD 端数・blank/重複の折り畳み
        var dict = new string[37];
        dict[0] = "";
        for (int i = 1; i <= 26; i++) dict[i] = ((char)('a' + i - 1)).ToString();
        for (int i = 27; i <= 35; i++) dict[i] = ((char)('0' + i - 27)).ToString();
        dict[36] = " ";
        const int T = 7, C = 37;
        var o = Enumerable.Repeat(-5f, T * C).ToArray();
        o[0 * C + 35] = -1.2f;                         // t=0: 末尾の端数（8）が負値の中で最大
        o[1 * C + 3] = 2.5f; o[1 * C + 29] = 2.5f;     // t=1: 同点は前の番号（c）が勝つ
        o[2 * C + 3] = 2.5f;                           // t=2: 同じ文字（c）の連続は落ちる
        o[3 * C + 0] = 0.1f;                           // t=3: blank
        o[4 * C + 10] = -0.5f;                         // t=4: j
        o[5 * C + 36] = 1.0f;                          // t=5: 末尾の端数（空白）
        o[6 * C + 11] = 3.0f;                          // t=6: k
        Assert.Equal("8cj k", PpOcrPost.CtcDecode(o, T, C, dict));

        static byte[] ScalarResize(byte[] src, int w, int h, int nw, int nh)
        {
            var r = new byte[nw * nh * 3];
            double sx = (double)w / nw, sy = (double)h / nh;
            for (int y = 0; y < nh; y++)
            {
                double fy = (y + 0.5) * sy - 0.5;
                int y0 = (int)Math.Floor(fy);
                double ay = fy - y0;
                int ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
                for (int x = 0; x < nw; x++)
                {
                    double fx = (x + 0.5) * sx - 0.5;
                    int x0 = (int)Math.Floor(fx);
                    double ax = fx - x0;
                    int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1);
                    for (int c = 0; c < 3; c++)
                    {
                        double v = (1 - ay) * ((1 - ax) * src[(ya * w + xa) * 3 + c] + ax * src[(ya * w + xb) * 3 + c])
                                 + ay * ((1 - ax) * src[(yb * w + xa) * 3 + c] + ax * src[(yb * w + xb) * 3 + c]);
                        r[(y * nw + x) * 3 + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                    }
                }
            }
            return r;
        }

        var rnd = new Random(25);
        var small = new byte[37 * 23 * 3];
        rnd.NextBytes(small);
        Assert.Equal(ScalarResize(small, 37, 23, 19, 13), PpOcrPost.ResizeBilinear(small, 37, 23, 19, 13));

        var large = new byte[300 * 200 * 3];
        rnd.NextBytes(large);
        Assert.Equal(ScalarResize(large, 300, 200, 192, 128), PpOcrPost.ResizeBilinear(large, 300, 200, 192, 128));
    }

    [Fact]
    public void J17_名前で内蔵を見分け_新しい設定のOCRプリセットは内蔵を使う()
    {
        Assert.True(BuiltinOcr.IsBuiltin(" " + BuiltinOcr.ModelName + " "));
        Assert.False(BuiltinOcr.IsBuiltin("glm-ocr:latest"));
        Assert.False(BuiltinOcr.IsBuiltin(null));
        var ocr = ConfigStore.DefaultPresets().OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "OCR");
        Assert.Equal("", ocr["model"]!.GetValue<string>());
        Assert.Equal(ConfigStore.ThrowModeBuiltinOcr, ocr["settings"]!["throw_mode"]!.GetValue<string>());
        Assert.Equal(BuiltinOcr.SizeSmall, ocr["settings"]!["builtin_ocr_size"]!.GetValue<string>());
    }

    // ---------------- 本物のモデル（ビルドで出力先に置いたもの） ----------------

    private static readonly string[] Truth =
    {
        "スクショはローカルAIにぶん投げろ！",
        "画面上の好きな範囲をキャプチャ枠で囲みます。",
        "The quick brown fox jumps over the lazy dog. 1234567890",
        "吾輩は猫である。名前はまだ無い。",
    };

    private static string Png(string[] lines, string font = "Yu Gothic UI", int px = 16)
    {
        using var f = new Font(font, px, GraphicsUnit.Pixel);
        int lh = (int)(px * 1.6), w = lines.Max(l => System.Windows.Forms.TextRenderer.MeasureText(l, f).Width);
        using var bmp = new Bitmap(w + 24, lh * lines.Length + 24, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            for (int i = 0; i < lines.Length; i++)
                System.Windows.Forms.TextRenderer.DrawText(g, lines[i], f, new Point(12, 12 + i * lh), Color.Black, Color.White);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string Norm(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC), @"\s", "");

    private static int Lev(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    [Fact]
    public void J17_モデルは出力先に置かれている()
    {
        Assert.True(BuiltinOcr.ModelsPresent(), "Models\\ppocrv6 が無い: " + BuiltinOcr.ModelDir);
    }

    [Fact]
    public async Task J17_本物のモデルで日本語と英語の画面を行ごとに読む()
    {
        var diag = new Dictionary<string, object?>();
        var text = await BuiltinOcr.ReadAsync(Png(Truth), default, diag);
        var lines = text.Split('\n');
        Assert.Equal(Truth.Length, lines.Length);
        string got = Norm(text), want = Norm(string.Concat(Truth));
        int err = Lev(got, want);
        Assert.True(err * 100 <= want.Length * 3, $"文字の誤りが 3% を超えた（{err}/{want.Length}）: {text}");
        Assert.Equal(Truth.Length, (int)diag["builtin_ocr_boxes"]!);
        // 単語の間の空白が詰まらない
        Assert.Contains("The quick brown fox jumps over the lazy dog.", text);
    }

    [Fact]
    public async Task J21_J108_このPCではGPUのDirectMLで読む()
    {
        var diag = new Dictionary<string, object?>();
        await BuiltinOcr.ReadAsync(Png(Truth), default, diag);
        Assert.Equal(PpOcrEngine.BackendGpu, (string)diag["builtin_ocr_backend"]!);
    }

    [Fact]
    public async Task J35_Unloadは読んでいる最中なら読み終わるのを待ってから外す()
    {
        string png = Png(Truth);
        await BuiltinOcr.ReadAsync(png);   // 先に読み込んでおく
        var reading = BuiltinOcr.ReadAsync(png);
        bool unloaded = await Task.Run(() => BuiltinOcr.Unload(60000));
        Assert.True(unloaded);
        Assert.False(BuiltinOcr.Loaded);
        var text = await reading;   // 外す前に読み終わっていて、途中で壊れていない
        Assert.Equal(Truth.Length, text.Split('\n').Length);
        Assert.Equal(text, await BuiltinOcr.ReadAsync(png));   // 外した後もまた読み込み直して読める
    }

    [Fact]
    public void J35_recのモデルが読めなければ作るのに失敗し_例外を投げる()
    {
        string P(string f) => Path.Combine(BuiltinOcr.ModelDir, f);
        string bad = Path.Combine(Path.GetTempPath(), "bunnagetaro_bad_rec_" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4 });
        try
        {
            Assert.ThrowsAny<Exception>(() => new PpOcrEngine(P("det.onnx"), bad, P("rec.yml")));
            Assert.ThrowsAny<Exception>(() => new PpOcrEngine(P("det.onnx"), bad, P("rec.yml"), failGpuForTest: true));
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void J21_J108_GPUが使えなければCPUへ戻り_同じ文を読む()
    {
        string P(string f) => Path.Combine(BuiltinOcr.ModelDir, f);
        var (bgr, w, h) = BuiltinOcr.DecodeBgr(Convert.FromBase64String(Png(Truth)));
        using var ov = new PpOcrEngine(P("det.onnx"), P("rec.onnx"), P("rec.yml"));
        using var cpu = new PpOcrEngine(P("det.onnx"), P("rec.onnx"), P("rec.yml"), failGpuForTest: true);
        Assert.Equal(PpOcrEngine.BackendGpu, ov.Backend);
        Assert.Equal(PpOcrEngine.BackendCpu, cpu.Backend);
        var a = ov.Read(bgr, w, h).Lines;
        var b = cpu.Read(bgr, w, h).Lines;
        Assert.Equal(Truth.Length, a.Count);
        Assert.Equal(a, b);   // 仕組みを替えても読めた文は同じ（計測の時も3枚とも同じだった）
    }

    [Fact]
    public void J24_recの幅は決まった段に切り上げる()
    {
        Assert.Equal(320, PpOcrEngine.BucketRecWidth(320));
        Assert.Equal(640, PpOcrEngine.BucketRecWidth(321));
        Assert.Equal(1280, PpOcrEngine.BucketRecWidth(1000));
        Assert.Equal(1920, PpOcrEngine.BucketRecWidth(1281));
        Assert.Equal(3200, PpOcrEngine.BucketRecWidth(3200));
        Assert.Equal(3200, PpOcrEngine.BucketRecWidth(5000));
    }

    [Fact]
    public void J24_GPUでは幅のちがう行をたくさん読んでもrecに入れる幅は段の数より増えない()
    {
        string P(string f) => Path.Combine(BuiltinOcr.ModelDir, f);
        using var ov = new PpOcrEngine(P("det.onnx"), P("rec.onnx"), P("rec.yml"));
        using var cpu = new PpOcrEngine(P("det.onnx"), P("rec.onnx"), P("rec.yml"), failGpuForTest: true);
        var rnd = new Random(7);
        for (int i = 0; i < 12; i++)
        {
            // 行の長さがばらばらの画像（前は幅ごとに OpenVINO が覚えてメモリが増え続けた）
            var lines = Enumerable.Range(0, 3).Select(_ => "Width test " + new string('w', 3 + rnd.Next(60))).ToArray();
            var (bgr, w, h) = BuiltinOcr.DecodeBgr(Convert.FromBase64String(Png(lines)));
            Assert.Equal(cpu.Read(bgr, w, h).Lines, ov.Read(bgr, w, h).Lines);   // 切り上げても読めた文は同じ
        }
        var seen = ov.RecWidthsSeen();
        Assert.All(seen, x => Assert.Contains(x, PpOcrEngine.RecWidthSteps));
        Assert.True(seen.Length <= PpOcrEngine.RecWidthSteps.Length);
        Assert.True(cpu.RecWidthsSeen().Length > seen.Length, "CPU の方は幅をそのまま入れる（比べのため）");
    }

    [Fact]
    public async Task J25_WarmAsyncでよく使うrecの幅を先に温める()
    {
        await BuiltinOcr.WarmAsync();
        var seen = BuiltinOcr.WarmedWidths();
        foreach (var w in PpOcrEngine.WarmWidthSteps)
            Assert.Contains(w, seen);
    }

    [Fact]
    public async Task J17_やめるとOllamaCancelledExceptionで止まる()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OllamaCancelledException>(() => BuiltinOcr.ReadAsync(Png(Truth), cts.Token));
    }

    // ---------------- 送る流れ（Ollama は偽物。内蔵の時は呼ばれない） ----------------

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            const string part = """{"model":"t","message":{"role":"assistant","content":"訳文。"},"done":false}""";
            const string done = """{"message":{},"done":true,"done_reason":"stop","eval_count":3,"eval_duration":1000000000}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(part + "\n" + done + "\n", Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }

    private StubHandler UseStub()
    {
        var h = new StubHandler();
        _clients.Add(new HttpClient(h));
        OllamaClient.SetHttp(_clients[^1]);
        return h;
    }

    private static JsonObject Cfg(string model, bool split) => new()
    {
        ["base_url"] = "http://test.ollama.local",
        ["model"] = model,
        ["prompt"] = "日本語に訳して",
        ["ollama_num_ctx"] = 4096,
        ["ollama_num_predict"] = 1024,
        ["ollama_temperature"] = 0.5,
        ["ollama_keep_alive_min"] = 30,
        ["ollama_think"] = "auto",
        ["ocr_split_mode"] = split,
    };

    [Fact]
    public async Task J17_モデルが内蔵ならOllamaに送らず読んだ文を返す()
    {
        var stub = UseStub();
        var deltas = new List<string>();
        var diag = new Dictionary<string, object?>();
        var res = await TranslatePipeline.TranslateImageAsync(Cfg(BuiltinOcr.ModelName, true), Png(Truth), deltas.Add, null, diag: diag);
        Assert.Empty(stub.Bodies);
        Assert.Equal("builtin_ocr", diag["pipeline"]);
        Assert.Contains("吾輩は猫", res);
        Assert.Equal(res, Assert.Single(deltas));
    }

    [Fact]
    public async Task J17_文字起こしのOCRプリセットが内蔵なら書き起こしは内蔵で_訳だけOllamaへ送る()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", true);
        var ocr = Cfg(BuiltinOcr.ModelName, false);
        ocr["prompt"] = "Text Recognition:";
        cfg[ConfigStore.OcrStageKey] = ocr;
        var diag = new Dictionary<string, object?>();
        var res = await TranslatePipeline.TranslateImageAsync(cfg, Png(Truth), null, null, diag: diag);
        Assert.Equal("訳文。", res);
        var body = JsonDocument.Parse(Assert.Single(stub.Bodies)).RootElement;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        var content = body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("吾輩は猫", content);   // 書き起こした文が訳へ渡る
        Assert.Equal(BuiltinOcr.ModelName, diag["ocr_model"]);
    }

    // 画面の幅で折り返した段落2つ（英語・日本語）
    private static readonly string[] Wrapped =
    {
        "The built-in OCR reads the text on the screen without",
        "using Ollama, and the translation model then turns the",
        "whole paragraph into Japanese in a single pass.",
        "",
        "",
        "内蔵の OCR は画面の文字をアプリの中で読み、そのあと翻訳の",
        "モデルが段落ごとまとめて日本語に直します。",
    };

    [Fact]
    public async Task J23_本物のモデルで_折り返しで切れた行を段落につないで読む()
    {
        var plain = await BuiltinOcr.ReadAsync(Png(Wrapped));
        Assert.Equal(5, plain.Split('\n').Length);                          // そのままなら見た目の行ごと
        var diag = new Dictionary<string, object?>();
        var para = await BuiltinOcr.ReadAsync(Png(Wrapped), default, diag, paragraphs: true);
        var lines = para.Split('\n');
        Assert.Equal(2, lines.Length);                                      // 段落ごと
        Assert.Equal(2, (int)diag["builtin_ocr_paragraphs"]!);
        Assert.Contains("without using Ollama", lines[0]);                  // 英字のつなぎ目は空白
        Assert.Contains("turns the whole paragraph", lines[0]);
        Assert.Contains("翻訳のモデル", Norm(lines[1]));                     // 日本語のつなぎ目は何も挟まない
    }

    [Fact]
    public async Task J95_本物のモデルで_段落ごとの文と位置を割合で読む()
    {
        var para = await BuiltinOcr.ReadAsync(Png(Wrapped), default, null, paragraphs: true);
        var diag = new Dictionary<string, object?>();
        var blocks = await BuiltinOcr.ReadBlocksAsync(Png(Wrapped), default, diag);
        Assert.Equal(para.Split('\n'), blocks.Select(b => b.Text).ToArray());   // 文は段落の読みと同じ
        Assert.Equal(2, (int)diag["builtin_ocr_paragraphs"]!);
        Assert.All(blocks, b =>
        {
            Assert.InRange(b.X, 0, 1); Assert.InRange(b.Y, 0, 1);
            Assert.True(b.W > 0 && b.H > 0 && b.X + b.W <= 1.0000001 && b.Y + b.H <= 1.0000001);
        });
        Assert.True(blocks[0].Y + blocks[0].H <= blocks[1].Y + 1e-6);         // 上の段落は下の段落より上
    }

    [Fact]
    public async Task J23_文字起こしで内蔵なら段落につないだ文を訳へ渡し_初めに出す原文も段落ごと()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", true);
        cfg[ConfigStore.OcrStageKey] = Cfg(BuiltinOcr.ModelName, false);
        var deltas = new List<string>();
        await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), deltas.Add, null);
        var body = JsonDocument.Parse(Assert.Single(stub.Bodies)).RootElement;
        var content = body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains("without using Ollama", content);
        Assert.Equal(2, deltas[0].Split('\n').Length);                      // 上書きの元になる原文も2行（段落）
    }

    [Fact]
    public async Task J96_位置の反映がONなら段落の位置を渡し_塊の行が合わなければ1段落ずつ頼み直す()
    {
        var stub = UseStub();   // いつも1行の「訳文。」を返す
        var cfg = Cfg("test-model", true);
        cfg[ConfigStore.OcrStageKey] = Cfg(BuiltinOcr.ModelName, false);
        cfg["builtin_ocr_layout"] = true;
        IReadOnlyList<OcrBlock>? layout = null;
        var deltas = new List<string>();
        var diag = new Dictionary<string, object?>();
        var text = await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), deltas.Add, null, default, 0, diag, b => layout = b);
        Assert.NotNull(layout);
        Assert.Equal(2, layout!.Count);
        Assert.Equal(2, deltas[0].Split('\n').Length);              // 初めは原文（1行＝1段落）
        Assert.Contains("without using Ollama", deltas[0]);
        // 2段落を1つの塊で頼み → 1行しか返らない → 1段落ずつ頼み直す（全部で3回）
        Assert.Equal(3, stub.Bodies.Count);
        Assert.Equal("訳文。\n訳文。", text);                           // 1行＝1段落の答え
        Assert.Equal("ocr_layout", diag["pipeline"]);
        Assert.Equal(1, diag["layout_retries"]);
        Assert.All(deltas, d => Assert.Equal(2, d.Split('\n').Length));   // 途中もずっと段落の数の行
    }

    [Fact]
    public async Task J96_位置の反映がOFFなら今までと同じく1回で訳し位置は渡さない()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", true);
        cfg[ConfigStore.OcrStageKey] = Cfg(BuiltinOcr.ModelName, false);
        bool called = false;
        await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), null, null, default, 0, null, _ => called = true);
        Assert.False(called);
        Assert.Single(stub.Bodies);
    }

    [Fact]
    public async Task J96_内蔵OCRのみで位置の反映がONなら段落ごとの文と位置を返しOllamaは使わない()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", false);
        cfg["throw_mode"] = ConfigStore.ThrowModeBuiltinOcr;
        cfg["builtin_ocr_layout"] = true;
        IReadOnlyList<OcrBlock>? layout = null;
        var text = await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), null, null, default, 0, null, b => layout = b);
        Assert.Empty(stub.Bodies);
        Assert.Equal(2, layout!.Count);
        Assert.Equal(layout.Select(b => b.Text).ToArray(), text.Split('\n'));
    }

    [Fact]
    public async Task J17_内蔵では追加の問いはできないと言って止まる()
    {
        var stub = UseStub();
        var ex = await Assert.ThrowsAsync<OllamaException>(() =>
            TranslatePipeline.FollowupAsync(Cfg(BuiltinOcr.ModelName, false), "これは何？", "前の答え", null, null, null));
        Assert.Contains("追加の問い", ex.Message);
        Assert.Empty(stub.Bodies);
    }

    [Fact]
    public async Task J26_smallも内蔵と見分け_出力先に置かれ_本物のモデルで日英を読める()
    {
        Assert.True(BuiltinOcr.IsBuiltin(" " + BuiltinOcr.SmallModelName + " "));
        Assert.True(BuiltinOcr.ModelsPresent(BuiltinOcr.SmallModelName), "Models\\ppocrv6-small が無い: " + BuiltinOcr.SmallModelDir);
        var diag = new Dictionary<string, object?>();
        var text = await BuiltinOcr.ReadAsync(Png(Truth), default, diag, model: BuiltinOcr.SmallModelName);
        var lines = text.Split('\n');
        Assert.Equal(Truth.Length, lines.Length);
        string got = Norm(text), want = Norm(string.Concat(Truth));
        int err = Lev(got, want);
        Assert.True(err * 100 <= want.Length * 4, $"small の文字の誤りが 4% を超えた（{err}/{want.Length}）: {text}");
        Assert.Equal(BuiltinOcr.SmallModelName, diag["builtin_ocr_model"]);
    }

    [Fact]
    public async Task J26_文字起こしのOCRがsmallならsmallで読む()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", true);
        cfg[ConfigStore.OcrStageKey] = Cfg(BuiltinOcr.SmallModelName, false);
        var diag = new Dictionary<string, object?>();
        var res = await TranslatePipeline.TranslateImageAsync(cfg, Png(Truth), null, null, diag: diag);
        Assert.Equal("訳文。", res);
        Assert.Single(stub.Bodies);
        Assert.Equal(BuiltinOcr.SmallModelName, diag["ocr_model"]);
        Assert.Equal(BuiltinOcr.SmallModelName, diag["builtin_ocr_model"]);
    }

    [Fact]
    public async Task J27_throw_modeがbuiltin_ocrならOllamaを呼ばず指定サイズで読む()
    {
        var stub = UseStub();
        var cfg = Cfg("", false);
        cfg["throw_mode"] = ConfigStore.ThrowModeBuiltinOcr;
        cfg["builtin_ocr_size"] = BuiltinOcr.SizeSmall;
        var diag = new Dictionary<string, object?>();
        var res = await TranslatePipeline.TranslateImageAsync(cfg, Png(Truth), null, null, diag: diag);
        Assert.Empty(stub.Bodies);
        Assert.Contains("スクショ", res);
        Assert.Equal(BuiltinOcr.SmallModelName, diag["builtin_ocr_model"]);
    }

    [Fact]
    public async Task J27_throw_modeがocrでocr_engineがbuiltinとvlmで切り替わる()
    {
        var stub = UseStub();
        var cfgBuiltin = Cfg("test-model", true);
        cfgBuiltin["throw_mode"] = ConfigStore.ThrowModeOcr;
        cfgBuiltin["ocr_engine"] = ConfigStore.OcrEngineBuiltin;
        cfgBuiltin["builtin_ocr_size"] = BuiltinOcr.SizeSmall;
        var diag1 = new Dictionary<string, object?>();
        var res1 = await TranslatePipeline.TranslateImageAsync(cfgBuiltin, Png(Truth), null, null, diag: diag1);
        Assert.Equal("訳文。", res1);
        Assert.Single(stub.Bodies);
        Assert.Equal(BuiltinOcr.SmallModelName, diag1["ocr_model"]);

        stub.Bodies.Clear();
        var cfgVlm = Cfg("test-model", true);
        cfgVlm["throw_mode"] = ConfigStore.ThrowModeOcr;
        cfgVlm["ocr_engine"] = ConfigStore.OcrEngineVlm;
        cfgVlm["ocr_vlm_model"] = "glm-ocr:latest";
        cfgVlm["ocr_vlm_prompt"] = "Text Recognition:";
        cfgVlm[ConfigStore.OcrStageKey] = Cfg("glm-ocr:latest", false);
        var diag2 = new Dictionary<string, object?>();
        var res2 = await TranslatePipeline.TranslateImageAsync(cfgVlm, Png(Truth), null, null, diag: diag2);
        Assert.Equal("訳文。", res2);
        Assert.Equal(2, stub.Bodies.Count);   // VLM OCR + 翻訳で 2 回 Ollama を呼ぶ
        Assert.Equal("glm-ocr:latest", diag2["ocr_model"]);
    }

    // ---- レイアウトのモデルの区画に行を入れる ----

    private static LayoutRegion Reg(int l, int t, int r, int b, string label = "text", float order = 0) =>
        new(Rectangle.FromLTRB(l, t, r, b), label, 0.9f, order);

    [Fact]
    public void J109_行はいちばん重なる区画に入り_区画ごとに読む順番でつながる()
    {
        // 左右2段の見出し（同じ高さに並ぶ）と、その下の本文。区画は左の見出し・右の見出し・本文の3つ
        var boxes = new List<Rectangle>
        {
            new(20, 915, 390, 29), new(403, 914, 369, 32),     // 1行目: 左の見出し・右の見出し（四角どうしは余白で重なる）
            new(20, 947, 382, 31), new(432, 949, 369, 29),     // 2行目
            new(20, 0, 800, 30), new(20, 34, 780, 30),          // 本文2行
        };
        var texts = new List<string> { "Russia's losses in Ukraine rise", "Inside the old gas pipeline", "faster than ever", "where Ukraine is", "The leaked statistics", "may not be complete." };
        var regions = new List<LayoutRegion> { Reg(14, 0, 830, 70, order: 1), Reg(14, 905, 410, 985, order: 2), Reg(425, 905, 810, 985, order: 3) };
        var paras = PpOcrPost.AssignToRegions(boxes, texts, regions);
        Assert.Equal(new[]
        {
            "The leaked statistics may not be complete.",
            "Russia's losses in Ukraine rise faster than ever",
            "Inside the old gas pipeline where Ukraine is",
        }, paras.Select(p => p.S));
        Assert.Equal(regions[1].R, paras[1].R);   // 位置は区画の四角
    }

    [Fact]
    public void J109_表の区画は行をつながない_どこにも入らない行は今までのつなぎ方で後ろに足す_文の無い区画は使わない()
    {
        var boxes = new List<Rectangle> { new(30, 60, 100, 20), new(30, 90, 100, 20), new(500, 500, 80, 20) };
        var texts = new List<string> { "Area Store", "Harbor 1,284", "Footnote" };
        var regions = new List<LayoutRegion> { Reg(26, 52, 610, 253, "table", 1), Reg(700, 0, 800, 50, "image", 2) };
        var paras = PpOcrPost.AssignToRegions(boxes, texts, regions);
        Assert.Equal(new[] { "Area Store", "Harbor 1,284", "Footnote" }, paras.Select(p => p.S));
    }

    [Fact]
    public void J109_日本語の行は何も挟まずにつなぐ()
    {
        var boxes = new List<Rectangle> { new(10, 10, 300, 24), new(10, 38, 200, 24) };
        var texts = new List<string> { "関係者以外の立ち入りを", "禁止します" };
        var paras = PpOcrPost.AssignToRegions(boxes, texts, new[] { Reg(0, 0, 320, 70) });
        Assert.Equal("関係者以外の立ち入りを禁止します", Assert.Single(paras).S);
    }

    [Fact]
    public void J109_同じ所を2つの種類で出した区画は確からしい方だけ残す()
    {
        var a = new LayoutRegion(Rectangle.FromLTRB(62, 127, 782, 206), "text", 0.56f, 87);
        var b = new LayoutRegion(Rectangle.FromLTRB(62, 127, 782, 206), "paragraph_title", 0.50f, 87);
        var c = new LayoutRegion(Rectangle.FromLTRB(64, 229, 636, 260), "text", 0.85f, 167);
        var kept = LayoutModel.Dedupe(new() { b, a, c });
        Assert.Equal(2, kept.Count);
        Assert.Contains(a, kept);
        Assert.DoesNotContain(b, kept);
    }

    [Fact]
    public async Task J109_本物のモデルで_区画を先に渡してから区画ごとの段落を返す()
    {
        Assert.True(File.Exists(LayoutModel.DefaultPath), "レイアウトのモデルがビルドの出力に無い: " + LayoutModel.DefaultPath);
        var diag = new Dictionary<string, object?>();
        IReadOnlyList<OcrBlock>? early = null;
        var blocks = await BuiltinOcr.ReadBlocksAsync(Png(Wrapped), default, diag, onRegions: r => early = r);
        Assert.NotNull(early);
        Assert.All(early!, b => Assert.Equal("", b.Text));   // 先に渡すのは位置だけ（文はまだ）
        Assert.NotEmpty(blocks);
        Assert.Contains("layout_model_ms", diag.Keys);
        Assert.All(blocks, b => { Assert.InRange(b.X, 0, 1); Assert.InRange(b.Y, 0, 1); });
    }

    // ---- VLM で位置の反映（区画ごとに切り抜いて頼む） ----

    [Fact]
    public async Task J110_画像そのままで位置の反映がONなら_区画を先に渡し_区画ごとに1回ずつ頼んで1行ずつ返す()
    {
        var stub = UseStub();   // いつも「訳文。」
        var cfg = Cfg("test-model", false);   // 画像そのまま
        cfg["builtin_ocr_layout"] = true;
        var layouts = new List<IReadOnlyList<OcrBlock>>();
        var deltas = new List<string>();
        var diag = new Dictionary<string, object?>();
        var text = await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), deltas.Add, null, default, 0, diag, b => layouts.Add(b));
        var first = Assert.Single(layouts);
        Assert.All(first, b => Assert.Equal("", b.Text));          // 区画の位置だけを先に渡す
        int n = first.Count;
        Assert.True(n >= 1);
        Assert.Equal(n, stub.Bodies.Count);                         // 区画の数だけ頼む
        Assert.Equal(string.Join("\n", Enumerable.Repeat("訳文。", n)), text);
        Assert.Equal("direct_image_layout", diag["pipeline"]);
        Assert.All(deltas, d => Assert.Equal(n, d.Split('\n').Length));   // 途中もずっと区画の数の行
        // 送った画像は切り抜いた物（元の画像より小さい）
        var full = Convert.FromBase64String(Png(Wrapped)).Length;
        foreach (var body in stub.Bodies)
        {
            var img = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("images")[0].GetString()!;
            Assert.True(Convert.FromBase64String(img).Length < full);
        }
    }

    [Fact]
    public async Task J110_文字起こしがVLMで位置の反映がONなら_区画ごとに書き起こしてから段落ごとに訳す()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", true);
        cfg[ConfigStore.OcrStageKey] = Cfg("glm-ocr:latest", false);
        cfg["builtin_ocr_layout"] = true;
        var layouts = new List<IReadOnlyList<OcrBlock>>();
        var diag = new Dictionary<string, object?>();
        var text = await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), null, null, default, 0, diag, b => layouts.Add(b));
        Assert.True(layouts.Count >= 2);
        Assert.All(layouts[0], b => Assert.Equal("", b.Text));      // 初めは位置だけ
        int n = layouts[^1].Count;
        Assert.All(layouts[^1], b => Assert.Equal("訳文。", b.Text)); // 書き起こした文（偽の VLM の答え）
        Assert.Equal("ocr_layout", diag["pipeline"]);
        Assert.Equal(n, text.Split('\n').Length);
        Assert.True(stub.Bodies.Count > n);                          // 区画ごとの書き起こし＋訳
    }

    [Fact]
    public async Task J110_位置の反映がOFFなら画像そのままは今まで通り1回で頼む()
    {
        var stub = UseStub();
        var cfg = Cfg("test-model", false);
        bool called = false;
        await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), null, null, default, 0, null, _ => called = true);
        Assert.False(called);
        Assert.Single(stub.Bodies);
    }

    [Fact]
    public async Task J122_VLMで読んでそのまま出す時は_1回だけ頼み_訳さず_画像の文字は信用しないの行も足さない()
    {
        var stub = UseStub();   // いつも「訳文。」
        var cfg = Cfg("glm-ocr:latest", false);
        cfg["throw_mode"] = ConfigStore.ThrowModeVlmOcr;
        cfg["prompt"] = "OCR:";
        var diag = new Dictionary<string, object?>();
        var text = await TranslatePipeline.TranslateImageAsync(cfg, Png(Wrapped), null, null, default, 0, diag);
        Assert.Equal("訳文。", text);
        var body = JsonDocument.Parse(Assert.Single(stub.Bodies)).RootElement;
        var msgs = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(msgs);                                         // system の行は足さない（OCR の決まった短い指示だけ）
        Assert.Equal("OCR:", msgs[0].GetProperty("content").GetString());
        Assert.Equal("vlm_ocr", diag["pipeline"]);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 画像の加工と記録（手順10）: ImagePrep（send_prep・真っ黒判定）、ShotArchive、
/// LogStore の AddPending/Finish。本物には繋がず、HTTPは差し替えた偽の相手で行う。
/// </summary>
public class ImagePrepArchiveTests : IDisposable
{
    // ---- 使い捨てのフォルダ ----

    private readonly List<string> _tempDirs = new();

    private Paths TempPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "b5step10-" + Guid.NewGuid().ToString("N")[..8]);
        _tempDirs.Add(root);
        return new Paths(root);
    }

    public void Dispose()
    {
        foreach (HttpClient c in _clients) c.Dispose();
        _clients.Clear();
        foreach (string d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---- 小さな画像を作る ----

    /// <summary>全画素を明るさ v の灰色（BGRA）にした w×h の画像。</summary>
    private static BitmapSource Gray(int w, int h, byte v)
    {
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var row = new byte[4 * w];
        for (int x = 0; x < w; x++)
        {
            int i = 4 * x;
            row[i] = row[i + 1] = row[i + 2] = v;
            row[i + 3] = 255;
        }
        for (int y = 0; y < h; y++)
            wb.WritePixels(new Int32Rect(0, y, w, 1), row, 4 * w, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>(2,2) の1画素だけ明るさ 250 の点を作った黒画像。</summary>
    private static BitmapSource BlackWithOneDot()
    {
        var wb = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgra32, null);
        var row = new byte[4 * 8];
        for (int y = 0; y < 8; y++) wb.WritePixels(new Int32Rect(0, y, 8, 1), row, 4 * 8, 0);
        byte[] dot = { 250, 250, 250, 255 };
        wb.WritePixels(new Int32Rect(2, 2, 1, 1), dot, 4, 0);
        wb.Freeze();
        return wb;
    }

    // ============================================================
    // ImagePrep: send_prep と真っ黒判定
    // ============================================================

    [Fact]
    public void SendPrep_長辺2000を超えたら縮小()
    {
        BitmapSource outp = ImagePrep.SendPrep(Gray(3000, 1500, 128));
        Assert.Equal(2000, outp.PixelWidth);
        Assert.Equal(1000, outp.PixelHeight);
    }

    [Fact]
    public void SendPrep_極小は長辺640まで拡大()
    {
        BitmapSource outp = ImagePrep.SendPrep(Gray(100, 50, 128));
        Assert.Equal(640, outp.PixelWidth);
        Assert.Equal(320, outp.PixelHeight);
    }

    [Fact]
    public void SendPrep_範囲内はそのまま同じ参照を返す()
    {
        BitmapSource img = Gray(800, 600, 128);
        Assert.Same(img, ImagePrep.SendPrep(img));
        // 極小窓でも「長辺」で見る（Python版と同じ式）: 1×800 は変えない
        BitmapSource thin = Gray(1, 800, 128);
        Assert.Same(thin, ImagePrep.SendPrep(thin));
    }

    [Fact]
    public void SendPrep_極細でも1画素は割らない()
    {
        BitmapSource outp = ImagePrep.SendPrep(Gray(1, 3000, 128));
        Assert.Equal(1, outp.PixelWidth);     // int(1*0.666)=0 → max(1,0)
        Assert.Equal(2000, outp.PixelHeight);
    }

    [Fact]
    public void IsBlackFrameは最も明るい灰が10未満で真()
    {
        Assert.True(ImagePrep.IsBlackFrame(Gray(16, 16, 0)));
        Assert.True(ImagePrep.IsBlackFrame(Gray(16, 16, 9)));   // 9 はセーフ（<10）
        Assert.False(ImagePrep.IsBlackFrame(Gray(16, 16, 10))); // 10 はアウト（not <10）
        Assert.False(ImagePrep.IsBlackFrame(BlackWithOneDot())); // 1画素でも明るければ送る
    }

    // ============================================================
    // ShotArchive: 撮ったまま・送ったサイズ・メタJSON
    // ============================================================

    [Fact]
    public void Stemはtsを時刻の並びに変えてidと結ぶ()
    {
        Assert.Equal("20261107-180512-abc", ShotArchive.Stem("2026-11-07 18:05:12", "abc"));
        // ts が壊れていれば今の時刻に逃げる（Python版 time.strftime と同じ形）
        Assert.Matches(@"^\d{8}-\d{6}-x$", ShotArchive.Stem("", "x"));
    }

    [Fact]
    public void SaveImagesは撮ったまと送ったサイズの両方を書く()
    {
        Paths paths = TempPaths();
        BitmapSource img = Gray(300, 150, 128);          // 長辺300 < 640 → 拡大される
        var names = ShotArchive.SaveImages(new JsonObject(), paths, img,
            ImagePrep.SendPrep(img), "2026-11-07 18:05:12", "eid1");
        string dir = Path.Combine(paths.Root, "shots");
        Assert.Equal(dir, names["dir"]);
        Assert.Equal("20261107-180512-eid1-raw.png", names["raw"]);
        Assert.Equal("20261107-180512-eid1-send.png", names["send"]);
        Assert.True(File.Exists(Path.Combine(dir, (string)names["raw"]!)));
        Assert.True(File.Exists(Path.Combine(dir, (string)names["send"]!)));
        Assert.Matches("^[0-9a-f]{16}$", (string)names["raw_sha1"]!);
        Assert.True((int)names["raw_png_bytes"]! > 0);
        Assert.Equal(new int[] { 300, 150 }, (int[])names["raw_size"]!);
        Assert.Equal(new int[] { 640, 320 }, (int[])names["send_size"]!);
        Assert.False(names.ContainsKey("error"));
    }

    [Fact]
    public void SaveImagesはサイズが変わらなければsendを書かない()
    {
        Paths paths = TempPaths();
        BitmapSource img = Gray(800, 600, 128);
        var names = ShotArchive.SaveImages(new JsonObject(), paths, img,
            ImagePrep.SendPrep(img), "2026-11-07 18:05:12", "eid2");
        Assert.Null(names["send"]);
        Assert.Equal(new[] { "20261107-180512-eid2-raw.png" },
            Directory.GetFiles(Path.Combine(paths.Root, "shots")).Select(Path.GetFileName));
    }

    [Fact]
    public void SaveImagesはshots_dir指定ならそこへ書く()
    {
        Paths paths = TempPaths();
        string alt = Path.Combine(paths.Root, "別の shots");
        var cfg = new JsonObject { ["shots_dir"] = alt };
        var names = ShotArchive.SaveImages(cfg, paths, Gray(700, 700, 128),
            null, "2026-11-07 18:05:12", "eid3");
        Assert.Equal("20261107-180512-eid3-raw.png", names["raw"]);
        Assert.True(File.Exists(Path.Combine(alt, (string)names["raw"]!)));
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "shots")));
    }

    [Fact]
    public void SaveImagesはidが無ければ何も書かない()
    {
        Paths paths = TempPaths();
        var names = ShotArchive.SaveImages(new JsonObject(), paths, Gray(700, 700, 128), null, "", "");
        Assert.Null(names["raw"]);
        Assert.Null(names["send"]);
    }

    [Fact]
    public void WriteMetaはtmp経由で1件1JSONを書く()
    {
        Paths paths = TempPaths();
        var meta = new JsonObject
        {
            ["id"] = "m-eid",
            ["ts"] = "2026-11-07 18:05:12",
            ["status"] = "ok",
            ["text"] = "日本語の訳",
        };
        string? name = ShotArchive.WriteMeta(new JsonObject(), paths, meta);
        Assert.Equal("20261107-180512-m-eid.json", name);
        string body = File.ReadAllText(Path.Combine(Path.Combine(paths.Root, "shots"), name!));
        JsonNode? back = JsonNode.Parse(body);
        Assert.Equal("日本語の訳", back!["text"]!.GetValue<string>()); // ensure_ascii=False
        // tmp の残骸が無いこと
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.Root, "shots"), "*.tmp"));
        // id が無ければ書かない
        Assert.Null(ShotArchive.WriteMeta(new JsonObject(), paths, new JsonObject()));
    }

    // ---- model_info（Ollamaの事実）は差し替えた相手から ----

    private readonly List<HttpClient> _clients = new();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<string> Uris = new();
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.AbsolutePath);
            if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    [Fact]
    public void ModelInfoはversionとshowを1回だけ引いてcacheする()
    {
        ShotArchive.ClearModelInfoCache();
        var stub = new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/version"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"version":"0.12.3"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""
                  {"details":{"family":"gemma4","quantization_level":"Q4_K_M","parameter_size":"4.6B","parent_model":"","format":"gguf"},
                   "model_info":{"general.architecture":"gemma4","gemma4.context_length":131072,
                    "tokenizer.ggml.model":"gemma4","general.filename":"C:\\models\\gguf\\shot-Q4.gguf"}}
                  """, Encoding.UTF8, "application/json") });
        _clients.Add(new HttpClient(stub));
        OllamaClient.SetHttp(_clients[^1]);

        var cfg = new JsonObject { ["model"] = "shot-model", ["base_url"] = "http://stub.local" };
        Dictionary<string, object?> info = ShotArchive.ModelInfo(cfg);
        Assert.Equal("shot-model", info["model"]);
        Assert.Equal("0.12.3", info["ollama_version"]);
        Assert.Equal("gemma4", info["family"]);
        Assert.Equal("Q4_K_M", info["quantization"]);
        Assert.Equal("4.6B", info["parameter_size"]);
        Assert.Equal("gemma4", info["architecture"]);
        Assert.False(info.ContainsKey("parent_model"));          // 空文字は載せない（Python版の truthy）
        Assert.Equal(131072L, info["trained_context"]);
        Assert.Equal("shot-Q4.gguf", info["gguf"]);              // basename だけ
        Assert.Equal(new[] { "/api/version", "/api/show" }, stub.Uris);

        // 2回目は引かない（modelごとに一度だけ）
        var again = ShotArchive.ModelInfo(cfg);
        Assert.Equal(new[] { "/api/version", "/api/show" }, stub.Uris);
        Assert.Same(info, again);

        // model が空なら通信しない
        Assert.Empty(ShotArchive.ModelInfo(new JsonObject()));
        Assert.Equal(2, stub.Uris.Count);
    }

    [Fact]
    public void EnvInfoはアプリとPCの状勢を1度だけ集める()
    {
        var e = ShotArchive.EnvInfo();
        Assert.Equal("", e["app_version"]);
        Assert.False(string.IsNullOrEmpty((string?)e["os"]));
        Assert.False(string.IsNullOrEmpty((string?)e["machine"]));
        Assert.True((double)e["ram_gb"]! > 0);
        e["os"] = "書き換えても";   // 返るのは写し（cache は動かない）
        Assert.NotEqual("書き換えても", ShotArchive.EnvInfo()["os"]);
    }

    // ============================================================
    // LogStore: add_pending → finish（分析用アーカイブ付き）
    // ============================================================

    private static LogStore NewStore(Paths paths, JsonObject? cfg = null)
    {
        var store = new LogStore(paths);
        store.ShotsCfg = () => cfg ?? new JsonObject { ["model"] = "", ["shots_dir"] = "", ["save_shots"] = true };
        return store;
    }

    [Fact]
    public void AddPendingは撮影の瞬間に条目と写真を作る()
    {
        Paths paths = TempPaths();
        LogStore store = NewStore(paths);
        JsonObject entry = store.AddPending(Gray(700, 500, 128), "test-model", "manual",
            new Dictionary<string, object?> { ["raw_size"] = new int[] { 700, 500 } });
        string eid0 = entry["id"]!.GetValue<string>();
        Assert.Equal(12, eid0.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", entry["ts"]!.GetValue<string>());
        Assert.True(entry["pending"]!.GetValue<bool>());
        Assert.Equal("", entry["text"]!.GetValue<string>());
        // logs/images へ送ったサイズの写し
        string imgName = Path.GetFileName(entry["image"]!.GetValue<string>());
        Assert.Equal(eid0 + ".png", imgName);
        Assert.True(File.Exists(Path.Combine(paths.ImagesDir, imgName)));
        // shots へ撮ったまま（長辺700は範囲内なので send は無し）
        Assert.Single(Directory.GetFiles(paths.DefaultShotsDir, $"*{eid0}-raw.png"));
        Assert.Empty(Directory.GetFiles(paths.DefaultShotsDir, "*-send.png"));
        // entries.json にも pending のまま出ている
        JsonArray saved = (JsonArray)JsonNode.Parse(File.ReadAllText(paths.EntriesJson))!;
        Assert.Equal(eid0, saved[0]!["id"]!.GetValue<string>());
        Assert.True(saved[0]!["pending"]!.GetValue<bool>());
    }

    [Fact]
    public void Finishは訳文とdiagを確定しメタJSONも残す()
    {
        Paths paths = TempPaths();
        LogStore store = NewStore(paths);
        JsonObject entry = store.AddPending(Gray(3000, 1500, 128), "test-model", "auto",
            new Dictionary<string, object?> { ["raw_size"] = new int[] { 3000, 1500 } });
        var diag = new JsonObject { ["pipeline"] = "ocr_split", ["chunk_count"] = 1, ["ocr_sec"] = 1.25 };
        store.Finish(entry, "訳文です", "ok", diag);

        JsonArray saved = (JsonArray)JsonNode.Parse(File.ReadAllText(paths.EntriesJson))!;
        Assert.Equal("訳文です", saved[0]!["text"]!.GetValue<string>());
        Assert.Equal("ok", saved[0]!["status"]!.GetValue<string>());
        Assert.Equal("ocr_split", saved[0]!["diag"]!["pipeline"]!.GetValue<string>());
        Assert.Equal(1.25, saved[0]!["diag"]!["ocr_sec"]!.GetValue<double>());
        Assert.False(saved[0]!.AsObject().ContainsKey("pending"));

        // shots へ撮ったまま＋送ったサイズ＋メタJSON
        string eid = entry["id"]!.GetValue<string>();
        Assert.Single(Directory.GetFiles(paths.DefaultShotsDir, $"*{eid}-raw.png"));
        Assert.Single(Directory.GetFiles(paths.DefaultShotsDir, $"*{eid}-send.png")); // 3000x1500→2000x1000
        string metaFile = Directory.GetFiles(paths.DefaultShotsDir, $"*{eid}.json").Single();
        JsonObject meta = (JsonObject)JsonNode.Parse(File.ReadAllText(metaFile))!;
        Assert.Equal(eid, meta["id"]!.GetValue<string>());
        Assert.Equal("ok", meta["status"]!.GetValue<string>());
        Assert.Equal("auto", meta["kind"]!.GetValue<string>());
        Assert.Equal("訳文です", meta["text"]!.GetValue<string>());
        Assert.NotNull(meta["config"]!.AsObject()["model"]);            // config は切り出し
        Assert.False(meta["config"]!.AsObject().ContainsKey("prompt")); // プロンプト全文は載せない
        Assert.NotNull(meta["shot_files"]!.AsObject()["raw"]);
        Assert.NotNull(meta["shot_files"]!.AsObject()["send"]);
        Assert.Equal(3000, meta["capture"]!.AsObject()["raw_size"]!.AsArray()[0]!.GetValue<int>());
        Assert.Equal("ocr_split", meta["diag"]!.AsObject()["pipeline"]!.GetValue<string>());
        Assert.NotNull(meta["env"]);
        Assert.True(meta["model_info"]!.AsObject().Count == 0);         // model空 → 引かない
        Assert.Empty(Directory.GetFiles(paths.DefaultShotsDir, "*.tmp"));
    }

    [Fact]
    public void 二重finishは効かない_成功訳が失敗文言で上書きされない()
    {
        Paths paths = TempPaths();
        LogStore store = NewStore(paths);
        JsonObject entry = store.AddPending(Gray(700, 500, 128), "m", "manual");
        store.Finish(entry, "成功訳", "ok");
        store.Finish(entry, "(中断: アプリ終了)", "failed");
        JsonArray saved = (JsonArray)JsonNode.Parse(File.ReadAllText(paths.EntriesJson))!;
        Assert.Equal("成功訳", saved[0]!["text"]!.GetValue<string>());
        Assert.Equal("ok", saved[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void 落ち残ったpendingは起動時にfail確定()
    {
        Paths paths = TempPaths();
        LogStore store = NewStore(paths);
        JsonObject entry = store.AddPending(Gray(700, 500, 128), "m", "manual");
        // 「アプリ終了」で worker が finish しなかった想定 → 起動し直す
        LogStore again = new LogStore(paths);
        JsonObject re = again.Snapshot().Single(o => o["id"]!.GetValue<string>() == entry["id"]!.GetValue<string>());
        Assert.Equal("failed", re["status"]!.GetValue<string>());
        Assert.Equal("(中断: アプリ終了)", re["text"]!.GetValue<string>());
        Assert.False(re.ContainsKey("pending"));
    }

    [Fact]
    public void Addは完了済み条目をまとめて1件で追加する_互換経路()
    {
        Paths paths = TempPaths();
        LogStore store = NewStore(paths);
        JsonObject entry = store.Add(Gray(700, 500, 128), "手動で入れた訳", "m", "manual");
        Assert.False(entry.ContainsKey("pending"));
        JsonArray saved = (JsonArray)JsonNode.Parse(File.ReadAllText(paths.EntriesJson))!;
        Assert.Equal("手動で入れた訳", saved[0]!["text"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(paths.ImagesDir, Path.GetFileName(entry["image"]!.GetValue<string>()))));
        // shots へは行かない（Python版 add も _archive_capture を呼ばない）
        Assert.False(Directory.Exists(paths.DefaultShotsDir));
    }

    [Fact]
    public void SaveShotsがOFFの時はshotsに何も書かずログ条目と写真だけ作る()
    {
        ShotArchive.ClearModelInfoCache();
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        _clients.Add(new HttpClient(stub));
        OllamaClient.SetHttp(_clients[^1]);

        // save_shots キーが無い設定でも OFF 扱いになり、ModelInfo の HTTP も引かない
        Paths paths = TempPaths();
        LogStore store = NewStore(paths, new JsonObject { ["model"] = "shot-model", ["base_url"] = "http://stub.local" });
        JsonObject entry = store.AddPending(Gray(3000, 1500, 128), "shot-model", "manual");
        store.Finish(entry, "訳文", "ok");

        string eid = entry["id"]!.GetValue<string>();
        Assert.False(Directory.Exists(paths.DefaultShotsDir));
        Assert.Empty(stub.Uris);
        Assert.True(File.Exists(Path.Combine(paths.ImagesDir, eid + ".png")));
        JsonArray saved = (JsonArray)JsonNode.Parse(File.ReadAllText(paths.EntriesJson))!;
        Assert.Equal("訳文", saved[0]!["text"]!.GetValue<string>());
        Assert.Equal("ok", saved[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void SaveShotsの既定はfalseで保存済みconfigにキーが無い時もfalse()
    {
        Paths paths = TempPaths();
        var cfg1 = new ConfigStore(paths);
        cfg1.Load();
        Assert.False(cfg1.Settings.SaveShots);

        // 既存 config に save_shots キーが無い場合も false で読み込まれる
        File.WriteAllText(paths.ConfigJson, """{"model":"gemma4:e2b"}""", Encoding.UTF8);
        var cfg2 = new ConfigStore(paths);
        cfg2.Load();
        Assert.False(cfg2.Settings.SaveShots);
    }

    [Fact]
    public void ModelInfo失敗はキャッシュせず1分以内は抑制し1分後に再試行する()
    {
        ShotArchive.ClearModelInfoCache();
        DateTime now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        ShotArchive.UtcNow = () => now;
        try
        {
            bool fail = true;
            var stub = new StubHandler(req =>
            {
                if (fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return req.RequestUri!.AbsolutePath == "/api/version"
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"version":"0.12.3"}""", Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"details":{"family":"qwen2"}}""", Encoding.UTF8, "application/json") };
            });
            _clients.Add(new HttpClient(stub));
            OllamaClient.SetHttp(_clients[^1]);

            var cfg = new JsonObject { ["model"] = "retry-model", ["base_url"] = "http://stub.local" };
            var first = ShotArchive.ModelInfo(cfg);
            Assert.True(first.ContainsKey("model_info_error"));
            int callsAfterFirst = stub.Uris.Count;

            // 30秒後はリトライ抑制（HTTPを呼ばない）
            now = now.AddSeconds(30);
            var suppressed = ShotArchive.ModelInfo(cfg);
            Assert.True(suppressed.ContainsKey("model_info_error"));
            Assert.Equal(callsAfterFirst, stub.Uris.Count);

            // 61秒後に成功へ切り替えると再試行してキャッシュされる
            fail = false;
            now = now.AddSeconds(31);
            var recovered = ShotArchive.ModelInfo(cfg);
            Assert.False(recovered.ContainsKey("model_info_error"));
            Assert.Equal("0.12.3", recovered["ollama_version"]);
            int callsAfterRecovery = stub.Uris.Count;
            Assert.True(callsAfterRecovery > callsAfterFirst);

            // 成功後はキャッシュヒット
            var cached = ShotArchive.ModelInfo(cfg);
            Assert.Same(recovered, cached);
            Assert.Equal(callsAfterRecovery, stub.Uris.Count);
        }
        finally
        {
            ShotArchive.UtcNow = () => DateTime.UtcNow;
        }
    }

    [Fact]
    public async Task Finish中のModelInfo待ちでもSnapshotがブロックされない()
    {
        ShotArchive.ClearModelInfoCache();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var stub = new StubHandler(req =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return req.RequestUri!.AbsolutePath == "/api/version"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"version":"0.12.3"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"details":{"family":"gemma4"}}""", Encoding.UTF8, "application/json") };
        });
        _clients.Add(new HttpClient(stub));
        OllamaClient.SetHttp(_clients[^1]);

        Paths paths = TempPaths();
        var cfg = new JsonObject
        {
            ["model"] = "slow-model",
            ["base_url"] = "http://stub.local",
            ["save_shots"] = true,
        };
        LogStore store = NewStore(paths, cfg);
        JsonObject entry = store.AddPending(Gray(700, 500, 128), "slow-model", "manual");

        Task finishTask = Task.Run(() => store.Finish(entry, "確定訳", "ok"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)), "ModelInfo HTTP待ちに入らなかった");

            // ロック解放済みなので Snapshot() は HTTP 完了を待たずに返る
            Task<List<JsonObject>> snapTask = Task.Run(() => store.Snapshot());
            Task completed = await Task.WhenAny(snapTask, Task.Delay(500));
            Assert.Same(snapTask, completed);
            List<JsonObject> snap = await snapTask;
            Assert.Equal("確定訳", snap[0]["text"]!.GetValue<string>());
            Assert.False(snap[0].ContainsKey("pending"));
        }
        finally
        {
            release.Set();
            await finishTask;
        }
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>手順4: LogStore（entries.json読み込み・pending後始末・孤児画像掃除・200件上限）</summary>
public class LogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "b5_log_" + Guid.NewGuid().ToString("N"));

    public LogStoreTests()
    {
        Directory.CreateDirectory(_dir);
        AppLog.Init(P);   // app.log を実フォルダでなく temp へ
    }

    private Paths P => new(_dir);

    private void Write(string rel, string content)
    {
        string p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
    }

    private static JsonObject Entry(string id, string ts, string? image = null,
                                   bool pending = false, string status = "ok", string text = "答え")
    {
        var o = new JsonObject { ["id"] = id, ["ts"] = ts, ["status"] = status, ["text"] = text };
        if (image is not null) o["image"] = image;
        if (pending) o["pending"] = true;
        return o;
    }

    private string EntriesJson() => File.ReadAllText(Path.Combine(_dir, "logs", "entries.json"));
    private JsonArray ReadSaved() => JsonNode.Parse(EntriesJson())!.AsArray();

    private LogStore Make() => new(P);

    [Fact]
    public void LoadsSortsNewestFirst()
    {
        var arr = new JsonArray(
            Entry("a", "2026-01-01 00:00:00"), Entry("b", "2026-01-02 00:00:00"));
        Write("logs/entries.json", arr.ToJsonString());
        var s = Make();
        Assert.Equal(new[] { "b", "a" }, s.Entries.Select(e => e["id"]!.GetValue<string>()));
    }

    [Fact]
    public void SameTsKeepsFileOrder()
    {
        // python版の安定ソート（reverse=True）と同じで、同じ ts はファイル内の順のまま
        var arr = new JsonArray(Entry("old1", "2026-01-01 00:00:00"),
                                Entry("new1", "2026-01-01 00:00:00"));
        Write("logs/entries.json", arr.ToJsonString());
        var s = Make();
        Assert.Equal("old1", s.Entries[0]["id"]!.GetValue<string>());
    }

    [Fact]
    public void EntriesWithoutIdDropped()
    {
        var arr = new JsonArray(
            Entry("a", "2026-01-01 00:00:00"),
            new JsonObject { ["ts"] = "2026-01-02 00:00:00" },
            JsonValue.Create("ただの文字列"));
        Write("logs/entries.json", arr.ToJsonString());
        var s = Make();
        Assert.Single(s.Entries);
    }

    [Fact]
    public void PendingBecomeFailedWithReason()
    {
        var arr = new JsonArray(
            Entry("p1", "2026-01-01 00:00:00", pending: true, text: ""),
            Entry("ok", "2026-01-02 00:00:00"));
        Write("logs/entries.json", arr.ToJsonString());
        var s = Make();
        var p = s.Entries.First(e => e["id"]!.GetValue<string>() == "p1");
        Assert.Null(p["pending"]);
        Assert.Equal("failed", p["status"]!.GetValue<string>());
        Assert.Equal("(中断: アプリ終了)", p["text"]!.GetValue<string>());
        // ファイルへも書き戻っている
        Assert.DoesNotContain("pending", EntriesJson());
        // 完了済み条目は無傷
        var ok = s.Entries.First(e => e["id"]!.GetValue<string>() == "ok");
        Assert.Equal("ok", ok["status"]!.GetValue<string>());
    }

    [Fact]
    public void PendingWithTextKeepsText()
    {
        var arr = new JsonArray(
            Entry("p1", "2026-01-01 00:00:00", pending: true, text: "途中の途中"));
        Write("logs/entries.json", arr.ToJsonString());
        var s = Make();
        Assert.Equal("途中の途中", s.Entries[0]["text"]!.GetValue<string>());
        Assert.Equal("failed", s.Entries[0]["status"]!.GetValue<string>());
    }

    [Fact]
    public void CorruptEntriesFallBackToBakAndKeepBad()
    {
        Write("logs/entries.json", "{これは壊れている");
        var good = new JsonArray(Entry("saved", "2026-01-01 00:00:00"));
        Write("logs/entries.json.bak", good.ToJsonString());
        var s = Make();
        Assert.Single(s.Entries);
        Assert.Equal("saved", s.Entries[0]["id"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_dir, "logs", "entries.bad.json")));
    }

    [Fact]
    public void OrphanImagesDeletedOnLoad()
    {
        var arr = new JsonArray(Entry("a", "2026-01-01 00:00:00", image: "images/a.png"));
        Write("logs/entries.json", arr.ToJsonString());
        Write("logs/images/a.png", "PNG");
        Write("logs/images/zombie.png", "PNG");
        Make();
        Assert.True(File.Exists(Path.Combine(_dir, "logs", "images", "a.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "logs", "images", "zombie.png")));
    }

    [Fact]
    public void StaleTmpSweptOwnTmpKept()
    {
        Write("logs/entries.json", new JsonArray(Entry("a", "2026-01-01 00:00:00")).ToJsonString());
        Write("logs/entries.999999.tmp", "残骸");
        string mine = Path.Combine(_dir, "logs", $"entries.{Environment.ProcessId}.tmp");
        File.WriteAllText(mine, "自分の途中");
        Make();
        Assert.False(File.Exists(Path.Combine(_dir, "logs", "entries.999999.tmp")));
        Assert.True(File.Exists(mine));   // 保存の最中に消さないよう、自分は残す
        File.Delete(mine);
    }

    [Fact]
    public void InsertOverCapDropsOldestAndItsImage()
    {
        // 200件分の条目と画像を先に置いてから、1件追加で最古が落ちるはず
        var arr = new JsonArray();
        for (int i = 0; i < LogStore.MaxEntries; i++)
        {
            string id = $"e{i:D3}";
            arr.Add(Entry(id, $"2026-01-{(i / 24) + 1:D2} {(i % 24):D2}:00:00", image: $"images/{id}.png"));
        }
        Write("logs/entries.json", arr.ToJsonString());
        for (int i = 0; i < LogStore.MaxEntries; i++)
            Write($"logs/images/e{i:D3}.png", "PNG");
        var s = Make();
        Assert.Equal(LogStore.MaxEntries, s.Entries.Count);

        string newId = "fresh01";
        Write("logs/images/fresh01.png", "PNG");
        s.InsertAndDump(Entry(newId, "2026-02-01 00:00:00", image: $"images/{newId}.png"));

        Assert.Equal(LogStore.MaxEntries, s.Entries.Count);
        Assert.Equal(newId, s.Entries[0]["id"]!.GetValue<string>());
        // 押し出された最古（e000）は条目からも画像ファイルからも消える
        Assert.DoesNotContain(s.Entries, e => e["id"]!.GetValue<string>() == "e000");
        Assert.False(File.Exists(Path.Combine(_dir, "logs", "images", "e000.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "logs", "images", "e001.png")));
        // 保存は entries.json に反映済み
        Assert.Equal(LogStore.MaxEntries, ReadSaved().Count);
    }

    [Fact]
    public void SaveRotatesBakWithPreviousContent()
    {
        Write("logs/entries.json", new JsonArray(Entry("a", "2026-01-01 00:00:00")).ToJsonString());
        var s = Make();
        s.InsertAndDump(Entry("b", "2026-01-02 00:00:00"));
        // .bak は置換前のファイル（1件だった時点）の中身
        Assert.Single(JsonNode.Parse(
            File.ReadAllText(Path.Combine(_dir, "logs", "entries.json.bak")))!.AsArray());
        Assert.Equal(2, ReadSaved().Count);
        Assert.Equal("b", ReadSaved()[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownKeysInEntrySurviveRoundTrip()
    {
        var e = Entry("a", "2026-01-01 00:00:00");
        e["diag"] = new JsonObject { ["elapsed"] = 1.5 };
        Write("logs/entries.json", new JsonArray(e).ToJsonString());
        var s = Make();
        s.InsertAndDump(Entry("b", "2026-01-02 00:00:00"));   // 保存を経由
        var saved = ReadSaved().First(x => x!["id"]!.GetValue<string>() == "a");
        Assert.Equal(1.5, saved!["diag"]!["elapsed"]!.GetValue<double>());
    }

    [Fact]
    public void ImagePathOnlyLooksInsideImagesDir()
    {
        var e = Entry("a", "2026-01-01 00:00:00", image: "..\\..\\secret.png");
        Write("logs/entries.json", new JsonArray(e).ToJsonString());
        Write("logs/images/secret.png", "PNG");   // 名前だけ拾って ImagesDir 直下を見る
        var s = Make();
        string? p = s.ImagePath(s.Entries[0]);
        Assert.NotNull(p);
        Assert.Equal(Path.Combine(_dir, "logs", "images", "secret.png"), p);
    }

    [Fact]
    public void EmptyWhenNoFile()
    {
        var s = Make();
        Assert.Empty(s.Entries);
        Assert.False(File.Exists(Path.Combine(_dir, "logs", "entries.json")));
    }

    [Fact]
    public void ToNodeKeepsNestedListsAsArrays()
    {
        // raw_size（List<object?>）や chunks・llm_calls（List<Dictionary>）を
        // 文字列にせず JSON の配列・物件として残す（手順13で実機に気づかれた）
        var diag = new Dictionary<string, object?>
        {
            ["raw_size"] = new List<object?> { 640, 400 },
            ["chunks"] = new List<object?>
            {
                new Dictionary<string, object?> { ["chars"] = 28, ["ok"] = true },
            },
        };
        var node = LogStore.ToNode(diag) as JsonObject;
        Assert.NotNull(node);
        Assert.Equal(JsonValueKind.Array, node!["raw_size"]!.GetValueKind());
        Assert.Equal(640, node["raw_size"]![0]!.GetValue<int>());
        Assert.Equal(400, node["raw_size"]![1]!.GetValue<int>());
        var chunks = node["chunks"] as JsonArray;
        Assert.NotNull(chunks);
        Assert.Equal(28, chunks![0]!["chars"]!.GetValue<int>());
        Assert.True(chunks[0]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void FinishStoresDiagListsAsArrays()
    {
        var s = Make();
        var e = Entry("a", "2026-01-01 00:00:00", pending: true);
        s.InsertAndDump(e);
        var diag = new Dictionary<string, object?> { ["raw_size"] = new List<object?> { 3, 4 } };
        s.Finish(e, "done", "ok", LogStore.ToNode(diag) as JsonObject);
        var saved = ReadSaved().First(x => x!["id"]!.GetValue<string>() == "a");
        Assert.Equal(JsonValueKind.Array, saved!["diag"]!["raw_size"]!.GetValueKind());
        Assert.Equal(4, saved["diag"]!["raw_size"]![1]!.GetValue<int>());
    }

    [Fact]
    public async Task AddPendingDeferred_InsertsImmediatelyAndPersistsInBackground()
    {
        var s = Make();
        var px = new byte[16 * 16 * 4];
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 16 * 4);
        bmp.Freeze();
        var prep = ImagePrep.SendPrep(bmp);
        var png = ScreenShot.PngBytes(prep);

        var (entry, persistTask) = s.AddPendingDeferred(bmp, "m1", "manual", null, prep, png);
        Assert.Single(s.Entries);
        Assert.True(entry["pending"]!.GetValue<bool>());

        await persistTask;
        var imgRel = LogStore.GetStr(entry, "image")!;
        Assert.True(File.Exists(Path.Combine(P.LogsDir, imgRel.Replace('/', Path.DirectorySeparatorChar))));
        var saved = ReadSaved();
        Assert.Single(saved);

        s.Finish(entry, "訳文", "ok");
        var finished = ReadSaved()[0]!.AsObject();
        Assert.Equal("ok", LogStore.GetStr(finished, "status"));
        Assert.Equal("訳文", LogStore.GetStr(finished, "text"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

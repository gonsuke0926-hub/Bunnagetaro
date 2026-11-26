using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace Bunnagetaro5.Services;

/// <summary>
/// 履歴とログの保管（WPF_PORT_SPEC 5.4 / Python版 tw_store.LogStore）。
/// logs/entries.json と logs/images を管理する。MaxEntries超過の掃除や
/// 孤児画像の削除はここが行うが、分析用の保存物（shots）は別フォルダでここでは消さない。
/// entries.json → .bak の順に読む（壊れても履歴を丸ごと失わない作り）。
/// 条目は JsonObject のまま持つ（知らないキーも消さない方針はログでも同じ）。
/// </summary>
public sealed class LogStore
{
    public const int MaxEntries = 200;

    private readonly Paths _paths;
    private readonly object _lock = new();

    /// <summary>新しい→古い順（Python版 self.entries と同じ向き）。</summary>
    public List<JsonObject> Entries { get; private set; } = new();

    public LogStore(Paths paths)
    {
        _paths = paths;
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!ReadInto(_paths.EntriesJson))
            {
                Entries = new List<JsonObject>();
                if (ReadInto(_paths.EntriesJson + ".bak"))
                    AppLog.Warning("entries.json が読めず、.bakから復旧しました");
            }
            // 新しい順。同じ ts の並びはファイル内の順を保つ（Pythonの安定ソートと同じ）
            Entries = Entries
                .Select((e, i) => (e, i))
                .OrderByDescending(p => GetStr(p.e, "ts") ?? "")
                .ThenBy(p => p.i)
                .Select(p => p.e)
                .ToList();
            if (FinalizePendingLocked() > 0)
                AppLog.Warning("ぶん投げ途中だった条目を検知し、失敗確定に書き換えました");
            // クラッシュ残骸の entries.<pid>.tmp を掃討
            // （本アプリは単一インスタンス想定。多重起動時は自分以外のtmpを消す）
            try
            {
                foreach (string p in Directory.GetFiles(_paths.LogsDir, "entries.*.tmp"))
                {
                    if (Path.GetFileName(p) != $"entries.{Environment.ProcessId}.tmp")
                    {
                        try { File.Delete(p); } catch (IOException) { }
                    }
                }
            }
            catch (IOException) { }
            GcOrphansLocked();
        }
    }

    private bool ReadInto(string path)
    {
        try
        {
            // File.ReadAllText は BOM 付き UTF-8 も拾える（手編集・PowerShell対策）
            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            if (node is JsonArray arr)
            {
                var list = new List<JsonObject>();
                foreach (JsonNode? item in arr)
                    if (item is JsonObject o && o.ContainsKey("id"))
                        list.Add((JsonObject)item!.DeepClone()); // 解析ツリーから切り離して所有
                Entries = list;
                return true;
            }
            return false; // 配列で無い（dict等）なら「読めなかった」扱い
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;   // logs フォルダ自体がまだ無い（初回起動）
        }
        catch (Exception ex)
        {
            AppLog.Error($"log load failed: {path}", ex);
            try
            {
                // 壊れた本体は .bad に退避して証拠を残す
                File.Move(path, Path.Combine(
                    Path.GetDirectoryName(path) ?? ".",
                    Path.GetFileNameWithoutExtension(path) + ".bad" + Path.GetExtension(path)),
                    overwrite: true);
            }
            catch { }
            return false;
        }
    }

    /// <summary>起動時にpending残りをfail確定（Python版 finalize_pending と同じ）。</summary>
    public int FinalizePending(string reason = "(中断: アプリ終了)")
    {
        lock (_lock) return FinalizePendingLocked(reason);
    }

    private int FinalizePendingLocked(string reason = "(中断: アプリ終了)")
    {
        int n = 0;
        bool changed = false;
        foreach (JsonObject e in Entries)
        {
            JsonNode? p = e["pending"];
            if (p is not null && p.GetValueKind() != JsonValueKind.False)
            {
                e.Remove("pending");
                e["status"] = "failed";
                if (string.IsNullOrEmpty(GetStr(e, "text"))) e["text"] = reason;
                n++;
                changed = true;
            }
        }
        if (changed) DumpLocked();
        return n;
    }

    /// <summary>条目を先頭へ挿入し、MaxEntries超過分を捨てて保存（Python版 _insert_dump_locked）。
    /// 画像ファイルの削除はロックの外で（Python版も lock の外で _drop_imgs）。</summary>
    public void InsertAndDump(JsonObject entry)
    {
        List<JsonObject> trimmed = new();
        lock (_lock)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
            {
                trimmed = Entries.GetRange(MaxEntries, Entries.Count - MaxEntries);
                Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            }
            DumpLocked();
        }
        DropImgs(trimmed);
    }

    // ---- v1.8: 撮影の瞬間に条目を作り、finish で訳文を追いつかせる ----

    /// <summary>shots への保存に使う設定をもらう口（本体が App の設定へ差し替える）。
    /// 未設定なら既定の設定（Python版 shots_cfg / DEFAULT_CONFIG と同じ）。</summary>
    public Func<JsonObject>? ShotsCfg { get; set; }

    private readonly Dictionary<string, Dictionary<string, object?>> _shotCtx = new();
    private readonly object _shotCtxLock = new();

    private JsonObject CfgForShots()
    {
        try
        {
            return ShotsCfg?.Invoke() ?? ConfigStore.DefaultConfig();
        }
        catch (Exception ex)
        {
            AppLog.Error("shots cfg failed", ex);
            return ConfigStore.DefaultConfig();
        }
    }

    /// <summary>完了済みの条目をまとめて1件で追加する経路（Python版 add。
    /// ぶん投げの本体は AddPending/Finish の二段階を使う。互換・手動追加用）。</summary>
    public JsonObject Add(BitmapSource img, string text, string model, string source)
    {
        string eid = NewId();
        var entry = new JsonObject
        {
            ["id"] = eid,
            ["ts"] = NowStamp(),
            ["model"] = model,
            ["source"] = source,
            ["text"] = text,
            ["image"] = SavePng(img, eid + ".png"),
        };
        List<JsonObject> trimmed = new();
        lock (_lock)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
            {
                trimmed = Entries.GetRange(MaxEntries, Entries.Count - MaxEntries);
                Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            }
            DumpLocked();
        }
        DropImgs(trimmed);
        return entry;
    }

    /// <summary>v1.8要望: 条目は撮影の瞬間に作る。写真は先に出し、訳文は Finish() で
    /// 後から追いつく。workerスレッドから呼ぶので全更新はロックの下。
    /// 落ち残ったpendingは Load() でfail確定（「ぶん投げ中…」が出っ放しにならない）。</summary>
    public JsonObject AddPending(BitmapSource img, string model, string source,
                                 Dictionary<string, object?>? extra = null)
    {
        var (entry, persistTask) = AddPendingDeferred(img, model, source, extra);
        persistTask.GetAwaiter().GetResult();
        return entry;
    }

    /// <summary>G6: 条目を即座にメモリへ積み、PNG保存・shots保存・entries.json 書き出しを
    /// 裏タスクへ回して送信開始を先へ進める。prepared/sendPng を渡すと重複エンコードを省く。</summary>
    public (JsonObject Entry, Task PersistTask) AddPendingDeferred(
        BitmapSource img, string model, string source,
        Dictionary<string, object?>? extra = null,
        BitmapSource? prepared = null, byte[]? sendPng = null)
    {
        string eid = NewId();
        var entry = new JsonObject
        {
            ["id"] = eid,
            ["ts"] = NowStamp(),
            ["model"] = model,
            ["source"] = source,
            ["text"] = "",
            ["image"] = "images/" + eid + ".png",
            ["pending"] = true,
        };
        List<JsonObject> trimmed = new();
        lock (_lock)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
            {
                trimmed = Entries.GetRange(MaxEntries, Entries.Count - MaxEntries);
                Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            }
        }
        var persistTask = Task.Run(() =>
        {
            var prep = prepared ?? ImagePrep.SendPrep(img);
            var png = sendPng ?? ScreenShot.PngBytes(prep);
            var saved = SavePngBytes(png, eid + ".png");
            if (saved is null)
            {
                lock (_lock) { entry["image"] = null; }
            }
            ArchiveCapture(entry, img, prep, png, extra);
            lock (_lock) { DumpLocked(); }
            DropImgs(trimmed);
        });
        return (entry, persistTask);
    }

    /// <summary>pending条目を確定する。status: "ok" | "failed"（workerから呼ばれる）。
    /// 確定済みの条目を二重finishしない（成功訳が後続の例外経路の失敗文言で
    /// 上書きされる道を塞ぐ）。冪等。</summary>
    public void Finish(JsonObject entry, string text, string status, JsonObject? diag = null)
    {
        JsonObject? entrySnapshot = null;
        lock (_lock)
        {
            JsonNode? p = entry["pending"];
            if (p is null || p.GetValueKind() == JsonValueKind.False) return;
            entry["text"] = text;
            entry["status"] = status;
            if (diag is not null) entry["diag"] = ToNode(diag);
            entry.Remove("pending");
            DumpLocked();
            entrySnapshot = (JsonObject)entry.DeepClone();
        }
        // G7: HTTP(ModelInfo)やfsync(WriteMeta)を _lock の外で実行し、Snapshot()等のUI呼び出しをブロックしない
        ArchiveMeta(entrySnapshot);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string NowStamp() =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private string? SavePng(BitmapSource img, string fname)
    {
        try
        {
            BitmapSource prep = ImagePrep.SendPrep(img);
            return SavePngBytes(ScreenShot.PngBytes(prep), fname);
        }
        catch (Exception ex)
        {
            AppLog.Error("log image save failed", ex);
            return null;
        }
    }

    private string? SavePngBytes(byte[] pngBytes, string fname)
    {
        try
        {
            Directory.CreateDirectory(_paths.ImagesDir);
            File.WriteAllBytes(Path.Combine(_paths.ImagesDir, fname), pngBytes);
            return "images/" + fname;
        }
        catch (Exception ex)
        {
            AppLog.Error("log image save failed", ex);
            return null;
        }
    }

    internal static bool ShouldSaveShots(JsonObject? cfg)
    {
        JsonNode? n = cfg?["save_shots"];
        return n is not null && n.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>撮影の瞬間に、撮ったままの画像と実際に送るサイズの画像を残す。</summary>
    private void ArchiveCapture(JsonObject entry, BitmapSource img, BitmapSource prepared,
                                byte[] sendPng, Dictionary<string, object?>? extra)
    {
        try
        {
            JsonObject cfg = CfgForShots();
            if (!ShouldSaveShots(cfg)) return;
            string eid = GetStr(entry, "id") ?? "";
            Dictionary<string, object?> names = ShotArchive.SaveImages(
                cfg, _paths, img, prepared: prepared,
                tsLocal: GetStr(entry, "ts") ?? "", eid: eid, sendPng: sendPng);
            names["extra"] = extra ?? new Dictionary<string, object?>();
            lock (_shotCtxLock) _shotCtx[eid] = names;
        }
        catch (Exception ex)
        {
            AppLog.Error("shot archive failed", ex);
        }
    }

    /// <summary>確定した1件分のメタ情報をJSONで残す（entries.jsonより詳しい）。</summary>
    private void ArchiveMeta(JsonObject entry)
    {
        try
        {
            string eid = GetStr(entry, "id") ?? "";
            JsonObject cfg = CfgForShots();
            Dictionary<string, object?>? ctxNode;
            lock (_shotCtxLock)
            {
                _shotCtx.TryGetValue(eid, out ctxNode);
                _shotCtx.Remove(eid);
            }
            if (!ShouldSaveShots(cfg)) return;
            ctxNode ??= new Dictionary<string, object?>();
            var meta = new JsonObject
            {
                ["id"] = eid,
                ["ts"] = GetStr(entry, "ts"),
                ["status"] = ToNodeOr(entry["status"]),
                ["kind"] = GetStr(entry, "source"),
                ["text"] = ToNodeOr(entry["text"]),
                ["model"] = GetStr(entry, "model"),
                ["config"] = MetaConfigSubset(cfg),
                ["prompt"] = ToNodeOr(cfg["prompt"]),
                ["shots_dir"] = ShotArchive.DirFor(cfg, _paths),
                ["shot_files"] = new JsonObject
                {
                    ["raw"] = ctxNode.TryGetValue("raw", out object? r) ? ToNode(r) : null,
                    ["send"] = ctxNode.TryGetValue("send", out object? s) ? ToNode(s) : null,
                },
                ["capture"] = ToNode(ctxNode.TryGetValue("extra", out object? ex) ? ex : null),
                ["diag"] = ToNodeOr(entry["diag"]),
                ["env"] = ToNode(ShotArchive.EnvInfo()),
                ["model_info"] = ToNode(ShotArchive.ModelInfo(cfg)),
            };
            ShotArchive.WriteMeta(cfg, _paths, meta);
        }
        catch (Exception ex)
        {
            AppLog.Error("shot meta failed", ex);
        }
    }

    // ---- C# の値（Dictionary/配列）を JSON の node へ ---

    internal static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        JsonNode jn => (JsonNode)jn.DeepClone(),   // 条目やconfigのツリーをそのまま移さない
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        int[] arr => new JsonArray([.. arr.Select(x => (JsonNode)x)]),
        Dictionary<string, object?> dict => DictToJson(dict),
        // Python版は dict/list をそのまま json に載せる。List<object?>（raw_size 等）や
        // List<Dictionary>（chunks・llm_calls）も文字列にせず配列・物件として残す
        System.Collections.IList list => new JsonArray(
            [.. list.Cast<object?>().Select(x => ToNode(x))]),
        System.Collections.IDictionary d => DictToNode(d),
        _ => JsonValue.Create(v.ToString()),
    };

    private static JsonObject DictToNode(System.Collections.IDictionary d)
    {
        var o = new JsonObject();
        foreach (System.Collections.DictionaryEntry e in d)
            o[Convert.ToString(e.Key) ?? ""] = ToNode(e.Value);
        return o;
    }

    private static JsonObject DictToJson(Dictionary<string, object?> dict)
    {
        var o = new JsonObject();
        foreach (var (k, v) in dict) o[k] = ToNode(v);
        return o;
    }

    private static JsonNode? ToNodeOr(JsonNode? n) => n?.DeepClone();

    /// <summary>メタJSONに載せるconfigの切り出し（tw_shotsの _archive_meta と同じキーだけ。
    /// プロンプト全文などは shots_dir の隣の config には載せない）。</summary>
    private static readonly string[] MetaConfigKeys =
    {
        "model", "base_url", "ocr_split_mode", "mode", "result_place", "overlay_alpha",
        "trig_poll_interval", "trig_change_ratio", "trig_stable_ratio", "trig_stable_samples",
        "trig_cooldown", "capture_width", "capture_height",
    };

    private static JsonObject MetaConfigSubset(JsonObject cfg)
    {
        var o = new JsonObject();
        foreach (string k in MetaConfigKeys) o[k] = ToNodeOr(cfg[k]);
        return o;
    }


    private void DropImgs(List<JsonObject> trimmed)
    {
        foreach (JsonObject e in trimmed) // 超過分の画像ファイルも削除
        {
            string? img = GetStr(e, "image");
            if (string.IsNullOrEmpty(img)) continue;
            try
            {
                File.Delete(Path.Combine(_paths.ImagesDir, Path.GetFileName(img)));
            }
            catch (Exception ex)
            {
                AppLog.Error("log image delete failed", ex);
            }
        }
    }

    private void DumpLocked()
    {
        try
        {
            // fsync→bak退避→replace の順で、停止・突落でも履歴が飛びにくい
            Directory.CreateDirectory(_paths.LogsDir);
            string tmp = Path.Combine(_paths.LogsDir, $"entries.{Environment.ProcessId}.tmp");
            var arr = new JsonArray(Entries.Select(e => (JsonNode)e.DeepClone()).ToArray());
            var opts = new JsonWriterOptions
            {
                Indented = true,               // .NET 8 の既定2文字字下げ（見ためだけの差）
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // ensure_ascii=False
            };
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           4096, FileOptions.WriteThrough))
            {
                using (var w = new Utf8JsonWriter(fs, opts)) arr.WriteTo(w);
                fs.Flush(true);
            }
            try
            {
                if (File.Exists(_paths.EntriesJson))
                    File.Move(_paths.EntriesJson, _paths.EntriesJson + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("entries bak rotate failed", ex);
            }
            File.Move(tmp, _paths.EntriesJson, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("log json save failed", ex);
        }
    }

    /// <summary>条目から参照されていない images/*.png を掃除（Python版 _gc_orphans）。</summary>
    public void GcOrphans()
    {
        lock (_lock) GcOrphansLocked();
    }

    private void GcOrphansLocked()
    {
        try
        {
            if (!Directory.Exists(_paths.ImagesDir)) return;   // まだ画像一枚も無い
            var refs = new HashSet<string>(
                Entries.Select(e => GetStr(e, "image"))
                       .Where(s => !string.IsNullOrEmpty(s))
                       .Select(s => Path.GetFileName(s!)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string p in Directory.GetFiles(_paths.ImagesDir, "*.png"))
            {
                if (!refs.Contains(Path.GetFileName(p)))
                {
                    try { File.Delete(p); }
                    catch (Exception ex) { AppLog.Error($"orphan unlink failed: {p}", ex); }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("orphan GC failed", ex);
        }
    }

    /// <summary>条目の新し→古の写し（UI・workerはこれを見て反復する。本体リストは動かさない）。</summary>
    public List<JsonObject> Snapshot()
    {
        lock (_lock) return new List<JsonObject>(Entries);
    }

    /// <summary>条目の画像実体。entries.jsonが外部改変されても ImagesDir 直下しか指さないよう名前だけ使う。</summary>
    public string? ImagePath(JsonObject entry)
    {
        string? img = GetStr(entry, "image");
        if (string.IsNullOrEmpty(img)) return null;
        string p = Path.Combine(_paths.ImagesDir, Path.GetFileName(img));
        return File.Exists(p) ? p : null;
    }

    // ---- JsonObject の簡易アクセサ（ConfigStore と同じ方針：kindを見て読む） ----

    public static string? GetStr(JsonObject o, string key)
    {
        JsonNode? n = o[key];
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            JsonValueKind.String => n.GetValue<string>(),
            JsonValueKind.Number => n.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Bunnagetaro5.Services;

/// <summary>ぶん投げの失敗（HTTP・生成の打ち切り・接続切れなど）。「やめる」とは別扱い。</summary>
public class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }
}

/// <summary>ぶん投げを途中でやめた（バーの「やめる」・アプリ終了）。失敗とは分けて扱う
/// （Python版 Cancelled と同じ）。</summary>
public class OllamaCancelledException : Exception
{
    public OllamaCancelledException() : base("ぶん投げをやめました") { }
}

/// <summary>
/// Ollamaへの問い合わせ（Python版 tw_ollama.py に対応）。
/// tags/show/chatストリーミング・温め・降ろし・「やめる」。温め・降ろし・vision確認は MainWindowEnv.cs から使う。
/// </summary>
public static class OllamaClient
{
    /// <summary>接続テストの一発待ち。指示書6.4（5秒）。</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>HTTP_TIMEOUT の読み側（Python版 (5,180) の 180。接続5秒はハンドラ側）。</summary>
    public static readonly TimeSpan HttpReadTimeout = TimeSpan.FromSeconds(180);

    /// <summary>ストリームで「次の1行」を待つ上限（Python版 STREAM_READ_TIMEOUT。
    /// 初めての載せ直しはHDDだと1分を超えることがあるので広く取る）。</summary>
    public const int StreamReadTimeoutSec = 300;

    /// <summary>1回のぶん投げ全体の上限（Python版 STREAM_TOTAL_TIMEOUT）。</summary>
    public const int StreamTotalTimeoutSec = 900;

    // HttpClientは使い回す（Python版のrequests.Sessionと同じで、接続の張り直しが軽い）。
    // 接続5秒（Python版 HTTP_TIMEOUT の最初の値）をハンドラで待つ。
    // テストでは internal の SetHttp で差し替える（本番はここだけ）。
    private static HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp() =>
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) });

    internal static void SetHttp(HttpClient client) => Http = client;

    /// <summary>使い回しのHttpClient（ShotArchive の model_info も同じ接続を使う。
    /// テストの SetHttp が全HTTPに効くようにするため内部で見せる）。</summary>
    internal static HttpClient Client => Http;

    /// <summary>base_url + apiの道。スキーム無しは http:// を補い、末尾の / を落とす
    /// （Python版 _url と同じ）。</summary>
    public static string Url(string baseUrl, string path)
    {
        var u = ConfigStore.NormBase(baseUrl).TrimEnd('/');
        return u.Length == 0 ? path : u + path;
    }

    /// <summary>名前で重さ(bytes)を引ける辞書（Python版 ollama_tag_info と同じ）。
    /// 名前が空の行は読み飛ばし、重さが読めなければ0。</summary>
    public static async Task<Dictionary<string, long>> GetTagInfoAsync(string baseUrl, CancellationToken ct = default)
    {
        var root = await GetJsonAsync(baseUrl, "/api/tags", ct);
        return ParseTagInfo(root);
    }

    private static async Task<JsonNode> GetJsonAsync(string baseUrl, string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TestTimeout);
        using var resp = await Http.GetAsync(Url(baseUrl, path), cts.Token);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    // ---- 解析（通信と切り離してテストできるようにした） ----

    public static List<string> ParseTagNames(JsonNode? root)
    {
        var list = new List<string>();
        if (root is not JsonObject obj || obj["models"] is not JsonArray arr)
            return list;
        foreach (var m in arr)
        {
            var nm = m is JsonObject o && o["name"] is { } n && n.GetValueKind() == JsonValueKind.String
                ? n.GetValue<string>() : "";
            list.Add(nm);
        }
        return list;
    }

    public static Dictionary<string, long> ParseTagInfo(JsonNode? root)
    {
        var outp = new Dictionary<string, long>();
        if (root is not JsonObject obj || obj["models"] is not JsonArray arr)
            return outp;
        foreach (var m in arr)
        {
            if (m is not JsonObject o) continue;
            var nm = o["name"] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : "";
            if (nm.Length == 0) continue;
            long size = 0;
            if (o["size"] is { } s && s.GetValueKind() == JsonValueKind.Number)
                long.TryParse(s.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            outp[nm] = size;
        }
        return outp;
    }

    /// <summary>導入済みの判定。Ollamaは"gemma4"のようにtagを省いてもlatestを引ける
    /// ので、省略名も導入扱い（Python版 model_installed と同じ）。</summary>
    public static bool ModelInstalled(string model, IEnumerable<string> names)
    {
        var m = (model ?? "").Trim();
        if (m.Length == 0) return false;
        foreach (var raw in names)
        {
            var n = raw ?? "";
            if (n == m) return true;
            if (!m.Contains(':') && (n == m + ":latest" || n.Split(':')[0] == m)) return true;
        }
        return false;
    }

    /// <summary>重さをGBへ（OllamaのUIと同じ丸め）。読めなければ空文字（fmt_gb と同じ）。</summary>
    public static string FmtGb(long nbytes)
    {
        if (nbytes <= 0) return "";
        return (nbytes / Math.Pow(1024, 3)).ToString("F1", CultureInfo.InvariantCulture) + "GB";
    }

    /// <summary>一覧の重さを1行で（自分のPCに載る大きさを見るため、Python版と同じ
    /// 先頭6件・「なし」）。</summary>
    public static string SizesLine(Dictionary<string, long> info, IEnumerable<string> sortedNames)
    {
        var parts = new List<string>();
        foreach (var n in sortedNames)
        {
            if (parts.Count >= 6) break;
            var gb = FmtGb(info.TryGetValue(n, out var b) ? b : 0);
            parts.Add(n + " " + gb);
        }
        return parts.Count == 0 ? "なし" : string.Join(", ", parts);
    }

    // ---- 生成設定の手入れ（Python版 gen_setting / _keep_alive / _think） ----

    // (key, 下限, 上限, 整数か) — Python版 OLLAMA_FIELDS と同じ範囲。
    // 設定ページの欄の範囲（指示書6.4）とは別で、「手編集のconfigを送る時に寄せる」範囲。
    private static readonly (string Key, double Lo, double Hi, bool IsInt)[] OllamaFields =
    {
        ("ollama_num_ctx", 2048, 262144, true),
        ("ollama_num_predict", 256, 65536, true),
        ("ollama_temperature", 0.0, 2.0, false),
        ("ollama_keep_alive_min", -1, 1440, true),
    };

    private static readonly Dictionary<string, double> OllamaDefaults = new()
    {
        ["ollama_num_ctx"] = 8192,
        ["ollama_num_predict"] = 4096,
        ["ollama_temperature"] = 0.0,
        ["ollama_keep_alive_min"] = 30,
    };

    /// <summary>生成設定を1つ読む。手編集の不正値・範囲外は既定値・範囲の端へ寄せる
    /// （Python版 gen_setting と同じ）。</summary>
    public static double GenSetting(JsonObject cfg, string key)
    {
        var f = Array.Find(OllamaFields, x => x.Key == key);
        double v;
        var node = cfg[key];
        if (node is not null && node.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        {
            // ok
        }
        else
        {
            v = OllamaDefaults.TryGetValue(key, out var d) ? d : 0;
        }
        if (f.IsInt) v = Math.Round(Math.Min(Math.Max(v, f.Lo), f.Hi));
        else v = Math.Min(Math.Max(v, f.Lo), f.Hi);
        return v;
    }

    private static long GenSettingInt(JsonObject cfg, string key) => (long)GenSetting(cfg, key);

    /// <summary>keep_alive は分で持つ。負＝ずっと載せておく（Ollamaは負の数をそう読む）。
    /// Python版 _keep_alive と同じで、負は数値 -1、そうでないなら "Nm"。</summary>
    public static JsonNode KeepAliveNode(JsonObject cfg)
    {
        var m = GenSettingInt(cfg, "ollama_keep_alive_min");
        return m < 0 ? JsonValue.Create(-1L)! : JsonValue.Create(m + "m")!;
    }

    /// <summary>送る think の値。auto は null＝送らない（モデル任せ）。
    /// 切り替えられないモデルへ on/off を送るとOllamaが断ることがあるので既定は送らない。</summary>
    public static bool? ThinkFlag(JsonObject cfg)
    {
        var t = cfg["ollama_think"] is { } n && n.GetValueKind() == JsonValueKind.String
            ? n.GetValue<string>() : "auto";
        return t switch { "on" => true, "off" => false, _ => null };
    }

    // ---- vision 判定（/api/show） ----

    /// <summary>capabilities に vision があるか。確認できなければ null
    /// （Python版 model_has_vision と同じ）。</summary>
    public static async Task<bool?> ModelHasVisionAsync(JsonObject cfg, string model, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TestTimeout);
            using var content = new StringContent(new JsonObject { ["name"] = model }.ToJsonString(),
                Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(Url(BaseOf(cfg), "/api/show"), content, cts.Token);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return ParseVision(JsonNode.Parse(text));
        }
        catch (Exception ex)
        {
            AppLog.Warning("api/show に失敗しました: " + ex.Message);
            return null;
        }
    }

    /// <summary>/api/show 応答から vision 判定だけ取り出す（小さく丸い値は小文字化して比べる）。</summary>
    public static bool? ParseVision(JsonNode? root)
    {
        if (root is not JsonObject obj || obj["capabilities"] is not JsonArray arr)
            return null;
        foreach (var c in arr)
        {
            if (c is { } v && v.GetValueKind() == JsonValueKind.String &&
                v.GetValue<string>().ToLowerInvariant() == "vision")
                return true;
        }
        return false;
    }

    // ---- 温め・降ろし（/api/generate） ----

    internal static string BaseOf(JsonObject cfg) =>
        cfg["base_url"] is { } b && b.GetValueKind() == JsonValueKind.String ? b.GetValue<string>() : ConfigStore.DefaultBaseUrl;

    internal static string ModelOf(JsonObject cfg) =>
        cfg["model"] is { } m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : "";

    /// <summary>モデルをすぐ降ろす（Python版 ollama_unload_model）。空名は何もしない。</summary>
    public static async Task UnloadModelAsync(JsonObject cfg, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TestTimeout);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = "",
            ["stream"] = false,
            ["keep_alive"] = 0,
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(Url(BaseOf(cfg), "/api/generate"), content, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>選ぶモデルを載せておく（Python版 ollama_warm_model）。モデルを変えたら
    /// 前のモデルは先に降ろす（失敗しても温めは続ける）。num_ctxはぶん投げる時と同じ値
    /// （違うとOllamaが毎回載せ直す）。</summary>
    public static async Task WarmModelAsync(JsonObject cfg, string model, string? prevModel = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(prevModel) && prevModel != model)
        {
            try { await UnloadModelAsync(cfg, prevModel!, ct); }
            catch (Exception ex) { AppLog.Warning("前のモデルを降ろせませんでした: " + ex.Message); }
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpReadTimeout);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = "",
            ["stream"] = false,
            ["keep_alive"] = KeepAliveNode(cfg),
            ["options"] = new JsonObject { ["num_predict"] = 1, ["num_ctx"] = GenSettingInt(cfg, "ollama_num_ctx") },
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(Url(BaseOf(cfg), "/api/generate"), content, cts.Token);
        resp.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        if (body is JsonObject bo && bo["error"] is { } e && e.GetValueKind() == JsonValueKind.String)
            throw new OllamaException(e.GetValue<string>());
    }

    // ---- chat ストリーミング（本文と思考を別々に通知） ----

    // 今流れている応答（1本だけ。ぶん投げはワーカー1本で順番に走る）。
    // 「やめる」はここを止めて、応答待ちで止まっている読み取りをすぐ起こす。
    // Ollamaは接続が切れると生成をやめる。
    private static readonly object ActiveLock = new();
    private static CancellationTokenSource? _activeCts;

    /// <summary>流れているぶん投げをやめる（別のスレッドから呼ぶ。流れていなければ何もしない）。</summary>
    public static void AbortActive()
    {
        CancellationTokenSource? cts;
        lock (ActiveLock) { cts = _activeCts; }
        try { cts?.Cancel(); }
        catch (Exception ex) { AppLog.Warning("やめるの処理で失敗: " + ex.Message); }
    }

    /// <summary>ぶん投げ1回の実行記録（分析・調整用。Python版 stats と同じ形）。
    /// done 行で statsSink（呼び手の実績列）に1件足される。</summary>
    private static Dictionary<string, object?> NewStats(JsonObject cfg, JsonArray messages,
        JsonObject options, bool? think, JsonNode keepAlive)
    {
        var st = new Dictionary<string, object?>
        {
            ["model"] = ModelOf(cfg),
            ["think"] = think,
            ["keep_alive"] = keepAlive.GetValueKind() == JsonValueKind.Number
                ? (object)keepAlive.GetValue<long>() : keepAlive.GetValue<string>(),
            ["options"] = options,
            ["prompt_msgs"] = messages.Count,
            ["img_b64_chars"] = 0L,
        };
        long imgChars = 0;
        foreach (var m in messages)
        {
            if (m is JsonObject mo && mo["images"] is JsonArray imgs)
                foreach (var s in imgs)
                    if (s is { } sv && sv.GetValueKind() == JsonValueKind.String)
                        imgChars += sv.GetValue<string>().Length;
        }
        st["img_b64_chars"] = imgChars;
        return st;
    }

    /// <summary>
    /// NDJSONストリームのぶん投げ（Python版 _stream_chat と同じ契約）。
    /// onDelta / onThinking には毎回「これまでの全文」を渡す（差分ではない）。
    /// numPredict を渡さない時も、設定の生成の上限（256〜65536）を必ず options に入れる（F17）。
    /// ct（または AbortActive）でやめると OllamaCancelledException。
    /// 本文は message.content、思考は message.thinking として別々に届く（返り値の本文に思考は混ぜない）。
    /// </summary>
    public static async Task<string> StreamChatAsync(JsonObject cfg, JsonArray messages,
        Action<string>? onDelta, Action<string>? onThinking,
        CancellationToken ct = default, int? numPredict = null, double deltaMinIntervalSec = 0.0,
        List<Dictionary<string, object?>>? statsSink = null)
    {
        var options = new JsonObject
        {
            ["temperature"] = GenSetting(cfg, "ollama_temperature"),
            ["num_ctx"] = GenSettingInt(cfg, "ollama_num_ctx"),
        };
        options["num_predict"] = numPredict ?? (int)GenSetting(cfg, "ollama_num_predict");
        var keepAlive = KeepAliveNode(cfg);
        var payload = new JsonObject
        {
            ["model"] = ModelOf(cfg),
            ["messages"] = messages,
            ["stream"] = true,
            ["keep_alive"] = keepAlive,
            ["options"] = options,
        };
        bool? think = ThinkFlag(cfg);
        if (think.HasValue) payload["think"] = think.Value;
        var stats = NewStats(cfg, messages, options, think, keepAlive);

        if (ct.IsCancellationRequested) throw new OllamaCancelledException();
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (ActiveLock) { _activeCts = streamCts; }
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            HttpResponseMessage resp;
            try
            {
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, Url(BaseOf(cfg), "/api/chat"))
                {
                    Content = content,
                };
                // 応答はヘッダだけ先に見て、本文は1行ずつ読む（stream:true の NDJSON）
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, streamCts.Token);
            }
            catch (OperationCanceledException) when (streamCts.IsCancellationRequested)
            {
                throw new OllamaCancelledException();
            }
            catch (OperationCanceledException)
            {
                // G11: 「やめる」以外のキャンセル（ConnectTimeout / HttpClient.Timeout 等）は時間切れの日本語案内へ
                throw new OllamaException($"AIサーバーに接続できません（時間切れ: {BaseOf(cfg)}）");
            }

            using (resp)
            {
                if ((int)resp.StatusCode != 200)
                {
                    string detail;
                    try { detail = Truncate(await resp.Content.ReadAsStringAsync(), 200); }
                    catch { detail = ""; }
                    if (think.HasValue && detail.ToLowerInvariant().Contains("think"))
                        throw new OllamaException(
                            "このモデルは思考の切り替えに対応していません。設定タブの「思考」を"
                            + "「モデル任せ」にしてください（" + Truncate(detail, 120) + "）");
                    throw new OllamaException($"HTTP {(int)resp.StatusCode}: {detail}");
                }
                if (ct.IsCancellationRequested) throw new OllamaCancelledException();

                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(streamCts.Token));
                var parts = new List<string>();
                var thinkParts = new List<string>();
                bool doneSeen = false;
                var deadline = Stopwatch.GetElapsedTime(t0).Add(TimeSpan.FromSeconds(StreamTotalTimeoutSec));
                double nextPush = 0, nextPushThink = 0;

                while (true)
                {
                    if (streamCts.IsCancellationRequested) throw new OllamaCancelledException();
                    if (Stopwatch.GetElapsedTime(t0) > deadline)
                        throw new OllamaException(
                            $"translation generation exceeded {StreamTotalTimeoutSec}s");
                    string? line;
                    try
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(streamCts.Token);
                        readCts.CancelAfter(TimeSpan.FromSeconds(StreamReadTimeoutSec));
                        line = await reader.ReadLineAsync(readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 「やめる」で応答を止めたときは接続エラーの種類はまちまちだが、
                        // やめた合図として読み替える（Python版 _lines_or_cancel と同じ）
                        if (streamCts.IsCancellationRequested) throw new OllamaCancelledException();
                        throw new OllamaException(
                            $"model の返事がありません（{StreamReadTimeoutSec}秒 待ちました）");
                    }
                    catch (IOException ex)
                    {
                        if (streamCts.IsCancellationRequested) throw new OllamaCancelledException();
                        throw new OllamaException("接続が切れました: " + ex.Message);
                    }
                    if (line is null) break;           // 応答の終わり
                    if (line.Length == 0) continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); }
                    catch (JsonException) { continue; } // 壊れた行は読み飛ばす（Pythonと同じ）
                    if (node is not JsonObject obj) continue;
                    if (obj["error"] is { } err &&
                        (err.GetValueKind() == JsonValueKind.String || err.GetValueKind() == JsonValueKind.Number))
                        throw new OllamaException(NodeToStr(err));

                    var chunk = StrIn(obj, "message", "content");
                    if (chunk.Length > 0)
                    {
                        if (!stats.ContainsKey("first_token_sec"))
                            stats["first_token_sec"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
                        parts.Add(chunk);
                        if (onDelta is not null)
                        {
                            double now = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                            if (now >= nextPush)
                            {
                                onDelta(string.Concat(parts));
                                // 修正J113: onDelta（同期UI更新）が間隔より長くかかった時、先に nextPush を足していると
                                // 次の1トークンで即発火して間引きが壊れる（暴走生成で数分フリーズ）。呼び終わった後から間隔を空ける
                                nextPush = Stopwatch.GetElapsedTime(t0).TotalSeconds + deltaMinIntervalSec;
                            }
                        }
                    }
                    // 思考は本文と別の欄で届く。本文の文字数やfirst_token_secには数え入れない
                    var thinkChunk = StrIn(obj, "message", "thinking");
                    if (thinkChunk.Length > 0)
                    {
                        thinkParts.Add(thinkChunk);
                        if (onThinking is not null)
                        {
                            double tnow = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                            if (tnow >= nextPushThink)
                            {
                                onThinking(string.Concat(thinkParts));
                                nextPushThink = Stopwatch.GetElapsedTime(t0).TotalSeconds + deltaMinIntervalSec;
                            }
                        }
                    }
                    if (obj["done"] is { } d && d.GetValueKind() == JsonValueKind.True)
                    {
                        doneSeen = true;
                        string? reason = obj["done_reason"] is { } r && r.GetValueKind() == JsonValueKind.String
                            ? r.GetValue<string>() : null;
                        if (thinkParts.Count > 0) stats["thinking_chars"] = string.Concat(thinkParts).Length;
                        stats["done_reason"] = reason;
                        stats["total_sec"] = Math.Round(Stopwatch.GetElapsedTime(t0).TotalSeconds, 3);
                        CopyNum(stats, obj, "prompt_eval_count");
                        CopyNum(stats, obj, "eval_count");
                        foreach (var k in new[] { "prompt_eval_duration", "eval_duration", "load_duration", "total_duration" })
                        {
                            if (obj[k] is { } dur && dur.GetValueKind() == JsonValueKind.Number)
                                stats[k + "_ms"] = Math.Round(dur.GetValue<double>() / 1e6, 1);
                        }
                        long ev = 0; double dur2 = 0;
                        if (obj["eval_count"] is { } ec && ec.GetValueKind() == JsonValueKind.Number)
                            ev = (long)ec.GetValue<double>();
                        if (obj["eval_duration"] is { } ed && ed.GetValueKind() == JsonValueKind.Number)
                            dur2 = ed.GetValue<double>();
                        if (ev > 0 && dur2 > 0)
                            stats["tokens_per_sec"] = Math.Round(ev / (dur2 / 1e9), 1);
                        statsSink?.Add(stats);
                        // G3: done_reason != "stop"（length 等）でも例外にせず途中までの本文を返す。
                        // 打ち切り理由の案内・印付けは stats["done_reason"] を受ける呼び手側で行う。
                        break;
                    }
                }
                if (!doneSeen)
                {
                    if (streamCts.IsCancellationRequested) throw new OllamaCancelledException();
                    throw new OllamaException("生成が途中で切れました（接続を確認してください）");
                }
                return string.Concat(parts).Trim();
            }
        }
        finally
        {
            lock (ActiveLock)
            {
                if (ReferenceEquals(_activeCts, streamCts)) _activeCts = null;
            }
        }
    }

    private static string NodeToStr(JsonNode n) =>
        n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToString();

    private static string StrIn(JsonObject obj, string key, string sub) =>
        obj[key] is JsonObject o && o[sub] is { } v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>() : "";

    private static void CopyNum(Dictionary<string, object?> stats, JsonObject obj, string key)
    {
        if (obj[key] is { } v && v.GetValueKind() == JsonValueKind.Number)
            stats[key] = (long)v.GetValue<double>();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace Bunnagetaro5.Services;

/// <summary>
/// 分析・model調整用のアーカイブ。
/// 翻訳1件ごとに、撮ったままの画像（-raw.png）、実際にmodelへ送ったサイズ（-send.png）、
/// その1件のメタ情報（.json）を shots フォルダへ自動で書き出す。
/// entries.json 側の掃除とは別物で、ここでは**削除しない**。
/// workerスレッドから呼ぶ。保存に失敗しても翻訳は止めない（例外は握って記録だけ）。
/// </summary>
public static class ShotArchive
{
    /// <summary>本体が起動時に代入する。</summary>
    public static string AppVersion = "";

    private static readonly object MiLock = new();
    private static readonly Dictionary<string, Dictionary<string, object?>> MiCache = new();
    private static readonly Dictionary<string, (DateTime At, string Error)> MiFailAt = new();
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;
    internal static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(1);
    private static Dictionary<string, object?>? _envCache;

    /// <summary>保存フォルダを決めて、無ければ作る。指定が空なら既定のフォルダ。</summary>
    public static string DirFor(JsonObject? cfg, Paths paths)
    {
        string raw = "";
        JsonNode? n = cfg?["shots_dir"];
        if (n is not null && n.GetValueKind() == JsonValueKind.String)
            raw = (n.GetValue<string>() ?? "").Trim();
        if (raw.Length > 0)
            raw = Environment.ExpandEnvironmentVariables(raw); // expandvars（~ は Windows に無い）
        string baseDir = raw.Length > 0 ? raw : paths.DefaultShotsDir;
        try { Directory.CreateDirectory(baseDir); }
        catch (Exception) { /* 作れなくても呼び手が握る */ }
        return baseDir;
    }

    /// <summary>entries.json の ts を 20261107-180512 の形にして id と結ぶ（時系列で並ぶ）。</summary>
    internal static string Stem(string tsLocal, string eid)
    {
        var sb = new StringBuilder();
        foreach (char ch in tsLocal ?? "")
            if (ch is >= '0' and <= '9') sb.Append(ch);
        string digits = sb.ToString();
        if (digits.Length >= 14)
            return $"{digits[..8]}-{digits.Substring(8, 6)}-{eid}";
        return DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
               + "-" + eid;
    }

    internal static byte[] PngBytes(BitmapSource img)
    {
        if (!img.IsFrozen) img.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha1_16(byte[] buf)
        => Convert.ToHexString(SHA1.HashData(buf)).ToLowerInvariant()[..16];

    /// <summary>撮ったまま（raw）と送ったサイズ（send）のPNGを書き、ファイル名と
    /// ハッシュ・バイト数を返す。ここでの失敗は error に入れて翻訳は続ける。</summary>
    public static Dictionary<string, object?> SaveImages(JsonObject? cfg, Paths paths,
        BitmapSource img, BitmapSource? prepared, string tsLocal, string eid, byte[]? sendPng = null)
    {
        var outp = new Dictionary<string, object?> { ["raw"] = null, ["send"] = null };
        if (eid.Length == 0 || img is null) return outp;
        try
        {
            string baseDir = DirFor(cfg, paths);
            string stem = Stem(tsLocal, eid);
            bool resized = prepared is not null &&
                (prepared.PixelWidth != img.PixelWidth || prepared.PixelHeight != img.PixelHeight);
            byte[] rawBuf = !resized && sendPng is not null ? sendPng : PngBytes(img);
            outp["raw"] = stem + "-raw.png";
            File.WriteAllBytes(Path.Combine(baseDir, (string)outp["raw"]!), rawBuf);
            outp["raw_sha1"] = Sha1_16(rawBuf);
            outp["raw_png_bytes"] = rawBuf.Length;
            outp["raw_size"] = new int[] { img.PixelWidth, img.PixelHeight };
            if (resized)
            {
                byte[] pbuf = sendPng ?? PngBytes(prepared!);
                outp["send"] = stem + "-send.png";
                File.WriteAllBytes(Path.Combine(baseDir, (string)outp["send"]!), pbuf);
                outp["send_sha1"] = Sha1_16(pbuf);
                outp["send_png_bytes"] = pbuf.Length;
                outp["send_size"] = new int[] { prepared!.PixelWidth, prepared.PixelHeight };
            }
            outp["dir"] = baseDir;
        }
        catch (Exception)
        {
            outp["error"] = "shot save failed";
        }
        return outp;
    }

    /// <summary>Ollama側が持つmodelの事実（quant・パラメータ数・family・対応context）。
    /// modelごとに一度だけ引いて cache する。
    /// workerから同期で呼ぶ前提（翻訳を止めないため失敗は model_info_error に入れる）。</summary>
    public static Dictionary<string, object?> ModelInfo(JsonObject? cfg)
        => ModelInfoAsync(cfg).GetAwaiter().GetResult();

    public static async Task<Dictionary<string, object?>> ModelInfoAsync(JsonObject? cfg)
    {
        if (cfg is not null && TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeBuiltinOcr)
            return new Dictionary<string, object?> { ["model"] = TranslatePipeline.BuiltinOcrModelOf(cfg), ["builtin"] = true };
        string model = "";
        JsonNode? mn = cfg?["model"];
        if (mn is not null && mn.GetValueKind() == JsonValueKind.String)
            model = (mn.GetValue<string>() ?? "").Trim();
        if (model.Length == 0) return new Dictionary<string, object?>();
        // 内蔵の OCR は Ollama に問い合わせない
        if (BuiltinOcr.IsBuiltin(model)) return new Dictionary<string, object?> { ["model"] = model, ["builtin"] = true };
        lock (MiLock)
        {
            if (MiCache.TryGetValue(model, out Dictionary<string, object?>? hit))
                return hit;
            if (MiFailAt.TryGetValue(model, out var fail) && UtcNow() - fail.At < RetryCooldown)
                return new Dictionary<string, object?> { ["model"] = model, ["model_info_error"] = fail.Error };
        }
        var info = new Dictionary<string, object?> { ["model"] = model };
        bool ok = false;
        try
        {
            string baseUrl = "";
            JsonNode? bn = cfg?["base_url"];
            if (bn is not null && bn.GetValueKind() == JsonValueKind.String)
                baseUrl = bn.GetValue<string>() ?? "";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string verJson = await OllamaClient.Client.GetStringAsync(
                OllamaClient.Url(baseUrl, "/api/version"), cts.Token);
            JsonNode? ver = JsonNode.Parse(verJson);
            JsonNode? v = ver?["version"];
            if (v is not null && v.GetValueKind() == JsonValueKind.String)
                info["ollama_version"] = v.GetValue<string>();
            var body = new JsonObject { ["name"] = model };
            using var req = new HttpRequestMessage(
                HttpMethod.Post, OllamaClient.Url(baseUrl, "/api/show"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage resp = await OllamaClient.Client.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            string showJson = await resp.Content.ReadAsStringAsync(cts.Token);
            JsonNode? show = JsonNode.Parse(showJson);
            JsonNode? details = show?["details"];
            foreach (string k in new[] { "family", "parameter_size", "parent_model", "architecture" })
            {
                JsonNode? dk = details?[k];
                if (dk is not null && dk.GetValueKind() == JsonValueKind.String &&
                    (dk.GetValue<string>() ?? "").Length > 0)
                    info[k] = dk.GetValue<string>();
            }
            JsonNode? ql = details?["quantization_level"] ?? details?["quantization"];
            if (ql is not null && ql.GetValueKind() == JsonValueKind.String &&
                (ql.GetValue<string>() ?? "").Length > 0)
                info["quantization"] = ql.GetValue<string>();

            // Ollama /api/show の model_info は model 名でネストされず平らな辞書
            if (show?["model_info"] is JsonObject mo)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in mo)
                {
                    string low = kv.Key.ToLowerInvariant();
                    if (kv.Value is null) continue;
                    if (low == "general.architecture" &&
                        kv.Value.GetValueKind() == JsonValueKind.String &&
                        (kv.Value.GetValue<string>() ?? "").Length > 0 &&
                        !info.ContainsKey("architecture"))
                    {
                        info["architecture"] = kv.Value.GetValue<string>();
                    }
                    else if (low.EndsWith("context_length") &&
                        kv.Value.GetValueKind() == JsonValueKind.Number)
                    {
                        double dv = kv.Value.GetValue<double>();
                        if (Math.Floor(dv) == dv) info["trained_context"] = (long)dv;
                    }
                    else if (low.EndsWith("filename") &&
                        kv.Value.GetValueKind() == JsonValueKind.String)
                        info["gguf"] = Path.GetFileName(kv.Value.GetValue<string>() ?? "");
                }
            }
            ok = true;
        }
        catch (Exception ex)
        {
            string m = ex.Message ?? "";
            string err = m.Length > 160 ? m[..160] : m;
            info["model_info_error"] = err;
            lock (MiLock) MiFailAt[model] = (UtcNow(), err);
        }
        if (ok)
        {
            lock (MiLock)
            {
                MiCache[model] = info;
                MiFailAt.Remove(model);
            }
        }
        return info;
    }

    /// <summary>テスト用：model_info の cache を空にする（SetHttp と対で使う）。</summary>
    internal static void ClearModelInfoCache()
    {
        lock (MiLock)
        {
            MiCache.Clear();
            MiFailAt.Clear();
        }
    }

    /// <summary>このアプリとPCの環境情報。modelの比較は環境が変わると条件が読めなくなるため記録する（ランタイムは dotnet として記録）。</summary>
    public static Dictionary<string, object?> EnvInfo()
    {
        if (_envCache is not null) return new Dictionary<string, object?>(_envCache);
        var info = new Dictionary<string, object?>
        {
            ["app_version"] = AppVersion,
            ["dotnet"] = Environment.Version.ToString(),
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["machine"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["cpu"] = Trunc(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", 80),
        };
        try
        {
            if (TryTotalRamGb(out double ramGb)) info["ram_gb"] = ramGb;
        }
        catch (Exception) { /* 入っていれば積む、と同じで握る */ }
        _envCache = info;
        return new Dictionary<string, object?>(info);
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength;   // Marshal.SizeOf の値填入（MSVC の sizeof(MEMORYSTATUSEX) と同じ）
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    private static bool TryTotalRamGb(out double ramGb)
    {
        ramGb = 0;
        var m = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatusEx)) };
        if (!GlobalMemoryStatusEx(m)) return false;
        ramGb = Math.Round(m.ullTotalPhys / Math.Pow(2, 30), 1, MidpointRounding.ToEven);
        return true;
    }

    /// <summary>メタ情報を1件1JSONで書き出す。entries.json の id と同じ id を載せる。</summary>
    public static string? WriteMeta(JsonObject? cfg, Paths paths, JsonObject meta)
    {
        JsonNode? idn = meta["id"];
        string eid = idn is not null && idn.GetValueKind() == JsonValueKind.String
            ? (idn.GetValue<string>() ?? "") : "";
        if (eid.Length == 0) return null;
        try
        {
            string baseDir = DirFor(cfg, paths);
            JsonNode? tsNode = meta["ts"];
            string ts = tsNode is not null && tsNode.GetValueKind() == JsonValueKind.String
                ? (tsNode.GetValue<string>() ?? "") : "";
            string name = Stem(ts, eid) + ".json";
            string tmp = Path.Combine(baseDir, $"{name}.{Environment.ProcessId}.tmp");
            var opts = new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // ensure_ascii=False
            };
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           4096, FileOptions.WriteThrough))
            {
                using (var w = new Utf8JsonWriter(fs, opts)) meta.WriteTo(w);
                fs.Flush(true); // os.fsync
            }
            File.Move(tmp, Path.Combine(baseDir, name), overwrite: true); // tmp.replace
            return name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

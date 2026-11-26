using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 起動時の一覧読み・モデルの温め・設定変更時の載せ直し・前提チェック。
/// 本物の MainWindow に、偽の Ollama（HttpClient の差し替え）を向けて確かめる。
/// 呼ばれていないと状態欄の表示も出ないので、状態欄の実際の文字まで見る。
/// </summary>
public class EnvChecksTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;
    private readonly List<HttpClient> _clients = new();

    public EnvChecksTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_env_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    // ---- 偽の Ollama ----

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Reqs = new();
        public bool Down;                              // 立っていない（接続拒否）
        public string[] Models = { "good:1b" };        // 導入済み
        public bool Vision = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            string path = req.RequestUri!.AbsolutePath;
            lock (Reqs) Reqs.Add((path, body));
            if (Down) throw new HttpRequestException("接続できません");
            string json = path switch
            {
                "/api/tags" => new JsonObject
                {
                    ["models"] = new JsonArray(Models.Select(m =>
                        (JsonNode)new JsonObject { ["name"] = m, ["size"] = 1000000000L }).ToArray())
                }.ToJsonString(),
                "/api/show" => new JsonObject
                {
                    ["capabilities"] = Vision ? new JsonArray("completion", "vision") : new JsonArray("completion")
                }.ToJsonString(),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        public int Count(string path) { lock (Reqs) return Reqs.Count(r => r.Path == path); }
        public List<(string Path, string Body)> Snapshot() { lock (Reqs) return Reqs.ToList(); }
        public void Clear() { lock (Reqs) Reqs.Clear(); }
    }

    private Fake UseFake()
    {
        var f = new Fake();
        var c = new HttpClient(f);
        _clients.Add(c);
        OllamaClient.SetHttp(c);
        return f;
    }

    // ---- helpers ----

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(Dispatcher d, int ms) => TestUtil.Pump(ms);

    private static void Await(MainWindow w, Task t, int maxMs = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!t.IsCompleted && DateTime.UtcNow < end) Pump(w.Dispatcher, 15);
        Assert.True(t.IsCompleted, "非同期処理が終わらなかった");
        t.GetAwaiter().GetResult();
    }

    private static void WaitUntil(MainWindow w, Func<bool> cond, int maxMs = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!cond() && DateTime.UtcNow < end) Pump(w.Dispatcher, 25);
    }

    private void OnSta(Action body)
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { err = e; }
            finally
            {
                // 失敗した時に窓を閉じ忘れると、スレッド終了で WPF が落ちてテスト全体が止まる
                foreach (var w in _opened) { try { w.Close(); } catch { } }
                _opened.Clear();
            }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("STAテストが終わらない");
        if (err is not null) throw err;
    }

    private readonly List<MainWindow> _opened = new();

    private (MainWindow W, ConfigStore Cfg) NewWindow(Action<ConfigStore>? setup = null, bool envChecks = false)
    {
        EnsureAppResources();
        var cfg = new ConfigStore(_paths);
        cfg.Load();
        cfg.SetStr("base_url", "http://localhost:11434");
        cfg.SetStr("model", "good:1b");
        SetModel(cfg, "good:1b");   // モデルはいつもプリセットごと
        cfg.SetBool("quick_enabled", false);
        // 既定の「翻訳」は専用のモデル（PLaMo）と文字起こしを持つ。ここでは
        // グローバルのモデル・画像そのままの前提を確かめるので、翻訳をグローバル設定に戻す
        foreach (var n in cfg.Root["prompts"]!.AsArray())
            if (n is System.Text.Json.Nodes.JsonObject p && p["name"]?.GetValue<string>() == ConfigStore.DefaultPromptName)
                p["use_global"] = true;
        cfg.SetBool("ocr_split_mode", false);
        setup?.Invoke(cfg);
        var w = new MainWindow(_paths, cfg);
        w.Throw.EnvChecks = envChecks;
        _opened.Add(w);
        return (w, cfg);
    }

    /// <summary>使っているプリセットのモデルを変える（モデルはいつもプリセットごと）</summary>
    private static void SetModel(ConfigStore c, string model) => c.ActivePreset()!["model"] = model;

    private static object? Fld(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private static void SetFld(object o, string name, object? v)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, v);

    private static string Problem(MainWindow w) => w.Status.EnvProblem;

    /// <summary>状態欄を巡回の1回ぶん進めて、実際に見える文字を返す</summary>
    private static string StatusNow(MainWindow w)
    {
        typeof(MainWindow).GetMethod("TickStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
        return ((TextBlock)Fld(w, "StatusText")!).Text;
    }

    private static ComboBox? FindCombo(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is ComboBox c && c.Name == name) return c;
            var d = FindCombo(ch, name);
            if (d is not null) return d;
        }
        return null;
    }

    // ---- 前提チェック ----

    [Fact]
    public void AIサーバーが立っていないと状態欄に未起動と出る()
    {
        OnSta(() =>
        {
            var f = UseFake(); f.Down = true;
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            Await(w, w.Throw.EnvProbeAsync());
            Assert.Equal("AIサーバー未起動", Problem(w));
            Assert.StartsWith("AIサーバー未起動", StatusNow(w));
            w.Close();
        });
    }

    [Fact]
    public void 入っていないモデル名なら状態欄にモデル未導入と出る()
    {
        OnSta(() =>
        {
            UseFake();
            var (w, _) = NewWindow(c => SetModel(c, "nothing:7b"), envChecks: true);
            w.Show();
            Await(w, w.Throw.EnvProbeAsync());
            Assert.Equal("モデル未導入", Problem(w));
            Assert.StartsWith("モデル未導入", StatusNow(w));
            w.Close();
        });
    }

    [Fact]
    public void モデル欄が空ならモデル未選択_画像を読めないモデルならvision非対応_揃えば空()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => SetModel(c, ""), envChecks: true);
            w.Show();
            Await(w, w.Throw.EnvProbeAsync());
            Assert.Equal("モデル未選択", Problem(w));
            w.Close();

            // 文字起こしONの時は vision非対応 を出さない
            var (w2, _) = NewWindow(c => c.SetBool("ocr_split_mode", true), envChecks: true);
            f.Vision = false;
            w2.Show();
            Await(w2, w2.Throw.EnvProbeAsync());
            Assert.Equal("", Problem(w2));
            w2.Close();

            // 画像そのまま（throw_mode=image）の時は vision非対応 を出す
            var (w3, _) = NewWindow(c => { c.SetStr("throw_mode", ConfigStore.ThrowModeImage); c.SetBool("ocr_split_mode", false); }, envChecks: true);
            f.Vision = false;
            w3.Show();
            Await(w3, w3.Throw.EnvProbeAsync());
            Assert.Equal("vision非対応", Problem(w3));
            f.Vision = true;
            Await(w3, w3.Throw.EnvProbeAsync());
            Assert.Equal("", Problem(w3));
            w3.Close();
        });
    }

    // ---- 起動時の一覧読み ----

    [Fact]
    public void 起動時の一覧読みでmodel_listが更新される()
    {
        OnSta(() =>
        {
            var f = UseFake(); f.Models = new[] { "zeta:1b", "alpha:2b", "good:1b" };
            var (w, cfg) = NewWindow(envChecks: true);
            w.Show();
            w.ShowSettingsPage();
            Pump(w.Dispatcher, 200);
            Await(w, w.Throw.StartupModelsAsync());
            var list = ((JsonArray)cfg.GetRaw("model_list")!).Select(x => x!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "alpha:2b", "good:1b", "zeta:1b" }, list);
            Assert.Equal(true, Fld(w.Throw, "_modelsReady"));
            var cb = FindCombo(w, "ModelCombo");
            Assert.NotNull(cb);
            Assert.Equal(3, cb!.Items.Count);   // モデル欄には Ollama のモデルだけ並ぶ（内蔵 OCR はぶん投げ方側）
            Assert.Equal("alpha:2b", cb.Items[0]);
            w.Close();
        });
    }

    // ---- 温め ----

    [Fact]
    public void 温めは設定のモデルをnum_ctx付きで載せ_連続では積み直さない()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            var gen = f.Snapshot().First(r => r.Path == "/api/generate");
            var body = JsonNode.Parse(gen.Body)!.AsObject();
            Assert.Equal("good:1b", body["model"]!.GetValue<string>());
            Assert.Equal(1, body["options"]!["num_predict"]!.GetValue<int>());
            Assert.True(body["options"]!["num_ctx"]!.GetValue<int>() > 0);
            Assert.Equal("good:1b", (string)Fld(w.Throw, "_activeModel")!);
            Assert.False(w.Status.WarmRunning);
            int n = f.Count("/api/generate");
            Await(w, w.Throw.WarmAiAsync("test2"));
            Assert.Equal(n, f.Count("/api/generate"));   // 240秒以内は積み直さない
            w.Close();
        });
    }

    [Fact]
    public void localhost以外のサーバーへは勝手に載せない()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => c.SetStr("base_url", "http://example.com:11434"), envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            Assert.Equal(0, f.Count("/api/generate"));
            w.Close();
        });
    }

    [Fact]
    public void 温めの失敗は状態欄に出る_かつ温め中の印は戻る()
    {
        OnSta(() =>
        {
            var f = UseFake(); f.Down = true;
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            Assert.False(w.Status.WarmRunning);
            Assert.Contains("モデル起動に失敗", ((TextBlock)Fld(w, "StatusText")!).Text);
            w.Close();
        });
    }

    // ---- 設定が変わった時の載せ直し ----

    [Fact]
    public void モデルを変えると前のモデルを降ろして新しいモデルを載せる()
    {
        OnSta(() =>
        {
            var f = UseFake(); f.Models = new[] { "good:1b", "next:2b" };
            var (w, cfg) = NewWindow(envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            Pump(w.Dispatcher, 400);   // 温め後の前提チェック・一覧読みを落ち着かせる
            f.Clear();
            SetModel(cfg, "next:2b");
            w.Throw.AfterRunCfgChange();
            WaitUntil(w, () => f.Count("/api/generate") >= 2);
            var gens = f.Snapshot().Where(r => r.Path == "/api/generate")
                .Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();
            Assert.Equal(2, gens.Count);
            Assert.Equal("good:1b", gens[0]["model"]!.GetValue<string>());      // 先に前のモデルを降ろす
            Assert.Equal(0, gens[0]["keep_alive"]!.GetValue<int>());
            Assert.Equal("next:2b", gens[1]["model"]!.GetValue<string>());      // 新しい方を載せる
            Assert.Equal("next:2b", (string)Fld(w.Throw, "_activeModel")!);
            w.Close();
        });
    }

    [Fact]
    public void ぶん投げに関係ない設定の変更では載せ直さない()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, cfg) = NewWindow(envChecks: true);
            w.Show();
            f.Clear();
            cfg.SetInt("strip_width", 600);
            w.Throw.AfterRunCfgChange();
            Pump(w.Dispatcher, 300);
            Assert.Equal(0, f.Count("/api/generate"));
            w.Close();
        });
    }

    [Fact]
    public void 設定ページでモデル欄を変えて確定するとその場で載せ直しが走る()
    {
        OnSta(() =>
        {
            var f = UseFake(); f.Models = new[] { "good:1b", "next:2b" };
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            w.ShowSettingsPage();
            Pump(w.Dispatcher, 200);
            var pc = FindCombo(w, "PresetCombo");
            Assert.NotNull(pc);
            pc!.SelectedIndex = 1;              // 「翻訳」を編集（モデルはいつもプリセットごと）
            Pump(w.Dispatcher, 100);
            var cb = FindCombo(w, "ModelCombo");
            Assert.NotNull(cb);
            Pump(w.Dispatcher, 1700);          // 起動時の自動の温めなどを済ませる
            f.Clear();
            cb!.Text = "next:2b";
            cb.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(cb)!, 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = UIElement.KeyDownEvent });
            // 300msの間引き保存のあとで AfterSave → 載せ直し（降ろし+載せ=generate 2本）
            WaitUntil(w, () => f.Count("/api/generate") >= 2);
            Assert.True(f.Count("/api/generate") >= 2, "設定を変えても載せ直しが走らなかった");
            w.Close();
        });
    }

    // ---- 窓の起動に配線されている（「どこからも呼ばれていなかった」不具合そのもの） ----

    [Fact]
    public void 窓を出すと一覧読み_温め_前提チェックが自動で走る()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => { c.SetStr("throw_mode", ConfigStore.ThrowModeImage); c.SetBool("ocr_split_mode", false); }, envChecks: true);
            w.Show();
            WaitUntil(w, () => f.Count("/api/generate") >= 1 && f.Count("/api/show") >= 1);
            Assert.True(f.Count("/api/tags") >= 1, "一覧読みが走っていない");
            Assert.True(f.Count("/api/generate") >= 1, "温めが走っていない");
            Assert.True(f.Count("/api/show") >= 1, "前提チェック（vision確認）が走っていない");
            w.Close();
        });
    }

    [Fact]
    public void キャプチャ窓を開くと温めが走る()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            Pump(w.Dispatcher, 1800);          // 起動時の温めを済ませる
            f.Clear();
            SetFld(w.Throw, "_warmTsUtc", DateTime.MinValue);   // 240秒の間隔を空けたことにする
            w.Session.OpenCapture();
            WaitUntil(w, () => f.Count("/api/generate") >= 1);
            Assert.True(f.Count("/api/generate") >= 1, "窓を開いても温めが走らない");
            w.Close();
        });
    }

    [Fact]
    public void 終了時に載せたモデルを降ろす()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c => c.SetBool("ocr_split_mode", false), envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            f.Clear();
            w.Close();
            var unloads = f.Snapshot().Where(r => r.Path == "/api/generate")
                .Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();
            Assert.Contains(unloads, b => b["model"]!.GetValue<string>() == "good:1b" && b["keep_alive"]!.GetValue<int>() == 0);
        });
    }

    [Fact]
    public void J13_終了時に文字起こしVLMならVLMモデルも降ろす()
    {
        OnSta(() =>
        {
            var f = UseFake();
            // 文字起こし VLM のモデルを終了時に降ろす
            var (w, _) = NewWindow(c =>
            {
                c.SetStr("throw_mode", ConfigStore.ThrowModeOcr);
                c.SetBool("ocr_split_mode", true);
                c.SetStr("ocr_engine", ConfigStore.OcrEngineVlm);
                c.SetStr("ocr_vlm_model", ConfigStore.OcrPresetModel);
            }, envChecks: true);
            w.Show();
            f.Clear();
            w.Close();
            var unloads = f.Snapshot().Where(r => r.Path == "/api/generate")
                .Select(r => JsonNode.Parse(r.Body)!.AsObject()).ToList();
            Assert.Contains(unloads, b => b["model"]!.GetValue<string>() == "good:1b" && b["keep_alive"]!.GetValue<int>() == 0);
            Assert.Contains(unloads, b => b["model"]!.GetValue<string>() == ConfigStore.OcrPresetModel && b["keep_alive"]!.GetValue<int>() == 0);
        });
    }

    [Fact]
    public void J17_文字起こしが内蔵OCRなら温めも終了時の降ろしもOllamaに内蔵の名前を送らない()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c =>
            {
                c.SetStr("throw_mode", ConfigStore.ThrowModeOcr);
                c.SetBool("ocr_split_mode", true);
                c.SetStr("ocr_engine", ConfigStore.OcrEngineBuiltin);
                c.SetStr("builtin_ocr_size", BuiltinOcr.SizeSmall);
            }, envChecks: true);
            w.Show();
            Await(w, w.Throw.WarmAiAsync("test"));
            w.Close();
            var sent = f.Snapshot().Where(r => r.Path is "/api/generate" or "/api/chat" or "/api/show")
                .Where(r => r.Body.Length > 0).Select(r => JsonNode.Parse(r.Body)!.AsObject()["model"]?.GetValue<string>()).ToList();
            Assert.DoesNotContain(BuiltinOcr.ModelName, sent);
            Assert.DoesNotContain(BuiltinOcr.SmallModelName, sent);
            Assert.Contains("good:1b", sent);   // 訳すモデルは今まで通り Ollama で温めて降ろす
        });
    }

    [Fact]
    public void J17_内蔵OCRのみなら前提チェックはOllamaを見ずモデルのファイルだけ見る()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, _) = NewWindow(c =>
            {
                c.SetStr("throw_mode", ConfigStore.ThrowModeBuiltinOcr);
                c.SetStr("builtin_ocr_size", BuiltinOcr.SizeSmall);
                c.SetStr("model", "");
                SetModel(c, "");
            }, envChecks: true);
            w.Show();
            f.Clear();
            Await(w, w.Throw.EnvProbeAsync());
            Assert.Equal("", Problem(w));
            Assert.DoesNotContain(f.Snapshot(), r => r.Path == "/api/tags");
            w.Close();
        });
    }

    [Fact]
    public void 終了時のモデル降ろしはリモート設定だと送らない()
    {
        OnSta(() =>
        {
            var f = UseFake();
            var (w, cfg) = NewWindow(envChecks: true);
            cfg.Settings.BaseUrl = "http://192.168.10.50:11434";
            cfg.Save();
            w.Show();
            w.Session.OpenCapture();
            Pump(w.Dispatcher, 100);
            f.Clear();
            w.Close();
            var unloads = f.Snapshot().Where(r => r.Path == "/api/generate").ToList();
            Assert.Empty(unloads);
            Assert.False(w.IsVisible);
        });
    }

    [Fact]
    public void J30_配信録画に映す設定で開いているキャプチャ窓のDisplayAffinityが即切り替わる()
    {
        OnSta(() =>
        {
            var (w, cfg) = NewWindow(envChecks: false);
            w.Show();
            w.Session.OpenCapture(animate: false);
            Pump(w.Dispatcher, 100);
            var cap = w.Session.CaptureWin;
            Assert.NotNull(cap);
            var h = new System.Windows.Interop.WindowInteropHelper(cap!).Handle;
            Assert.NotEqual(IntPtr.Zero, h);

            Assert.True(Native.GetWindowDisplayAffinity(h, out uint aff0));
            Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, aff0);

            cfg.Settings.AllowCaptureOverlay = true;
            Pump(w.Dispatcher, 50);
            Assert.True(Native.GetWindowDisplayAffinity(h, out uint aff1));
            Assert.Equal(Native.WDA_NONE, aff1);

            cfg.Settings.AllowCaptureOverlay = false;
            Pump(w.Dispatcher, 50);
            Assert.True(Native.GetWindowDisplayAffinity(h, out uint aff2));
            Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, aff2);

            w.Close();
        });
    }
}

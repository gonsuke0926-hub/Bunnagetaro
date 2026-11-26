using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// OllamaClientの通信部分。本物には繋がず、自分で用意した
/// HttpMessageHandlerで「 NDJSONが細切れで届く」様子と「やめる」を再現し、
/// 分割受信の組み立てと中断の例外を確認する。
/// </summary>
public class OllamaStreamTests : IDisposable
{
    // ---- 使い捨ての通信相手 ----

    private readonly List<HttpClient> _clients = new();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<(string Uri, string Body)> Requests = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            return _respond(request);
        }
    }

    /// <summary>与えたバイト列を「細切れで」「途中で止めて」返す偽の応答ストリーム。
    /// 本物のネットワークで1行が2回に分かれて届く様子（とブロック）を再現する。</summary>
    private sealed class ChunkyStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _cur;
        private int _pos;
        private TaskCompletionSource<bool>? _gate;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public void Add(string s) => _chunks.Enqueue(Encoding.UTF8.GetBytes(s));

        /// <summary>これ以上チャンクが無い所でここで止まる（OpenGateまで永久待ち）。</summary>
        public void Block() => _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OpenGate() => _gate?.TrySetResult(true);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_cur is null)
                {
                    if (_chunks.Count == 0)
                    {
                        if (_gate is null) return 0;      // 応答の終わり（EOF）
                        await _gate.Task.WaitAsync(ct);   // 「やめる」で OperationCanceledException
                        _gate = null;
                        continue;
                    }
                    _cur = _chunks.Dequeue();
                    _pos = 0;
                }
                if (_pos >= _cur.Length) { _cur = null; continue; }
                int n = Math.Min(buffer.Length, _cur.Length - _pos);
                _cur.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
        }

        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private StubHandler Use(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var h = new StubHandler(respond);
        _clients.Add(new HttpClient(h));
        OllamaClient.SetHttp(_clients[^1]);
        return h;
    }

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _clients.Clear();
    }

    private static JsonObject Cfg() => new()
    {
        ["base_url"] = "http://test.ollama.local",
        ["model"] = "test-model",
        ["ollama_num_ctx"] = 4096,
        ["ollama_num_predict"] = 1024,
        ["ollama_temperature"] = 0.5,
        ["ollama_keep_alive_min"] = 30,
        ["ollama_think"] = "auto",
    };

    private static JsonArray Msgs(params (string Role, string Content)[] msgs)
    {
        var arr = new JsonArray();
        foreach (var (role, content) in msgs)
            arr.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return arr;
    }

    private static HttpResponseMessage StreamOk(Stream s) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(s) };

    // ---- NDJSONの分割受信 ----

    [Fact]
    public async Task StreamChat_1行が2回に分かれても本文を組み立てる()
    {
        var line1 = """{"model":"test-model","message":{"role":"assistant","content":"訳"},"done":false}""";
        var line2 = """{"message":{"content":"されて","thinking":"考え"},"done":false}""";
        var line3 = """{"message":{},"done":true,"done_reason":"stop","eval_count":4,"eval_duration":2000000000,"prompt_eval_count":7,"prompt_eval_duration":100000000}""";

        var stream = new ChunkyStream();
        stream.Add("これは壊れた行\n");                 // 壊れた行は読み飛ばす
        stream.Add(line1 + "\n" + line2[..30]);          // line2の途中で切れる
        stream.Add(line2[30..] + "\n" + line3[..20]);    // line3の途中でも切る
        stream.Add(line3[20..] + "\n");

        var stub = Use(_ => StreamOk(stream));
        var deltas = new List<string>();
        var thinks = new List<string>();
        var stats = new List<Dictionary<string, object?>>();

        var text = await OllamaClient.StreamChatAsync(
            Cfg(), Msgs(("user", "hello")),
            deltas.Add, thinks.Add, numPredict: 99, statsSink: stats);

        Assert.Equal("訳されて", text);
        // on_deltaは毎回「これまでの全文」（差分ではない）
        Assert.Equal(new[] { "訳", "訳されて" }, deltas);
        // 思考は本文と別（返り値にも本文にも混ぜない）
        Assert.Equal(new[] { "考え" }, thinks);

        // 送信内容: think=autoは送らない / keep_aliveは"30m" / num_predictは指定分
        var payload = JsonNode.Parse(stub.Requests[0].Body)!;
        Assert.Equal("http://test.ollama.local/api/chat", stub.Requests[0].Uri);
        Assert.True(payload["stream"]!.GetValue<bool>());
        Assert.Null(payload["think"]);
        Assert.Equal("30m", payload["keep_alive"]!.GetValue<string>());
        Assert.Equal(4096, payload["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(99, payload["options"]!["num_predict"]!.GetValue<int>());

        // 実行記録は done 行で1件
        Assert.Single(stats);
        Assert.Equal("stop", stats[0]["done_reason"]);
        Assert.Equal(4L, stats[0]["eval_count"]);
        Assert.Equal(2.0, stats[0]["tokens_per_sec"]);
        Assert.Equal(2, stats[0]["thinking_chars"]);
        Assert.NotNull(stats[0]["first_token_sec"]);
    }

    [Fact]
    public async Task StreamChat_deltaの合間隔を空けられる()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"message":{"content":"あ"},"done":false}""" + "\n");
        stream.Add("""{"message":{"content":"い"},"done":false}""" + "\n");
        stream.Add("""{"message":{"content":"う"},"done":true,"done_reason":"stop"}""" + "\n");
        Use(_ => StreamOk(stream));

        var deltas = new List<string>();
        var text = await OllamaClient.StreamChatAsync(
            Cfg(), Msgs(("user", "x")), deltas.Add, null, deltaMinIntervalSec: 5.0);

        Assert.Equal("あいう", text);
        Assert.Single(deltas);   // 2回目以降は5秒の内所以外へ出さない
    }

    // ---- 中断 ----

    [Fact]
    public async Task StreamChat_やめるは失败と違うきれいな例外だけ返す()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"message":{"content":"途中"},"done":false}""" + "\n");
        stream.Block();          // この先は本物が黙り込んでいる状態
        Use(_ => StreamOk(stream));

        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), _ => got.TrySetResult(true), null);

        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        OllamaClient.AbortActive();   // バーの「やめる」

        // 呼び出し側に漏れるのは「やめた」合図だけ（接続エラーやタイムアウトではない）
        var ex = await Assert.ThrowsAsync<OllamaCancelledException>(() => task);
        Assert.Equal("ぶん投げをやめました", ex.Message);
    }

    [Fact]
    public async Task StreamChat_CancellationTokenでも同じきれいな例外()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"message":{"content":"途中"},"done":false}""" + "\n");
        stream.Block();
        Use(_ => StreamOk(stream));

        using var cts = new CancellationTokenSource();
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")),
            _ => got.TrySetResult(true), null, cts.Token);

        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAsync<OllamaCancelledException>(() => task);
    }

    [Fact]
    public async Task StreamChatやめる済みの合図なら通信すらしない()
    {
        var stub = Use(_ => StreamOk(new ChunkyStream()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OllamaCancelledException>(
            () => OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), null, null, cts.Token));
        Assert.Empty(stub.Requests);
    }

    // ---- 失败の種類 ----

    [Fact]
    public async Task StreamChat_error行はそのまま失败にする()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"error":"no such model or adapter"}""");
        Use(_ => StreamOk(stream));

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), null, null));
        Assert.Equal("no such model or adapter", ex.Message);
    }

    [Fact]
    public async Task StreamChat_上限で打ち切られても例外にせず本文とdone_reasonを返す()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"message":{"content":"いっぱい"},"done":true,"done_reason":"length"}""");
        Use(_ => StreamOk(stream));

        var stats = new List<Dictionary<string, object?>>();
        var res = await OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), null, null, statsSink: stats);
        Assert.Equal("いっぱい", res);
        Assert.Single(stats);
        Assert.Equal("length", stats[0]["done_reason"]);
    }

    [Fact]
    public async Task StreamChat_接続タイムアウトは日本語の案内を返す()
    {
        Use(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing."));

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), null, null));
        Assert.DoesNotContain("OllamaCancelledException", ex.GetType().Name);
        Assert.Contains("AIサーバーに接続できません（時間切れ", ex.Message);
    }

    [Fact]
    public async Task StreamChat_doneを見ずに終わったら接続切れを言う()
    {
        var stream = new ChunkyStream();
        stream.Add("""{"message":{"content":"途中で"},"done":false}""" + "\n");
        Use(_ => StreamOk(stream));   // EOFへ（done行なし）

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => OllamaClient.StreamChatAsync(Cfg(), Msgs(("user", "x")), null, null));
        Assert.Contains("生成が途中で切れました", ex.Message);
    }

    [Fact]
    public async Task StreamChat_思考を切り替えられないモデルは日本語で案内()
    {
        var cfg = Cfg();
        cfg["ollama_think"] = "on";
        var stub = Use(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"this model does not support Think"}"""),
        });

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => OllamaClient.StreamChatAsync(cfg, Msgs(("user", "x")), null, null));
        Assert.Contains("このモデルは思考の切り替えに対応していません", ex.Message);
        Assert.Contains("does not support Think", ex.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task J113_onDeltaが間隔より長くかかっても呼び終わりから間隔を空けて間引く()
    {
        var stream = new ChunkyStream();
        for (int i = 0; i < 20; i++)
            stream.Add($"{{\"message\":{{\"content\":\"x\"}},\"done\":false}}\n");
        stream.Add("""{"message":{"content":""},"done":true,"done_reason":"stop"}""" + "\n");
        Use(_ => StreamOk(stream));

        int calls = 0;
        var res = await OllamaClient.StreamChatAsync(
            Cfg(), Msgs(("user", "x")),
            onDelta: _ => { calls++; Thread.Sleep(50); },   // 間隔(30ms)より長いコールバック
            onThinking: null,
            deltaMinIntervalSec: 0.03);
        Assert.Equal(new string('x', 20), res);
        // 呼び終わりから 30ms 空けるので、すぐ続けて届く残り19トークンは1回で間引かれる（20回全部にはならない）
        Assert.True(calls <= 2, $"expected throttled calls <= 2, got {calls}");
    }

    // ---- 生成設定の手入れ（gen_settingと同じ） ----

    [Theory]
    [InlineData("ollama_num_ctx", 100, 2048)]          // 下限へ
    [InlineData("ollama_num_ctx", 999999, 262144)]     // 上限へ
    [InlineData("ollama_num_ctx", 4096, 4096)]         // そのまま
    [InlineData("ollama_temperature", -3.0, 0.0)]
    [InlineData("ollama_temperature", 9.0, 2.0)]
    [InlineData("ollama_temperature", 0.5, 0.5)]
    public void GenSettingは範囲外を端へ寄せる(string key, double given, double want)
    {
        var cfg = new JsonObject { [key] = given };
        Assert.Equal(want, OllamaClient.GenSetting(cfg, key));
    }

    [Fact]
    public void GenSettingは読めない値を既定値へ()
    {
        var cfg = new JsonObject { ["ollama_num_ctx"] = "広い" };
        Assert.Equal(8192, OllamaClient.GenSetting(cfg, "ollama_num_ctx"));
        Assert.Equal(4096, OllamaClient.GenSetting(new JsonObject(), "ollama_num_predict"));
        // 小数の整数項目は四捨五入（Pythonのroundと同じ）
        Assert.Equal(2048, OllamaClient.GenSetting(new JsonObject { ["ollama_num_ctx"] = 2048.4 }, "ollama_num_ctx"));
        Assert.Equal(2049, OllamaClient.GenSetting(new JsonObject { ["ollama_num_ctx"] = 2048.6 }, "ollama_num_ctx"));
    }

    [Fact]
    public void keep_aliveは負だと数値でずっと載せる()
    {
        Assert.Equal("30m", OllamaClient.KeepAliveNode(new JsonObject { ["ollama_keep_alive_min"] = 30 }).GetValue<string>());
        Assert.Equal("0m", OllamaClient.KeepAliveNode(new JsonObject { ["ollama_keep_alive_min"] = 0 }).GetValue<string>());
        Assert.Equal(-1L, OllamaClient.KeepAliveNode(new JsonObject { ["ollama_keep_alive_min"] = -1 }).GetValue<long>());
        Assert.Equal(-1L, OllamaClient.KeepAliveNode(new JsonObject { ["ollama_keep_alive_min"] = -999 }).GetValue<long>());
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("変", null)]
    public void thinkはモデル任せなら送らない(string v, bool? want)
    {
        var cfg = new JsonObject { ["ollama_think"] = v };
        Assert.Equal(want, OllamaClient.ThinkFlag(cfg));
    }

    // ---- vision 判定（/api/show） ----

    [Theory]
    [InlineData("""{"capabilities":["completion","tools","Vision"]}""", true)]
    [InlineData("""{"capabilities":["completion"]}""", false)]
    [InlineData("""{"capabilities":[]}""", false)]
    [InlineData("""{"model_info":{}}""", null)]
    public void ParseVisionはcapabilitiesだけ見る(string json, bool? want)
    {
        Assert.Equal(want, OllamaClient.ParseVision(JsonNode.Parse(json)));
    }

    [Fact]
    public async Task ModelHasVisionはshowが壊れても失敗にしない()
    {
        Use(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        Assert.Null(await OllamaClient.ModelHasVisionAsync(Cfg(), "test-model"));
    }

    // ---- 温め・降ろし（/api/generate） ----

    [Fact]
    public async Task WarmModelはモデルを変えたら前を先に降ろす()
    {
        var stub = Use(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"response":"","done":true}"""),
        });

        await OllamaClient.WarmModelAsync(Cfg(), "model-b", "model-a");

        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Equal("http://test.ollama.local/api/generate", r.Uri));
        var first = JsonNode.Parse(stub.Requests[0].Body)!;
        var second = JsonNode.Parse(stub.Requests[1].Body)!;
        Assert.Equal("model-a", first["model"]!.GetValue<string>());
        Assert.Equal(0, first["keep_alive"]!.GetValue<int>());     // 降ろすは即時
        Assert.Equal("model-b", second["model"]!.GetValue<string>());
        Assert.Equal("30m", second["keep_alive"]!.GetValue<string>());
        Assert.Equal(1, second["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal(4096, second["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("", second["prompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task WarmModelは同じモデルなら降ろし直ししない()
    {
        var stub = Use(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });
        await OllamaClient.WarmModelAsync(Cfg(), "model-a", "model-a");
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task WarmModelは空名で失敗を返す()
    {
        Use(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"error":"no such model"}""") });
        var ex = await Assert.ThrowsAsync<OllamaException>(() => OllamaClient.WarmModelAsync(Cfg(), "gone"));
        Assert.Equal("no such model", ex.Message);
    }

    [Fact]
    public async Task UnloadModelは空名だと何も送らない()
    {
        var stub = Use(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await OllamaClient.UnloadModelAsync(Cfg(), "   ");
        await OllamaClient.UnloadModelAsync(Cfg(), "");
        Assert.Empty(stub.Requests);
    }
}

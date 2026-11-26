using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bunnagetaro5.Services;

/// <summary>KaTeX で描いた式の画像。W/H/Base は DIP（Base は上端から文字の基準線まで）。Scale は撮った倍率（1 DIP あたりの画素数。下げて撮った時は頼んだ倍率より小さい）</summary>
public sealed record MathImage(BitmapSource Bmp, double W, double H, double Base, double Scale = 0);

/// <summary>計測用: 直近の1式を撮った時の数字（診断。画面には出ない）</summary>
internal sealed record MathDiag(double X, double Y, double W, double H, double WinW, double WinH,
    double InnerW, double InnerH, int FramePxW, int FramePxH, int CropW, int CropH, int WantW, int WantH, double Rs);

/// <summary>
/// 数式の描画。見えない WebView2 を1つだけ持ち、
/// KaTeX 0.16.11（trust:false・ネットからは読まない）で式を描いて、式の範囲だけ切り出した画像にする。
/// WebView2 ランタイムが無い／立ち上がらない時は Active=false になり、解答欄は WpfMath を使う。
/// 同じ式・色・大きさ・倍率の画像はキャッシュする。UI スレッドから使う（await は UI を待たせない）。
/// </summary>
public static class MathRenderer
{
    public const string KatexVersion = "0.16.11";
    private const string Host = "katex.local";

    private static Task? _init;
    private static bool _ready;
    private static bool _failed;
    private static Window? _win;
    private static WebView2? _wv;
    /// <summary>1式ずつ描く順番札。Shutdown で新しくする（描きかけの式はブラウザが無くなると終わらず、古い札を返さないまま
    /// 放り出されるため。作り直さないと、次に立ち上げた後の式が順番待ちのまま止まる）</summary>
    private static SemaphoreSlim Gate = new(1, 1);

    private static readonly Dictionary<string, MathImage?> Cache = new();
    private static readonly Queue<string> CacheOrder = new();
    private static readonly Dictionary<string, Task<MathImage?>> Inflight = new();
    /// <summary>描く順番待ちの式に興味を持っている呼び出しの取り消し札。全員が取り消したら、順番が来ても描かない</summary>
    private static readonly Dictionary<string, List<CancellationToken>> Interest = new();
    /// <summary>テスト用: 取り消されて描かずに飛ばした式の数</summary>
    internal static int SkippedCancelled { get; private set; }
    private const int CacheMax = 600;

    /// <summary>起動時に1回だけ判定。ランタイムがあり、まだ壊れていない間 true（Start 前は false）</summary>
    public static bool Active { get; private set; }

    /// <summary>画像が実際に作れる状態（立ち上げ済み）</summary>
    public static bool Ready => _ready && !_failed;

    /// <summary>env 行に出す値</summary>
    public static string Mode => Active ? "katex" : "wpfmath";

    /// <summary>立ち上げに掛かった時間（ms）。未完は -1</summary>
    public static long StartupMs { get; private set; } = -1;

    /// <summary>直近の1式の「JS 描画＋画像化」時間（ms）。診断用</summary>
    public static double LastRenderMs { get; private set; }

    /// <summary>直近の1式の計測値（診断・テスト用）</summary>
    internal static MathDiag? LastDiag { get; private set; }

    public static bool RuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    /// <summary>アプリ起動時に UI スレッドで呼ぶ。ランタイムが無ければ何もせず WpfMath のまま</summary>
    public static Task Start(Paths paths)
    {
        if (_init is not null) return _init;
        if (!RuntimeAvailable())
        {
            Active = false;
            AppLog.Info("env math_renderer=wpfmath (WebView2 runtime not found)");
            return _init = Task.CompletedTask;
        }
        Active = true;
        _shutdown = false;   // Shutdown 後の再 Start で InitAsync がすぐ終わらないように
        return _init = InitAsync(paths);
    }

    /// <summary>終了時（本番）: WebView2 と見えない窓を UI スレッドで解放する。
    /// これを呼ばないと見えない窓が「最後の窓」として残りプロセスが終わらない</summary>
    public static void Shutdown() => Shutdown(1000);

    /// <summary>waitMs: Dispose の後にブラウザのプロセスが終わるのを待つ最長（ms。0 で待たない）。
    /// 本番は UI を長く止めないよう 1秒、テストは 5秒。終わったら true</summary>
    public static bool Shutdown(int waitMs)
    {
        _shutdown = true;
        int pid = 0;
        try { pid = _wv?.CoreWebView2?.BrowserProcessId is { } id ? (int)id : 0; } catch { }
        try { _wv?.Dispose(); } catch { }   // 作った UI スレッドで解放する（終了時に別スレッドで落ちない）
        try { _win?.Close(); } catch { }
        _win = null; _wv = null; _init = null; _ready = false; _failed = false; Active = false;
        StartupMs = -1;
        Cache.Clear(); CacheOrder.Clear(); Inflight.Clear(); TooBig.Clear();
        Gate = new SemaphoreSlim(1, 1);
        if (pid == 0 || waitMs <= 0) return true;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.WaitForExit(waitMs);
        }
        catch { return true; }   // もう居ない
    }

    /// <summary>Shutdown 済みか（立ち上げ途中で閉じられた時、後から窓を作らない）</summary>
    internal static bool IsShutdown => _shutdown;
    private static bool _shutdown;

    /// <summary>見えない窓の HWND（無ければ 0）。テスト・診断用</summary>
    internal static IntPtr WindowHandle { get { try { return _win is null ? IntPtr.Zero : new WindowInteropHelper(_win).Handle; } catch { return IntPtr.Zero; } } }

    /// <summary>テスト用: 窓ごと捨てて最初からやり直せるようにする。
    /// 前の窓を作ったスレッドが終わっていると Dispose が効かず、PC が忙しい時はブラウザのプロセスが5秒たっても残る。
    /// その時はブラウザを止めてから戻る</summary>
    internal static void ResetForTests()
    {
        int pid = 0;
        try { pid = _wv?.CoreWebView2?.BrowserProcessId is { } id ? (int)id : 0; } catch { }
        if (!Shutdown(5000) && pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(10000);
            }
            catch { }   // もう居ない
        }
        _shutdown = false;
    }

    private static async Task InitAsync(Paths paths)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string dir = ExtractAssets(paths.Root);
            var win = new Window
            {
                Left = -20000, Top = -20000, Width = 1400, Height = 500,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                ResizeMode = ResizeMode.NoResize, Focusable = false, Title = "Bunnagetaro5 math",
            };
            win.SourceInitialized += (_, _) =>
            {
                try
                {
                    var h = new WindowInteropHelper(win).Handle;
                    long ex = GetWindowLongPtr(h, -20).ToInt64();
                    SetWindowLongPtr(h, -20, new IntPtr(ex | 0x80 | 0x08000000)); // TOOLWINDOW | NOACTIVATE
                    HwndSource.FromHwnd(h)?.AddHook(MinMaxHook);
                }
                catch { }
            };
            var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            win.Content = wv;
            if (_shutdown) { try { wv.Dispose(); } catch { } return; }
            win.Show();
            _win = win;
            _wv = wv;
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(paths.Root, "WebView2"));
            await wv.EnsureCoreWebView2Async(env);
            if (_shutdown) { try { wv.Dispose(); } catch { } try { win.Close(); } catch { } return; }
            var core = wv.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, dir, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Host != Host) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.ProcessFailed += (_, e) =>
            {
                _failed = true; Active = false;
                AppLog.Warning("math: WebView2 process failed: " + e.ProcessFailedKind + " -> WpfMath");
            };
            var tcs = new TaskCompletionSource<bool>();
            string navErr = "";
            core.NavigationCompleted += (_, e) =>
            {
                // 失敗の理由をログに残す
                if (!e.IsSuccess) navErr = $"WebErrorStatus={e.WebErrorStatus} HttpStatusCode={e.HttpStatusCode}";
                tcs.TrySetResult(e.IsSuccess);
            };
            core.Navigate($"https://{Host}/index.html");
            if (!await tcs.Task) throw new InvalidOperationException("index.html の読み込みに失敗 " + navErr + " dir=" + dir);
            // Web フォントを読み終えるまで待つ（読み前に測ると式の幅がずれる）
            for (int i = 0; i < 100; i++)
            {
                if (await core.ExecuteScriptAsync("window.fontsOk === true") == "true") break;
                await Task.Delay(50);
            }
            _ready = true;
            StartupMs = sw.ElapsedMilliseconds;
            AppLog.Info($"env math_renderer=katex katex={KatexVersion} webview2={CoreWebView2Environment.GetAvailableBrowserVersionString()} startup_ms={StartupMs}");
        }
        catch (Exception ex) when (_shutdown)
        {
            // 立ち上げ中に終了した: 作りかけは Shutdown が解放済み。ログも出さない
            _ = ex;
        }
        catch (Exception ex)
        {
            _failed = true; Active = false;
            AppLog.Error("math: WebView2 の立ち上げに失敗。WpfMath を使う", ex);
            AppLog.Info("env math_renderer=wpfmath (WebView2 init failed)");
        }
    }

    /// <summary>埋め込んだ KaTeX 一式を %APPDATA%\Bunnagetaro5\katex-版 へ展開（同じ大きさなら再利用）</summary>
    internal static string ExtractAssets(string root)
    {
        string dir = Path.Combine(root, "katex-" + KatexVersion);
        var asm = typeof(MathRenderer).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("katex/", StringComparison.Ordinal)) continue;
            string rel = name.Substring("katex/".Length).Replace('\\', '/');
            string dest = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            using var s = asm.GetManifestResourceStream(name)!;
            if (File.Exists(dest) && new FileInfo(dest).Length == s.Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var f = File.Create(dest);
            s.CopyTo(f);
        }
        return dir;
    }

    private static string Key(string tex, bool display, Color fg, Color? bg, double fontDip, double scale)
        => string.Create(CultureInfo.InvariantCulture,
            $"{(display ? "D" : "I")}|{fg}|{(bg is { } b ? b.ToString() : "-")}|{fontDip:F2}|{scale:F2}|{tex}");

    /// <summary>キャッシュにあれば返す。失敗済みの式は true で img=null</summary>
    public static bool TryGetCached(string tex, bool display, Color fg, Color? bg, double fontDip, double scale, out MathImage? img)
        => Cache.TryGetValue(Key(tex, display, fg, bg, fontDip, scale), out img);

    /// <summary>式を画像にする。使えない時・式が KaTeX で通らない時・窓に収まらず欠けた時は null（理由は AppLog に1行）。
    /// bg が null なら透明の地（解答欄の地がそのまま見える）。fontDip は本文の字の大きさ（DIP）、
    /// scale は 1 DIP あたりの画素数（表示は画面の倍率×2、画像でコピーは 3）。
    /// minScale が scale より小さい時は、見えない窓に収まらない式を minScale まで倍率を下げて撮り直す
    /// （画像の <see cref="MathImage.Scale"/> が実際に使った倍率）。0 や scale 以上なら下げない。
    /// 欠けた結果はキャッシュに入れない（入れると同じ式が欠けたまま・原文字のままに固定される）</summary>
    public static Task<MathImage?> RenderAsync(string tex, bool display, Color fg, Color? bg, double fontDip, double scale, double minScale = 0, CancellationToken ct = default)
    {
        string key = Key(tex, display, fg, bg, fontDip, scale);
        if (Cache.TryGetValue(key, out var hit)) return Task.FromResult(hit);
        if (TooBig.TryGetValue(key, out long until))
        {
            if (Environment.TickCount64 < until) return Task.FromResult<MathImage?>(null);   // 欠けた式は少しの間やり直さない（ストリーム中の再描画で毎回撮らない）
            TooBig.Remove(key);
        }
        if (Inflight.TryGetValue(key, out var t))
        {
            if (Interest.TryGetValue(key, out var l)) l.Add(ct);
            return t;
        }
        if (ct.IsCancellationRequested) return Task.FromResult<MathImage?>(null);   // 頼んだ側がもう要らない
        Interest[key] = new List<CancellationToken> { ct };
        t = RenderCoreAsync(key, tex, display, fg, bg, fontDip, scale, minScale);
        if (!t.IsCompleted) Inflight[key] = t;
        return t;
    }

    /// <summary>窓に収まらず欠けた式の、やり直しを控える時刻（Environment.TickCount64）。キャッシュではない</summary>
    private static readonly Dictionary<string, long> TooBig = new();
    private const long TooBigMs = 20_000;

    /// <summary>見えない窓の最大の大きさ（画素。WebView2 の撮れる上限 16384 の内側）</summary>
    private const double MaxWinPx = 16000;

    private enum Capture { Ok, Short, Failed, Unstable }

    private static async Task<MathImage?> RenderCoreAsync(string key, string tex, bool display, Color fg, Color? bg, double fontDip, double scale, double minScale)
    {
        try
        {
            if (_init is not null) await _init;
            if (!Ready || _wv?.CoreWebView2 is null || _win is null) return null;   // 失敗は覚えない
            var gate = Gate;   // 待っている間に Shutdown で札が替わっても、借りた札に返す
            await gate.WaitAsync();
            try
            {
                if (AllCancelled(key)) { SkippedCancelled++; return null; }   // 順番が来る前に全員が取り消した（キャッシュにも入れない）
                var sw = Stopwatch.StartNew();
                var (st, img, rect) = await RenderOnceAsync(tex, display, fg, bg, fontDip, scale);
                if (st == Capture.Unstable) return null;   // 倍率の切り替え中で撮れなかった（式の性質ではないので覚えない）
                if (st == Capture.Failed)
                {
                    Remember(key, null);   // KaTeX が通さない式（これは式の性質なのでキャッシュしてよい）
                    return null;
                }
                if (st == Capture.Short)
                {
                    string head = tex.Replace('\n', ' ');
                    if (head.Length > 60) head = head[..60];
                    AppLog.Warning(string.Create(CultureInfo.InvariantCulture,
                        $"math: 式が見えない窓に収まらず欠けた [{head}] 必要 {rect.W:F0}x{rect.H:F0} 窓 {rect.InnerW:F0}x{rect.InnerH:F0} 倍率 {scale:F2}"));
                    if (minScale > 0 && minScale < scale && rect.W > 0 && rect.H > 0)
                    {
                        // 届く倍率まで下げて1回だけ撮り直す（式の大きさは倍率に比例する）
                        double fit = Math.Min((rect.InnerW - 4) / (rect.X + rect.W), (rect.InnerH - 4) / (rect.Y + rect.H));
                        double s2 = Math.Floor(scale * fit * 0.97 * 100) / 100;
                        if (s2 >= minScale)
                        {
                            var (st2, img2, _) = await RenderOnceAsync(tex, display, fg, bg, fontDip, s2);
                            if (st2 == Capture.Ok && img2 is not null)
                            {
                                AppLog.Info(string.Create(CultureInfo.InvariantCulture, $"math: 倍率を {scale:F2} から {s2:F2} に下げて撮った"));
                                Remember(Key(tex, display, fg, bg, fontDip, s2), img2);
                                Remember(key, img2);   // 欠けてはいない（解像度が低いだけ）ので、元の倍率の問い合わせにも返す
                                LastRenderMs = sw.Elapsed.TotalMilliseconds;
                                return img2;
                            }
                        }
                    }
                    TooBig[key] = Environment.TickCount64 + TooBigMs;   // キャッシュには入れない
                    return null;
                }
                Remember(key, img);
                LastRenderMs = sw.Elapsed.TotalMilliseconds;
                return img;
            }
            finally { gate.Release(); }
        }
        catch (Exception ex)
        {
            AppLog.Warning("math: 描画に失敗: " + ex.Message);
            return null;
        }
        finally { Inflight.Remove(key); Interest.Remove(key); }
    }

    private static bool AllCancelled(string key)
    {
        if (!Interest.TryGetValue(key, out var l)) return false;
        foreach (var c in l) if (!c.IsCancellationRequested) return false;
        return true;
    }

    private readonly record struct Rect4(double X, double Y, double W, double H, double InnerW, double InnerH);

    /// <summary>見えない窓で式を1回描いて撮る。窓が足りなければ広げ、本当に届いたのを確かめてから撮る。
    /// 撮れた画像が式より小さければ1回だけ撮り直し、それでも足りなければ Short を返す（黙って切らない）</summary>
    /// <remarks>倍率（DPI）の切り替え中は、見えない窓の WPF の倍率と WebView2 の倍率（devicePixelRatio）が一瞬ずれる
    /// （ノート実測: 150%→250% の直後に WPF 1.5・WebView2 2.5）。字の大きさと切り抜きは WebView2 自身の
    /// devicePixelRatio で出し、撮る前と後で倍率が食い違ったら（切り替えの途中）少し待って撮り直す。
    /// それでも揃わなければ Unstable（キャッシュに入れない。間違った大きさ・欠けた画像を覚えない）</remarks>
    private static async Task<(Capture St, MathImage? Img, Rect4 R)> RenderOnceAsync(string tex, bool display, Color fg, Color? bg, double fontDip, double scale)
    {
        for (int round = 0; round < 4; round++)
        {
            if (round > 0) await Task.Delay(150);   // 倍率の切り替えが落ち着くのを待つ
            var res = await RenderTryAsync(tex, display, fg, bg, fontDip, scale);
            if (res.St != Capture.Unstable) return res;
        }
        string head = tex.Replace('\n', ' ');
        if (head.Length > 60) head = head[..60];
        AppLog.Warning("math: 画面の倍率が切り替わっている途中で、式を正しい大きさで撮れなかった [" + head + "]");
        return (Capture.Unstable, null, default);
    }

    /// <summary>WebView2 の中の (innerWidth, innerHeight, devicePixelRatio)。取れなければ null</summary>
    private static async Task<(double W, double H, double Dpr)?> PageMetricsAsync(CoreWebView2 core)
    {
        try
        {
            string s = await core.ExecuteScriptAsync("window.innerWidth+','+window.innerHeight+','+window.devicePixelRatio");
            var p = s.Trim('"').Split(',');
            return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture),
                    double.Parse(p[2], CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    private static async Task<(Capture St, MathImage? Img, Rect4 R)> RenderTryAsync(string tex, bool display, Color fg, Color? bg, double fontDip, double scale)
    {
        var wv = _wv!; var core = wv.CoreWebView2; var win = _win!;
        double dpi = Math.Max(0.5, VisualTreeHelper.GetDpi(win).DpiScaleX);
        if (await PageMetricsAsync(core) is not { } m0 || m0.Dpr <= 0) return (Capture.Unstable, null, default);
        double dpr = m0.Dpr;                 // CSS 1px あたりの画素数（WebView2 が実際に描く倍率）
        double cssToDip = dpr / dpi;         // 窓の大きさ（WPF の DIP）へ直す比。ふだんは 1
        string fgCss = $"rgb({fg.R},{fg.G},{fg.B})";
        string bgCss = bg is { } b ? $"rgb({b.R},{b.G},{b.B})" : "transparent";
        double cssPx = fontDip * scale / dpr;
        string js = string.Create(CultureInfo.InvariantCulture,
            $"renderOne({JsonSerializer.Serialize(tex)},{(display ? "true" : "false")},{JsonSerializer.Serialize(fgCss)},{JsonSerializer.Serialize(bgCss)},{cssPx:R})");
        string raw = await core.ExecuteScriptAsync(js);
        string inner = JsonSerializer.Deserialize<string>(raw) ?? "";
        using var doc = JsonDocument.Parse(inner);
        var root = doc.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            string head = tex.Replace('\n', ' ');
            if (head.Length > 60) head = head[..60];
            AppLog.Info("math: KaTeX で描けない式 [" + head + "]: " + root.GetProperty("err").GetString());
            return (Capture.Failed, null, default);
        }
        double x = root.GetProperty("x").GetDouble(), y = root.GetProperty("y").GetDouble();
        double w = root.GetProperty("w").GetDouble(), h = root.GetProperty("h").GetDouble();
        double baseY = root.GetProperty("base").GetDouble();
        double maxCss = MaxWinPx / dpr;
        double needW = Math.Min(maxCss, Math.Ceiling(x + w) + 4), needH = Math.Min(maxCss, Math.Ceiling(y + h) + 4);   // CSS px
        var r = new Rect4(x, y, w, h, 0, 0);
        // renderOne が返した窓の中の大きさ（古い render.js なら無い）
        double iw0 = root.TryGetProperty("iw", out var jiw) ? jiw.GetDouble() : -1;
        double ih0 = root.TryGetProperty("ih", out var jih) ? jih.GetDouble() : -1;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool grew = false;
            if (needW * cssToDip > win.Width) { win.Width = needW * cssToDip; grew = true; }
            if (needH * cssToDip > win.Height) { win.Height = needH * cssToDip; grew = true; }
            // 広げた大きさが WebView2 の中（innerWidth/innerHeight）に届くまで待つ（最大1秒）。
            // 1回目で窓を広げず、中の大きさもう足りていれば待たない（待っても1回目の読みで抜けるので同じ）
            double iw, ih;
            if (attempt == 0 && !grew && iw0 >= needW - 0.5 && ih0 >= needH - 0.5) (iw, ih) = (iw0, ih0);
            else (iw, ih) = await WaitViewportAsync(core, needW, needH, 1000);
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            // 撮った後も倍率が同じで、撮れた画像の画素数が「中の幅 × 倍率」と合っていること（切り替えの途中でない）
            if (await PageMetricsAsync(core) is not { } m1 || Math.Abs(m1.Dpr - dpr) > 0.001 ||
                Math.Abs(frame.PixelWidth - m1.W * m1.Dpr) > m1.Dpr + 1)
                return (Capture.Unstable, null, default);
            iw = m1.W; ih = m1.H;
            double rs = dpr;   // CSS px → 画素
            int px = Math.Max(0, (int)Math.Floor(x * rs)), py = Math.Max(0, (int)Math.Floor(y * rs));
            int wantW = (int)Math.Ceiling(w * rs), wantH = (int)Math.Ceiling(h * rs);
            int pw = Math.Min(frame.PixelWidth - px, wantW);
            int ph = Math.Min(frame.PixelHeight - py, wantH);
            r = new Rect4(x, y, w, h, iw, ih);
            LastDiag = new MathDiag(x, y, w, h, wv.ActualWidth, wv.ActualHeight, iw, ih, frame.PixelWidth, frame.PixelHeight, pw, ph, wantW, wantH, rs);
            bool shortCap = pw < 1 || ph < 1 || wantW - pw > 2 || wantH - ph > 2;   // 端数の丸め（1〜2画素）は欠けとみなさない
            if (shortCap) continue;   // 1回だけ撮り直す
            var crop = new CroppedBitmap(frame, new Int32Rect(px, py, pw, ph));
            crop.Freeze();
            return (Capture.Ok, new MathImage(crop, pw / scale, ph / scale, Math.Clamp(baseY * rs / scale, 0, ph / scale), scale), r);
        }
        return (Capture.Short, null, r);
    }

    /// <summary>window.innerWidth/innerHeight が必要な大きさに届くまで待つ（届いたら即戻る）。最後に見えた大きさを返す</summary>
    private static async Task<(double W, double H)> WaitViewportAsync(CoreWebView2 core, double needW, double needH, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        double iw = 0, ih = 0;
        while (true)
        {
            try
            {
                string s = await core.ExecuteScriptAsync("window.innerWidth+','+window.innerHeight");
                var parts = s.Trim('"').Split(',');
                iw = double.Parse(parts[0], CultureInfo.InvariantCulture);
                ih = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            catch { }
            if (iw >= needW - 0.5 && ih >= needH - 0.5) break;
            if (sw.ElapsedMilliseconds >= maxMs) break;
            await Task.Delay(15);
        }
        return (iw, ih);
    }

    private static void Remember(string key, MathImage? img)
    {
        if (!Cache.ContainsKey(key)) CacheOrder.Enqueue(key);
        Cache[key] = img;
        while (CacheOrder.Count > CacheMax) Cache.Remove(CacheOrder.Dequeue());
    }

    /// <summary>テスト用: 窓の最大の大きさ（画素）をわざと小さくして、収まらない時の動きを確かめる（0 で無効）</summary>
    internal static int MaxTrackOverridePx { get; set; }

    /// <summary>Windows は枠なし窓の大きさをモニタ前後で頭打ちにする（ptMaxTrackSize）。式の大きさがそれを超えると欠けるので、
    /// この窓だけ上限を広げる</summary>
    private static IntPtr MinMaxHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024)   // WM_GETMINMAXINFO
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            int v = MaxTrackOverridePx > 0 ? MaxTrackOverridePx : (int)MaxWinPx;
            mmi.ptMaxTrackSize = new POINT { X = v, Y = v };
            if (mmi.ptMaxSize.X < v) mmi.ptMaxSize = new POINT { X = v, Y = v };
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return IntPtr.Zero;   // handled にはしない（WPF の既定の処理も通す）
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}

using System.Text.Json.Nodes;
using System.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Session;

/// <summary>モデル一覧の表示先（SettingsPage が実装する）</summary>
internal interface IModelListView
{
    bool RememberModelList(List<string> names);
    void ReloadModelList();
}

/// <summary>
/// 起動時・窓を開いた時・モデルを変えた時の、AIまわりの準備と前提チェック（修正A4）。
/// Python版 translator_window の _startup_models / _warm_ai / _switch_model / _env_probe の移植。
/// どれもUIを止めない（async）。失敗してもぶん投げ自体は止めない。
/// </summary>
internal sealed partial class ThrowController
{
    /// <summary>本番の既定。テストプロジェクトは ModuleInitializer で false にして、
    /// 窓を作っただけで本物の Ollama へ問い合わせ・モデルの積み込みが飛ばないようにする</summary>
    internal static bool EnvChecksDefault = true;

    /// <summary>この窓が裏で Ollama へ問い合わせる（温め・前提チェック・一覧読み・終了時の降ろし）か</summary>
    internal bool EnvChecks = EnvChecksDefault;

    /// <summary>連続で積み直さないための間隔（秒。Python版 WARM_MIN_INTERVAL）</summary>
    internal const double WarmMinIntervalSec = 240.0;

    private bool _modelsReady;                 // 起動時に一覧を読めたか
    private string _activeModel = "";          // いま積んだ（積んだつもりの）モデル
    private int _warmupGeneration;             // モデルの切り替えのたびに進む（古い温めを捨てる）
    private readonly SemaphoreSlim _warmupLock = new(1, 1);
    private DateTime _warmTsUtc = DateTime.MinValue;
    private int _probeGen;                     // 前提チェックの世代（遅れて返った古い結果を捨てる）
    private (string Model, string BaseUrl, string NumCtx, string KeepAlive, string ThrowMode, string OcrEngine, string BuiltinSize, string OcrVlm) _prevRun;
    private JsonObject _prevRunCfg = new();

    /// <summary>
    /// コンストラクタの最後で呼ぶ。「ぶん投げに使う設定」の控えを取り、
    /// 窓ができたあと 900/1100/1400ms 後に 温め・一覧読み・前提チェック を順に走らせる
    /// （Python版 App.__init__ の root.after(900/1100/1400, ...)）
    /// </summary>
    internal void InitRunKey()
    {
        _prevRunCfg = _cfg.EffectiveConfig();
        _prevRun = RunKeyOf(_prevRunCfg);
        _activeModel = _prevRun.Model;
    }

    /// <summary>窓が出来た（Loaded）時に呼ぶ: 900/1100/1400ms 後に 温め・一覧読み・前提チェック</summary>
    internal void ScheduleEnvChecks()
    {
        UiTimer.Later(900, () => _ = WarmAiAsync("startup"));
        UiTimer.Later(1100, () => _ = StartupModelsAsync());
        UiTimer.Later(1400, () => _ = EnvProbeAsync());
    }

    /// <summary>モデル一覧の表示先（設定ページ。後から付く）</summary>
    internal IModelListView? ModelList { get; set; }

    /// <summary>ぶん投げに内蔵の OCR を使う設定か（内蔵OCRのみ、または文字起こし＋内蔵OCR。修正J17・J26・J27）</summary>
    private static string? BuiltinModelOf(JsonObject cfg)
    {
        string mode = TranslatePipeline.ThrowModeOf(cfg);
        if (mode == ConfigStore.ThrowModeBuiltinOcr)
            return TranslatePipeline.BuiltinOcrModelOf(cfg);
        if (mode == ConfigStore.ThrowModeOcr && TranslatePipeline.UsesBuiltinOcrForTranscribe(cfg))
            return TranslatePipeline.BuiltinOcrModelOf(cfg);
        return null;
    }

    private static (string, string, string, string, string, string, string, string) RunKeyOf(JsonObject eff) => (
        ConfigStore.NodeToStr(eff["model"]).Trim(),
        ConfigStore.NodeToStr(eff["base_url"]),
        ConfigStore.NodeToStr(eff["ollama_num_ctx"]),
        ConfigStore.NodeToStr(eff["ollama_keep_alive_min"]),
        TranslatePipeline.ThrowModeOf(eff),
        ConfigStore.NodeToStr(eff["ocr_engine"]),
        ConfigStore.NodeToStr(eff["builtin_ocr_size"]),
        ConfigStore.NodeToStr(eff["ocr_vlm_model"]).Trim());

    /// <summary>localhost 以外のAIホストへ勝手に何GBも積まないための門番（_ai_is_local）</summary>
    private static bool AiIsLocal(JsonObject cfg)
    {
        var host = ConfigStore.SafeHost(OllamaClient.BaseOf(cfg));
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    // ---------------- 起動時: 導入済みモデルの一覧（_startup_models） ----------------

    /// <summary>
    /// 導入済みモデルの一覧を読んで model_list と設定ページのモデル欄を更新する。名前一覧の問い合わせ
    /// 1本でモデルは載せない。失敗（まだ Ollama が立っていない等）は黙って app.log へ。
    /// 理由が要る前提は EnvProbeAsync が出すので、ここは同じことを言いにいかない
    /// </summary>
    internal async Task StartupModelsAsync()
    {
        if (!EnvChecks) return;
        string baseUrl = _cfg.Settings.BaseUrl;
        try
        {
            var info = await OllamaClient.GetTagInfoAsync(baseUrl);
            var names = info.Keys.ToList();
            names.Sort(StringComparer.Ordinal);
            if (ModelList?.RememberModelList(names) == true) _cfg.Save();
            ModelList?.ReloadModelList();
            _modelsReady = true;
            AppLog.Info("起動時のモデル一覧 " + names.Count + "件: " + OllamaClient.SizesLine(info, names));
            _setStatus("モデル一覧を更新（" + names.Count + "件）");
        }
        catch (Exception ex)
        {
            AppLog.Info("起動時のモデル一覧読みは失敗（載り終わった折にもう一度読む）: " + ex.Message);
        }
    }

    // ---------------- 起動時・窓を開いた時: モデルを裏で載せる（_warm_ai） ----------------

    /// <summary>
    /// 裏でモデルをVRAMへ載せておく。ぶん投げる時と同じ設定（モデル・num_ctx・keep_alive）で載せる
    /// （num_ctx が違うと Ollama はぶん投げの時に載せ直す）。連続では積み直さない（240秒）。
    /// 失敗してもぶん投げは通常どおり走る（その時に読み込まれるだけ）
    /// </summary>
    internal async Task WarmAiAsync(string why)
    {
        if (!EnvChecks) return;
        var cfg = _cfg.EffectiveConfig();
        // 修正J17: 内蔵の OCR を使う設定なら、それも裏で読み込んでおく（Ollama には積まない）
        if (BuiltinModelOf(cfg) is { } bm) _ = BuiltinOcr.WarmAsync(bm);
        if (!AiIsLocal(cfg)) return;
        if (TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeBuiltinOcr) return;
        string model = ConfigStore.NodeToStr(cfg["model"]).Trim();
        if (model.Length == 0 || BuiltinOcr.IsBuiltin(model)) return;
        var now = DateTime.UtcNow;
        if (Status.WarmRunning || (now - _warmTsUtc).TotalSeconds < WarmMinIntervalSec) return;
        Status.WarmRunning = true;
        _warmTsUtc = now;
        AppLog.Info("AI warm-up start (" + why + "): " + model);
        // 切り替え（SwitchModelAsync）と同じ番号・同じ錠で順番に並ぶ。
        // 待っている間に切り替えが入ったら、古いモデルを後から載せ直さない
        int generation = _warmupGeneration;
        string? err = null;
        try
        {
            await _warmupLock.WaitAsync();
            try
            {
                if (generation == _warmupGeneration)
                {
                    await OllamaClient.WarmModelAsync(cfg, model);
                    _activeModel = model;
                }
                else AppLog.Info("AI warm-up skipped (switched meanwhile): " + model);
            }
            finally { _warmupLock.Release(); }
        }
        catch (Exception ex)
        {
            err = ex.GetType().Name + ": " + ex.Message;
            AppLog.Warning("AI warm-up failed (" + why + "): " + ex.Message);
        }
        finally
        {
            Status.WarmRunning = false;
        }
        // 成功は静か（毎回「準備できた」と言わない）。失敗だけ知らせる
        if (err is not null) _setStatus("モデル起動に失敗（実行時に再読込）");
        // 温めが終わった時点で前提を測り直す（Ollama の起動が間に合わず理由が出ていても、立ち上がれば消える）
        _ = EnvProbeAsync();
        // モデルが載った＝ Ollama は立っている。起動時の一覧読みが空振りだったらこの折にもう一度
        if (!_modelsReady)
        {
            await Task.Delay(300);
            _ = StartupModelsAsync();
        }
    }

    // ---------------- 設定が変わった時: 前のモデルを降ろして新しいのを載せる（_switch_model） ----------------

    /// <summary>
    /// 設定ページ・バーのプリセット切替で設定を書いた後に呼ぶ（_after_run_cfg_change）。
    /// ぶん投げに使う設定（プリセットを重ねた値）のうち、モデル・URL・num_ctx・keep_alive が
    /// 変わっていれば載せ直し、前提も測り直す。変わっていなければ何もしない
    /// </summary>
    internal void AfterRunCfgChange()
    {
        var cfg = _cfg.EffectiveConfig();
        var run = RunKeyOf(cfg);
        if (run == _prevRun) return;
        var prevCfg = _prevRunCfg;
        string prevModel = _activeModel.Length > 0 ? _activeModel : _prevRun.Model;
        _prevRun = run;
        _prevRunCfg = cfg;
        if (!EnvChecks) return;
        _ = SwitchModelAsync(cfg, prevModel, prevCfg);
        _ = EnvProbeAsync();   // モデルを直しさえすれば理由の文言が消える
    }

    /// <summary>
    /// 使うモデル（か載せ方）が変わった。前のモデルを外し、新しい方を載せる
    /// （localhost 以外へは勝手に載せない）。前のモデルは前に載せたサーバーから外す
    /// </summary>
    internal async Task SwitchModelAsync(JsonObject cfg, string prevModel, JsonObject prevCfg)
    {
        bool builtin = TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeBuiltinOcr;
        string model = builtin ? TranslatePipeline.BuiltinOcrModelOf(cfg) : ConfigStore.NodeToStr(cfg["model"]).Trim();
        if (model.Length == 0) return;
        if (BuiltinModelOf(cfg) is { } bm) _ = BuiltinOcr.WarmAsync(bm);   // 修正J17・J27
        int generation = ++_warmupGeneration;
        bool local = AiIsLocal(cfg) && !builtin;
        bool moved = ConfigStore.NodeToStr(prevCfg["base_url"]) != ConfigStore.NodeToStr(cfg["base_url"]);
        if (local) _setStatus(model + " を事前ロード中…");
        string? err = null;
        bool loaded = false;
        await _warmupLock.WaitAsync();
        try
        {
            if (generation != _warmupGeneration) return;
            if (prevModel.Length > 0 && !BuiltinOcr.IsBuiltin(prevModel) && (moved || prevModel != model))
            {
                try { await OllamaClient.UnloadModelAsync(prevCfg, prevModel); }
                catch (Exception ex) { AppLog.Info("previous model unload failed: " + prevModel + ": " + ex.Message); }
            }
            try
            {
                if (local)
                {
                    await OllamaClient.WarmModelAsync(cfg, model);
                    loaded = true;
                }
                _activeModel = model;
            }
            catch (Exception ex) { err = ex.Message; }
        }
        finally { _warmupLock.Release(); }
        if (generation != _warmupGeneration) return;
        _setStatus(err is not null ? "モデルの事前ロードに失敗: " + err
            : builtin ? "モデル: " + model + "（Ollama を使わずにアプリの中で読みます）"
            : loaded ? model + " を事前ロードしました"
            : "モデル: " + model + "（ローカル以外のサーバーなので事前ロードはしません）");
    }

    // ---------------- 前提チェック（_env_probe） ----------------

    /// <summary>
    /// 起動時・設定を変えた時に、前提が揃っているかを測って <c>Status.EnvProblem</c> に入れる。
    /// ぶん投げは止めない。押す前から分かるようにするだけ。
    ///   AIサーバー未起動 … Ollama が立っていないか URL 違い
    ///   モデル未選択     … 設定のモデル欄が空
    ///   モデル未導入     … 設定のモデルが導入済み一覧にない
    ///   vision非対応     … 画像をぶん投げるのに使えないモデルを選んでいる
    /// 揃っていれば空文字で、監視中／待機中がそのまま出る
    /// </summary>
    internal async Task EnvProbeAsync()
    {
        if (!EnvChecks) return;
        var cfg = _cfg.EffectiveConfig();
        int gen = ++_probeGen;
        string problem = "";
        // 修正J17・J27: 内蔵の OCR だけで読む設定は Ollama が無くても動く。モデルのファイルがあるかだけ見る
        if (TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeBuiltinOcr)
        {
            string bm = TranslatePipeline.BuiltinOcrModelOf(cfg);
            if (!BuiltinOcr.ModelsPresent(bm))
            {
                problem = "内蔵OCRのモデルが無い";
                AppLog.Warning("前提チェック: 内蔵の OCR のモデルが無い（" + BuiltinOcr.DirOf(bm) + "）");
            }
            if (gen == _probeGen) Status.EnvProblem = problem;
            return;
        }
        try
        {
            var info = await OllamaClient.GetTagInfoAsync(OllamaClient.BaseOf(cfg));
            string model = ConfigStore.NodeToStr(cfg["model"]).Trim();
            if (model.Length == 0) problem = "モデル未選択";
            else if (!OllamaClient.ModelInstalled(model, info.Keys))
            {
                problem = "モデル未導入";
                AppLog.Warning("前提チェック: " + model + " は導入済み一覧にない（導入済み: " +
                               (info.Count == 0 ? "0件" : string.Join(", ", info.Keys.OrderBy(k => k, StringComparer.Ordinal))) + "）。入れ方: ollama pull " + model);
            }
            else
            {
                // I9: 文字起こしONの時は翻訳モデルがテキスト専用でも動くため vision非対応 を出さない
                bool ocrSplit = TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeOcr;
                if (!ocrSplit)
                {
                    // 省略名で書かれている時は実際のtag名へ寄せる（/api/show は実在の name で聞かないと capabilities を返さない）
                    string real = info.ContainsKey(model) ? model
                        : info.Keys.FirstOrDefault(n => n.Split(':')[0] == model) ?? model;
                    if (await OllamaClient.ModelHasVisionAsync(cfg, real) == false)
                    {
                        problem = "vision非対応";
                        AppLog.Warning("前提チェック: " + real + " はvision非対応");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            problem = "AIサーバー未起動";
            AppLog.Warning("前提チェック: AIサーバーに疎通できない (" +
                           (ConfigStore.SafeHost(OllamaClient.BaseOf(cfg)) ?? "(URL不正)") + "): " + ex.Message);
        }
        if (gen == _probeGen) Status.EnvProblem = problem;
    }

    // ---------------- 終了時: 載せたモデルを降ろす ----------------

    /// <summary>
    /// 終了後も keep_alive で数GBが常駐しないよう、keep_alive:0 で最善努力の unload（3秒で諦める・
    /// 失敗は握り潰す。Ollama の死活で終了を止めない）。Python版 _quit の末尾と同じ
    /// </summary>
    internal void UnloadOnQuit()
    {
        if (!EnvChecks) return;
        try
        {
            var cfg = _cfg.EffectiveConfig();
            if (!AiIsLocal(cfg)) return;   // F33: 共有サーバーで他人のモデルを降ろさない
            var models = new HashSet<string>
            {
                _cfg.Settings.Model.Trim(),
                ConfigStore.NodeToStr(cfg["model"]).Trim(),
                _activeModel.Trim(),
            };
            if (TranslatePipeline.ThrowModeOf(cfg) == ConfigStore.ThrowModeOcr && !TranslatePipeline.UsesBuiltinOcrForTranscribe(cfg))
            {
                if (TranslatePipeline.OcrStageOf(cfg) is { } ocrCfg)
                    models.Add(ConfigStore.NodeToStr(ocrCfg["model"]).Trim());
                else
                    models.Add(ConfigStore.NodeToStr(cfg["ocr_vlm_model"]).Trim());
            }
            models.Remove("");
            models.RemoveWhere(BuiltinOcr.IsBuiltin);   // 修正J17: 内蔵の OCR は Ollama に積んでいない
            if (models.Count == 0) return;
            // UIスレッドの同期コンテキストを避けるため Task.Run で包んでから待つ
            Task.Run(() => Task.WhenAll(models.Select(m => OllamaClient.UnloadModelAsync(cfg, m))))
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            AppLog.Info("終了時のモデル降ろしは失敗（無視）: " + ex.Message);
        }
    }
}

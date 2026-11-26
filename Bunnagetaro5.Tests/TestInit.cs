using System;
using System.IO;
using System.Runtime.CompilerServices;
using Bunnagetaro5;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Tests;

/// <summary>
/// テストプロジェクト全体の最初の1回だけ走る初期化。
/// 窓を作っただけで本物の Ollama へ問い合わせ・モデルの積み込み・終了時の降ろしが
/// 飛ばないようにする（A4。必要なテストだけが窓ごとに EnvChecks=true にして偽サーバーへ向ける）。
/// </summary>
internal static class TestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Bunnagetaro5.Session.ThrowController.EnvChecksDefault = false;
        // 修正J80: 終わる時の薄れを待たない（窓を閉じるテストが多いので。薄れを見るテストは窓ごとに QuitFadeMs を入れる）
        Bunnagetaro5.MainWindow.QuitFadeDefaultMs = 0;

        // A5: AppLog は Init されていないと本物の %APPDATA%\Bunnagetaro5pp.log へ書く。
        // テスト全体の最初に一時フォルダへ向けておき、個別に Init しないテストが
        // 偽のエラー（「HTTP 500: boom」など）を本物のログへ混ぜないようにする。
        // 各テストが自分の一時フォルダで Init し直すのは従来どおり自由
        var root = Path.Combine(Path.GetTempPath(), "b5_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppLog.Init(new Paths(Path.Combine(root, "Bunnagetaro5")));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, true); } catch { }
        };
    }
}

using System.Text.Json.Nodes;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Tests;

/// <summary>テスト用の設定の書き方</summary>
internal static class CfgTestUtil
{
    /// <summary>
    /// 答えの表示場所を変える。表示場所はプリセットごとになり、既定の「翻訳」は
    /// 専用の設定を持つので、グローバルだけ変えても効かない。各プリセットの専用の設定へ
    /// 先に書き、最後にグローバルへ書く（グローバルの書き込みで変更の知らせが出て、解答欄が追従する）
    /// </summary>
    public static void SetPlace(ConfigStore cfg, string place)
    {
        if (cfg.Root["prompts"] is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject p && p["settings"] is JsonObject st)
                    st["result_place"] = place;
        cfg.Settings.ResultPlace = place;
    }
}

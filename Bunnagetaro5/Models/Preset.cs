using System.Text.Json.Nodes;

namespace Bunnagetaro5.Models;

/// <summary>ぶん投げ文のプリセット（config.json の prompts の1件）。</summary>
public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool UseGlobal { get; set; } = true;

    /// <summary>このプリセットのモデル（「グローバル設定を使う」に関係なく、いつもプリセットごと）</summary>
    public string Model { get; set; } = "";

    /// <summary>プリセット専用設定（PRESET_KEYS のキーだけ。UseGlobal の時は無い）。</summary>
    public Dictionary<string, JsonNode?> Settings { get; } = new();
}

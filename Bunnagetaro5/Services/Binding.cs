using System;
using System.Collections.Generic;
using System.Linq;

namespace Bunnagetaro5.Services;

/// <summary>
/// ホットキー・クイックぶん投げの割り当て文字列（"ctrl+shift+f12" / "f8" / "xbutton1" 等）。
/// Python版 tw_input.py の normalize_binding / parse_binding / validate_binding /
/// format_binding / binding_label / binding_is_key / QUICK_BUTTONS をそのまま移したもの。
/// キー名は物理キーの VK 名で持つ（IMEやJIS/US配列の影響を受けず、実機で押したものが
/// そのまま登録・発火する）。VKにないキー（メディアキー等）は使えない。
/// </summary>
public static class Binding
{
    // 修飾キーのビット（Python版と同じ値。設定文字列と入力フックの両方で使う共通語）
    public const int ModCtrl = 1;
    public const int ModShift = 2;
    public const int ModAlt = 4;
    public const int ModWin = 8;

    // マウスボタンのid（XBUTTON1/XBUTTON2 は WM_XBUTTON の mouseData と同じ値、
    // MButton は VK_MBUTTON と同じ番号。中央は mouseData に入らないので自前で決める）
    public const int XButton1 = 1;
    public const int XButton2 = 2;
    public const int MButton = 4;

    /// <summary>解析結果。IsMouse=true なら Button が、false なら Mods/Vk が有効。</summary>
    public readonly record struct Parsed(bool IsMouse, int Button, int Mods, int Vk);

    // 設定文字列 → 握るボタン集合（"off" は空集合＝マウスでの起動をしない）
    private static readonly IReadOnlySet<int> WheelSet = new HashSet<int> { MButton };
    private static readonly IReadOnlySet<int> BackSet = new HashSet<int> { XButton1 };
    private static readonly IReadOnlySet<int> FwdSet = new HashSet<int> { XButton2 };
    private static readonly IReadOnlySet<int> BothSet = new HashSet<int> { XButton1, XButton2 };
    private static readonly IReadOnlySet<int> EmptySet = new HashSet<int>();

    /// <summary>quick_mouse_button の設定値 → 握るマウスボタン集合（未知の値は wheel 扱い）。</summary>
    public static IReadOnlySet<int> QuickButtons(string? button) => button switch
    {
        "wheel" => WheelSet,
        "back" => BackSet,
        "forward" => FwdSet,
        "both" => BothSet,
        "off" => EmptySet,
        _ => WheelSet
    };

    private static readonly Dictionary<string, int> MouseKeys = new()
    {
        ["xbutton1"] = XButton1, ["xbutton2"] = XButton2, ["mbutton"] = MButton
    };

    // 修飾子は ctrl/alt/shift/win の固定順（正規形はこの順で並ぶ）
    private static readonly (string Name, int Bit)[] ModNames =
    {
        ("ctrl", ModCtrl), ("alt", ModAlt), ("shift", ModShift), ("win", ModWin)
    };
    private static readonly Dictionary<string, string> ModLabel = new()
    {
        ["ctrl"] = "Ctrl", ["alt"] = "Alt", ["shift"] = "Shift", ["win"] = "Win"
    };
    private static readonly HashSet<string> ModSet =
        new(ModNames.Select(m => m.Name));

    // 修飾キーそのものの VK（これ単体はキーとして扱わない）
    private static readonly HashSet<int> ModVks = new()
    {
        0x11, 0x10, 0x12, 0x5B, 0x5C,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0x5B, 0x5C
    };

    private static readonly Dictionary<string, int> Vk = BuildVk();

    // VK → 名前（正規形に戻す向き）。Python版と同じく最初に注册した名前が勝つ
    private static readonly Dictionary<int, string> Name = BuildName(Vk);

    private static readonly Dictionary<string, string> Pretty = new()
    {
        ["printscreen"] = "PrintScreen", ["scrolllock"] = "ScrollLock", ["numlock"] = "NumLock",
        ["capslock"] = "CapsLock", ["pause"] = "Pause", ["apps"] = "Apps", ["space"] = "Space",
        ["enter"] = "Enter", ["esc"] = "Esc", ["tab"] = "Tab", ["backspace"] = "BackSpace",
        ["insert"] = "Insert", ["delete"] = "Delete", ["home"] = "Home", ["end"] = "End",
        ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["left"] = "←", ["up"] = "↑",
        ["right"] = "→", ["down"] = "↓", ["lbracket"] = "[", ["backslash"] = "\\",
        ["rbracket"] = "]", ["quote"] = "'", ["semicolon"] = ";", ["equals"] = "=",
        ["comma"] = ",", ["minus"] = "-", ["period"] = ".", ["slash"] = "/",
        ["backquote"] = "`", ["multiply"] = "*", ["add"] = "+", ["subtract"] = "-",
        ["decimal"] = ".", ["divide"] = "/", ["help"] = "Help",
        ["num0"] = "Num0", ["num1"] = "Num1", ["num2"] = "Num2", ["num3"] = "Num3",
        ["num4"] = "Num4", ["num5"] = "Num5", ["num6"] = "Num6", ["num7"] = "Num7",
        ["num8"] = "Num8", ["num9"] = "Num9"
    };

    // 修飾なし（単独）で設定して良いキー。Home/End/Delete/Escなどのよく打つキーは
    // 握ると入力の邪魔になるので入れない（F1〜F24・PrintScreen等の目立たないキーだけ）
    private static readonly HashSet<int> SafeBare = BuildSafeBare(Vk);

    private static Dictionary<string, int> BuildVk()
    {
        var d = new Dictionary<string, int>();
        for (var c = 'a'; c <= 'z'; c++) d[c.ToString()] = 0x41 + (c - 'a');
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = 0x30 + (c - '0');
        var named = new (string, int)[]
        {
            ("backspace", 0x08), ("tab", 0x09), ("enter", 0x0D), ("pause", 0x13),
            ("capslock", 0x14), ("esc", 0x1B), ("space", 0x20), ("pgup", 0x21),
            ("pgdn", 0x22), ("end", 0x23), ("home", 0x24), ("left", 0x25),
            ("up", 0x26), ("right", 0x27), ("down", 0x28), ("printscreen", 0x2C),
            ("insert", 0x2D), ("delete", 0x2E), ("help", 0x2F),
            ("lbracket", 0xDB), ("backslash", 0xDC), ("rbracket", 0xDD), ("quote", 0xDE),
            ("semicolon", 0xBA), ("equals", 0xBB), ("comma", 0xBC), ("minus", 0xBD),
            ("period", 0xBE), ("slash", 0xBF), ("backquote", 0xC0), ("apps", 0x5D),
            ("scrolllock", 0x91), ("numlock", 0x90),
            ("multiply", 0x6A), ("add", 0x6B), ("subtract", 0x6C), ("decimal", 0x6E),
            ("divide", 0x6F)
        };
        foreach (var (n, v) in named) d[n] = v;      // Python の dict.update と同じ上書き順
        for (var n = 1; n <= 24; n++) d["f" + n] = 0x6F + n;   // F1=0x70 .. F24=0x87
        for (var n = 0; n <= 9; n++) d["num" + n] = 0x60 + n;
        return d;
    }

    private static Dictionary<int, string> BuildName(Dictionary<string, int> vk)
    {
        var d = new Dictionary<int, string>();
        foreach (var (k, v) in vk)
            if (!d.ContainsKey(v)) d[v] = k;          // Python の setdefault と同じ（初出が勝つ）
        return d;
    }

    private static HashSet<int> BuildSafeBare(Dictionary<string, int> vk) =>
        new(Enumerable.Range(1, 24).Select(n => vk["f" + n])
            .Append(vk["printscreen"]).Append(vk["scrolllock"])
            .Append(vk["pause"]).Append(vk["apps"]));

    /// <summary>バインド文字列を正規形（"ctrl+shift+a" / "f8" / "xbutton1"）へ。不正なら ""。
    /// 修飾子は ctrl/alt/shift/win の固定順に並べ直すので、手編集の設定も発火できる。</summary>
    public static string Normalize(string? tok)
    {
        var s = (tok ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace('＋', '+');
        if (s.Length == 0) return "";
        var parts = s.Split('+').Where(p => p.Length > 0).ToArray();
        if (parts.Length is < 1 or > 5) return "";
        var mods = new List<string>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!ModSet.Contains(parts[i]) || mods.Contains(parts[i])) return "";
            mods.Add(parts[i]);
        }
        var baseTok = parts[^1];
        if (ModSet.Contains(baseTok) || (!Vk.ContainsKey(baseTok) && !MouseKeys.ContainsKey(baseTok)))
            return "";
        var outList = ModNames.Where(m => mods.Contains(m.Name)).Select(m => m.Name).ToList();
        outList.Add(baseTok);
        return string.Join("+", outList);
    }

    /// <summary>正規形バインド → マウス（Button）/キー（Mods,Vk）。読めなければ null。
    /// Winキー付きや単独危険キーもここでは構造として返す（禁止するかどうかは
    /// <see cref="Validate"/> が決める。UIは理由を分けて出したいので分離している）。</summary>
    public static Parsed? Parse(string? tok)
    {
        var s = Normalize(tok);
        if (s.Length == 0) return null;
        var parts = s.Split('+');
        var baseTok = parts[^1];
        var mods = 0;
        for (var i = 0; i < parts.Length - 1; i++)
            foreach (var (name, bit) in ModNames)
                if (parts[i] == name) { mods |= bit; break; }
        if (MouseKeys.TryGetValue(baseTok, out var btn))
            return mods == 0 ? new Parsed(true, btn, 0, 0) : null;
        if (!Vk.TryGetValue(baseTok, out var vk) || ModVks.Contains(vk)) return null;
        return new Parsed(false, 0, mods, vk);
    }

    /// <summary>ホットキーとして設定して良いか。""=OK、それ以外=理由コード。
    /// unknown: そのキー名は使えない / win: Winキー付きは他のOSショートカットを潰すので不可 /
    /// bare: 修飾なしではよく打つキーを握ってしまうので不可 / empty: 未設定。</summary>
    public static string Validate(string? tok)
    {
        var h = Parse(tok);
        if (h is null)
            return (tok ?? "").Trim().Length > 0 ? "unknown" : "empty";
        var parts = Normalize(tok).Split('+');
        if (parts[..^1].Contains("win")) return "win";
        if (!h.Value.IsMouse && h.Value.Mods == 0 && !SafeBare.Contains(h.Value.Vk))
            return "bare";
        return "";
    }

    /// <summary>入力フックが拾ったキー（VK＋修飾キー状態）→ 正規形バインド文字列。</summary>
    public static string Format(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("ctrl");
        if ((mods & ModAlt) != 0) parts.Add("alt");
        if ((mods & ModShift) != 0) parts.Add("shift");
        if ((mods & ModWin) != 0) parts.Add("win");
        parts.Add(Name.TryGetValue(vk, out var n) ? n : "vk" + vk.ToString("X2"));
        return string.Join("+", parts);
    }

    /// <summary>正規形バインド → 人が読む表示（"Ctrl + F8" / "マウス戻る" / "（未設定）"）。</summary>
    public static string Label(string? tok)
    {
        var s = Normalize(tok);
        if (s.Length == 0) return "（未設定）";
        var parts = s.Split('+');
        var outList = parts[..^1].Select(p => ModLabel.TryGetValue(p, out var l) ? l : p).ToList();
        var baseTok = parts[^1];
        outList.Add(baseTok switch
        {
            "xbutton1" => "マウス戻る",
            "xbutton2" => "マウス進む",
            "mbutton" => "ホイールクリック（中央）",
            _ => Pretty.TryGetValue(baseTok, out var pv) ? pv : baseTok.ToUpperInvariant()
        });
        return string.Join(" + ", outList);
    }

    /// <summary>バインド文字列がキーボードのキーを指すか（マウスボタンなら false）。</summary>
    public static bool IsKey(string? tok)
    {
        var h = Parse(tok);
        return h is not null && !h.Value.IsMouse;
    }
}

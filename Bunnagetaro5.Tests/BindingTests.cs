using System.Linq;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>割り当て文字列</summary>
public class BindingTests
{
    [Fact]
    public void 正規化は修飾子を固定順に並べ直す()
    {
        Assert.Equal("ctrl+shift+a", Binding.Normalize("shift+ctrl+a"));
        Assert.Equal("ctrl+alt+shift+win+f12", Binding.Normalize("win+shift+alt+ctrl+f12"));
    }

    [Fact]
    public void 正規化は大文字小文字と空白と全角足し算を吸収する()
    {
        Assert.Equal("ctrl+a", Binding.Normalize("  Ctrl + A "));
        Assert.Equal("ctrl+a", Binding.Normalize("ctrl＋a"));
        Assert.Equal("ctrl+a", Binding.Normalize("CTRL+A"));
    }

    [Fact]
    public void 正規化は不正な形を空にする()
    {
        Assert.Equal("", Binding.Normalize(null));
        Assert.Equal("", Binding.Normalize("   "));
        Assert.Equal("", Binding.Normalize("ctrl+ctrl+a"));   // 修飾子の重複
        Assert.Equal("", Binding.Normalize("ctrl+shift+alt+win+a+x")); // 6語目
        Assert.Equal("", Binding.Normalize("ctrl++a+x"));     // 空要素を落として base が a でない語
        Assert.Equal("", Binding.Normalize("foo"));           // 知らないキー名
        Assert.Equal("", Binding.Normalize("ctrl"));          // 単体の修飾子は base にできない
        Assert.Equal("", Binding.Normalize("ctrl+"));         // base が無い
    }

    [Fact]
    public void 正規化は空要素を落としてから数える()
    {
        Assert.Equal("ctrl+a", Binding.Normalize("ctrl++a"));
        Assert.Equal("f8", Binding.Normalize("+f8"));
    }

    [Fact]
    public void 解析はキーを修飾ビットとVKで返す()
    {
        var h = Binding.Parse("ctrl+shift+f12");
        Assert.NotNull(h);
        Assert.False(h!.Value.IsMouse);
        Assert.Equal(Binding.ModCtrl | Binding.ModShift, h.Value.Mods);
        Assert.Equal(0x7B, h.Value.Vk);                       // F12
        Assert.Equal(0, Binding.Parse("f8")!.Value.Mods);
        Assert.Equal(0x77, Binding.Parse("f8")!.Value.Vk);    // F8
    }

    [Fact]
    public void 解析はマウスボタンを修飾無しだけで通す()
    {
        Assert.Equal(Binding.XButton1, Binding.Parse("xbutton1")!.Value.Button);
        Assert.True(Binding.Parse("mbutton")!.Value.IsMouse);
        Assert.Null(Binding.Parse("ctrl+back"));              // マウスに修飾子は付けない
        Assert.Equal("", Binding.Normalize("ctrl+back"));     // back がそもそもキー名でない
    }

    [Fact]
    public void 解析はVK表の範囲を見る_F1からF24とテンキー()
    {
        Assert.Equal(0x70, Binding.Parse("f1")!.Value.Vk);
        Assert.Equal(0x87, Binding.Parse("f24")!.Value.Vk);
        Assert.Equal(0x60, Binding.Parse("num0")!.Value.Vk);
        Assert.Equal(0x65, Binding.Parse("num5")!.Value.Vk);
        Assert.Equal(0xDB, Binding.Parse("lbracket")!.Value.Vk);
    }

    [Fact]
    public void 検証は理由コードを分けて返す()
    {
        Assert.Equal("empty", Binding.Validate(""));
        Assert.Equal("empty", Binding.Validate(null));
        Assert.Equal("unknown", Binding.Validate("foo+bar"));
        Assert.Equal("win", Binding.Validate("win+e"));       // Win付きはOSShortcutを潰す
        Assert.Equal("bare", Binding.Validate("a"));          // よく打つキーの単独は握れない
        Assert.Equal("bare", Binding.Validate("delete"));
        Assert.Equal("", Binding.Validate("f5"));             // 目立たないキーは単独OK
        Assert.Equal("", Binding.Validate("printscreen"));
        Assert.Equal("", Binding.Validate("ctrl+a"));
        Assert.Equal("", Binding.Validate("xbutton1"));       // マウス単独は常にOK
        Assert.Equal("", Binding.Validate("mbutton"));
    }

    [Fact]
    public void 検証はf24とappsも単独で通す()
    {
        Assert.Equal("", Binding.Validate("f24"));
        Assert.Equal("", Binding.Validate("apps"));
        Assert.Equal("", Binding.Validate("scrolllock"));
        Assert.Equal("", Binding.Validate("pause"));
    }

    [Fact]
    public void 表示名は人が読む形にする()
    {
        Assert.Equal("Ctrl + F8", Binding.Label("ctrl+f8"));
        Assert.Equal("Ctrl + Shift + F12", Binding.Label("ctrl+shift+f12"));
        Assert.Equal("マウス戻る", Binding.Label("xbutton1"));
        Assert.Equal("マウス進む", Binding.Label("xbutton2"));
        Assert.Equal("ホイールクリック（中央）", Binding.Label("mbutton"));
        Assert.Equal("（未設定）", Binding.Label(""));
        Assert.Equal("（未設定）", Binding.Label("junk!"));
        Assert.Equal("←", Binding.Label("left"));
        Assert.Equal("PageUp", Binding.Label("pgup"));
        Assert.Equal("F2", Binding.Label("f2"));              //pretty 無い語は大文字に
        Assert.Equal("Num5", Binding.Label("num5"));
    }

    [Fact]
    public void フックが拾ったキーから正規形を組み立てる()
    {
        Assert.Equal("ctrl+alt+shift+win+a",
            Binding.Format(Binding.ModCtrl | Binding.ModAlt | Binding.ModShift | Binding.ModWin, 0x41));
        Assert.Equal("f8", Binding.Format(0, 0x77));
        Assert.Equal("vkA5", Binding.Format(0, 0xA5));        // 名前の無いVKは 16進で残す
        Assert.Equal("ctrl+esc", Binding.Format(Binding.ModCtrl, 0x1B));
    }

    [Fact]
    public void 正規形と表示名は丸ごと往復できる()
    {
        // Format で組み立てた語は Normalize でそのまま通る（登録→保存→発火の輪）
        var tok = Binding.Format(Binding.ModCtrl, 0x77);
        Assert.Equal(tok, Binding.Normalize(tok));
        Assert.NotNull(Binding.Parse(tok));
    }

    [Fact]
    public void キーかマウスかの区別()
    {
        Assert.True(Binding.IsKey("f5"));
        Assert.True(Binding.IsKey("ctrl+a"));
        Assert.False(Binding.IsKey("xbutton1"));
        Assert.False(Binding.IsKey("mbutton"));
        Assert.False(Binding.IsKey("junk"));
    }

    [Fact]
    public void クイックボタンの集合は設定名どおり()
    {
        Assert.Equal(new[] { Binding.MButton }, Binding.QuickButtons("wheel").OrderBy(x => x));
        Assert.Equal(new[] { Binding.XButton1 }, Binding.QuickButtons("back").OrderBy(x => x));
        Assert.Equal(new[] { Binding.XButton2 }, Binding.QuickButtons("forward").OrderBy(x => x));
        Assert.Equal(new[] { Binding.XButton1, Binding.XButton2 }, Binding.QuickButtons("both").OrderBy(x => x));
        Assert.Empty(Binding.QuickButtons("off"));
        Assert.Equal(new[] { Binding.MButton }, Binding.QuickButtons("よくわからん").OrderBy(x => x)); // 既定=wheel
    }
}

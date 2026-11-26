using System;
using System.Runtime.InteropServices;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>手順22: 入力フック（tw_input.py QuickInputWatcher）のコールバック判定。
/// フック自体は張らず、コールバック相当を合成した HOOKSTRUCT へ直接叩いて確かめる。
/// （素通しの戻り値は次のフックの値が混ざりうるので「1でない＝素通し」で見る。
/// キー系は GetAsyncKeyState の実状態（テスト実行中に物理修飾キーが下がっていないこと）に依存する。）</summary>
public class QuickInputWatcherTests
{
    private static IntPtr PtrOf<T>(T s) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(s, p, false);
        return p;
    }

    private static IntPtr Mouse(QuickInputWatcher w, int cmd, int x, int y, uint data = 0)
    {
        var p = PtrOf(new QuickInputWatcher.MSLLHOOKSTRUCT { x = x, y = y, mouseData = data });
        try { return w.MouseProc(0, (IntPtr)cmd, p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101;

    private static IntPtr Key(QuickInputWatcher w, uint vk, bool up = false)
    {
        var p = PtrOf(new QuickInputWatcher.KBDLLHOOKSTRUCT
        { vkCode = vk, flags = up ? 0x80u : 0u });
        try { return w.KeyProc(0, (IntPtr)(up ? WmKeyUp : WmKeyDown), p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static QuickInputWatcher Fresh(bool enabled = true, string button = "wheel",
        string hotkey = "", string kdrag = "")
    {
        // 既存のテストは「離したらすぐ確定」を見るので、チャタリングの預かりは切っておく
        // 修正J46: 離した時の終点は本物のカーソルを読むので、合成したイベントの座標で見るテストでは読まない
        var w = new QuickInputWatcher { ChatterMs = 0, UpCursor = () => null };
        w.Apply(enabled, button, hotkey, kdrag);
        return w;
    }

    [Fact]
    public void チャタリング_預かり中に同じボタンが押されたら元の始点のまま続く()
    {
        var w = Fresh(true, "wheel");
        w.ChatterMs = 200;
        Mouse(w, 0x0207, 10, 20);                                      // down
        Assert.Equal(1, Mouse(w, 0x0208, 30, 40).ToInt32());          // 一瞬の up
        Assert.True(w.UpPending);
        Assert.Null(w.TakeResult());                                   // まだ確定しない
        Assert.Equal(1, Mouse(w, 0x0207, 31, 41).ToInt32());          // すぐ down
        Assert.Equal(new QuickInputWatcher.DragStart(10, 20, Binding.MButton), w.Drag);   // 始点は元のまま
        Assert.False(w.UpPending);
        Assert.True(w.TakeChatter()!.Absorbed);
        w.ChatterMs = 0;
        Mouse(w, 0x0208, 50, 60);                                      // 本当の up
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 50, 60), w.TakeResult());
    }

    [Fact]
    public void チャタリング_預かりの時間が過ぎたら離しが確定する()
    {
        var w = Fresh(true, "wheel");
        w.ChatterMs = 30;
        Mouse(w, 0x0207, 10, 20);
        Mouse(w, 0x0208, 30, 40);
        Assert.True(w.UpPending);
        System.Threading.Thread.Sleep(60);
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 30, 40), w.TakeResult());
        Assert.False(w.UpPending);
        Assert.Null(w.TakeChatter());
    }

    [Fact]
    public void チャタリング_時間を過ぎた押し直しは別の四角で記録だけ残る()
    {
        var w = Fresh(true, "wheel");
        w.ChatterMs = 10;
        Mouse(w, 0x0207, 10, 20);
        Mouse(w, 0x0208, 30, 40);
        System.Threading.Thread.Sleep(20);
        Mouse(w, 0x0207, 31, 41);                                      // 預かりは過ぎている（知らせの 45ms の内）
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 30, 40), w.TakeResult());   // 前の四角は確定
        Assert.Equal(new QuickInputWatcher.DragStart(31, 41, Binding.MButton), w.Drag);   // 新しい四角
        Assert.False(w.TakeChatter()!.Absorbed);
    }

    [Fact]
    public void J11_チャタリング_人の速い押し直しは知らせない()
    {
        var w = Fresh(true, "wheel");
        w.ChatterMs = 10;
        Mouse(w, 0x0207, 10, 20);
        Mouse(w, 0x0208, 30, 40);
        System.Threading.Thread.Sleep(QuickInputWatcher.SuspectRepressMs + 40);   // ログで多かった 70〜130ms の押し直し
        Mouse(w, 0x0207, 31, 41);
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 30, 40), w.TakeResult());
        Assert.Null(w.TakeChatter());
    }

    [Fact]
    public void nCodeがマイナスは何もせず素通し()
    {
        var w = Fresh();
        Assert.True(w.MouseProc(-1, IntPtr.Zero, IntPtr.Zero).ToInt64() != 1);
        Assert.True(w.KeyProc(-1, (IntPtr)WmKeyDown, IntPtr.Zero).ToInt64() != 1);
        Assert.Null(w.Drag);
    }

    [Fact]
    public void 割り当てたホイールボタンで追跡が始まる()
    {
        var w = Fresh(true, "wheel");
        Assert.Equal(1, Mouse(w, 0x0207, 10, 20).ToInt32());          // WM_MBUTTONDOWN
        Assert.Equal(new QuickInputWatcher.DragStart(10, 20, Binding.MButton), w.Drag);
        Assert.Equal(1, w.DownCount);
    }

    [Fact]
    public void ドラッグ中のセカンドクリックは素通し()
    {
        var w = Fresh(true, "both");
        Mouse(w, 0x020B, 5, 5, 0x00010000);                            // xbutton1 down（上位word実測）
        Assert.True(Mouse(w, 0x020B, 9, 9, 0x00020000).ToInt64() != 1); // xbutton2 down は素通し
        Assert.Equal(Binding.XButton1, w.Drag!.Value.Button);
    }

    [Fact]
    public void 同じボタンを離して矩形が確定しtake一度だけ()
    {
        var w = Fresh(true, "wheel");
        Mouse(w, 0x0207, 10, 20);
        Assert.Equal(1, Mouse(w, 0x0208, 30, 40).ToInt32());          // WM_MBUTTONUP
        Assert.Null(w.Drag);
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 30, 40), w.TakeResult());
        Assert.Null(w.TakeResult());
        Assert.Equal(1, w.UpCount);
    }

    [Fact]
    public void 割り当てていないボタンは追跡しない()
    {
        var w = Fresh(true, "wheel");
        Assert.True(Mouse(w, 0x0201, 1, 1).ToInt64() != 1);            // WM_LBUTTONDOWN
        Assert.True(Mouse(w, 0x020B, 1, 1, 0x00010000).ToInt64() != 1); // xbutton1 は wheel  mask でない
        Assert.Null(w.Drag);
    }

    [Fact]
    public void mouseDataの下位wordだけで来る経路も拾う()
    {
        var w = Fresh(true, "both");
        Assert.Equal(1, Mouse(w, 0x020B, 3, 4, 0x0002).ToInt32());     // 下位word=XBUTTON2
        Assert.Equal(Binding.XButton2, w.Drag!.Value.Button);
    }

    [Fact]
    public void 登録待ちはマウスを握って記録する()
    {
        var w = Fresh(true, "off");
        w.StartCatch();
        Assert.Equal(1, Mouse(w, 0x0208, 0, 0).ToInt32());             // upも握る（桌面へ漏れない）
        Assert.Equal(1, Mouse(w, 0x0207, 7, 8).ToInt32());             // 中央 down → 記録
        Assert.Equal(1, Mouse(w, 0x0208, 7, 8).ToInt32());
        Assert.Equal("mbutton", w.TakeCaptured());
        w.StopCatch();
        Assert.True(Mouse(w, 0x0207, 0, 0).ToInt64() != 1);            // offなので素通し
    }

    [Fact]
    public void 登録待ちはキーを修飾付きで記録し修飾単体は確定しない()
    {
        var w = Fresh(false, "off");
        w.StartCatch();
        Assert.Equal(1, Key(w, 0x10).ToInt32());                       // 物理Shift down（修飾単体）
        Assert.Null(w.Captured);
        Assert.Equal(1, Key(w, 0x41).ToInt32());                       // A down → 記録（物理修飾なしの実測前提）
        Assert.Equal("a", w.TakeCaptured());
        w.StopCatch();
        Assert.True(Key(w, 0x41).ToInt64() != 1);                      // 解除後は普通のtypingを素通し
    }

    [Fact]
    public void J46_離した時の終点はイベントの座標ではなく見えているカーソルの位置()
    {
        var w = Fresh(true, "wheel");
        w.UpCursor = () => (799, 1047);                                  // ダミー枠が追っていたカーソル
        Mouse(w, 0x0207, 1119, 778);                                     // 右上で押す
        Assert.Equal(1, Mouse(w, 0x0208, 795, 1056).ToInt32());          // 離したイベントは 左4px・下9px ずれて届いた（実測）
        Assert.Equal(new QuickInputWatcher.RectResult(1119, 778, 799, 1047), w.TakeResult());
        w.UpCursor = () => null;                                         // カーソルが読めなければイベントの座標
        Mouse(w, 0x0207, 10, 20);
        Mouse(w, 0x0208, 30, 40);
        Assert.Equal(new QuickInputWatcher.RectResult(10, 20, 30, 40), w.TakeResult());
    }

    [Fact]
    public void J37_登録待ちはフック側の期限を過ぎたらキーもクリックも素通しにする()
    {
        var f = typeof(QuickInputWatcher).GetField("_catchUntilMs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var w = Fresh(false, "off");
        w.StartCatch();
        Assert.Equal(1, Key(w, 0x42).ToInt32());                       // 期限内は握って記録
        Assert.Equal("b", w.TakeCaptured());
        f.SetValue(w, 0L);                                             // UI が詰まって中止が来ないまま期限を過ぎた想定
        Assert.True(Key(w, 0x41).ToInt64() != 1);                      // 素通し（全部のキーが死なない）
        Assert.Null(w.Captured);
        Assert.True(Mouse(w, 0x0207, 0, 0).ToInt64() != 1);            // クリックも素通し
        Assert.Null(w.Captured);
        w.StartCatch();                                                // 登録し直せば、また握る
        Assert.Equal(1, Key(w, 0x41).ToInt32());
        Assert.Equal("a", w.TakeCaptured());
        w.StopCatch();
    }

    [Fact]
    public void ホットキーは押した縁だけで一度発火しリピートとupは握る()
    {
        var w = Fresh(true, "wheel", hotkey: "f8");
        Assert.Equal(1, Key(w, 0x77).ToInt32());                       // F8 down → 発火
        Assert.Equal("f8", w.TakeHot());
        Assert.Equal(1, Key(w, 0x77).ToInt32());                       // 自動リピートも握る
        Assert.Null(w.HotFire);                                        // 再発火しない
        Assert.Equal(1, Key(w, 0x77, up: true).ToInt32());             // upも握る
        Assert.Equal(1, Key(w, 0x77).ToInt32());                       // 離してから再度 → 発火
        Assert.Equal("f8", w.TakeHot());
    }

    [Fact]
    public void 修飾キーが不一致なら素通し()
    {
        var w = Fresh(true, "wheel", hotkey: "ctrl+f8");
        Assert.True(Key(w, 0x77).ToInt64() != 1);                      // 物理Ctrlが下っていない（実測前提）
        Assert.Null(w.HotFire);
    }

    [Fact]
    public void 同じ設定でApplyを繰り返してもキー起動の押している状態は消えない()
    {
        var w = Fresh(true, "wheel", kdrag: "f8");
        Assert.Equal(1, Key(w, 0x77).ToInt32());
        Assert.Equal(QuickInputWatcher.KdDown, w.KdState);
        for (int i = 0; i < 5; i++) w.Apply(true, "wheel", "", "f8");   // 1秒ごとの読み直しの再現
        Assert.Equal(QuickInputWatcher.KdDown, w.KdState);
        Assert.Equal("f8", w.Kdrag());
        Assert.Equal(1, Key(w, 0x77, up: true).ToInt32());              // 離した時は普通に離しになる
        Assert.Equal(QuickInputWatcher.KdUp, w.KdState);
        // 別のキーへ変えた時は張り直す
        w.Apply(true, "wheel", "", "f9");
        Assert.Equal("f9", w.Kdrag());
        Assert.Equal(QuickInputWatcher.KdUp, w.KdState);
    }

    [Fact]
    public void 同じ設定でApplyを繰り返してもホットキーの押しっぱなしの判定は消えない()
    {
        var w = Fresh(true, "wheel", hotkey: "f8");
        Assert.Equal(1, Key(w, 0x77).ToInt32());
        Assert.Equal("f8", w.TakeHot());
        w.Apply(true, "wheel", "f8", "");
        Assert.Equal(1, Key(w, 0x77).ToInt32());                        // リピート: 再発火しない
        Assert.Null(w.TakeHot());
    }

    [Fact]
    public void キー起動は押している間だけ下線を置いてリピートも握る()
    {
        var w = Fresh(true, "wheel", kdrag: "f8");
        Assert.Equal(QuickInputWatcher.KdUp, w.KdState);
        Assert.Equal(1, Key(w, 0x77).ToInt32());
        Assert.Equal(QuickInputWatcher.KdDown, w.KdState);
        Assert.Equal(1, Key(w, 0x77).ToInt32());                       // リピート
        Assert.Equal(QuickInputWatcher.KdDown, w.KdState);
        Assert.Equal(1, Key(w, 0x77, up: true).ToInt32());
        Assert.Equal(QuickInputWatcher.KdUp, w.KdState);
        Assert.Null(w.HotFire);                                        // kd はホットキー発火にしない
    }

    [Fact]
    public void 同じ入力を起動とホットキーの両方にはしない()
    {
        var w = Fresh(true, "wheel", hotkey: "f8", kdrag: "f8");
        Assert.Equal("", w.Hotkey());                                  // 開始を優先して発火待ちを外す
        Assert.Equal("f8", w.Kdrag());
        var w2 = Fresh(true, "wheel", hotkey: "mbutton");
        Assert.Equal("", w2.Hotkey());                                 // ドラッグ起動と同じボタンは待たない
    }

    [Fact]
    public void 登録開始で進行中の追跡は打ち切る()
    {
        var w = Fresh(true, "wheel");
        Mouse(w, 0x0207, 4, 6);
        Assert.NotNull(w.Drag);
        w.StartCatch();
        Assert.Null(w.Drag);
        Assert.Equal(1, Mouse(w, 0x0207, 4, 6).ToInt32());             // 登録に切り替わったので記録側
        Assert.Equal("mbutton", w.TakeCaptured());
    }

    [Fact]
    public void OFFでもホットキーだけは生きていてクイック追跡だけ止まる()
    {
        // Python版と同じ：有効チェックはクイックぶん投げ用。hotkey 指定中はキーボードフックだけ張る
        var w = Fresh(false, "wheel", hotkey: "ctrl+f8");
        Assert.True(w.Enabled());
        Assert.True(Key(w, 0x77).ToInt64() != 1);                      // 修飾不一致で素通し（物理Ctrl前提）
        Assert.True(Mouse(w, 0x0207, 0, 0).ToInt64() != 1);            // マウスは割り当てなし＝素通し
        var w2 = Fresh(false, "wheel");
        Assert.False(w2.Enabled());                                    // 割り当てなし＝フックを張らない
    }

    [Fact]
    public void StartとStopは何度でも安全()
    {
        var w = Fresh(true, "wheel", hotkey: "ctrl+f8");
        w.Start();
        w.Start();                                                     // 二重Startは無視
        Assert.True(w.Enabled());
        w.Stop();
        Assert.False(w.Enabled());
        w.Stop();                                                      // 二重Stopも安全
    }

    [Fact]
    public async System.Threading.Tasks.Task 別スレッドからDragとTakeResultを並行読み取りしても壊れない()
    {
        var w = Fresh(true, "wheel");
        using var cts = new System.Threading.CancellationTokenSource();
        var reader = System.Threading.Tasks.Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var d = w.Drag;
                if (d is not null)
                {
                    Assert.Equal(10, d.Value.X0);
                    Assert.Equal(20, d.Value.Y0);
                    Assert.Equal(Binding.MButton, d.Value.Button);
                }
                var r = w.TakeResult();
                if (r is not null)
                {
                    Assert.Equal(10, r.Value.X0);
                    Assert.Equal(20, r.Value.Y0);
                    Assert.Equal(30, r.Value.X1);
                    Assert.Equal(40, r.Value.Y1);
                }
            }
        });
        for (int i = 0; i < 2000; i++)
        {
            Mouse(w, 0x0207, 10, 20);
            Mouse(w, 0x0208, 30, 40);
        }
        cts.Cancel();
        await reader;
        Assert.Null(w.Drag);
    }

    // ---- 修正J58: 範囲ドラッグのホットキー（2つ目の枠） ----

    private static QuickInputWatcher FreshSnip(string snip, string hotkey = "", bool enabled = false, string button = "off")
    {
        var w = new QuickInputWatcher { ChatterMs = 0, UpCursor = () => null };
        w.Apply(enabled, button, hotkey, "", snip);
        return w;
    }

    [Fact]
    public void J58_範囲ドラッグのキーは押した縁で一度だけ発火しリピートとupは握る()
    {
        var w = FreshSnip("f7");
        Assert.True(w.Enabled());                                      // これだけでもフックは張る
        Assert.Equal("f7", w.SnipHotkey());
        Assert.Equal(1, Key(w, 0x76).ToInt32());
        Assert.Equal("f7", w.TakeSnipHot());
        Assert.Null(w.TakeHot());                                      // ぶん投げの合図にはならない
        Assert.Equal(1, Key(w, 0x76).ToInt32());                       // リピートも握る
        Assert.Null(w.SnipFire);                                       // 再発火しない
        Assert.Equal(1, Key(w, 0x76, up: true).ToInt32());             // upも握る
        Assert.Equal(1, Key(w, 0x76).ToInt32());                       // 離してから再度 → 発火
        Assert.Equal("f7", w.TakeSnipHot());
    }

    [Fact]
    public void J58_範囲ドラッグのキーは修飾キーが不一致なら素通し()
    {
        var w = FreshSnip("ctrl+f7");
        Assert.True(Key(w, 0x76).ToInt64() != 1);
        Assert.Null(w.SnipFire);
        Assert.True(Key(w, 0x76, up: true).ToInt64() != 1);
    }

    [Fact]
    public void J58_ぶん投げホットキーと範囲ドラッグのキーは並んで働く()
    {
        var w = FreshSnip("f7", hotkey: "f8");
        Assert.Equal(1, Key(w, 0x77).ToInt32());
        Assert.Equal("f8", w.TakeHot());
        Assert.Null(w.SnipFire);
        Assert.Equal(1, Key(w, 0x76).ToInt32());
        Assert.Equal("f7", w.TakeSnipHot());
        Assert.Null(w.HotFire);
        Assert.Equal(1, Key(w, 0x77, up: true).ToInt32());
        Assert.Equal(1, Key(w, 0x76, up: true).ToInt32());
        w.Apply(false, "off", "f8", "", "f7");                         // 1秒ごとの読み直し
        Assert.Equal("f7", w.SnipHotkey());
    }

    [Fact]
    public void J58_範囲ドラッグのボタンはマウスフックで握り_ほかと同じ入力は待たない()
    {
        var w = FreshSnip("xbutton1");
        Assert.True(w.Enabled());
        Assert.Equal(1, Mouse(w, 0x020B, 0, 0, 0x00010000u).ToInt32());
        Assert.Equal("xbutton1", w.TakeSnipHot());
        Assert.Equal(1, Mouse(w, 0x020C, 0, 0, 0x00010000u).ToInt32());   // 離しも握る
        Assert.Null(w.TakeSnipHot());
        Assert.Equal("", FreshSnip("f8", hotkey: "f8").SnipHotkey());      // ぶん投げホットキーが先
        Assert.Equal("", FreshSnip("mbutton", enabled: true, button: "wheel").SnipHotkey());   // クイックの起動ボタンと同じ
        Assert.Equal("", FreshSnip("").SnipHotkey());                       // 既定は未設定
        Assert.False(FreshSnip("").Enabled());                              // 何も握らない
    }
}

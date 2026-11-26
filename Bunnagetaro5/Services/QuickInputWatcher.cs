using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bunnagetaro5.Services;

/// <summary>
/// 長押しドラッグを矩形として拾い、ぶん投げホットキーの発火も拾う入力フックスレッド
/// （Python版 tw_input.py の QuickInputWatcher の移植）。
///
/// 拾える入力は3系統（同じスレッドで最大2つの低レベルフックを張る）:
/// ・クイックぶん投げ（マウス）: 割り当てたボタンを長押しドラッグして四角を描き、離す
/// ・クイックぶん投げ（キー）: 設定キーを押しているあいだ、マウスを動かして四角を描く
///   （左手でキー・右手でマウス）。押した位置が基点、キーを離した位置が対角。
///   フックは KdState に 1/0 を置くだけで、矩形はUI側が GetCursorPos で読む
/// ・ぶん投げホットキー: 設定したキー／ボタンで、開いている窓をその場でぶん投げる
/// ほかに入力「登録」: 次に押された入力を1個だけ記録する（ゲームのキーコンフィグ式）
///
/// 割り当てた入力は return 1 で握るので、他のアプリ（ブラウザの戻る/進む、中クリックの
/// 自動スクロール、設定キーの通常の発火）へは届かない。キーボードでは設定したキーの
/// down/up だけを握り、普通の打鍵は一切握らないので typing は各アプリへ届く。
///
/// 設計メモ（Python版で実測で潰したはまりポイント）:
/// ・フックを張ったスレッドがメッセージをポンプしている間だけコールバックが呼ばれる
///   → 専用スレッドで MsgWaitForMultipleObjectsEx + PeekMessageW を回す
/// ・コールバックは超軽量に保つ（LowLevelHooksTimeout を超えるとフックが剥がされ、
///   それまでシステム全体の入力が遅延する）。重い処理やUI操作は禁止。
///   矩形の現在位置は move を流さず UI 側が GetCursorPos で読む
/// ・OFF のあいだはフック自体を剥がす（他アプリへの影響ゼロ・オーバーヘッドゼロ）
/// ・キーの自動リピート（押しっぱなし）は down が何回も来る。エッジ検出で1回だけ発火し、
///   リピートも握る（握らないと押しているあいだそのキーがアプリへ入り続ける）
/// ・コールバックの.delegate参照はスレッド生命期ぶん必ず生存させる（Marshal された
///   delegate が GC されるとコードアドレスが再利用され、他プロセスの入力で暴発し得る）
/// </summary>
public sealed class QuickInputWatcher
{
    // ---- Win32 定数 ----
    private const int WhMouseLl = 14, WhKeyboardLl = 13;
    internal const int WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;
    internal const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208;
    internal const int WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    internal const int LlkfUp = 0x80;             // KBDLLHOOKSTRUCT.flags の「up」ビット
    private const int WmQuit = 0x0012;
    private const int PmRemove = 0x0001;
    private const int QsAllInput = 0x04FF;
    private const int MwmoInputAvailable = 0x0004;
    private const int VkControl = 0x11, VkShift = 0x10, VkMenu = 0x12, VkLwin = 0x5B, VkRwin = 0x5C;

    // KdState の値（Python版の None/0/1 対応。-1=設定なし）
    public const int KdNone = -1, KdUp = 0, KdDown = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public int x, y;
        public uint mouseData, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public PointL pt;
        public uint time;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg lpMsg, IntPtr hWnd, uint minMsg, uint maxMsg, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);
    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint ms, uint msgFilter, uint flags);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    public readonly record struct DragStart(int X0, int Y0, int Button);
    public readonly record struct RectResult(int X0, int Y0, int X1, int Y1);
    private sealed class Box<T>(T value) where T : struct
    {
        public readonly T Value = value;
    }

    // ボタンid → バインド文字列（「登録」で拾ったものを欄へ返す向き）
    private static string? MouseTok(int btn) => btn switch
    {
        Binding.XButton1 => "xbutton1",
        Binding.XButton2 => "xbutton2",
        Binding.MButton => "mbutton",
        _ => null
    };

    // ---- 設定（UIスレッドが書き、巡回・フック側が読む） ----
    private IReadOnlySet<int> _mask = Binding.QuickButtons("off");
    private Binding.Parsed? _hot;
    private string _hotTok = "";
    private Binding.Parsed? _snip;           // 修正J58: 範囲ドラッグのホットキー（ぶん投げホットキーと同じ扱いの2つ目の枠）
    private string _snipTok = "";
    private Binding.Parsed? _kd;
    private string _kdTok = "";
    private volatile bool _catch;

    /// <summary>修正J46: 離した時の終点に使うカーソル位置（物理px）。取れなければ null（離したイベントの座標を使う）。
    /// テストは合成したイベントの座標で見るので null を返す物に差し替える</summary>
    internal Func<(int X, int Y)?> UpCursor { get; set; } = Geom.CursorPos;
    private long _catchUntilMs;   // 修正J37: 「登録」待ちのフック側の期限（NowMs）

    /// <summary>修正J37: 「登録」待ちはフック側でも期限（UI の8秒より長い10秒）を持つ。
    /// UI のスレッドが詰まって中止のタイマーが回らなくても、全部のキー・クリックを握り続けない（期限を過ぎたら素通し）</summary>
    internal const int CatchHookTimeoutMs = 10000;

    private bool CatchLive()
    {
        if (!_catch) return false;
        if (NowMs() <= Interlocked.Read(ref _catchUntilMs)) return true;
        _catch = false;
        return false;
    }

    // ---- 公開状態（UI側が poll して読み、take で取り出す） ----
    private Box<DragStart>? _drag;          // 長押しドラッグ中 (x0,y0,btn)
    private Box<RectResult>? _result;       // (x0,y0,x1,y1) UIが拾うまで保持
    private string? _captured;              // 「登録」待ちで拾ったバインド文字列
    private string? _hotFire;               // ホットキー発火（正規形）。UIが拾うまで保持
    private string? _snipFire;              // 修正J58: 範囲ドラッグのホットキー発火（正規形）。UIが拾うまで保持
    public DragStart? Drag => Volatile.Read(ref _drag)?.Value;
    public RectResult? Result => Volatile.Read(ref _result)?.Value;
    public string? Captured => Volatile.Read(ref _captured);
    public string? HotFire => Volatile.Read(ref _hotFire);
    public string? SnipFire => Volatile.Read(ref _snipFire);
    private int _kdState = KdNone;
    public int KdState => Volatile.Read(ref _kdState);   // 開始キー: None(-1)/0=離している/1=押している
    public string LastError = "";
    private int _downCount, _upCount;
    public int DownCount => Volatile.Read(ref _downCount);
    public int UpCount => Volatile.Read(ref _upCount);

    // ---- チャタリング（押している最中にボタンが一瞬だけ離れる）対策 ----
    // 離しはすぐ確定せず ChatterMs だけ預かる。その間に同じボタンが押されたら離しは無かったことにして、
    // 元の始点のまま描き続ける（始点が途中で切り替わる・2回ぶん投げる、を防ぐ）。0 なら預からない
    private sealed class PendingUp(RectResult result, int button, long ms)
    {
        public readonly RectResult Result = result;
        public readonly int Button = button;
        public readonly long Ms = ms;
    }
    private PendingUp? _pendingUp;
    private volatile int _chatterMs = 30;
    /// <summary>チャタリングの記録。Absorbed=預かり中に押し直されて続けた／false=預かりを過ぎた押し直し（別の四角になった）</summary>
    public sealed record ChatterEvent(bool Absorbed, long GapMs, int ChatterMs);
    private ChatterEvent? _chatterNote;      // UIが拾ってログ・状態欄へ出す
    /// <summary>離してからこれより早い押し直しを「チャタリングかも」と記録する（ms）。
    /// 修正J11: 300 → 60。人の速い押し直し（ログでは 67〜290ms）まで知らせていた（人の判断「もうちょい厳しく」）。
    /// 修正J11b: 60 → 45（人の判断「もうちょっとだけきつく」）</summary>
    public const int SuspectRepressMs = 45;
    private long _lastUpMs = long.MinValue;  // フック側だけが読み書き（間に合わなかった押し直しの検出用）
    private int _lastUpBtn;
    /// <summary>離しを預かる時間（ms。0〜200。0 で預からない）。設定 quick_chatter_ms</summary>
    public int ChatterMs { get => _chatterMs; set => _chatterMs = Math.Clamp(value, 0, 200); }
    /// <summary>離しを預かっている最中（まだ描いている扱い）</summary>
    public bool UpPending => Volatile.Read(ref _pendingUp) is not null;

    /// <summary>修正J49: 離した四角の終点（預かり中、または UI がまだ拾っていない確定）。無ければ null。
    /// ダミー枠が離した点で止まるのに使う（離した後もカーソルを追うと、確定した四角とずれて見えた）</summary>
    public (int X, int Y)? ReleasedEnd
    {
        get
        {
            var p = Volatile.Read(ref _pendingUp);
            if (p is not null) return (p.Result.X1, p.Result.Y1);
            var r = Volatile.Read(ref _result);
            return r is null ? null : (r.Value.X1, r.Value.Y1);
        }
    }
    /// <summary>チャタリングの記録（1件。無ければ null）</summary>
    public ChatterEvent? TakeChatter() => Interlocked.Exchange(ref _chatterNote, null);
    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    // ---- フック内部状態 ----
    private bool _hotDown;                  // キー発火のエッジ検出（自動リピート対策）
    private int _hotMdown;                  // ホットキー横ボタンの押しっぱなし
    private bool _snipDown;                 // 修正J58: 範囲ドラッグのキーのエッジ検出
    private int _snipMdown;                 // 修正J58: 範囲ドラッグのボタンの押しっぱなし
    private bool _kdDown;                   // 開始キーのエッジ検出（自動リピート対策）
    private IntPtr _hook, _khook;           // WH_MOUSE_LL / WH_KEYBOARD_LL
    private readonly HookProc _mouseCb;     // GC で collect されないよう生命期ぶん生存
    private readonly HookProc _keyCb;

    private readonly ManualResetEventSlim _want = new(false);
    private readonly ManualResetEventSlim _quit = new(false);
    private Thread? _thread;

    public QuickInputWatcher()
    {
        _mouseCb = MouseProc;
        _keyCb = KeyProc;
    }

    /// <summary>スレッドを起こす（フック自体は設定があるときだけ張る）。</summary>
    public void Start()
    {
        if (_thread is not null) return;
        // 修正J34: 低レベルフックは応答が遅れると OS に黙って外される（LowLevelHooksTimeout）。
        // 内蔵 OCR などで CPU が忙しくても先に回るよう、このスレッドだけ優先度を上げる（中身はすぐ返る処理だけ）
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "QuickInput", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    // ---------------- 設定反映 ----------------

    /// <summary>設定を即時反映（保存ボタンを待たずにON/OFF・割当変更・登録ができる）。
    /// hotkey / kdrag は正規形バインド文字列（""=未設定）。kdrag は「キーを押している
    /// あいだマウスで四角を描く」起動（両手操作）で、キー以外は受け付けない。
    /// クイックぶん投げの起動と同じマウスボタンはドラッグが優先なので発火待ちにしない。</summary>
    public void Apply(bool enabled, string button, string hotkey = "", string kdrag = "", string snip = "")
    {
        _mask = enabled ? Binding.QuickButtons(button) : Binding.QuickButtons("off");
        SetHotkey(hotkey);
        SetKdrag(enabled ? kdrag : "");
        SetSnipHotkey(snip);
        Resync();
    }

    public void SetHotkey(string? hotkey)
    {
        var tok = Binding.Normalize(hotkey);
        var h = tok.Length > 0 ? Binding.Parse(tok) : null;
        if (h is not null && h.Value.IsMouse && _mask.Contains(h.Value.Button))
            h = null;          // ドラッグ起動と同じボタン：発火待ちにしない（UIが警告）
        string newTok = h is not null ? tok : "";
        // 同じバインドなら状態（押しっぱなし・エッジ検出）に触らない。1秒ごとの設定の読み直し
        // （Apply）で毎回リセットすると、押している最中の状態が消える（FIX_ROUND5 F21）
        if (newTok == _hotTok && (h is null) == (_hot is null)) return;
        _hot = h;
        _hotTok = newTok;
        _hotDown = false;
        _hotMdown = 0;
    }

    /// <summary>いま発火待ちにしているバインド（実際有効な分だけ。""=なし）。</summary>
    public string Hotkey() => _hotTok;

    /// <summary>キー起動（長押し中マウスで四角）の割当。""=なし、キー以外のバインドもなし。</summary>
    public void SetKdrag(string? kdrag)
    {
        var tok = Binding.Normalize(kdrag);
        var h = tok.Length > 0 ? Binding.Parse(tok) : null;
        if (h is not null && h.Value.IsMouse)
        {
            h = null;   // キー起動はキーのバインドだけ受け付ける（マウス不可）
            tok = "";
        }
        if (h is not null && Equals(h, _hot))
        {
            // 同じ入力を「四角の開始」と「その場でぶん投げる」の両方にはできない（押すたびに
            // ぶん投げが走る）。開始を優先して発火待ちを外す（UI側が理由を表示する）
            AppLog.Info($"開始キーとぶん投げホットキーが同じ入力のため発火待ちを外した: {tok}");
            _hot = null;
            _hotTok = "";
            _hotDown = false;
        }
        // 同じバインドなら何もしない（押している最中に _kdDown/KdState を戻さない。F21）
        if (h is not null && _kd is not null && tok == _kdTok) return;
        _kd = h;
        _kdTok = h is not null ? tok : "";
        _kdDown = false;
        // 張り直した瞬間にキーが下がっているかは分からない。離しているものとして、
        // 次に押されたdownから起こす（下り途中のupは何もしない＝宙に浮かない）
        Volatile.Write(ref _kdState, h is not null ? KdUp : KdNone);
    }

    /// <summary>いま待っているキー起動のバインド（""=なし）。</summary>
    public string Kdrag() => _kdTok;

    /// <summary>修正J58: 範囲ドラッグのホットキーの割当（""=なし）。ぶん投げホットキーと同じ形（キーもボタンも可）で、
    /// 発火は別の合図（TakeSnipHot）。クイックの起動ボタン・キー起動・ぶん投げホットキーと同じ入力は待たない
    /// （UI側が理由を出す。先に決まっている側を優先する）</summary>
    public void SetSnipHotkey(string? snip)
    {
        var tok = Binding.Normalize(snip);
        var h = tok.Length > 0 ? Binding.Parse(tok) : null;
        if (h is not null && h.Value.IsMouse && _mask.Contains(h.Value.Button))
            h = null;          // ドラッグ起動と同じボタン
        if (h is not null && (Equals(h, _hot) || Equals(h, _kd)))
            h = null;          // ぶん投げホットキー・キー起動と同じ入力
        string newTok = h is not null ? tok : "";
        // 同じバインドなら状態（押しっぱなし・エッジ検出）に触らない（1秒ごとの Apply でリセットしない）
        if (newTok == _snipTok && (h is null) == (_snip is null)) return;
        _snip = h;
        _snipTok = newTok;
        _snipDown = false;
        _snipMdown = 0;
    }

    /// <summary>いま発火待ちにしている範囲ドラッグのバインド（実際有効な分だけ。""=なし）</summary>
    public string SnipHotkey() => _snipTok;

    /// <summary>(マウスフックが要る, キーボードフックが要る)</summary>
    private (bool Mouse, bool Key) Need()
    {
        var m = _mask.Count > 0 || _catch || (_hot is not null && _hot.Value.IsMouse)
            || (_snip is not null && _snip.Value.IsMouse);
        var k = _catch || _kd is not null || (_hot is not null && !_hot.Value.IsMouse)
            || (_snip is not null && !_snip.Value.IsMouse);
        return (m, k);
    }

    private void Resync()
    {
        var (m, k) = Need();
        if (m || k)
            _want.Set();
        else
        {
            _want.Reset();
            UnhookAll();
        }
    }

    public bool Enabled() => _want.IsSet;

    public RectResult? TakeResult()
    {
        var r = Interlocked.Exchange(ref _result, null);
        if (r is not null) return r.Value;
        // 預かっている離しは ChatterMs たってから確定（その前にフックが押し直しで取り消すことがある）
        var p = Volatile.Read(ref _pendingUp);
        if (p is not null && NowMs() - p.Ms >= _chatterMs &&
            ReferenceEquals(Interlocked.CompareExchange(ref _pendingUp, null, p), p))
            return p.Result;
        return null;
    }

    public string? TakeHot() => Interlocked.Exchange(ref _hotFire, null);
    public string? TakeSnipHot() => Interlocked.Exchange(ref _snipFire, null);

    // ---------------- 入力「登録」 ----------------

    /// <summary>押された入力（マウスボタン／キー＋修飾キー）を次1個だけ記録する状態にする。
    /// 握って記録するので、登録の押し手が桌面やブラウザへ漏れない。</summary>
    public void StartCatch()
    {
        Volatile.Write(ref _captured, null);
        Interlocked.Exchange(ref _catchUntilMs, NowMs() + CatchHookTimeoutMs);
        _catch = true;
        Volatile.Write(ref _drag, null);    // 登録開始時点で押しっぱなしなら、その追跡は打ち切る
        Volatile.Write(ref _pendingUp, null);   // 預かり中の離しも捨てる（登録の後でぶん投げが始まらない）
        _kdDown = false;        // 登録中に押すキーで四角を描き始めない
        _want.Set();
    }

    /// <summary>登録待ちをやめる（記録済みがあっても捨てる）。適用は呼び手のApplyに任せる。</summary>
    public void StopCatch()
    {
        _catch = false;
        Volatile.Write(ref _captured, null);
        _kdDown = false;
        Volatile.Write(ref _kdState, _kd is not null ? KdUp : KdNone);
    }

    public string? TakeCaptured() => Interlocked.Exchange(ref _captured, null);

    public void Stop(int timeoutMs = 1500)
    {
        _quit.Set();
        _want.Reset();
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            try { _thread.Join(timeoutMs); } catch { }
        }
        UnhookAll();
    }

    // ---------------- マウスフック ----------------

    internal IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var cmd = wParam.ToInt32();
                if (cmd is WmXButtonDown or WmXButtonUp or WmMButtonDown or WmMButtonUp)
                {
                    var down = cmd is WmXButtonDown or WmMButtonDown;
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int btn;
                    if (cmd is WmMButtonDown or WmMButtonUp)
                        btn = Binding.MButton;      // 中央はmouseDataに値が入らないのでmessageで決める
                    else
                    {
                        // 実測: MSLLHOOKSTRUCT.mouseDataのXBUTTON値は**上位word**に入っている
                        // （injectedでも md=0x00010000 / md高=1）。WM_XBUTTON*のwParamと同じ型。
                        // 下位wordに入ってくる経路もあるため両方見て判定する
                        var md = ms.mouseData;
                        var btnHi = (int)((md >> 16) & 0xFFFF);
                        var btnLo = (int)(md & 0xFFFF);
                        btn = btnHi is Binding.XButton1 or Binding.XButton2 ? btnHi : btnLo;
                    }
                    if (CatchLive())
                    {
                        // 「登録」待ち: 割り当て当前かどうかに関係なく握って記録だけする。
                        // down/upどちらも握るので、登録の押し手が桌面へ漏れない
                        if (down && Captured is null)
                        {
                            Volatile.Write(ref _captured, MouseTok(btn));
                            Interlocked.Increment(ref _downCount);
                        }
                        return (IntPtr)1;
                    }
                    var h = _hot;
                    if (h is not null && h.Value.IsMouse && btn == h.Value.Button)
                    {
                        // ぶん投げホットキーに割り当てたボタン（四角を描く起動とは別の入力なので
                        // ここで発火させる）
                        if (down)
                        {
                            if (Drag is null)
                            {
                                _hotMdown = btn;
                                Volatile.Write(ref _hotFire, _hotTok);
                                Interlocked.Increment(ref _downCount);
                                return (IntPtr)1;          // 握る（元の発火をさせない）
                            }
                        }
                        else if (_hotMdown == btn)
                        {
                            _hotMdown = 0;
                            return (IntPtr)1;
                        }
                    }
                    var sn = _snip;
                    if (sn is not null && sn.Value.IsMouse && btn == sn.Value.Button)
                    {
                        // 修正J58: 範囲ドラッグのホットキーに割り当てたボタン（ぶん投げホットキーと同じ扱い）
                        if (down)
                        {
                            if (Drag is null)
                            {
                                _snipMdown = btn;
                                Volatile.Write(ref _snipFire, _snipTok);
                                Interlocked.Increment(ref _downCount);
                                return (IntPtr)1;
                            }
                        }
                        else if (_snipMdown == btn)
                        {
                            _snipMdown = 0;
                            return (IntPtr)1;
                        }
                    }
                    if (down)
                    {
                        long now = NowMs();
                        bool promoted = false;
                        var pu = Interlocked.Exchange(ref _pendingUp, null);
                        if (pu is not null)
                        {
                            long gap = now - pu.Ms;
                            if (pu.Button == btn && gap <= _chatterMs)
                            {
                                // チャタリング: 離しは無かったことにして、元の始点のまま描き続ける
                                Volatile.Write(ref _drag, new Box<DragStart>(new DragStart(pu.Result.X0, pu.Result.Y0, btn)));
                                Volatile.Write(ref _chatterNote, new ChatterEvent(true, gap, _chatterMs));
                                _lastUpMs = long.MinValue;
                                Interlocked.Increment(ref _downCount);
                                return (IntPtr)1;
                            }
                            // 預かりの時間は過ぎていた（UIがまだ拾っていない）: 離しは確定
                            Volatile.Write(ref _result, new Box<RectResult>(pu.Result));
                            promoted = true;
                        }
                        // すでにドラッグ中のセカンドクリック（別ボタン）は素通し。
                        // キーで四角を描いているあいだのクリックも素通し（二重に追わない）
                        if (Drag is null && KdState != KdDown && _mask.Contains(btn))
                        {
                            if (btn == _lastUpBtn && _lastUpMs != long.MinValue && now - _lastUpMs < SuspectRepressMs)
                            {
                                // 間に合わなかった押し直し（離して確定した直後にまた押された）。
                                // 預かる時間を決める手がかりとして残す
                                Volatile.Write(ref _chatterNote, new ChatterEvent(false, now - _lastUpMs, _chatterMs));
                            }
                            _lastUpMs = long.MinValue;
                            Volatile.Write(ref _drag, new Box<DragStart>(new DragStart(ms.x, ms.y, btn)));
                            if (!promoted) Volatile.Write(ref _result, null);
                            Interlocked.Increment(ref _downCount);
                            return (IntPtr)1;     // 握る（戻る/進む・ホイールの自動スクロールを出さない）
                        }
                    }
                    else
                    {
                        var d = Drag;
                        if (d is not null && btn == d.Value.Button)
                        {
                            // 修正J46: 終点はいまのカーソル（ダミー枠が追っているのと同じ取り方）。離したイベントの座標は
                            // カーソルと数px食い違うことがあり（実測 左4px・下9px）、見えていた枠より広がって確定していた
                            var (ex, ey) = UpCursor() ?? (ms.x, ms.y);
                            var res = new RectResult(d.Value.X0, d.Value.Y0, ex, ey);
                            long now = NowMs();
                            _lastUpMs = now;
                            _lastUpBtn = btn;
                            // 修正J36: 離しの結果（預かり）を先に書いてから _drag を消す。逆だと、その隙間に UI の
                            // Poll が入ると「ドラッグも離しも無い＝up を取り損ねた」と見て四角を取り消していた
                            if (_chatterMs > 0)
                                Volatile.Write(ref _pendingUp, new PendingUp(res, btn, now));
                            else
                                Volatile.Write(ref _result, new Box<RectResult>(res));
                            Volatile.Write(ref _drag, null);
                            Interlocked.Increment(ref _upCount);
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            AppLog.Error("入力フック（マウス）で想定外", e);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ---------------- キーボードフック ----------------

    /// <summary>いま下がっている修飾キー（イベント時に読む。状態管理を持たない分ズレない）。</summary>
    private int Mods()
    {
        var m = 0;
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0) m |= Binding.ModCtrl;
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0) m |= Binding.ModShift;
        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0) m |= Binding.ModAlt;
        if ((GetAsyncKeyState(VkLwin) & 0x8000) != 0 || (GetAsyncKeyState(VkRwin) & 0x8000) != 0)
            m |= Binding.ModWin;
        return m;
    }

    internal IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && lParam != IntPtr.Zero)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var vk = (int)kb.vkCode;
                var up = (kb.flags & LlkfUp) != 0 ||
                         wParam.ToInt32() is WmKeyUp or WmSysKeyUp;
                if (CatchLive())
                {
                    // 「登録」待ち: 全てのキー入力を握る（押したキーが他のアプリへ漏れないため）。
                    // 修飾キーだけでまだ確定しないので記録しない。Escもここで握り、UI側が
                    // 「中止」として処理する
                    if (!up && Captured is null && !IsModVk(vk))
                    {
                        Volatile.Write(ref _captured, Binding.Format(Mods(), vk));
                        Interlocked.Increment(ref _downCount);
                    }
                    return (IntPtr)1;
                }
                var h = _hot;
                if (h is not null && !h.Value.IsMouse && vk == h.Value.Vk)
                {
                    if (up)
                    {
                        if (_hotDown)
                        {
                            _hotDown = false;
                            return (IntPtr)1;
                        }
                    }
                    else if (_hotDown)
                        return (IntPtr)1;         // 自動リピート（押しっぱなし）も握る
                    else if (Mods() == h.Value.Mods)
                    {
                        // 修飾キーまで完全に一致したときだけ発火（Ctrl+F8を設定して
                        // いて Ctrl+Alt+F8 を押したら素通し、という判定）
                        _hotDown = true;
                        Volatile.Write(ref _hotFire, _hotTok);
                        Interlocked.Increment(ref _downCount);
                        return (IntPtr)1;
                    }
                    // 不一致（他の修飾キーが下がっている等）は素通ししてそのキーを各アプリに渡す
                }
                var sn = _snip;
                if (sn is not null && !sn.Value.IsMouse && vk == sn.Value.Vk)
                {
                    // 修正J58: 範囲ドラッグのホットキー（ぶん投げホットキーと同じ。押した縁で1回・リピートと離しは握る）
                    if (up)
                    {
                        if (_snipDown)
                        {
                            _snipDown = false;
                            return (IntPtr)1;
                        }
                    }
                    else if (_snipDown)
                        return (IntPtr)1;
                    else if (Mods() == sn.Value.Mods)
                    {
                        _snipDown = true;
                        Volatile.Write(ref _snipFire, _snipTok);
                        Interlocked.Increment(ref _downCount);
                        return (IntPtr)1;
                    }
                    // 修飾キーの不一致は素通し
                }
                var kd = _kd;
                if (kd is not null && !kd.Value.IsMouse && vk == kd.Value.Vk)
                {
                    // 四角を描く起動（左手でキーを押し、右手でマウスを動かす）。
                    // 押しているあいだだけ握るので、そのキーの通常の発火は起きない
                    if (up)
                    {
                        if (_kdDown)
                        {
                            _kdDown = false;
                            // 離した: UI側が現在のポインタを対角にして確定する
                            Volatile.Write(ref _kdState, KdUp);
                            Interlocked.Increment(ref _upCount);
                            return (IntPtr)1;
                        }
                    }
                    else if (_kdDown)
                        return (IntPtr)1;         // 自動リピート（押しっぱなし）も握る
                    else if (Drag is null && Mods() == kd.Value.Mods)
                    {
                        _kdDown = true;
                        // 押した: UI側が現在のポインタを基点に窓を消す（ここは位置を知らない）
                        Volatile.Write(ref _kdState, KdDown);
                        Interlocked.Increment(ref _downCount);
                        return (IntPtr)1;
                    }
                    // 修飾キーが不一致、またはマウスで四角を描き中 → 素通し
                }
            }
        }
        catch (Exception e)
        {
            AppLog.Error("入力フック（キーボード）で想定外", e);
        }
        return CallNextHookEx(_khook, nCode, wParam, lParam);
    }

    private static bool IsModVk(int vk) => vk is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xA6 or 0xA7;

    // ---------------- フックの張替え ----------------

    private void Install()
    {
        var (m, k) = Need();
        if (m && _hook == IntPtr.Zero)
        {
            try
            {
                var hhk = SetWindowsHookExW(WhMouseLl, _mouseCb, GetModuleHandleW(null), 0);
                _hook = hhk;
                if (hhk == IntPtr.Zero)
                {
                    LastError = $"SetWindowsHookExW(mouse) err={Marshal.GetLastWin32Error()}";
                    AppLog.Warning($"クイック入力フックが張れない: {LastError}");
                }
            }
            catch (Exception e)
            {
                _hook = IntPtr.Zero;
                LastError = e.ToString();
                AppLog.Error("クイック入力フック設定で例外", e);
            }
        }
        if (k && _khook == IntPtr.Zero)
        {
            try
            {
                var hhk = SetWindowsHookExW(WhKeyboardLl, _keyCb, GetModuleHandleW(null), 0);
                _khook = hhk;
                if (hhk == IntPtr.Zero)
                {
                    LastError = $"SetWindowsHookExW(key) err={Marshal.GetLastWin32Error()}";
                    AppLog.Warning($"ホットキーフックが張れない: {LastError}");
                }
            }
            catch (Exception e)
            {
                _khook = IntPtr.Zero;
                LastError = e.ToString();
                AppLog.Error("ホットキーフック設定で例外", e);
            }
        }
    }

    /// <summary>必要なくらいなら張れていなくても可。張るべきものが全部張られていればtrue。</summary>
    private bool Live()
    {
        var (m, k) = Need();
        return (!m || _hook != IntPtr.Zero) && (!k || _khook != IntPtr.Zero);
    }

    /// <summary>不用になった方のフックだけ剥がす（ONのまま hotkey を解除したときなど）。</summary>
    private void Drop(string which)
    {
        if (which == "m" && _hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); }
            catch (Exception e) { AppLog.Error("クイック入力フック解除で例外", e); }
            _hook = IntPtr.Zero;
            Volatile.Write(ref _drag, null);
            Volatile.Write(ref _result, null);
            Volatile.Write(ref _pendingUp, null);   // OFF にした直後に預かりの離しが確定しない
            _hotMdown = 0;
            _snipMdown = 0;
        }
        else if (which == "k" && _khook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_khook); }
            catch (Exception e) { AppLog.Error("ホットキーフック解除で例外", e); }
            _khook = IntPtr.Zero;
            _hotDown = false;
            _snipDown = false;
        }
    }

    private void UnhookAll()
    {
        Drop("m");
        Drop("k");
    }

    private void RunLoop()
    {
        while (!_quit.IsSet)
        {
            if (!_want.IsSet)
            {
                UnhookAll();
                if (_quit.Wait(200)) break;
                continue;
            }
            // 不用になった方だけ剥がす（ONのまま hotkey を解除したときなど）
            var (m, k) = Need();
            if (!m) Drop("m");
            if (!k) Drop("k");
            Install();
            if (!Live())
            {
                // 競合・権限等で張れない。アプリ本体は動くので1秒後に再試行
                if (_quit.Wait(1000)) break;
                continue;
            }
            // フックの配布にはメッセージ取得が必須。20ms周期でON/OFF・終了も見る
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 20, QsAllInput, MwmoInputAvailable);
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                if (msg.message == WmQuit)
                {
                    _quit.Set();
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        UnhookAll();
    }
}

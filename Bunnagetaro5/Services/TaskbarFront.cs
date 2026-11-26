using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bunnagetaro5.Services;

/// <summary>
/// 修正J72: Windows のタスクバーは、アクティブな窓が変わると自分を最前面の一番手前へ出し直す。
/// そのため、窓を動かし終わった時（キャプチャ窓・解答欄がアクティブになる）などに、手前へ出し直した
/// バー・解答欄が約 100ms 後にまたタスクバーの裏へ回っていた。
/// このアプリの窓がアクティブになった時だけ、しばらく（WatchMs）の間タスクバーより手前を保つ。
/// 人がタスクバーを押した時はタスクバーがアクティブになるので何もしない（裏に回ったままでよい。人の判断 2026-11-22）。
/// 修正J73: 決まった時間（150ms・450ms）に出し直すと、その間「裏→手前」を行き来してちらついた。
/// 見張る間は専用スレッドが画面の1フレームごと（DwmFlush）に重なり順を見て、タスクバーが手前に来たらその場で戻す。
/// 見張りの開始は UI スレッドで作った SetWinEventHook（WINEVENT_OUTOFCONTEXT）から。
/// 修正J74: 見張りの途中でも、アクティブな窓がこのアプリでなくなったら（タスクバーを押した等）すぐやめる。
/// 前は 800ms の間はタスクバーを押しても手前へ戻していた（人の操作を外から記録して分かった）。
/// </summary>
internal sealed class TaskbarFront : IDisposable
{
    /// <summary>アクティブになってから手前を保つ長さ（ms）。タスクバーが自分を出し直すのは 100ms 前後の間に1〜2回</summary>
    internal const int WatchMs = 800;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private readonly Func<IEnumerable<IntPtr>> _windows;
    private readonly WinEventProc _proc;   // GC されないよう保持
    private readonly IntPtr _hook;
    private readonly uint _pid = (uint)Environment.ProcessId;
    private readonly object _lk = new();
    private IntPtr[] _targets = Array.Empty<IntPtr>();
    private long _until;                   // 見張りを終える時刻（Stopwatch の目盛り）
    private Thread? _thread;
    private volatile bool _disposed;

    /// <summary>修正J74: アクティブな窓がこのアプリの窓か。テストでは差し替える（テストの間はほかのアプリがアクティブなことがある）</summary>
    internal volatile Func<bool> AppIsForeground;

    private bool RealAppIsForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == _pid;
    }

    /// <summary>直近の見張りで、タスクバーが手前に来たのを戻した回数（テスト・調べ用）</summary>
    internal int LastRaises { get; private set; }

    /// <summary>見張っている最中か（テスト用）</summary>
    internal bool Watching { get { lock (_lk) return _thread is { IsAlive: true }; } }

    /// <summary>windows: 手前を保つ候補（下→上の順: キャプチャ窓・窓の中の解答欄・バー・外の解答欄。UI スレッドで呼ぶ）</summary>
    internal TaskbarFront(Func<IEnumerable<IntPtr>> windows)
    {
        _windows = windows;
        _proc = OnEvent;
        AppIsForeground = RealAppIsForeground;
        try
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero) AppLog.Warning("TaskbarFront: SetWinEventHook に失敗（タスクバーより手前へ出し直すのは置いた時だけ）");
        }
        catch (Exception ex) { AppLog.Error("TaskbarFront: hook", ex); }
    }

    /// <summary>アクティブになった窓 hwnd がこのアプリの窓なら、しばらく手前を保つ（UI スレッドから呼ぶ）</summary>
    internal void OnForeground(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _pid)
        {
            // 修正J74: タスクバーやほかのアプリがアクティブになったら、見張りの途中でもやめる（タスクバーを押したら裏に回る）
            lock (_lk) _until = 0;
            return;
        }
        IntPtr[] targets;
        try { targets = _windows().Where(h => h != IntPtr.Zero).ToArray(); }
        catch (Exception ex) { AppLog.Error("TaskbarFront: targets", ex); return; }
        if (targets.Length == 0) return;
        lock (_lk)
        {
            _targets = targets;
            _until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * WatchMs / 1000;
            if (_thread is { IsAlive: true }) return;   // 見張り中なら延ばすだけ
            LastRaises = 0;
            _thread = new Thread(Watch) { IsBackground = true, Name = "TaskbarFront" };
            _thread.Start();
        }
    }

    private void Watch()
    {
        try
        {
            while (!_disposed)
            {
                IntPtr[] targets;
                lock (_lk)
                {
                    if (Stopwatch.GetTimestamp() >= _until) return;
                    targets = _targets;
                }
                // 修正J74: アクティブな窓がこのアプリでなくなったらやめる（フックの知らせより先に気づけるよう毎フレーム見る）
                if (!AppIsForeground()) { lock (_lk) _until = 0; return; }
                var trays = Trays();
                var toRaise = new List<IntPtr>(targets.Length);
                bool trayAboveAny = false;
                bool anyLower = false;
                foreach (var h in targets)
                {
                    if (!IsWindow(h) || !IsWindowVisible(h) || !Native.GetWindowRect(h, out var r))
                        continue;
                    bool outside = Native.OutsideWorkArea(r);
                    if (!outside && !anyLower) continue;
                    toRaise.Add(h);
                    anyLower = true;
                    if (outside && TrayAbove(h, trays)) trayAboveAny = true;
                }
                if (trayAboveAny)
                {
                    // 修正J90: 下→上（キャプチャ窓 → 窓の中の解答欄 → バー → 外の解答欄）の順で手前へ出し、
                    // 下側の窓を持ち上げた時に上側の可視窓（解答欄・バー）より手前に来てクリックを奪わないようにする
                    foreach (var h in toRaise)
                    {
                        Native.SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);
                        LastRaises++;
                    }
                }
                try { if (Native.DwmFlush() != 0) Thread.Sleep(4); }
                catch { Thread.Sleep(4); }
            }
        }
        catch (Exception ex) { AppLog.Error("TaskbarFront: watch", ex); }
    }

    /// <summary>タスクバーの窓（主・ほかのモニタ）</summary>
    private static HashSet<IntPtr> Trays()
    {
        var set = new HashSet<IntPtr>();
        var t = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (t != IntPtr.Zero) set.Add(t);
        var s = IntPtr.Zero;
        while ((s = FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) set.Add(s);
        return set;
    }

    /// <summary>重なり順で h より手前にタスクバーがいるか</summary>
    private static bool TrayAbove(IntPtr h, HashSet<IntPtr> trays)
    {
        if (trays.Count == 0) return false;
        for (var p = GetWindow(h, GW_HWNDPREV); p != IntPtr.Zero; p = GetWindow(p, GW_HWNDPREV))
            if (trays.Contains(p)) return true;
        return false;
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { if (idObject == 0) OnForeground(hwnd); }
        catch (Exception ex) { AppLog.Error("TaskbarFront: event", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_hook != IntPtr.Zero) UnhookWinEvent(_hook); } catch { }
        Thread? th;
        lock (_lk) th = _thread;
        th?.Join(200);
    }

    private const uint GW_HWNDPREV = 3;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
}

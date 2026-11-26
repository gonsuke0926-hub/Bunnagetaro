using System.Security.Principal;
using System.Threading;

namespace Bunnagetaro5.Services;

/// <summary>
/// 多重起動防止（G4）。ユーザーごとの名前付き Mutex（Local\Bunnagetaro5-&lt;ユーザーSID&gt;）を取り、
/// 2つ目以降はすでに動いているメイン窓を前に出して終了する。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string ActivateMessageName = "Bunnagetaro5.ActivateMainWindow";
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>ユーザーごとの Mutex 名（Local\Bunnagetaro5-&lt;ユーザーSID&gt;）。</summary>
    public static string BuildMutexName(string? sid = null)
    {
        string? id = sid;
        if (string.IsNullOrEmpty(id))
        {
            try { id = WindowsIdentity.GetCurrent().User?.Value; } catch { }
        }
        if (string.IsNullOrEmpty(id))
            id = Environment.UserName;
        return $"Local\\Bunnagetaro5-{id}";
    }

    /// <summary>Mutex の取得を試みる。既に別インスタンスが持っていれば null。</summary>
    public static SingleInstanceGuard? TryAcquire(string? mutexName = null)
    {
        string name = string.IsNullOrEmpty(mutexName) ? BuildMutexName() : mutexName;
        var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
        if (createdNew)
            return new SingleInstanceGuard(mutex);

        // 前の持ち主が解放せず終了していた場合は WaitOne(0) で引き取れる
        try
        {
            if (mutex.WaitOne(0))
                return new SingleInstanceGuard(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>すでに動いているメイン窓を探して最小化解除＋最前面へ出す。</summary>
    public static bool ActivateExisting(string windowTitle)
    {
        IntPtr hwnd = Native.FindWindow(null, windowTitle);
        if (hwnd == IntPtr.Zero) return false;

        if (Native.IsIconic(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        Native.SetForegroundWindow(hwnd);

        int msg = Native.RegisterWindowMessage(ActivateMessageName);
        if (msg != 0)
            Native.PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public void Dispose()
    {
        var m = Interlocked.Exchange(ref _mutex, null);
        if (m is null) return;
        try { m.ReleaseMutex(); } catch { }
        m.Dispose();
    }
}

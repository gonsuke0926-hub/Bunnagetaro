using System.IO;
using System.Text;

namespace Bunnagetaro5.Services;

/// <summary>
/// app.log / crash.log への記録。1MBで回転・3世代（app.log, app.log.1, app.log.2）。
/// 形式は「yyyy-MM-dd HH:mm:ss,fff LEVEL 本文」。
/// Windowsでは別プロセスがapp.logを開いているとリネームできないので、
/// 回れない時は黙ってそのまま書き続ける。
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private const int Keep = 2; // app.log.1 と app.log.2 を残す

    private static readonly object Gate = new();
    private static string _file = "";
    private static string _crashFile = "";
    private static bool _ready;

    public static void Init(Paths p)
    {
        lock (Gate)
        {
            _file = p.AppLogFile;
            _crashFile = p.CrashLog;
            try { Directory.CreateDirectory(p.Root); } catch { }
            _ready = true;
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warning(string msg) => Write("WARNING", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex is null ? msg : msg + "\n" + ex);

    /// <summary>異常終了の記録（crash.log）。回転なし・追記だけ。</summary>
    public static void Crash(string text)
    {
        lock (Gate)
        {
            if (!_ready) Init(new Paths());
            try { File.AppendAllText(_crashFile, text, new UTF8Encoding(false)); }
            catch { }
        }
    }

    private static void Write(string level, string msg)
    {
        lock (Gate)
        {
            if (!_ready) Init(new Paths());
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {msg}\n";
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(_file, line, new UTF8Encoding(false));
            }
            catch
            {
                // ログの失敗でアプリを止めない
            }
        }
    }

    private static void RotateIfNeeded(int incomingBytes)
    {
        try
        {
            var fi = new FileInfo(_file);
            if (!fi.Exists || fi.Length + incomingBytes < MaxBytes) return;
            for (int i = Keep; i >= 1; i--)
            {
                string src = i == 1 ? _file : _file + "." + (i - 1);
                if (File.Exists(src))
                    File.Move(src, _file + "." + i, overwrite: true);
            }
        }
        catch (IOException)
        {
            // 古いexe等が掴んでいて回れない。次回の機会に任せる
        }
    }
}

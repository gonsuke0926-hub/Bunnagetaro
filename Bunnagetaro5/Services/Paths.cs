using System.IO;

namespace Bunnagetaro5.Services;

/// <summary>
/// データの置き場所。このアプリが読み書きするのは Bunnagetaro5 だけ。
/// </summary>
public sealed class Paths
{
    public string Root { get; }      // %APPDATA%\Bunnagetaro5

    public Paths(string? root = null)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = root ?? Path.Combine(appData, "Bunnagetaro5");
    }

    public string ConfigJson => Path.Combine(Root, "config.json");
    public string ConfigBak => ConfigJson + ".bak";
    public string LogsDir => Path.Combine(Root, "logs");
    public string EntriesJson => Path.Combine(LogsDir, "entries.json");
    public string ImagesDir => Path.Combine(LogsDir, "images");
    public string DefaultShotsDir => Path.Combine(Root, "shots");
    public string AppLogFile => Path.Combine(Root, "app.log");
    public string CrashLog => Path.Combine(Root, "crash.log");
}

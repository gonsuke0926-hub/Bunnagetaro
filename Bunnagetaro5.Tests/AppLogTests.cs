using System.IO;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>app.log の記録と1MB回転（3世代）・crash.log。</summary>
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public AppLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_log_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void WritesTimestampLevelAndMessage()
    {
        AppLog.Info("テスト本文");
        string last = File.ReadAllLines(_paths.AppLogFile)[^1];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3} INFO テスト本文$", last);
    }

    [Fact]
    public void RotatesAtOneMegabyte_KeepingThreeGenerations()
    {
        string line = new('x', 20_000);
        for (int i = 0; i < 55; i++) AppLog.Info(line); // 約1.1MB → 1回回転
        Assert.True(File.Exists(_paths.AppLogFile));
        Assert.True(File.Exists(_paths.AppLogFile + ".1"));
        Assert.False(File.Exists(_paths.AppLogFile + ".2"));
        for (int i = 0; i < 55; i++) AppLog.Info(line); // もう1回回転
        Assert.True(File.Exists(_paths.AppLogFile + ".2"));
        Assert.False(File.Exists(_paths.AppLogFile + ".3"));
        Assert.True(new FileInfo(_paths.AppLogFile).Length < 1_100_000);
        foreach (string f in Directory.GetFiles(_paths.Root, "app.log*"))
            Assert.True(new FileInfo(f).Length <= 1_050_000, f + " が大きすぎる");
    }

    [Fact]
    public void CrashGoesToSeparateFile()
    {
        AppLog.Crash("異常な落ち方の記録\n");
        Assert.Contains("異常な落ち方の記録", File.ReadAllText(_paths.CrashLog));
        Assert.False(File.Exists(_paths.AppLogFile)); // crash.logはapp.logに混ざらない
    }
}

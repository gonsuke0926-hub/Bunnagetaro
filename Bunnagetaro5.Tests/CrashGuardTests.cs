using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>異常時の記録（crash.log）と起動時の環境情報（env_info）。</summary>
public sealed class CrashGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly Paths _paths;

    public CrashGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bt5_crash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new Paths(Path.Combine(_dir, "new"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 異常記録は時刻とタグと内部例外とスタックを全部載せる()
    {
        Exception ex;
        try
        {
            // StackTrace は「投げられて初めて」載る。実際の未処理例外と同じ形に作る
            throw new InvalidOperationException("boom文字列", new ArgumentException("なかみboom"));
        }
        catch (Exception e) { ex = e; }
        string block = CrashGuard.Describe("unhandled(UI)", ex);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3} unhandled\(UI\)\n", block);
        Assert.Contains("boom文字列", block);
        Assert.Contains("なかみboom", block);              // ToString は内部例外も連ねる
        Assert.Contains("InvalidOperationException", block);
        Assert.Contains("at Bunnagetaro5.Tests", block);    // スタック行も残る
        Assert.EndsWith("\n\n", block);
    }

    [Fact]
    public void 環境情報はバージョンOSラムダ画面構成をenv行で並べる()
    {
        var lines = EnvInfo.Lines();
        Assert.All(lines, l => Assert.StartsWith("env ", l));
        Assert.Contains(lines, l => l.StartsWith("env app_version=" + Bunnagetaro5.Services.AppVersion.Text));
        Assert.Contains(lines, l => l.StartsWith("env runtime=.NET"));
        Assert.Contains(lines, l => l.StartsWith("env os=") && l.Length > "env os=".Length + 3);
        Assert.Contains(lines, l => l.StartsWith("env cpu=") && l.Contains("cores="));
        var ram = lines.First(l => l.StartsWith("env ram_gb="));
        Assert.True(double.TryParse(ram["env ram_gb=".Length..], out double gb) && gb > 0);
        Assert.Matches(@"env virtual=-?\d+,-?\d+ \d{3,}x\d{3,}",
                       lines.First(l => l.StartsWith("env virtual=")));
        var mons = lines.Where(l => l.StartsWith("env monitor ")).ToList();
        Assert.NotEmpty(mons);
        Assert.Equal(1, mons.Count(l => l.Contains("primary=yes")));   // 主表示は1台だけ
        Assert.All(mons, l => Assert.Matches(
            @"env monitor \d+: -?\d+,-?\d+ \d{3,}x\d{3,} work=-?\d+,-?\d+ \d{3,}x\d{3,} primary=(yes|no) dpi=\d+", l));
    }

    [Fact]
    public void 画面構成はモニタ実寸で主表示とDPIが載る()
    {
        var mons = EnvInfo.Monitors();
        Assert.NotEmpty(mons);
        Assert.Single(mons.Where(m => m.Primary));
        Assert.All(mons, m =>
        {
            Assert.True(m.W >= 640 && m.H >= 480, $"モニタ {m.W}x{m.H} が小さすぎる");
            Assert.True(m.WorkW > 0 && m.WorkH > 0);
            Assert.True(m.Dpi >= 0);   // 取れない環境は0
        });
    }

    [Fact]
    public void crashlogは追記だけで回らない()
    {
        AppLog.Crash("ひとつめの異常\n");
        AppLog.Crash("ふたつめの異常\n");
        string all = File.ReadAllText(_paths.CrashLog);
        Assert.Contains("ひとつめの異常", all);
        Assert.Contains("ふたつめの異常", all);          // 上の記録が消えない
        Assert.False(File.Exists(_paths.CrashLog + ".1"));   // 回転世代を作らない
    }
}

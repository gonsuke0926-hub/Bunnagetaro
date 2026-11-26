using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void BuildMutexName_IncludesLocalPrefixAndSid()
    {
        Assert.Equal("Local\\Bunnagetaro5-S-1-5-21-123", SingleInstanceGuard.BuildMutexName("S-1-5-21-123"));
        string actual = SingleInstanceGuard.BuildMutexName();
        Assert.StartsWith("Local\\Bunnagetaro5-", actual);
        Assert.True(actual.Length > "Local\\Bunnagetaro5-".Length);
    }

    [Fact]
    public void TryAcquire_SecondCallerFailsUntilFirstDisposes()
    {
        string name = "Local\\Bunnagetaro5-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        // 別スレッド（2つ目の起動相当）からは取得できない。
        // Task.Run だと await で空いた同じスレッドで走ることがあり、Mutex は同じスレッドなら取り直せるので
        // 時々落ちた（テストを全部流した時）。必ず新しいスレッドで試す
        static bool TryOnNewThread(string n)
        {
            bool got = false;
            var th = new Thread(() =>
            {
                using var second = SingleInstanceGuard.TryAcquire(n);
                got = second is not null;
            });
            th.Start();
            th.Join();
            return got;
        }
        Assert.False(TryOnNewThread(name));

        // 最初の持ち主が解放すれば次は取得できる
        first!.Dispose();
        Assert.True(TryOnNewThread(name));
    }
}

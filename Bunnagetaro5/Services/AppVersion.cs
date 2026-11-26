namespace Bunnagetaro5.Services;

/// <summary>
/// アプリの版。正本は csproj の &lt;Version&gt; の1か所だけ。
/// 窓の題名・バージョン情報はここから出す。README だけは手で合わせる。
/// </summary>
public static class AppVersion
{
    /// <summary>「1.0.0」の形（4つ目の数は出さない）</summary>
    public static string Text { get; } = Make();

    private static string Make()
    {
        var v = typeof(AppVersion).Assembly.GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }
}

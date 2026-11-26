using System.Runtime.InteropServices;

namespace Bunnagetaro5.Services;

/// <summary>
/// 起動時に app.log へ1行ずつ書く環境情報（指示書 7.10 env_info）。
/// Python版 tw_shots.env_info（app_version/os/machine/cpu/ram）に対応させ、
/// 指示書に合わせて「画面構成」（モニタ1台ずつと仮想デスクトップ）も足す。
/// crash.log の調査で「そのPCで何が起きたか」を突き止めるための足場。
/// </summary>
public static class EnvInfo
{
    public static IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        lines.Add("env app_version=" + (typeof(EnvInfo).Assembly.GetName().Version?.ToString() ?? "?"));
        lines.Add("env runtime=" + RuntimeInformation.FrameworkDescription);
        lines.Add("env os=" + RuntimeInformation.OSDescription);
        lines.Add("env machine=" + RuntimeInformation.OSArchitecture + " proc=" + RuntimeInformation.ProcessArchitecture);
        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        if (cpu.Length > 80) cpu = cpu[..80];   // Python版 env_info の [:80] と同じ切り方
        lines.Add($"env cpu={cpu} cores={Environment.ProcessorCount}");
        double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1);
        lines.Add($"env ram_gb={ramGb}");

        lines.Add("env dpi_awareness=" + DpiAwareness());

        // 画面構成: 仮想デスクトップ全体 → モニタ1台ずつ（解像・作業領域・主表示・DPI）
        var vd = Native.VirtualDesktop();
        lines.Add($"env virtual={vd.Left},{vd.Top} {vd.Right - vd.Left}x{vd.Bottom - vd.Top}");
        int i = 0;
        foreach (var m in Monitors())
            lines.Add($"env monitor {++i}: {m.X},{m.Y} {m.W}x{m.H} work={m.WorkX},{m.WorkY} {m.WorkW}x{m.WorkH} primary={(m.Primary ? "yes" : "no")} dpi={m.Dpi}");
        return lines;
    }

    /// <summary>プロセスのDPIモード。0=非対応 1=System 2=PerMonitor（V2含む）。取れなければ -1</summary>
    public static int DpiAwareness()
    {
        try { return GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()); }
        catch { return -1; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

    public readonly record struct Monitor(int X, int Y, int W, int H,
                                          int WorkX, int WorkY, int WorkW, int WorkH,
                                          bool Primary, int Dpi);

    /// <summary>EnumDisplayMonitors + GetMonitorInfoW。DPI取れなかった台は dpi=0</summary>
    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfoW(hMon, ref mi))
            {
                int dpi = 0;
                try
                {
                    if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out uint _) == 0)
                        dpi = (int)dx;
                }
                catch { /* shcore の無い古い環境では0のまま */ }
                list.Add(new Monitor(
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    mi.rcWork.Left, mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, dpi));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);   // デリゲートが列挙中にコレクトされないように
        return list;
    }

    // ---- P/Invoke（このファイル専用。tw_win32 に対応する物ではない） ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    private const uint MONITORINFOF_PRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}

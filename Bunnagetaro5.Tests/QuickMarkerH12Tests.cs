using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Bunnagetaro5.Input;
using Bunnagetaro5.Services;
using Xunit;
using Xunit.Abstractions;

namespace Bunnagetaro5.Tests;

/// <summary>
/// FIX_ROUND7 H12: ダミー枠を動かしている最中に線の一部が欠けないか、画面の画素で測る。
/// 白い背景窓の上へダミーを Place で大きさを変えながら動かし、窓の矩形の「枠の所」が背景の白のままの画素
/// （＝欠け）と「中」が白でない画素（＝形が古い）を数える。WDA_EXCLUDEFROMCAPTURE はこのテストの中だけで外す。
/// マウス・キーは注入しない。窓は約2秒だけ出る。
/// </summary>
public class QuickMarkerH12Tests
{
    private const int Step = 25;
    private readonly ITestOutputHelper _out;
    public QuickMarkerH12Tests(ITestOutputHelper o) { _out = o; }

    // ---- Win32（測る用） ----
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wp, lp; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BMI { public int Size, W, H; public short Planes, Bpp; public int Comp, SizeImg, XPpm, YPpm, ClrUsed, ClrImp; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint rm);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr prm);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BMI bmi, uint usage, out IntPtr bits, IntPtr sec, uint off);

    private sealed class Backdrop : IDisposable
    {
        public IntPtr Hwnd;
        private readonly Thread _th;
        private volatile bool _stop;
        public Backdrop(int x, int y, int w, int h)
        {
            var ready = new ManualResetEventSlim();
            _th = new Thread(() =>
            {
                // SS_WHITERECT(6): 白で塗るだけの窓。TOPMOST|TOOLWINDOW|NOACTIVATE|TRANSPARENT
                Hwnd = CreateWindowEx(0x8 | 0x80 | 0x08000000 | 0x20, "STATIC", "", 0x80000000 | 0x10000000 | 0x6, x, y, w, h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                ready.Set();
                while (!_stop)
                {
                    while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
                    Thread.Sleep(5);
                }
                DestroyWindow(Hwnd);
            }) { IsBackground = true };
            _th.SetApartmentState(ApartmentState.STA);
            _th.Start();
            ready.Wait(2000);
        }
        public void Dispose() { _stop = true; _th.Join(1000); }
    }

    private sealed class Result
    {
        public int Samples, Skipped, DefectSamples, MissingPx, ExtraPx, MaxMissing;
        public override string ToString() =>
            $"samples={Samples} skipped(torn read)={Skipped} defectSamples={DefectSamples} missingRingPx(total)={MissingPx} maxMissingInOneSample={MaxMissing} extraInnerPx(total)={ExtraPx}";
    }

    /// <summary>白い背景の上で、ダミーを大きさを変えながら動かしている間の画面を読み、欠け・はみ出しを数える</summary>
    private Result Measure(int border, bool leftTopDrag, bool shrinking = false, int ms = 2500)
    {
        using var bg = new Backdrop(100, 100, 1300, 800);
        Assert.NotEqual(IntPtr.Zero, bg.Hwnd);
        Thread.Sleep(150);
        var m = new QuickMarker(0x00, 0x67, 0xC0, border);
        var res = new Result();
        try
        {
            m.SetStyle(0x00, 0x67, 0xC0, 0xFF, border);
            Assert.True(m.StartExternal(700, 550, 200, 150));
            // 写る状態にする（この測定の中だけ。アプリのコードには残さない）
            Native.SetWindowDisplayAffinity(m.Hwnd, Native.WDA_NONE);
            Thread.Sleep(200);
            using var cts = new CancellationTokenSource();
            var driver = new Thread(() =>
            {
                int i = 0, dir = 1;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!cts.IsCancellationRequested)
                {
                    // 左・上の辺を引く: 右下は固定、左上が動く（大きさが毎回変わる）
                    int d = i;                                   // 0..300
                    int x = 900 - 200 - d, y = 700 - 150 - d * 3 / 4, w = 200 + d, h = 150 + d * 3 / 4;
                    if (!leftTopDrag) { x = 400; y = 300; w = 200 + d; h = 150 + d * 3 / 4; }
                    m.Place(x, y, w, h);
                    i += dir * Step;
                    if (i >= 500) dir = -1;
                    if (i <= 0) dir = 1;
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            driver.Start();

            // 背景窓の全面を読み、白でない画素の外接矩形を枠の外縁とみなして、枠が欠けなく・中が白かを調べる
            // （窓の矩形の取得と画面の合成の前後ずれに左右されないよう、窓の矩形は使わない）
            const int BX = 100, BY = 100, BW = 1300, BH = 800;
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            var bmi = new BMI { Size = 40, W = BW, H = -BH, Planes = 1, Bpp = 32 };
            IntPtr bmp = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mem, bmp);
            var px = new int[BW * BH];
            var px2 = new int[BW * BH];
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                BitBlt(mem, 0, 0, BW, BH, screen, BX, BY, 0x00CC0020 | 0x40000000);   // SRCCOPY | CAPTUREBLT
                Marshal.Copy(bits, px, 0, BW * BH);
                // 画面の読み取り自体が画面の更新の途中で切れる（上下で別のフレーム）ことがあるので、2回読んで同じ時だけ使う
                BitBlt(mem, 0, 0, BW, BH, screen, BX, BY, 0x00CC0020 | 0x40000000);
                Marshal.Copy(bits, px2, 0, BW * BH);
                if (!px.AsSpan().SequenceEqual(px2)) { res.Skipped++; continue; }
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int y = 0; y < BH; y++)
                    for (int x = 0; x < BW; x++)
                    {
                        int c = px[y * BW + x];
                        if ((c & 0xFF) >= 0xF8 && ((c >> 8) & 0xFF) >= 0xF8 && ((c >> 16) & 0xFF) >= 0xF8) continue;
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                if (maxX < 0) { res.Skipped++; continue; }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w < 2 * border + 2 || h < 2 * border + 2) { res.Skipped++; continue; }
                res.Samples++;
                int missing = 0, extra = 0;
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        bool ring = x - minX < border || y - minY < border || maxX - x < border || maxY - y < border;
                        int c = px[y * BW + x];
                        bool white = (c & 0xFF) >= 0xF8 && ((c >> 8) & 0xFF) >= 0xF8 && ((c >> 16) & 0xFF) >= 0xF8;
                        if (ring && white) missing++;
                        else if (!ring && !white) extra++;
                    }
                if (missing > 0 || extra > 0) res.DefectSamples++;
                res.MissingPx += missing; res.ExtraPx += extra;
                if (missing > res.MaxMissing) res.MaxMissing = missing;
            }
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            cts.Cancel();
            driver.Join(500);
        }
        finally { m.Dispose(); }
        return res;
    }

    [Fact]
    public void H12_測定_大きさを変えながら動かす間に枠の線が欠けない()
    {
        foreach (int b in new[] { 4, 1 })
        {
            var r = Measure(b, leftTopDrag: true);
            _out.WriteLine($"H12 border={b}px 左上を引く: {r}");
            Console.WriteLine($"H12 border={b}px 左上を引く: {r}");
            // 他のテストの窓が背景に重なった等で標本が取れない時は判定しない（環境依存の測定）
            if (r.Samples < 20) { _out.WriteLine("標本が少ないので判定しない"); continue; }
            Assert.Equal(0, r.DefectSamples);
        }
    }

    // ---- 枠の絵（純関数） ----

    [Fact]
    public void H12_枠の絵は枠の所だけalpha_中は0_太さどおり()
    {
        uint[] buf = Array.Empty<uint>();
        uint colorRef = 0x00C06700;                  // R=0x00 G=0x67 B=0xC0
        foreach (int b in new[] { 1, 4, 6 })
        {
            const int w = 40, h = 30;
            QuickMarker.BuildFrame(ref buf, w, h, b, colorRef, 0xB3);
            uint px = QuickMarker.PremulArgb(colorRef, 0xB3);
            Assert.Equal(0xB3u, px >> 24);
            Assert.Equal((0x67u * 0xB3 + 127) / 255, (px >> 8) & 0xFF);   // プリマルチプライド
            int ring = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inRing = x < b || y < b || x >= w - b || y >= h - b;
                    Assert.Equal(inRing ? px : 0u, buf[y * w + x]);
                    if (inRing) ring++;
                }
            Assert.Equal(w * h - (w - 2 * b) * (h - 2 * b), ring);
        }
    }

    [Fact]
    public void H12_最小サイズ未満は半分のalpha_内側が無い小ささは全面が枠()
    {
        uint colorRef = 0x00C06700;
        Assert.Equal(0xB3u / 2, (uint)(byte)(0xB3 / 2));
        uint full = QuickMarker.PremulArgb(colorRef, 0xB3), half = QuickMarker.PremulArgb(colorRef, (byte)(0xB3 / 2));
        Assert.Equal(0xB3u, full >> 24);
        Assert.Equal((uint)(0xB3 / 2), half >> 24);
        uint[] buf = Array.Empty<uint>();
        QuickMarker.BuildFrame(ref buf, 6, 6, 4, colorRef, (byte)(0xB3 / 2));   // 4*2 >= 6: 内側無し
        for (int i = 0; i < 36; i++) Assert.Equal(half, buf[i]);
    }

    [Fact]
    public void H12_大きさを変え続けてもGDIハンドルが増えない()
    {
        const uint GR_GDIOBJECTS = 0;
        var proc = System.Diagnostics.Process.GetCurrentProcess().Handle;
        uint Count() => GetGuiResources(proc, GR_GDIOBJECTS);
        var m = new QuickMarker(0x00, 0x67, 0xC0, 4);
        try
        {
            Assert.True(m.StartExternal(300, 300, 100, 100));
            for (int i = 0; i < 30; i++) { m.Place(300, 300, 100 + i, 100 + i); Thread.Sleep(20); }
            uint before = Count();
            for (int i = 0; i < 150; i++) { m.Place(300 - i, 300 - i, 150 + 2 * i, 150 + 2 * i); Thread.Sleep(10); }
            uint after = Count();
            Assert.True(after <= before + 2, $"GDI ハンドルが増えた {before} -> {after}");
        }
        finally { m.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr proc, uint flags);
}

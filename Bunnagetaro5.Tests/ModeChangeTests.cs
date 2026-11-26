using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bunnagetaro5;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>
/// 点検B4 → F12c: 自動トリガを切り替えても、出ている答え（解答欄・外の解答欄）は消さない
/// （Python版 tw_mode._mode_changed は消していたが、人の判断で意図して変える）。
/// </summary>
public class ModeChangeTests : IDisposable
{
    private readonly string _temp;
    private readonly Paths _paths;

    public ModeChangeTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "b5_mode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new Paths(Path.Combine(_temp, "Bunnagetaro5"));
        AppLog.Init(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static void EnsureAppResources() => TestUtil.EnsureAppResources();

    private static void Pump(int ms) => TestUtil.Pump(ms);

    private static void OnSta(Action body) => TestUtil.OnSta(body, 60);

    [Fact]
    public void 自動トリガを切り替えても出ていた解答欄は消えない()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("result_place", "inside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(600);
                var panel = (AnswerPanel)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.Session)!;
                panel.ApplyBody("前の答え");
                Pump(100);
                Assert.True(panel.IsVisible);
                w.Session.BarAutoTrig(true);
                Pump(600);   // 以前は薄れ(150ms)が終わって隠れていた（B4）。F12c: 消さない
                Assert.True(panel.IsVisible);
                Assert.False(WindowMotion.IsFadingOut(panel));
                Assert.Equal("前の答え", panel.ModelText.Trim());
                Assert.Equal("auto", cfg.GetStr("mode", ""));
                // 手動へ戻す時も同じ
                w.Session.BarAutoTrig(false);
                Pump(600);
                Assert.True(panel.IsVisible);
                Assert.Equal("前の答え", panel.ModelText.Trim());
                // 設定ページのチェック欄から切り替えた時（SyncBarFromCfg）も同じ
                cfg.Settings.Mode = "auto";
                w.Session.SyncBarFromCfg();
                Pump(600);
                Assert.True(panel.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    /// <summary>E3: 薄れている最中でも、撮影前の隠し(HideForCapture)は即消え、出す要求は薄れを取り消して戻す</summary>
    [Fact]
    public void 薄れの最中でも撮影前の隠しは即時_出す要求は戻す()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("result_place", "inside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(600);
                var panel = (AnswerPanel)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.Session)!;
                panel.ApplyBody("答え");
                Pump(300);
                Assert.True(panel.IsVisible);
                panel.HideFade();
                Assert.True(panel.IsVisible);          // まだ薄れている最中
                panel.HideForCapture();
                Assert.False(panel.IsVisible);         // 即
                Assert.Equal(1.0, panel.Opacity, 3);   // 次に出す時のため設定値へ戻っている
                panel.ApplyBody("次の答え");
                Pump(50);
                panel.HideFade();
                panel.ApplyBody("さらに次");            // 消えかけに出す要求
                Pump(600);
                Assert.True(panel.IsVisible);
                Assert.Equal(1.0, panel.Opacity, 3);
            }
            finally { w.Close(); }
        });
    }

    /// <summary>E4: 最初の案内は動き無しで残り、2回目以降の案内は薄れて消える（隠れてから本文が空になる）</summary>
    [Fact]
    public void 案内は最初は残り_2回目以降は薄れて消える()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("result_place", "inside");
            var w = new MainWindow(_paths, cfg);
            try
            {
                w.Show();
                w.Session.OpenCapture();            // 最初の案内（答えが出るまで残す札）
                Pump(600);
                var panel = (AnswerPanel)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.Session)!;
                Assert.True(panel.IsVisible);
                Assert.Equal(Bunnagetaro5.Session.CaptureSession.HintText, panel.ModelText.Trim());
                Assert.False(Bunnagetaro5.Services.WindowMotion.IsFadingOut(panel));

                // 2回目: 隠してから案内を出し直し、消す
                w.Session.HintForTests.HintFirst = false;
                panel.ResetRun();
                panel.HideForCapture();
                w.Session.ShowHint();
                Pump(300);
                Assert.True(panel.IsVisible);
                int tok = w.Session.HintForTests.HintTok;
                w.Session.HideHint(tok);
                Assert.True(panel.IsVisible);                       // 薄れている最中
                Assert.Equal(Bunnagetaro5.Session.CaptureSession.HintText, panel.ModelText.Trim());   // 文字は残ったまま
                Pump(700);
                Assert.False(panel.IsVisible);
                Assert.Equal("", panel.ModelText.Trim());           // 隠れてから空へ
            }
            finally { w.Close(); }
        });
    }

    /// <summary>E5: クイック開始で窓は吸い込まれて隠れる。吸い込み中にドラッグが始まれば即打ち切って元の見え方で追従</summary>
    [Fact]
    public void クイック開始の吸い込みはドラッグ開始で即打ち切られる()
    {
        OnSta(() =>
        {
            EnsureAppResources();
            var cfg = new ConfigStore(_paths);
            cfg.Load();
            cfg.SetStr("result_place", "inside");
            cfg.SetBool("quick_enabled", false);
            var w = new MainWindow(_paths, cfg);
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            try
            {
                w.Show();
                w.Session.OpenCapture();
                Pump(600);
                var cap = (CaptureWindow)typeof(Bunnagetaro5.Session.CaptureSession).GetField("_capture", F)!.GetValue(w.Session)!;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(cap).Handle;
                Bunnagetaro5.Services.Native.GetWindowRect(hwnd, out var r0);
                int cx = (r0.Left + r0.Right) / 2, cy = (r0.Top + r0.Bottom) / 2;

                // 1) 押した → すぐ吸い込みが始まる（まだ見えている）→ 終わると隠れて、見え方は元へ戻っている
                w.Quick.BeginAt(cx, cy);
                Assert.True(cap.IsVisible);
                Assert.True(Bunnagetaro5.Services.WindowMotion.IsFadingOut(cap));
                Pump(500);
                Assert.False(cap.IsVisible);
                Assert.Equal(1.0, cap.Opacity, 3);
                w.Quick.CancelRestore();   // 元へ戻す（ふわっと）
                Pump(500);
                Assert.True(cap.IsVisible);
                Assert.Equal(1.0, cap.Opacity, 3);
                Bunnagetaro5.Services.Native.GetWindowRect(hwnd, out var r1);
                Assert.Equal((r0.Left, r0.Top, r0.Right, r0.Bottom), (r1.Left, r1.Top, r1.Right, r1.Bottom));

                // 2) 押した → 吸い込み中にドラッグ開始（QuickPlace）→ 即打ち切り・不透明・指定矩形
                w.Quick.BeginAt(cx, cy);
                Pump(40);
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { 400, 300, 320, 240, false });
                Assert.True(cap.IsVisible);
                Assert.False(Bunnagetaro5.Services.WindowMotion.IsFadingOut(cap));
                Assert.Equal(1.0, cap.Opacity, 3);
                Pump(400);
                Assert.True(cap.IsVisible);                    // 後から隠されない
                Bunnagetaro5.Services.Native.GetWindowRect(hwnd, out var r2);
                Assert.Equal((400, 300, 720, 540), (r2.Left, r2.Top, r2.Right, r2.Bottom));

                // 3) E5: 四角が前回と同じなら QuickPlace は SetWindowPos を呼ばない
                int movingCount = 0;
                cap.Moving += _ => movingCount++;
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { 400, 300, 320, 240, false });
                Assert.Equal(0, movingCount);
                typeof(Bunnagetaro5.Session.CaptureSession).GetMethod("QuickPlace", F)!.Invoke(w.Session, new object[] { 410, 310, 330, 250, false });
                Assert.Equal(1, movingCount);
            }
            finally { w.Close(); }
        });
    }
}

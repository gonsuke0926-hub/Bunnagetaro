using System;
using System.Runtime.InteropServices;
using Bunnagetaro5.Services;
using Xunit;

namespace Bunnagetaro5.Tests;

/// <summary>Windows 標準の色選択ダイアログ（ChooseColorW）が構造体エラー無く開くことの検証</summary>
public class ColorPickerTests
{
    private const uint WM_INITDIALOG = 0x0110;
    private const uint WM_COMMAND = 0x0111;
    private static readonly IntPtr IDCANCEL = (IntPtr)2;

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void ChooseColorWがCDERR_STRUCTSIZEにならずWM_INITDIALOGまで開く()
    {
        TestUtil.OnSta(() =>
        {
            bool initCalled = false;
            var res = ColorPicker.Pick(IntPtr.Zero, "#0067c0", (hdlg, msg, _, _) =>
            {
                if (msg == WM_INITDIALOG)
                {
                    initCalled = true;
                    PostMessageW(hdlg, WM_COMMAND, IDCANCEL, IntPtr.Zero);
                }
                return IntPtr.Zero;
            });

            Assert.Null(res);
            Assert.Equal(0u, ColorPicker.CommDlgExtendedError());
            Assert.True(initCalled, "ChooseColorW のダイアログ初期化に到達していない");
        });
    }
}

using System;
using System.Runtime.InteropServices;

namespace Bunnagetaro5.Services;

/// <summary>
/// Windowsの色選択ダイアログ（comdlg32 の ChooseColor）。
/// 選ぶと #rrggbb、取り消しは null。
/// </summary>
public static class ColorPicker
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public uint lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpCustColors;
        public uint Flags;
        public uint rgbResult;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    private const uint CC_RGBINIT = 0x00000001;   // 今の色から始める
    private const uint CC_FULLOPEN = 0x00000002;  // 詳細パレットを開いたまま
    private const uint CC_ANYCOLOR = 0x00000100;

    // 「定義色」16枠はアプリの起動中ずっと同じ物を使う（Windows標準の挙動）
    private static readonly uint[] CustomColors = new uint[16];

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "ChooseColorW")]
    private static extern bool ChooseColorW(ref CHOOSECOLOR lpcc);

    public static string? Pick(IntPtr owner, string? initial)
    {
        var handle = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = (uint)Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = owner,
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
                rgbResult = ColorUtil.ToColorRef(initial) ?? 0u,
            };
            if (!ChooseColorW(ref cc)) return null;
            return ColorUtil.FromColorRef(cc.rgbResult);
        }
        finally
        {
            handle.Free();
        }
    }
}

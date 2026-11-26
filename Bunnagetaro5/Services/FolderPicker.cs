using System;
using System.Runtime.InteropServices;

namespace Bunnagetaro5.Services;

/// <summary>
/// フォルダ選択ダイアログ（指示書6.4「参照」）。Win32 P/Invoke（shell32 の
/// SHBrowseForFolder）。WPFには標準のフォルダピッカーが無い（WinFormsは
/// NuGet本体増やしまで使わない方針なので使わない）。
/// Python版 tw_apparel._pick_shots_dir の filedialog.askdirectory 相当。
/// </summary>
public static class FolderPicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, IntPtr pszPath);

    [DllImport("ole32.dll")]
    private static extern IntPtr CoTaskMemFree(IntPtr pv);

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;   // ファイルシステムの実フォルダだけ
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;     // 近代の見た目（リボン風の新規作成付き）
    private const uint BIF_EDITBOX = 0x0010;            // 名前行（フォルダ名を直接編集できる）
    private const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

    /// <summary>フォルダを選ばせ、選ばれたパスを返す。キャンセルなら null。</summary>
    public static string? Pick(IntPtr ownerHwnd, string title)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = ownerHwnd,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = new string('\0', 520),
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
            lpfn = IntPtr.Zero,
            lParam = IntPtr.Zero,
            iImage = 0,
        };
        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;
        try
        {
            // MAX_PATH を越えるパスもあるが、まず収まる長さのバッファで取る
            IntPtr buf = Marshal.AllocHGlobal(2 * 1024);
            try
            {
                if (!SHGetPathFromIDListW(pidl, buf)) return null;
                string path = Marshal.PtrToStringUni(buf) ?? "";
                return path.Length == 0 ? null : path;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CoTaskMemFree(pidl); }
    }
}

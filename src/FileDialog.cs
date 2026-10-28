using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StickerStudio;

/// Picking a picture, on its own thread so the game keeps drawing behind it. On a Mac (the game running in Wine /
/// Vinora) it's the real macOS picker (MacPicker); on Windows, or if that can't start, the Windows "Open" window
/// (comdlg32). The Mac's disk is Z:\ inside Wine. The answer comes back on the game's main thread via StudioDriver.Post.
internal static class FileDialog
{
    public static bool IsOpen { get; private set; }

    /// Calls done(path) — null if cancelled — or failed(message) if no picker could be shown at all.
    public static void Open(Action<string> done, Action<string> failed)
    {
        if (IsOpen) return;
        IsOpen = true;
        var owner = GameWindow();
        var start = StartFolder();
        var thread = new Thread(() =>
        {
            string path = null, error = null;
            try
            {
                var mac = false;
                if (MacPicker.CanUse)
                {
                    try { path = MacPicker.Pick(start); mac = true; }
                    catch (Exception e) { Plugin.Logger.LogWarning($"Mac picker didn't work ({e.Message}), using the Windows one"); }
                }
                if (!mac) path = Show(owner, start);
            }
            catch (Exception e) { error = e.Message; }
            StudioDriver.Post(() =>
            {
                IsOpen = false;
                if (error != null) failed(error); else done(path);
            });
        }) { IsBackground = true, Name = "StickerStudioOpen" };
        try { thread.SetApartmentState(ApartmentState.STA); } catch { /* not on Windows: the dialog will fail and say so */ }
        thread.Start();
    }

    static string Show(IntPtr owner, string startFolder)
    {
        const int Size = 4096;
        var buffer = Marshal.AllocHGlobal(Size * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = "Pictures (*.png, *.jpg)\0*.png;*.jpg;*.jpeg\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = Size,
                lpstrInitialDir = startFolder,
                lpstrTitle = "Pick a picture for your sticker",
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_ENABLESIZING,
            };
            if (!GetOpenFileNameW(ref ofn))
            {
                var code = CommDlgExtendedError();
                if (code != 0) throw new InvalidOperationException($"the Open window failed (code {code:X})");
                return null; // cancelled
            }
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// Last folder used, else the Mac Desktop under Wine (Z:\Users\<you>\Desktop), else Pictures.
    static string StartFolder()
    {
        try
        {
            var last = StickerStore.LastFolder;
            if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            if (Directory.Exists(@"Z:\Users"))
                foreach (var user in Directory.GetDirectories(@"Z:\Users"))
                {
                    var desktop = Path.Combine(user, "Desktop");
                    if (!user.EndsWith("Shared", StringComparison.OrdinalIgnoreCase) && Directory.Exists(desktop)) return desktop;
                }
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (Directory.Exists(pictures)) return pictures;
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        catch
        {
            return null;
        }
    }

    /// The game's window, so the Open window stays on top of it (even in fullscreen).
    static IntPtr GameWindow()
    {
        try
        {
            var handle = FindWindowW("UnityWndClass", null);
            return handle != IntPtr.Zero ? handle : Process.GetCurrentProcess().MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    const int OFN_EXPLORER = 0x00080000, OFN_FILEMUSTEXIST = 0x00001000, OFN_PATHMUSTEXIST = 0x00000800;
    const int OFN_NOCHANGEDIR = 0x00000008, OFN_ENABLESIZING = 0x00800000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    static extern int CommDlgExtendedError();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string className, string windowName);
}

using System;
using System.Runtime.InteropServices;

namespace StickerStudio;

/// Backup picture decoder: Windows' GDI+ (Wine has it too), used if Unity's decoder can't be reached.
internal static class WindowsImage
{
    static IntPtr token;

    /// PNG/JPG file -> picture (RGBA, rows bottom-up), or null.
    public static Picture Decode(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (token == IntPtr.Zero)
        {
            var input = new StartupInput { GdiplusVersion = 1 };
            Check(GdiplusStartup(out token, ref input, IntPtr.Zero), "start");
        }
        Check(GdipCreateBitmapFromFile(path, out var bitmap), "open");
        try
        {
            Check(GdipGetImageWidth(bitmap, out var w), "size");
            Check(GdipGetImageHeight(bitmap, out var h), "size");
            var rect = new Rect { Width = (int)w, Height = (int)h };
            var data = new BitmapData();
            Check(GdipBitmapLockBits(bitmap, ref rect, ImageLockModeRead, PixelFormat32bppARGB, ref data), "read");
            try
            {
                int width = (int)w, height = (int)h;
                var rgba = new byte[width * height * 4];
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    var o = (height - 1 - y) * width * 4; // GDI+ rows go top-down, ours bottom-up
                    for (var x = 0; x < width; x++)
                    {
                        rgba[o + x * 4] = row[x * 4 + 2];     // BGRA -> RGBA
                        rgba[o + x * 4 + 1] = row[x * 4 + 1];
                        rgba[o + x * 4 + 2] = row[x * 4];
                        rgba[o + x * 4 + 3] = row[x * 4 + 3];
                    }
                }
                return new Picture(width, height, rgba);
            }
            finally
            {
                GdipBitmapUnlockBits(bitmap, ref data);
            }
        }
        finally
        {
            GdipDisposeImage(bitmap);
        }
    }

    static void Check(int status, string step)
    {
        if (status != 0) throw new InvalidOperationException($"GDI+ {step} failed (status {status})");
    }

    const int PixelFormat32bppARGB = 0x26200A;
    const uint ImageLockModeRead = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapData
    {
        public uint Width, Height;
        public int Stride;
        public int PixelFormat;
        public IntPtr Scan0;
        public IntPtr Reserved;
    }

    [DllImport("gdiplus.dll")] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus.dll")] static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus.dll")] static extern int GdipBitmapLockBits(IntPtr bitmap, ref Rect rect, uint flags, int format, ref BitmapData data);
    [DllImport("gdiplus.dll")] static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);
    [DllImport("gdiplus.dll")] static extern int GdipDisposeImage(IntPtr image);
}

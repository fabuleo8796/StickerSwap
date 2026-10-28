using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StickerStudio;

/// Moving pixels between files, the baker's byte arrays and Unity textures.
internal static class Textures
{
    public const int MaxPictureSide = 2048;
    static uint? cachedPtrOffset;
    static LoadImageCall loadImage;

    [return: MarshalAs(UnmanagedType.U1)]
    delegate bool LoadImageCall(IntPtr texture, IntPtr span, [MarshalAs(UnmanagedType.U1)] bool markNonReadable);

    /// Same layout as the engine's ManagedSpanWrapper (pointer, then length).
    [StructLayout(LayoutKind.Sequential)]
    struct SpanWrapper
    {
        public IntPtr Begin;
        public int Length;
    }

    /// PNG/JPG -> picture (rows bottom-up). Big photos are shrunk to at most 2048 px. Null if unreadable.
    /// Unity's own decoder first; Windows' (GDI+, also in Wine) if that fails.
    public static Picture ReadPicture(byte[] file, string path)
    {
        if (file == null || file.Length == 0) return null;
        Picture picture = null;
        try { picture = UnityDecode(file); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Unity couldn't decode the picture ({e.Message}), trying Windows' decoder"); }
        if (picture == null)
        {
            try { picture = WindowsImage.Decode(path); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Windows couldn't decode the picture either: {e.Message}"); }
        }
        if (picture == null) return null;
        var factor = (int)Math.Ceiling(Math.Max(picture.Width, picture.Height) / (double)MaxPictureSide);
        return factor > 1 ? picture.Shrunk(factor) : picture;
    }

    /// ImageConversion.LoadImage's managed wrapper needs a ReadOnlySpan method this game's build stripped, so this
    /// calls the engine's decoder it wraps directly: the texture's native pointer and a {pointer, length} span.
    static unsafe Picture UnityDecode(byte[] file)
    {
        loadImage ??= IL2CPP.ResolveICall<LoadImageCall>("UnityEngine.ImageConversion::LoadImage_Injected");
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            bool ok;
            fixed (byte* bytes = file)
            {
                var span = new SpanWrapper { Begin = (IntPtr)bytes, Length = file.Length };
                ok = loadImage(NativePointer(tex), (IntPtr)(&span), false);
            }
            if (!ok || tex.width <= 2 && tex.height <= 2) return null;
            int w = tex.width, h = tex.height;
            var pixels = tex.GetPixels32();
            var rgba = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                var c = pixels[i];
                rgba[i * 4] = c.r; rgba[i * 4 + 1] = c.g; rgba[i * 4 + 2] = c.b; rgba[i * 4 + 3] = c.a;
            }
            return new Picture(w, h, rgba);
        }
        finally
        {
            Object.Destroy(tex);
        }
    }

    /// The engine-side object behind a UnityEngine.Object (its m_CachedPtr field).
    static IntPtr NativePointer(Object obj)
    {
        cachedPtrOffset ??= IL2CPP.il2cpp_field_get_offset(
            IL2CPP.il2cpp_class_get_field_from_name(Il2CppClassPointerStore<Object>.NativeClassPtr, "m_CachedPtr"));
        var native = Marshal.ReadIntPtr(obj.Pointer + (int)cachedPtrOffset.Value);
        if (native == IntPtr.Zero) throw new InvalidOperationException("texture has no native object");
        return native;
    }

    public static Texture2D Make(int width, int height, bool mips, bool linear = false)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, mips, linear)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        if (mips) tex.anisoLevel = 4;
        return tex;
    }

    /// Writes RGBA bytes (rows bottom-up) into the texture, resizing it if needed, and rebuilds its mipmaps.
    public static Texture2D Upload(Texture2D tex, int width, int height, byte[] rgba, bool mips = true)
    {
        if (tex == null) tex = Make(width, height, mips);
        else if (tex.width != width || tex.height != height) tex.Reinitialize(width, height);
        var pixels = new Color32[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
        tex.SetPixels32(pixels);
        tex.Apply(mips, false);
        return tex;
    }

    public static Texture2D Solid(Color32 color, bool linear)
    {
        var tex = Make(4, 4, false, linear);
        var pixels = new Color32[16];
        for (var i = 0; i < 16; i++) pixels[i] = color;
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }
}

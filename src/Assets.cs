using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace StickerStudio;

/// Geist (embedded, OFL), the line icons (embedded strip drawn from the design mockups) and a few generated sprites.
internal static class Assets
{
    static readonly string[] IconNames = { "folder", "check", "close", "rotl", "rotr", "undo", "reset", "flip", "fit", "fill", "border", "zoom", "move", "star" };
    static readonly Dictionary<string, Sprite> icons = new();
    static readonly Dictionary<string, TMP_FontAsset> fonts = new();
    static Sprite rounded, roundedPanel, circle;
    static Texture2D checker;

    public static TMP_FontAsset SemiBold => Font("SemiBold");
    public static TMP_FontAsset Medium => Font("Medium");
    public static TMP_FontAsset Regular => Font("Regular");

    /// A white rounded square, 9-sliced (slider tracks, key caps, the toggle).
    public static Sprite Rounded => rounded ??= MakeRounded(64, 24);
    /// Same corners as the glass panel (48), 9-sliced: the panel's plain backing.
    public static Sprite RoundedPanel => roundedPanel ??= MakeRounded(128, 48);
    /// A white disc (slider knobs, dots).
    public static Sprite Circle => circle ??= MakeRounded(64, 32);

    /// Two-tone 2x2 texture; shown with a tiled uvRect as the editor's "transparent" checkerboard.
    public static Texture2D Checker
    {
        get
        {
            if (checker != null) return checker;
            checker = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
            Color32 a = new(255, 255, 255, 14), b = new(255, 255, 255, 30);
            checker.SetPixels32(new[] { a, b, b, a });
            checker.Apply(false, false);
            return checker;
        }
    }

    public static Sprite Icon(string name)
    {
        if (icons.Count == 0) LoadIcons();
        return icons.TryGetValue(name, out var sprite) ? sprite : null;
    }

    static void LoadIcons()
    {
        const int size = 64;
        var bytes = ReadResource("icons.rgba");
        var tex = Textures.Upload(Textures.Make(size * IconNames.Length, size, true), size * IconNames.Length, size, bytes);
        for (var i = 0; i < IconNames.Length; i++)
        {
            var sprite = Sprite.Create(tex, new Rect(i * size, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            icons[IconNames[i]] = sprite;
        }
    }

    /// Geist as a dynamic TMP font (glyphs are added as they're used). Falls back to the game's own font.
    static TMP_FontAsset Font(string weight)
    {
        if (fonts.TryGetValue(weight, out var cached) && cached != null) return cached;
        TMP_FontAsset font = null;
        var game = GameFont();
        try
        {
            var folder = Path.Combine(StickerStore.Folder, "fonts");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Geist-{weight}.ttf");
            if (!File.Exists(path)) File.WriteAllBytes(path, ReadResource($"Geist-{weight}.ttf"));
            var license = Path.Combine(folder, "OFL-Geist.txt");
            if (!File.Exists(license)) File.WriteAllBytes(license, ReadResource("OFL-Geist.txt"));
            font = TMP_FontAsset.CreateFontAsset(path, 0, 72, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (font != null)
            {
                font.name = $"Geist-{weight} (StickerSwap)";
                font.hideFlags = HideFlags.HideAndDontSave;
                var shader = font.material != null ? font.material.shader : null;
                if ((shader == null || !shader.isSupported) && game != null && game.material != null) font.material.shader = game.material.shader;
                if (game != null) font.fallbackFontAssetTable?.Add(game);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't load Geist {weight}, using the game's font: {e.Message}");
            font = null;
        }
        return fonts[weight] = font ?? game;
    }

    static TMP_FontAsset GameFont()
    {
        var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        return all.FirstOrDefault(f => f != null && f.name.StartsWith("Jersey10"))
            ?? all.FirstOrDefault(f => f != null && f.name.StartsWith("Bungee"))
            ?? all.FirstOrDefault(f => f != null && !f.name.Contains("StickerSwap"));
    }

    static byte[] ReadResource(string name)
    {
        using var stream = typeof(Assets).Assembly.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    static Sprite MakeRounded(int size, int radius)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
            var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var border = radius < size / 2 ? new Vector4(radius, radius, radius, radius) : Vector4.zero;
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}

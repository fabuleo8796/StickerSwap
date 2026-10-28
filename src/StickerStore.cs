using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;

namespace StickerStudio;

/// How one slot's custom sticker is made: which picture, and the editor's settings.
internal class SlotSettings
{
    public string Source { get; set; }        // our copy of the picture, e.g. "source2.png"
    public string PickedFrom { get; set; }    // where it was opened from (shown under "Recent")
    public float Zoom { get; set; } = 1f;
    public float Rotation { get; set; }       // degrees, counter-clockwise, 0 = straight on the phone
    public float OffsetX { get; set; }        // fraction of the sticker's width
    public float OffsetY { get; set; }
    public bool Flip { get; set; }
    public bool Fill { get; set; }
    public bool Border { get; set; } = true;
    public float BorderPx { get; set; } = 14f; // at 512 px

    public SlotSettings Copy() => (SlotSettings)MemberwiseClone();
}

internal class StoreData
{
    public Dictionary<int, SlotSettings> Slots { get; set; } = new();
    public List<string> Recent { get; set; } = new();
    public string LastFolder { get; set; }
}

/// Everything the mod keeps, in BepInEx/config/FabuleosStickerStudio/: stickers.json, a copy of each picture
/// (so moving or deleting the original doesn't break the sticker), and the inbox/ folder for the fallback picker.
internal static class StickerStore
{
    public static readonly string Folder = Path.Combine(Paths.ConfigPath, "FabuleosStickerStudio");
    public static readonly string Inbox = Path.Combine(Folder, "inbox");
    static readonly string FilePath = Path.Combine(Folder, "stickers.json");
    static StoreData data = new();

    public static int CustomCount => data.Slots.Count;
    public static IReadOnlyList<string> Recent => data.Recent;

    public static string LastFolder
    {
        get => data.LastFolder;
        set { data.LastFolder = value; Save(); }
    }

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Inbox);
            if (File.Exists(FilePath))
                data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath)) ?? new();
            data.Slots = data.Slots.Where(kv => kv.Key is >= 1 and <= 3 && kv.Value != null && File.Exists(SourcePath(kv.Value)))
                                   .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read {FilePath}: {e.Message}. Starting with no custom stickers.");
            data = new();
        }
    }

    public static SlotSettings Get(int slot) => data.Slots.TryGetValue(slot, out var s) ? s : null;

    public static string SourcePath(SlotSettings s) => s?.Source == null ? null : Path.Combine(Folder, s.Source);

    /// Saves a slot's sticker: keeps a copy of the picture's bytes next to the settings.
    public static void Set(int slot, SlotSettings settings, byte[] picture, string pickedFrom)
    {
        var ext = Path.GetExtension(pickedFrom ?? "").ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg") ext = ".png";
        settings.Source = $"source{slot}{ext}";
        settings.PickedFrom = pickedFrom;
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var old in Directory.GetFiles(Folder, $"source{slot}.*")) File.Delete(old);
            File.WriteAllBytes(SourcePath(settings), picture);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Couldn't keep a copy of the picture: {e.Message}");
        }
        data.Slots[slot] = settings;
        AddRecent(pickedFrom);
        Save();
    }

    public static void Clear(int slot)
    {
        if (!data.Slots.Remove(slot)) return;
        try { foreach (var old in Directory.GetFiles(Folder, $"source{slot}.*")) File.Delete(old); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Couldn't delete the old picture copy: {e.Message}"); }
        Save();
    }

    public static void AddRecent(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        data.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        data.Recent.Insert(0, path);
        if (data.Recent.Count > 6) data.Recent.RemoveRange(6, data.Recent.Count - 6);
    }

    /// Pictures dropped into the inbox folder (newest first) — the fallback when the Open dialog can't show.
    public static List<string> InboxPictures()
    {
        try
        {
            return new DirectoryInfo(Inbox).GetFiles()
                .Where(f => IsPicture(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName).ToList();
        }
        catch { return new(); }
    }

    public static bool IsPicture(string path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg";
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Couldn't save stickers.json: {e.Message}");
        }
    }
}

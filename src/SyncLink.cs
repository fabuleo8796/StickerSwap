using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using EHS;
using EHS.Skins;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StickerStudio;

/// Other players' custom stickers through FIH Sync (a separate, optional mod).
/// - Sent: each custom sticker as the baked pixels your phone shows (512×512 at most), with its slot.
/// - Received: put on that player's phone, on the slots where they have a game sticker, with material copies of their
///   own (never the ones your phone uses). Nothing is decoded: the pixels are copied as they are.
/// Without FIH Sync installed this does nothing; the calls into it sit in NoInlining methods.
internal static class SyncLink
{
    const int MaxSize = 512, Magic = 0x4B545346; // "FSTK"

    static readonly byte[][] baked = new byte[3][];
    static readonly int[] bakedSize = new int[3];
    static int generation;

    sealed class Remote
    {
        public int Owner;
        public readonly Texture2D[] Textures = new Texture2D[3];
        public readonly List<(int slot, Renderer r, Material original, Material ours)> Parts = new();
        public IntPtr Phone;
        public readonly IntPtr[] Instances = new IntPtr[3];
    }

    static readonly Dictionary<int, Remote> remote = new();
    static bool linked;
    static float nextCheck;

    internal static void Start()
    {
        try { Link(); linked = true; }
        catch (Exception) { Plugin.Logger.LogInfo("FIH Sync isn't installed: other players won't see your stickers"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Link() => FIHSync.Share.Register("stickers", Version, Data, Received, Gone);

    // ---------------------------------------------------------------- sending

    /// A sticker was stuck on or taken off (rgba null).
    internal static void SetBaked(int slot, byte[] rgba, int size)
    {
        baked[slot - 1] = rgba != null && size > 0 && rgba.Length >= size * size * 4 ? rgba : null;
        bakedSize[slot - 1] = size;
        generation++;
    }

    static string Version()
    {
        bool any = baked[0] != null || baked[1] != null || baked[2] != null;
        return any ? generation.ToString() : null;
    }

    static byte[] Data()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic);
            for (int i = 0; i < 3; i++)
            {
                if (baked[i] == null) { w.Write(0); continue; }
                var (px, s) = Shrink(baked[i], bakedSize[i]);
                w.Write(s);
                var packed = new MemoryStream();
                using (var dz = new DeflateStream(packed, System.IO.Compression.CompressionLevel.Fastest, true)) dz.Write(px, 0, s * s * 4);
                w.Write((int)packed.Length);
                w.Write(packed.GetBuffer(), 0, (int)packed.Length);
            }
        }
        return ms.ToArray();
    }

    /// 2×2 averages until it fits MaxSize.
    static (byte[] px, int size) Shrink(byte[] px, int size)
    {
        while (size > MaxSize)
        {
            int n = size / 2;
            var o = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = ((y * 2) * size + x * 2) * 4, b = a + 4, c = a + size * 4, d = c + 4;
                    for (int k = 0; k < 4; k++) o[(y * n + x) * 4 + k] = (byte)((px[a + k] + px[b + k] + px[c + k] + px[d + k] + 2) / 4);
                }
            px = o; size = n;
        }
        return (px, size);
    }

    // ---------------------------------------------------------------- receiving

    static void Received(int ownerId, byte[] data, string folder)
    {
        Gone(ownerId);
        var r = new Remote { Owner = ownerId };
        using var br = new BinaryReader(new MemoryStream(data));
        if (br.ReadInt32() != Magic) return;
        int count = 0;
        for (int i = 0; i < 3; i++)
        {
            int s = br.ReadInt32();
            if (s == 0) continue;
            if (s < 0 || s > MaxSize) return;
            int len = br.ReadInt32();
            if (len <= 0 || len > s * s * 4 + 1024) return;
            var px = new byte[s * s * 4];
            using (var z = new DeflateStream(new MemoryStream(br.ReadBytes(len)), CompressionMode.Decompress))
            {
                int got = 0, n;
                while (got < px.Length && (n = z.Read(px, got, px.Length - got)) > 0) got += n;
                if (got != px.Length) return;
            }
            r.Textures[i] = global::StickerStudio.Textures.Upload(null, s, s, px);
            r.Textures[i].name = $"StickerSwap (player {ownerId}, sticker {i + 1})";
            r.Textures[i].hideFlags = HideFlags.HideAndDontSave;
            count++;
        }
        remote[ownerId] = r;
        Plugin.Logger.LogInfo($"player {ownerId}'s custom stickers arrived ({count})");
    }

    static void Gone(int ownerId)
    {
        if (!remote.TryGetValue(ownerId, out var r)) return;
        Restore(r);
        foreach (var t in r.Textures) if (t != null) Object.Destroy(t);
        remote.Remove(ownerId);
    }

    static void Restore(Remote r)
    {
        foreach (var (_, rend, original, ours) in r.Parts)
        {
            if (rend != null && original != null && rend.sharedMaterial != null && rend.sharedMaterial.Pointer == ours.Pointer) rend.sharedMaterial = original;
            if (ours != null) Object.Destroy(ours);
        }
        r.Parts.Clear();
        r.Phone = IntPtr.Zero;
        Array.Clear(r.Instances, 0, 3);
    }

    /// Twice a second: each player's sticker objects (they change on respawns, level changes and sticker swaps), with
    /// their custom picture on the slots they sent.
    internal static void Tick()
    {
        if (!linked || remote.Count == 0 || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        foreach (var r in remote.Values)
        {
            try { Check(r); }
            catch (Exception e) { Plugin.Logger.LogWarning($"player {r.Owner}'s stickers: {e.Message}"); Restore(r); }
        }
    }

    static void Check(Remote r)
    {
        PlayerRef p = null;
        try { if (!PlayerRef.TryGetConnectedPlayerByClientId(r.Owner, out p)) p = null; }
        catch { p = null; }
        if (p != null && PlayerRef.IsLocalPlayer(p)) p = null;
        var phone = p?.playerSkins?.handlers;
        var phonePtr = phone != null ? phone.Pointer : IntPtr.Zero;
        bool rebuilt = phonePtr != r.Phone;
        var handlers = new SkinPrefabHandler[3];
        for (int slot = 1; slot <= 3 && phone != null; slot++)
        {
            handlers[slot - 1] = StickerSlots.Find(phone, StickerSlots.Category(slot));
            var inst = handlers[slot - 1]?.instanceRef;
            var ptr = inst != null ? inst.Pointer : IntPtr.Zero;
            if (ptr != r.Instances[slot - 1]) rebuilt = true;
        }
        if (rebuilt)
        {
            Restore(r);
            r.Phone = phonePtr;
            if (phone == null) return;
            for (int slot = 1; slot <= 3; slot++)
            {
                var h = handlers[slot - 1];
                var inst = h?.instanceRef;
                r.Instances[slot - 1] = inst != null ? inst.Pointer : IntPtr.Zero;
                var tex = r.Textures[slot - 1];
                var socket = h != null && tex != null ? StickerSlots.Socket(h, slot) : null;
                if (socket == null) continue; // an empty slot on their phone: nothing to put it on
                foreach (var rend in socket.GetComponentsInChildren<Renderer>(true))
                {
                    var original = rend.sharedMaterial;
                    if (original == null) continue;
                    var ours = StickerSlots.NewMaterial(original, $"FabuleoSticker{slot} (player {r.Owner})");
                    StickerSlots.SetTexture(ours, tex);
                    r.Parts.Add((slot, rend, original, ours));
                }
            }
            if (r.Parts.Count > 0) Plugin.Logger.LogInfo($"showing player {r.Owner}'s custom stickers");
        }
        foreach (var (_, rend, original, ours) in r.Parts)
        {
            if (rend == null || original == null) continue;
            var current = rend.sharedMaterial;
            if (current != null && current.Pointer == original.Pointer) rend.sharedMaterial = ours; // the game re-applied its own
        }
    }
}

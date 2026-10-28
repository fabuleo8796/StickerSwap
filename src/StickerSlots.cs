using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EHS;
using EHS.Skins;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StickerStudio;

/// Puts custom stickers on the local player's phones: the one you play, the customization-screen phone and the
/// menu phone. Every game sticker is the same mesh per slot (Sticker_Socket_01/02/03 inside each sticker prefab) with
/// its own material, so a custom sticker is that mesh with a copy of the material that uses our texture. The game's
/// sticker isn't unequipped or saved over — "Restore" just puts its material back. Other players are never touched.
internal static class StickerSlots
{
    /// How much the game tilts each slot's sticker on the phone (measured from the slot meshes, degrees CCW). The baker
    /// turns pictures by this much so that 0° in the editor sits straight on the phone.
    public static readonly float[] Tilt = { -11.6f, 33.3f, -36.1f };
    static readonly string[] Sockets = { "Sticker_Socket_01", "Sticker_Socket_02", "Sticker_Socket_03" };
    const string CloneName = "FabuleoSticker_";

    static readonly Texture2D[] saved = new Texture2D[3];
    static readonly Texture2D[] preview = new Texture2D[3];
    static readonly bool[] previewing = new bool[3];
    static readonly Dictionary<IntPtr, Material>[] materials = { new(), new(), new() }; // game material -> our copy
    static readonly Dictionary<IntPtr, (Renderer renderer, Material original)> swapped = new();
    static readonly Dictionary<IntPtr, GameObject> clones = new(); // empty slot's handler -> our copy of its sticker
    static Texture2D flatNormal, neutralMsao;

    public static bool Dirty { get; private set; }
    public static void MarkDirty() => Dirty = true;

    public static SkinCategory Category(int slot) =>
        slot == 1 ? SkinCategory.Sticker1 : slot == 2 ? SkinCategory.Sticker2 : SkinCategory.Sticker3;

    public static int SlotOf(SkinCategory category) =>
        category == SkinCategory.Sticker1 ? 1 : category == SkinCategory.Sticker2 ? 2 : category == SkinCategory.Sticker3 ? 3 : 0;

    public static Texture2D Saved(int slot) => saved[slot - 1];

    public static void SetSaved(int slot, Texture2D tex)
    {
        saved[slot - 1] = tex;
        Refresh(slot);
    }

    /// While editing, the phone shows the editor's texture instead of the saved one.
    public static void ShowPreview(int slot, Texture2D tex)
    {
        preview[slot - 1] = tex;
        previewing[slot - 1] = true;
        Refresh(slot);
    }

    public static void EndPreview(int slot)
    {
        if (!previewing[slot - 1]) return;
        previewing[slot - 1] = false;
        Refresh(slot);
    }

    static Texture2D Current(int slot) => previewing[slot - 1] ? preview[slot - 1] : saved[slot - 1];

    /// Re-checks every slot (cheap when nothing is custom). Catches stickers the game re-applied or rebuilt.
    public static void RefreshAll()
    {
        Dirty = false;
        if (swapped.Count == 0 && clones.Count == 0 && Current(1) == null && Current(2) == null && Current(3) == null) return;
        Forget();
        var phones = LocalPhones();
        for (var slot = 1; slot <= 3; slot++)
            foreach (var phone in phones) ApplyTo(phone, slot);
    }

    public static void Refresh(int slot)
    {
        foreach (var phone in LocalPhones()) ApplyTo(phone, slot);
    }

    /// A sticker handler finished applying a sticker: if it's on one of our phones, redo that phone.
    public static void OnStickerApplied(SkinPrefabHandler handler)
    {
        if (SlotOf(handler.category) == 0) return;
        foreach (var phone in LocalPhones())
        {
            if (!Contains(phone, handler)) continue;
            for (var slot = 1; slot <= 3; slot++) ApplyTo(phone, slot);
            return;
        }
    }

    /// Can a custom sticker show on this slot right now? (It needs this slot's sticker, or any other sticker on the
    /// phone to borrow the slot's mesh from.)
    public static bool CanShow(int slot)
    {
        foreach (var phone in LocalPhones())
        {
            var target = Find(phone, Category(slot));
            if (target == null) continue;
            if (Socket(target, slot) != null) return true;
            for (var i = 0; i < phone.Count; i++)
                if (phone[i] != null && SlotOf(phone[i].category) != 0 && Socket(phone[i], slot) != null) return true;
        }
        return false;
    }

    /// What the game has in this slot: its icon and a friendly name ("Butterfly"), or nulls for an empty slot.
    public static (Sprite icon, string name) GameSticker(int slot)
    {
        foreach (var phone in LocalPhones())
        {
            var target = Find(phone, Category(slot));
            var key = target != null ? target.CurrentSkinKey : null;
            if (string.IsNullOrEmpty(key)) continue;
            var skin = LookupSkin(key);
            return (skin != null ? skin.Sprite : null, Pretty(skin != null && !string.IsNullOrEmpty(skin.name) ? skin.name : key));
        }
        return (null, null);
    }

    // ---------------------------------------------------------------- finding our phones

    /// The local player's phones, each as its list of skin handlers.
    static List<Il2CppSystem.Collections.Generic.List<SkinPrefabHandler>> LocalPhones()
    {
        var phones = new List<Il2CppSystem.Collections.Generic.List<SkinPrefabHandler>>();
        try
        {
            var local = PlayerRef.LocalPlayer;
            var found = false;
            foreach (var skins in Object.FindObjectsOfType<PlayerSkins>())
                if (skins != null && skins.handlers != null && IsLocal(skins, local))
                {
                    phones.Add(skins.handlers);
                    found = true;
                }
            // No skin list matched: take the sticker handlers straight from under the local player.
            if (!found && local != null)
            {
                var list = new Il2CppSystem.Collections.Generic.List<SkinPrefabHandler>();
                foreach (var h in local.GetComponentsInChildren<SkinPrefabHandler>(true)) list.Add(h);
                if (list.Count > 0) phones.Add(list);
            }
            foreach (var dummy in Object.FindObjectsOfType<PlayerVisualsDummy>())
                if (dummy != null && dummy.handlers != null && dummy.IsCustomizationDummy)
                    phones.Add(dummy.handlers);
            foreach (var menu in Object.FindObjectsOfType<EquippedPlayerSkins>())
                if (menu != null && menu.handlers != null)
                    phones.Add(menu.handlers);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't look for your phone: {e.Message}");
        }
        return phones;
    }

    static bool IsLocal(PlayerSkins skins, PlayerRef local)
    {
        var owner = skins.playerRef;
        if (owner != null && (local != null && owner.Pointer == local.Pointer || PlayerRef.IsLocalPlayer(owner))) return true;
        return local != null && skins.transform.IsChildOf(local.transform);
    }

    static SkinPrefabHandler Find(Il2CppSystem.Collections.Generic.List<SkinPrefabHandler> phone, SkinCategory category)
    {
        for (var i = 0; i < phone.Count; i++)
            if (phone[i] != null && phone[i].category == category) return phone[i];
        return null;
    }

    static bool Contains(Il2CppSystem.Collections.Generic.List<SkinPrefabHandler> phone, SkinPrefabHandler handler)
    {
        for (var i = 0; i < phone.Count; i++)
            if (phone[i] != null && phone[i].Pointer == handler.Pointer) return true;
        return false;
    }

    /// The slot's sticker mesh inside a handler's current sticker (every sticker prefab carries all three).
    static Transform Socket(SkinPrefabHandler handler, int slot)
    {
        var root = InstanceRoot(handler);
        return root != null ? FindDeep(root, Sockets[slot - 1]) : null;
    }

    static Transform InstanceRoot(SkinPrefabHandler handler)
    {
        var instance = handler.instanceRef;
        if (instance == null && handler.TryGetInstance(out var found)) instance = found;
        if (instance != null) return instance.transform;
        return handler.socket; // where the handler parents its sticker
    }

    /// Writes what the mod sees on your phones to the BepInEx log (opening the Studio and "Stick it!").
    public static void Report(string why)
    {
        try
        {
            var log = new System.Text.StringBuilder($"[report: {why}] local player: {(PlayerRef.LocalPlayer != null ? PlayerRef.LocalPlayer.name : "none")}");
            foreach (var skins in Object.FindObjectsOfType<PlayerSkins>())
                log.Append($"\n  PlayerSkins '{skins.name}' owner={(skins.playerRef != null ? skins.playerRef.name : "null")} local={IsLocal(skins, PlayerRef.LocalPlayer)} handlers={skins.handlers?.Count}");
            var phones = LocalPhones();
            log.Append($"\n  phones used: {phones.Count}");
            for (var p = 0; p < phones.Count; p++)
            {
                var phone = phones[p];
                for (var slot = 1; slot <= 3; slot++)
                {
                    var h = Find(phone, Category(slot));
                    if (h == null) { log.Append($"\n  phone {p} Sticker{slot}: no handler"); continue; }
                    var root = InstanceRoot(h);
                    var socket = Socket(h, slot);
                    log.Append($"\n  phone {p} Sticker{slot}: '{h.name}' under '{h.transform.root.name}' key={h.CurrentSkinKey} instance={(h.instanceRef != null)} root={(root != null ? root.name : "none")} socket={(socket != null ? socket.name + (socket.gameObject.activeInHierarchy ? " (on)" : " (off)") : "none")}");
                    if (socket == null) continue;
                    foreach (var r in socket.GetComponentsInChildren<Renderer>(true))
                    {
                        var m = r.sharedMaterial;
                        var tex = m != null && m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                        log.Append($"\n      {r.name}: enabled={r.enabled} active={r.gameObject.activeInHierarchy} visible={r.isVisible} mat={(m != null ? m.name : "none")} shader={(m != null && m.shader != null ? m.shader.name : "none")} tex={(tex != null ? tex.name + " " + tex.width : "none")} tile={(m != null && m.HasProperty("_Tile") ? m.GetFloat("_Tile") : -1)}");
                    }
                }
            }
            Plugin.Logger.LogInfo(log.ToString());
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"report: {e}");
        }
    }

    static Transform FindDeep(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (var i = 0; i < parent.childCount; i++)
        {
            var found = FindDeep(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    // ---------------------------------------------------------------- putting it on

    static void ApplyTo(Il2CppSystem.Collections.Generic.List<SkinPrefabHandler> phone, int slot)
    {
        var target = Find(phone, Category(slot));
        if (target == null) return;
        var socket = Socket(target, slot);
        var tex = Current(slot);
        if (tex == null)
        {
            if (socket != null) Restore(socket);
            DestroyClone(target);
            return;
        }
        if (socket != null)
        {
            DestroyClone(target);
            Swap(socket, slot, tex);
            return;
        }
        // Empty slot: borrow this slot's mesh from another sticker on the same phone.
        var clone = EnsureClone(phone, target, slot);
        if (clone != null) Swap(clone.transform, slot, tex);
    }

    static void Swap(Transform socket, int slot, Texture2D tex)
    {
        foreach (var renderer in socket.GetComponentsInChildren<Renderer>(true))
        {
            var current = renderer.sharedMaterial;
            if (current == null) continue;
            var original = swapped.TryGetValue(renderer.Pointer, out var entry) && entry.renderer != null ? entry.original : current;
            var ours = OurMaterial(slot, original);
            if (ours.GetTexture("_MainTex")?.Pointer != tex.Pointer) SetTexture(ours, tex);
            if (current.Pointer != ours.Pointer) renderer.sharedMaterial = ours;
            swapped[renderer.Pointer] = (renderer, original);
        }
    }

    static void Restore(Transform socket)
    {
        foreach (var renderer in socket.GetComponentsInChildren<Renderer>(true))
        {
            if (!swapped.TryGetValue(renderer.Pointer, out var entry)) continue;
            if (entry.original != null) renderer.sharedMaterial = entry.original;
            swapped.Remove(renderer.Pointer);
        }
    }

    static GameObject EnsureClone(Il2CppSystem.Collections.Generic.List<SkinPrefabHandler> phone, SkinPrefabHandler target, int slot)
    {
        if (clones.TryGetValue(target.Pointer, out var existing) && existing != null) return existing;
        for (var i = 0; i < phone.Count; i++)
        {
            var other = phone[i];
            if (other == null || other.Pointer == target.Pointer || SlotOf(other.category) == 0) continue;
            var source = Socket(other, slot);
            if (source == null) continue;
            // A copy of the slot's mesh, inside the borrowed sticker so it follows the phone (and breaks off with it).
            var copy = Object.Instantiate(source.gameObject, source.parent);
            copy.name = CloneName + slot;
            copy.SetActive(true);
            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = !Regex.IsMatch(renderer.gameObject.name, @"_LOD[1-9]$"); // no LODGroup on the copy: keep LOD0 only
            clones[target.Pointer] = copy;
            Plugin.Logger.LogInfo($"Sticker {slot} was empty: borrowed its spot from {Pretty(other.CurrentSkinKey)}");
            return copy;
        }
        return null;
    }

    static void DestroyClone(SkinPrefabHandler target)
    {
        if (!clones.TryGetValue(target.Pointer, out var clone)) return;
        clones.Remove(target.Pointer);
        if (clone != null) Object.Destroy(clone);
    }

    /// Drops bookkeeping for renderers and copies the game has already destroyed.
    static void Forget()
    {
        var dead = new List<IntPtr>();
        foreach (var kv in swapped) if (kv.Value.renderer == null) dead.Add(kv.Key);
        foreach (var key in dead) swapped.Remove(key);
        dead.Clear();
        foreach (var kv in clones) if (kv.Value == null) dead.Add(kv.Key);
        foreach (var key in dead) clones.Remove(key);
    }

    // ---------------------------------------------------------------- material

    /// A copy of the game's sticker material that shows our whole texture: _Tile 1 / _Offset 0 instead of one cell
    /// of the sticker atlas, a flat normal map, plain glossy vinyl, and no holo / glow from the original sticker.
    static Material OurMaterial(int slot, Material original)
    {
        if (materials[slot - 1].TryGetValue(original.Pointer, out var ours) && ours != null) return ours;
        flatNormal ??= Textures.Solid(new Color32(128, 128, 255, 255), true);
        neutralMsao ??= Textures.Solid(new Color32(0, 255, 255, 255), true);
        ours = new Material(original) { name = $"FabuleoSticker{slot}", hideFlags = HideFlags.HideAndDontSave };
        Set(ours, "_Tile", 1f);
        if (ours.HasProperty("_Offset")) ours.SetVector("_Offset", Vector4.zero);
        if (ours.HasProperty("_NormalTex")) ours.SetTexture("_NormalTex", flatNormal);
        if (ours.HasProperty("_MSAOTex")) ours.SetTexture("_MSAOTex", neutralMsao);
        if (ours.HasProperty("_HoloMask")) ours.SetTexture("_HoloMask", Texture2D.blackTexture);
        if (ours.HasProperty("_EmissiveTex")) ours.SetTexture("_EmissiveTex", Texture2D.blackTexture);
        Set(ours, "_HOLOSTICKER", 0f);
        ours.DisableKeyword("_HOLOSTICKER");
        ours.DisableKeyword("_HOLOSTICKER_ON");
        Set(ours, "_EmissiveStrength", 0f);
        Set(ours, "_NormalStrength", 1f);
        Set(ours, "_SmoothnessStrength", 0.85f);
        materials[slot - 1][original.Pointer] = ours;
        return ours;
    }

    static void SetTexture(Material m, Texture2D tex)
    {
        m.SetTexture("_MainTex", tex);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
    }

    static void Set(Material m, string name, float value)
    {
        if (m.HasProperty(name)) m.SetFloat(name, value);
    }

    // ---------------------------------------------------------------- names and icons

    static SkinUISO LookupSkin(string key)
    {
        try
        {
            var manager = Object.FindObjectOfType<SkinsManager>();
            var library = manager != null ? manager.skinAddressableLibrary : null;
            if (library != null && library.TryGetLoaded(key, out var skin, false) && skin != null) return skin;
            foreach (var so in Resources.FindObjectsOfTypeAll<SkinUISO>())
                if (so != null && so.addressableKey == key) return so;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't find the sticker {key}: {e.Message}");
        }
        return null;
    }

    /// "SkinUI_Sticker_RossBurger" -> "Ross Burger".
    public static string Pretty(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var i = key.LastIndexOf("Sticker_", StringComparison.OrdinalIgnoreCase);
        var name = i >= 0 ? key.Substring(i + 8) : key;
        name = Regex.Replace(name.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
        return name.Trim();
    }
}

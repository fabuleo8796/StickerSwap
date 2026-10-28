using System;
using EHS.Skins;
using HarmonyLib;

namespace StickerStudio;

/// Hooks the game's sticker code. Postfixes only (CharmReplacer patches the same skin classes), and nothing here
/// changes what the game saves or sends to other players.
internal static class StickerPatches
{
    /// StickerSkinPrefabHandler.OnSkinApplied turns on the right Sticker_Socket_0N once a sticker has loaded.
    [HarmonyPatch(typeof(StickerSkinPrefabHandler), nameof(StickerSkinPrefabHandler.OnSkinApplied))]
    [HarmonyPostfix]
    static void StickerApplied(StickerSkinPrefabHandler __instance)
    {
        try { StickerSlots.OnStickerApplied(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"sticker applied: {e}"); }
    }

    /// The phone was put back together after breaking: re-check our stickers next frame.
    [HarmonyPatch(typeof(SkinPrefabHandler), nameof(SkinPrefabHandler.OnRepaired))]
    [HarmonyPostfix]
    static void Repaired() => StickerSlots.MarkDirty();

    /// A player's skins were (re)built, e.g. after loading into a world.
    [HarmonyPatch(typeof(PlayerSkins), nameof(PlayerSkins.Init))]
    [HarmonyPostfix]
    static void PlayerSkinsInit() => StickerSlots.MarkDirty();
}

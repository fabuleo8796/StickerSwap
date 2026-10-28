using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StickerStudio;

/// StickerSwap for Flipping is Hard: press K, pick Sticker 1, 2 or 3, open a picture, adjust it,
/// and it shows on the back of your phone in place of that sticker. Only on your own phone and only on your
/// computer: nothing is sent to other players and the game's saves are never touched. Everything the mod keeps
/// lives in BepInEx/config/FabuleosStickerStudio/.
[BepInPlugin(Guid, "StickerSwap", "1.0.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.stickerstudio";
    internal static ManualLogSource Logger;
    internal static ConfigEntry<Key> Hotkey;
    internal static ConfigEntry<int> Resolution;
    internal static ConfigEntry<string> FlipBackground;
    internal static ConfigEntry<bool> MacFilePicker;

    public override void Load()
    {
        Logger = Log;
        Hotkey = Config.Bind("General", "Hotkey", Key.K, "Opens and closes StickerSwap.");
        Resolution = Config.Bind("General", "StickerResolution", 512,
            new ConfigDescription("Size of your sticker textures in pixels (the game's own stickers are 256).",
                                  new AcceptableValueRange<int>(256, 1024)));
        FlipBackground = Config.Bind("Glass", "FlipBackground", "Auto",
            new ConfigDescription("Set to Yes if the blurred game behind the panel shows upside down (No forces it off).",
                                  new AcceptableValueList<string>("Auto", "Yes", "No")));
        MacFilePicker = Config.Bind("General", "MacFilePicker", true,
            "On a Mac (the game running in Wine), use the macOS file picker instead of the Windows one.");
        StickerStore.Load();
        ClassInjector.RegisterTypeInIl2Cpp<StudioDriver>();
        var host = new GameObject("FabuleosStickerStudio");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<StudioDriver>();
        new Harmony(Guid).PatchAll(typeof(StickerPatches));
        Log.LogInfo($"StickerSwap loaded ({StickerStore.CustomCount} custom sticker(s)), press {Hotkey.Value} in game");
    }
}

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace StickerStudio;

/// Keys and timing: the hotkey opens/closes the Studio (after grabbing the screen for the glass), Esc goes back,
/// Enter opens a picture / sticks it, 1-2-3 pick a slot. Also loads the saved stickers once the game is up, and
/// re-checks them every 1.5 s (only when there are custom stickers) so they survive the game rebuilding the phone.
public class StudioDriver : MonoBehaviour
{
    public StudioDriver(IntPtr ptr) : base(ptr) { }

    static readonly ConcurrentQueue<Action> mainThread = new();
    bool loaded, prepared, opening;
    float nextCheck;

    /// Runs an action on the game's main thread (the Open window answers from its own thread).
    public static void Post(Action action) => mainThread.Enqueue(action);

    void Update()
    {
        while (mainThread.TryDequeue(out var action)) Safe(action);
        if (!loaded) { loaded = true; Safe(LoadSaved); }
        // Build the panel a few seconds in (usually the main menu) so pressing the hotkey doesn't freeze the game.
        if (!prepared && Time.unscaledTime > 8f) { prepared = true; Safe(StudioPanel.Prepare); }
        var keyboard = Keyboard.current;
        try
        {
            GameInputPause.Tick(keyboard);
            if (keyboard != null && !opening && !FileDialog.IsOpen) Keys(keyboard);
            StudioPanel.Tick();
            if (StudioPanel.IsOpen) Squish();
            if (StickerSlots.Dirty || Time.unscaledTime >= nextCheck)
            {
                nextCheck = Time.unscaledTime + 1.5f;
                StickerSlots.RefreshAll();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"update: {e.Message}");
        }
    }

    [HideFromIl2Cpp]
    void Keys(Keyboard keyboard)
    {
        if (keyboard[Plugin.Hotkey.Value].wasPressedThisFrame && !TypingSomewhere())
        {
            if (StudioPanel.IsOpen) StudioPanel.Close();
            else { opening = true; StartCoroutine(OpenAfterThisFrame().WrapToIl2Cpp()); }
            return;
        }
        if (!StudioPanel.IsOpen) return;
        if (keyboard.escapeKey.wasPressedThisFrame) StudioPanel.Back();
        else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) StudioPanel.Confirm();
        else if (keyboard.digit1Key.wasPressedThisFrame) StudioPanel.SelectSlot(1);
        else if (keyboard.digit2Key.wasPressedThisFrame) StudioPanel.SelectSlot(2);
        else if (keyboard.digit3Key.wasPressedThisFrame) StudioPanel.SelectSlot(3);
    }

    /// The glass needs a picture of the game without the panel on it: grab the finished frame, then show the panel.
    [HideFromIl2Cpp]
    IEnumerator OpenAfterThisFrame()
    {
        yield return new WaitForEndOfFrame();
        try
        {
            Glass.Snapshot();
            StudioPanel.Open();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"open: {e}");
        }
        finally
        {
            opening = false;
        }
    }

    /// Buttons squish a little when pressed and grow a hair on hover (only while the panel is open).
    static void Squish()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        var pos = mouse.position.ReadValue();
        var step = Mathf.Clamp01(Time.unscaledDeltaTime * 18f);
        foreach (var rt in StudioPanel.Pressables)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            var hover = RectTransformUtility.RectangleContainsScreenPoint(rt, pos, null);
            var target = hover && mouse.leftButton.isPressed ? 0.95f : hover ? 1.02f : 1f;
            var s = rt.localScale.x;
            if (Mathf.Abs(s - target) < 0.001f) continue;
            s = Mathf.Lerp(s, target, step);
            rt.localScale = new Vector3(s, s, 1f);
        }
    }

    /// Re-makes each saved sticker from its picture copy and settings.
    static void LoadSaved()
    {
        var size = Mathf.Clamp(Plugin.Resolution.Value, 256, 1024);
        for (var slot = 1; slot <= 3; slot++)
        {
            var settings = StickerStore.Get(slot);
            if (settings == null) continue;
            var source = StickerStore.SourcePath(settings);
            var picture = Textures.ReadPicture(File.ReadAllBytes(source), source);
            if (picture == null)
            {
                Plugin.Logger.LogWarning($"Sticker {slot}: couldn't read {settings.Source}");
                continue;
            }
            var bytes = StickerBaker.Bake(picture, size, settings, StickerSlots.Tilt[slot - 1]);
            StickerSlots.SetSaved(slot, Textures.Upload(null, size, size, bytes));
            Plugin.Logger.LogInfo($"Sticker {slot}: {Path.GetFileName(settings.PickedFrom ?? settings.Source)}");
        }
    }

    /// Never steal the hotkey while the player types in one of the game's text fields.
    static bool TypingSomewhere()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.GetComponent<TMP_InputField>() != null;
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"{e}"); }
    }
}

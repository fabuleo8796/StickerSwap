using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace StickerStudio;

/// The StickerSwap panel: step 1 picks the slot and the picture, step 2 is the little editor. Liquid glass on the
/// left of the screen (design/studio_*.png). Positions are in canvas units from the panel's top-left corner, the same
/// numbers as the mockups (1920 x 1080 reference).
internal static class StudioPanel
{
    const float PW = 800, PH = 888, Radius = 48;
    static readonly Color32 Green = new(61, 255, 122, 255), Red = new(255, 77, 77, 255), White = new(255, 255, 255, 255);

    static GameObject root, toastRoot;
    static RectTransform panel;
    static CanvasGroup group, slotsView, editorView, toastGroup;
    static readonly List<GlassShape> shapes = new(), toastShapes = new();
    static readonly List<RectTransform> pressables = new();
    static TextMeshProUGUI title, subtitle, step, infoTitle, infoText, recentLabel, toastTitle;
    static readonly Card[] cards = new Card[3];
    static readonly Recent[] recents = new Recent[2];
    static Button editButton, restoreButton;
    static CanvasGroup editGroup, restoreGroup;
    static float openedAt, toastAt = -10f;
    static CursorLockMode oldLock;
    static bool oldVisible;

    // editor
    static RectTransform previewFrame;
    static RawImage preview;
    static Slider zoom, rotate, thickness;
    static TextMeshProUGUI zoomValue, rotateValue, thicknessValue;
    static Image borderPill, borderKnob;
    static ChipStyle fitStyle, fillStyle;
    static GlassShape fitShape, fillShape;
    static Picture picture;
    static byte[] pictureBytes;
    static string picturePath;
    static SlotSettings edit;
    static Texture2D previewTex;
    static bool bakeDirty, dragging;
    static float nextBake;
    static Vector2 dragFrom;

    public static int Slot { get; private set; } = 1;
    public static bool IsOpen => root != null && root.activeSelf;
    public static bool Editing => IsOpen && editorView != null && editorView.interactable;
    public static IReadOnlyList<RectTransform> Pressables => pressables;
    public static RectTransform Panel => panel;

    // ---------------------------------------------------------------- open / close

    /// Builds the panel (fonts, glass, widgets) ahead of time, hidden, so the first press of the hotkey is instant.
    public static void Prepare()
    {
        if (root != null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Build();
        Plugin.Logger.LogInfo($"StickerSwap ready ({watch.ElapsedMilliseconds} ms)");
    }

    public static void Open()
    {
        if (root == null) Build();
        EnsureEventSystem();
        root.SetActive(true);
        panel.localScale = Vector3.one;
        Canvas.ForceUpdateCanvases();
        ShowSlots();
        foreach (var s in shapes) s.Rebuild();
        panel.localScale = new Vector3(0.96f, 0.96f, 1f);
        group.alpha = 0f;
        openedAt = -1f; // the fade starts on the next frame, however long opening took
        GameInputPause.Begin();
        StickerSlots.Report("studio opened");
        oldLock = Cursor.lockState; oldVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    public static void Close()
    {
        if (!IsOpen) return;
        if (Editing) StickerSlots.EndPreview(Slot);
        dragging = false;
        root.SetActive(false);
        GameInputPause.End();
        Cursor.lockState = oldLock; Cursor.visible = oldVisible;
    }

    /// Esc: leave the editor (without saving), or close.
    public static void Back()
    {
        if (Editing) CancelEdit(); else Close();
    }

    /// Enter: open a picture, or stick it.
    public static void Confirm()
    {
        if (FileDialog.IsOpen) return;
        if (Editing) StickIt(); else OpenPicture();
    }

    public static void SelectSlot(int slot)
    {
        if (Editing || slot < 1 || slot > 3) return;
        Slot = slot;
        RefreshSlots();
    }

    // ---------------------------------------------------------------- per frame (only while something moves)

    public static void Tick()
    {
        if (toastRoot != null && toastRoot.activeSelf)
        {
            var t = Time.unscaledTime - toastAt;
            toastGroup.alpha = t < 0.2f ? t / 0.2f : t > 2.6f ? Mathf.Clamp01(1 - (t - 2.6f) / 0.35f) : 1f;
            if (t > 3f) toastRoot.SetActive(false);
        }
        if (!IsOpen) return;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None; // the game may grab it back
        if (!Cursor.visible) Cursor.visible = true;
        if (openedAt < 0f) openedAt = Time.unscaledTime;
        var since = Time.unscaledTime - openedAt;
        if (since < 0.3f)
        {
            var k = Mathf.Clamp01(since / 0.22f);
            var back = 1f + 2.2f * Mathf.Pow(k - 1f, 3) + 1.2f * Mathf.Pow(k - 1f, 2); // ease-out-back
            var s = Mathf.LerpUnclamped(0.96f, 1f, back);
            panel.localScale = new Vector3(s, s, 1f);
            group.alpha = Mathf.Clamp01(since / 0.16f);
        }
        else if (group.alpha < 1f || panel.localScale.x != 1f)
        {
            // a slow frame skipped past the fade: land on fully shown
            group.alpha = 1f;
            panel.localScale = Vector3.one;
        }
        if (Editing) EditorTick();
    }

    // ---------------------------------------------------------------- step 1: slots

    static void ShowSlots()
    {
        Show(slotsView, true); Show(editorView, false);
        title.text = "StickerSwap";
        subtitle.text = "Pick the sticker you want to replace";
        step.text = "1 / 2";
        RefreshSlots();
    }

    static void RefreshSlots()
    {
        for (var i = 0; i < 3; i++) cards[i].Refresh(i + 1 == Slot);
        var custom = StickerStore.Get(Slot) != null;
        var (_, gameName) = StickerSlots.GameSticker(Slot);
        infoTitle.text = $"Sticker {Slot} · " + (custom ? "Your picture" : gameName ?? "Empty");
        Message(custom ? "Your picture is on this spot. Open a new one, edit it, or bring the game's sticker back."
                       : "Your picture will take its place on the back of your phone.", false);
        SetEnabled(editButton, editGroup, custom);
        SetEnabled(restoreButton, restoreGroup, custom);
        RefreshRecent();
    }

    static void Message(string text, bool error)
    {
        infoText.text = text;
        infoText.color = error ? new Color(1f, 0.62f, 0.6f, 1f) : new Color(1, 1, 1, 0.68f);
    }

    static void OpenPicture()
    {
        Message("Pick a picture in the window that just opened...", false);
        FileDialog.Open(path =>
        {
            if (path == null) { RefreshSlots(); return; }
            StickerStore.LastFolder = Path.GetDirectoryName(path);
            Load(path, null);
        }, error =>
        {
            Plugin.Logger.LogWarning($"Open window: {error}");
            Message($"The Open window didn't work here. Put pictures in BepInEx/config/FabuleosStickerStudio/inbox and pick them under Recent.", true);
            RefreshRecent();
        });
    }

    /// Opens a picture in the editor: a file the player picked, or this slot's saved picture (settings != null).
    static void Load(string path, SlotSettings settings)
    {
        byte[] bytes;
        Picture pic;
        try
        {
            bytes = File.ReadAllBytes(path);
            pic = Textures.ReadPicture(bytes, path);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read {path}: {e.Message}");
            pic = null; bytes = null;
        }
        if (pic == null)
        {
            Message($"Couldn't open {Path.GetFileName(path)}. Try a PNG or JPG picture.", true);
            return;
        }
        if (!StickerSlots.CanShow(Slot))
        {
            Message("Put any sticker on your phone first (customize), then come back. The Studio needs a sticker spot to use.", true);
            return;
        }
        picture = pic; pictureBytes = bytes;
        picturePath = settings?.PickedFrom ?? path;
        edit = settings?.Copy() ?? new SlotSettings();
        if (settings == null) StickerStore.AddRecent(path);
        ShowEditor();
    }

    // ---------------------------------------------------------------- step 2: editor

    static void ShowEditor()
    {
        Show(slotsView, false); Show(editorView, true);
        title.text = "Adjust sticker";
        subtitle.text = "Drag to move · scroll to zoom";
        step.text = "2 / 2";
        var tilt = StickerSlots.Tilt[Slot - 1];
        // The baked texture is turned by the slot's tilt; turn the preview back so it shows straight.
        var fit = 296f / (Mathf.Abs(Mathf.Cos(tilt * Mathf.Deg2Rad)) + Mathf.Abs(Mathf.Sin(tilt * Mathf.Deg2Rad)));
        preview.rectTransform.sizeDelta = new Vector2(fit, fit);
        preview.rectTransform.localEulerAngles = new Vector3(0, 0, -tilt);
        SyncControls();
        Bake(true);
    }

    static void SyncControls()
    {
        zoom.SetValueWithoutNotify(ZoomToSlider(edit.Zoom));
        rotate.SetValueWithoutNotify(edit.Rotation);
        thickness.SetValueWithoutNotify(edit.BorderPx);
        zoomValue.text = $"{Mathf.RoundToInt(edit.Zoom * 100)} %";
        rotateValue.text = $"{Mathf.RoundToInt(edit.Rotation)}°";
        thicknessValue.text = edit.Border ? $"{Mathf.RoundToInt(edit.BorderPx)} px" : "off";
        borderPill.color = edit.Border ? new Color32(Green.r, Green.g, Green.b, 200) : new Color32(255, 255, 255, 40);
        borderKnob.rectTransform.anchoredPosition = new Vector2(edit.Border ? 13 : -13, 0);
        if (fitStyle.Active == edit.Fill)
        {
            fitStyle.Active = !edit.Fill; fillStyle.Active = edit.Fill;
            fitShape.Rebuild(); fillShape.Rebuild();
        }
    }

    static void Changed()
    {
        bakeDirty = true;
        SyncControls();
    }

    static void EditorTick()
    {
        var mouse = Mouse.current;
        if (mouse != null && !FileDialog.IsOpen)
        {
            var pos = mouse.position.ReadValue();
            var over = RectTransformUtility.RectangleContainsScreenPoint(previewFrame, pos, null);
            if (mouse.leftButton.wasPressedThisFrame && over) { dragging = true; dragFrom = pos; }
            if (!mouse.leftButton.isPressed) dragging = false;
            if (dragging && pos != dragFrom)
            {
                // Screen pixels -> fraction of the sticker (the preview shows the sticker's width as its own size).
                var scale = preview.canvas != null ? preview.canvas.scaleFactor : 1f;
                var shown = preview.rectTransform.sizeDelta.x * scale;
                edit.OffsetX += (pos.x - dragFrom.x) / shown;
                edit.OffsetY += (pos.y - dragFrom.y) / shown;
                edit.OffsetX = Mathf.Clamp(edit.OffsetX, -0.75f, 0.75f);
                edit.OffsetY = Mathf.Clamp(edit.OffsetY, -0.75f, 0.75f);
                dragFrom = pos;
                bakeDirty = true;
            }
            var wheel = mouse.scroll.ReadValue().y;
            if (over && Mathf.Abs(wheel) > 0.01f)
            {
                edit.Zoom = Mathf.Clamp(edit.Zoom * (wheel > 0 ? 1.08f : 1f / 1.08f), 0.1f, 4f);
                Changed();
            }
        }
        if (bakeDirty && Time.unscaledTime >= nextBake) Bake(false);
    }

    /// The editor's preview bake (320 px), also shown live on the phone. ~10 per second at most.
    static void Bake(bool now)
    {
        if (picture == null) return;
        bakeDirty = false;
        nextBake = Time.unscaledTime + (now ? 0f : 0.1f);
        try
        {
            const int size = 320;
            var bytes = StickerBaker.Bake(picture, size, edit, StickerSlots.Tilt[Slot - 1]);
            previewTex = Textures.Upload(previewTex, size, size, bytes);
            preview.texture = previewTex;
            StickerSlots.ShowPreview(Slot, previewTex);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"preview: {e}");
        }
    }

    static void StickIt()
    {
        if (picture == null) return;
        try
        {
            var size = Mathf.Clamp(Plugin.Resolution.Value, 256, 1024);
            var bytes = StickerBaker.Bake(picture, size, edit, StickerSlots.Tilt[Slot - 1]);
            var tex = Textures.Upload(StickerSlots.Saved(Slot), size, size, bytes);
            StickerStore.Set(Slot, edit.Copy(), pictureBytes, picturePath);
            StickerSlots.EndPreview(Slot);
            StickerSlots.SetSaved(Slot, tex);
            Plugin.Logger.LogInfo($"Sticker {Slot} is now {Path.GetFileName(picturePath)}");
            StickerSlots.Report("stick it");
            var slot = Slot;
            Close();
            Toast($"Stuck on Sticker {slot}!");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"stick it: {e}");
            Message("Something went wrong making the sticker (see BepInEx/LogOutput.log).", true);
        }
    }

    static void CancelEdit()
    {
        StickerSlots.EndPreview(Slot);
        dragging = false;
        ShowSlots();
    }

    static void EditSaved()
    {
        var saved = StickerStore.Get(Slot);
        var path = StickerStore.SourcePath(saved);
        if (saved != null && File.Exists(path)) Load(path, saved);
    }

    static void RestoreOriginal()
    {
        StickerStore.Clear(Slot);
        StickerSlots.SetSaved(Slot, null);
        Plugin.Logger.LogInfo($"Sticker {Slot} is the game's sticker again");
        RefreshSlots();
    }

    static float ZoomToSlider(float z) => Mathf.InverseLerp(Mathf.Log(0.1f), Mathf.Log(4f), Mathf.Log(Mathf.Max(z, 0.1f)));
    static float SliderToZoom(float v) => Mathf.Exp(Mathf.Lerp(Mathf.Log(0.1f), Mathf.Log(4f), v));

    // ---------------------------------------------------------------- recent pictures

    static void RefreshRecent()
    {
        var list = new List<string>();
        foreach (var p in StickerStore.InboxPictures()) if (!list.Contains(p)) list.Add(p);
        foreach (var p in StickerStore.Recent) if (File.Exists(p) && !list.Contains(p)) list.Add(p);
        recentLabel.gameObject.SetActive(list.Count > 0);
        for (var i = 0; i < recents.Length; i++) recents[i].Set(i < list.Count ? list[i] : null);
    }

    // ---------------------------------------------------------------- toast

    static void Toast(string text)
    {
        if (toastRoot == null) BuildToast();
        toastTitle.text = text;
        toastRoot.SetActive(true);
        foreach (var s in toastShapes) s.Rebuild();
        toastAt = Time.unscaledTime;
        toastGroup.alpha = 0f;
    }

    // ================================================================ building

    static void Build()
    {
        root = NewCanvas("FabuleosStickerStudio", 30000, out _);
        group = root.AddComponent<CanvasGroup>();

        var panelGo = new GameObject("Panel");
        panel = panelGo.AddComponent<RectTransform>();
        panel.SetParent(root.transform, false);
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(PW, PH);
        panel.anchoredPosition = new Vector2(90 + PW / 2 - 960, 540 - (96 + PH / 2));

        // A plain dark rounded backing, only seen if the glass meshes can't draw on this setup.
        var backing = NewImage(panel, "Backing", Assets.RoundedPanel, new Color(0.09f, 0.1f, 0.13f, 0.86f), 0, 0, PW, PH);
        backing.type = Image.Type.Sliced;
        // the glass: shadow, lens (blurred game, bent at the edge), then tint + gloss + rim on top
        AddShape(panel, "Shadow", s => new MeshBuilder().Rounded(new Vector2(0, -18), new Vector2(PW, PH), Radius,
            new[] { -70f, -46f, -26f, -10f, 0f, 24f },
            (p, n, d) => new Color32(0, 0, 0, (byte)(97 * Smooth(1f + d / 70f)))), 0, 0, PW, PH);
        AddShape(panel, "Lens", Lens, 0, 0, PW, PH, () => Glass.Backdrop);
        AddShape(panel, "Tint", s => Overlay(new Vector2(PW, PH), Radius, new Color32(16, 18, 26, 92), 34, 210, 36, 2.2f), 0, 0, PW, PH);

        title = Label(panel, "", Assets.SemiBold, 44, White, 48, 46, 600, 60);
        subtitle = Label(panel, "", Assets.Regular, 23, new Color(1, 1, 1, 0.68f), 48, 112, 600, 32);
        AddShape(panel, "StepChip", s => Chip(new Vector2(88, 40), 20, new Color32(255, 255, 255, 26), 150, null, false), 664, 40, 88, 40);
        step = Label(panel, "", Assets.Medium, 19, new Color(1, 1, 1, 0.82f), 664, 40, 88, 40, TextAlignmentOptions.Center);

        slotsView = View("Slots");
        editorView = View("Editor");
        BuildSlots(slotsView.GetComponent<RectTransform>());
        BuildEditor(editorView.GetComponent<RectTransform>());
        root.SetActive(false);
    }

    static GameObject NewCanvas(string name, int order, out Canvas canvas)
    {
        var go = new GameObject(name);
        Object.DontDestroyOnLoad(go);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return go;
    }

    static CanvasGroup View(string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(panel, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        return go.AddComponent<CanvasGroup>();
    }

    static void Show(CanvasGroup view, bool on)
    {
        view.alpha = on ? 1f : 0f;
        view.interactable = on;
        view.blocksRaycasts = on;
    }

    static void BuildSlots(RectTransform view)
    {
        for (var i = 0; i < 3; i++) cards[i] = new Card(view, i + 1, 48 + i * 239, 184);
        infoTitle = Label(view, "", Assets.SemiBold, 26, White, 48, 452, 704, 36);
        infoText = Label(view, "", Assets.Regular, 21, new Color(1, 1, 1, 0.68f), 48, 490, 704, 30);
        infoText.overflowMode = TextOverflowModes.Ellipsis;

        BigButton(view, "Open picture", "folder", 48, 546, 704, 72, Green, true, OpenPicture);
        editButton = BigButton(view, "Edit current", "move", 48, 634, 344, 64, null, false, EditSaved);
        editGroup = editButton.gameObject.AddComponent<CanvasGroup>();
        restoreButton = BigButton(view, "Restore original", "undo", 408, 634, 344, 64, null, false, RestoreOriginal);
        restoreGroup = restoreButton.gameObject.AddComponent<CanvasGroup>();

        recentLabel = Label(view, "Recent", Assets.Medium, 20, new Color(1, 1, 1, 0.58f), 48, 712, 300, 26);
        for (var i = 0; i < recents.Length; i++) recents[i] = new Recent(view, 48 + i * 356, 744);

        var x = 48f;
        foreach (var (key, label) in new[] { (Plugin.Hotkey.Value.ToString(), "Close"), ("Esc", "Back"), ("Enter", "Open"), ("1 2 3", "Slot") })
            x = KeyHint(view, x, 850, key, label);
    }

    static void BuildEditor(RectTransform view)
    {
        AddShape(view, "PreviewGlass", s => Chip(new Vector2(320, 320), 32, new Color32(255, 255, 255, 18), 150, null, true), 48, 176, 320, 320);
        previewFrame = Node(view, "Preview", 48, 176, 320, 320);
        var check = NewRaw(previewFrame, "Checker", Assets.Checker, 12, 12, 296, 296, 320);
        check.uvRect = new Rect(0, 0, 296 / 36f, 296 / 36f);
        preview = NewRaw(previewFrame, "Sticker", null, 12, 12, 296, 296, 320);
        NewImage(previewFrame, "MoveHint", Assets.Icon("move"), new Color(1, 1, 1, 0.6f), 278, 278, 28, 28, 320);

        var tools = new (string icon, string label, Action click)[]
        {
            ("rotl", "-90°", () => { edit.Rotation = Wrap(edit.Rotation + 90); Changed(); }),
            ("rotr", "+90°", () => { edit.Rotation = Wrap(edit.Rotation - 90); Changed(); }),
            ("flip", "Flip", () => { edit.Flip = !edit.Flip; Changed(); }),
            ("fit", "Fit", () => { edit.Fill = false; edit.Zoom = 1; edit.OffsetX = edit.OffsetY = 0; Changed(); }),
            ("fill", "Fill", () => { edit.Fill = true; edit.Zoom = 1; edit.OffsetX = edit.OffsetY = 0; Changed(); }),
            ("reset", "Reset", () => { edit = new SlotSettings(); Changed(); }),
        };
        for (var i = 0; i < tools.Length; i++)
        {
            float cx = 440 + i % 3 * 120, cy = 232 + i / 3 * 136;
            var style = new ChipStyle { Fill = new Color32(255, 255, 255, 30) };
            var (b, shape) = RoundButton(view, tools[i].icon, cx, cy, 62, style, tools[i].click);
            Label(view, tools[i].label, Assets.Medium, 18, new Color(1, 1, 1, 0.72f), cx - 60, cy + 38, 120, 26, TextAlignmentOptions.Center);
            if (tools[i].icon == "fit") { fitStyle = style; fitShape = shape; }
            if (tools[i].icon == "fill") { fillStyle = style; fillShape = shape; }
        }

        zoom = SliderRow(view, 548, "zoom", "Zoom", 0, 1, v => { edit.Zoom = SliderToZoom(v); Changed(); }, out zoomValue);
        rotate = SliderRow(view, 612, "rotr", "Rotate", -180, 180, v => { edit.Rotation = Mathf.Round(v); Changed(); }, out rotateValue);
        NewImage(view, "BorderIcon", Assets.Icon("border"), new Color(1, 1, 1, 0.78f), 48, 664, 24, 24);
        Label(view, "White border", Assets.Medium, 21, new Color(1, 1, 1, 0.84f), 86, 660, 300, 32);
        var toggle = MakeButton(view, "BorderToggle", 692, 659, 60, 34, () => { edit.Border = !edit.Border; Changed(); });
        borderPill = NewImage(toggle.transform, "Pill", Assets.Rounded, Color.white, 0, 0, 60, 34, 60, 34);
        borderPill.type = Image.Type.Sliced;
        borderPill.raycastTarget = false;
        borderKnob = NewImage(borderPill.transform, "Knob", Assets.Circle, Color.white, 17, 4, 26, 26, 60, 34);
        borderKnob.raycastTarget = false;
        thickness = SliderRow(view, 738, "border", "Thickness", 0, 24, v => { edit.BorderPx = Mathf.Round(v); edit.Border = edit.BorderPx > 0; Changed(); }, out thicknessValue);

        BigButton(view, "Cancel", "close", 48, 784, 252, 72, Red, false, CancelEdit);
        BigButton(view, "Stick it!", "check", 320, 784, 432, 72, Green, true, StickIt);
    }

    static void BuildToast()
    {
        toastRoot = NewCanvas("FabuleosStickerStudioToast", 30001, out _);
        toastGroup = toastRoot.AddComponent<CanvasGroup>();
        toastGroup.blocksRaycasts = false;
        var box = Node(toastRoot.transform, "Toast", 0, 0, 520, 90, 0, 0);
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0f);
        box.anchoredPosition = new Vector2(0, 105);
        toastShapes.Add(new GlassShape(box, "Glass", s => Chip(new Vector2(520, 90), 45, new Color32(20, 22, 30, 170), 190, null, true)));
        toastShapes[0].Rect.sizeDelta = new Vector2(520, 90);
        var dot = NewImage(box, "Dot", Assets.Circle, (Color)Green, 26, 22, 46, 46, 520, 90);
        NewImage(dot.transform, "Check", Assets.Icon("check"), new Color32(10, 40, 20, 255), 8, 8, 30, 30, 46, 46);
        toastTitle = Label(box, "", Assets.SemiBold, 26, White, 92, 14, 400, 36, TextAlignmentOptions.Left, 520, 90);
        Label(box, $"Press {Plugin.Hotkey.Value} to change it anytime", Assets.Regular, 19, new Color(1, 1, 1, 0.68f), 92, 48, 400, 28, TextAlignmentOptions.Left, 520, 90);
        toastRoot.SetActive(false);
    }

    // ---------------------------------------------------------------- glass meshes

    static MeshBuilder Lens(GlassShape shape)
    {
        var size = new Vector2(PW, PH);
        const float band = 42f, push = 38f;
        var hasBackdrop = Glass.Backdrop != null;
        return new MeshBuilder().Rounded(Vector2.zero, size, Radius, new[] { -1f, 0f, 2f, 5f, 9f, 14f, 20f, 27f, 34f, 42f, 120f, 260f },
            (p, n, d) => d < 0 ? new Color32(220, 224, 232, 0) : hasBackdrop ? new Color32(220, 224, 232, 255) : new Color32(36, 40, 52, 225),
            (p, n, d) =>
            {
                var bend = d < band ? push * Mathf.Pow(1f - Mathf.Max(d, 0f) / band, 2f) : 0f;
                return Glass.BackdropUV(PanelToScreen(p + n * bend));
            });
    }

    /// A point of the panel (centre-origin canvas units) on screen, using the CanvasScaler's own formula
    /// (1920x1080 reference, match 0.5), so it's right even before the canvas has laid itself out.
    static Vector2 PanelToScreen(Vector2 local)
    {
        float w = Screen.width, h = Screen.height;
        var scale = Mathf.Sqrt(w / 1920f * (h / 1080f));
        return new Vector2(w / 2f, h / 2f) + (panel.anchoredPosition + local) * scale;
    }

    /// Tint, a soft top gloss and the bright edge (strong top-left, faint bottom-right).
    static MeshBuilder Overlay(Vector2 size, float radius, Color32 tint, byte gloss, byte strong, byte weak, float rimWidth)
    {
        var top = size.y / 2f;
        return new MeshBuilder()
            .Rounded(Vector2.zero, size, radius, new[] { -1f, 0f, 3f, 10f, 24f, 60f, 140f },
                (p, n, d) =>
                {
                    if (d < 0) return new Color32(tint.r, tint.g, tint.b, 0);
                    var g = Mathf.Clamp01(1f - (top - p.y) / (size.y * 0.45f)) * gloss / 255f;
                    return Mix(tint, g);
                })
            .Rounded(Vector2.zero, size, radius, new[] { -0.8f, 0f, rimWidth, rimWidth + 1.2f },
                (p, n, d) => new Color32(255, 255, 255, (byte)(d < 0 || d > rimWidth ? 0 : Mathf.Lerp(weak, strong, Mathf.Clamp01(0.5f + 0.5f * Vector2.Dot(n, new Vector2(-0.7071f, 0.7071f)))))),
                fill: false);
    }

    /// Small glass-on-glass element (cards, buttons, chips): optional glow and shadow, fill with gloss, rim.
    static MeshBuilder Chip(Vector2 size, float radius, Color32 fill, byte rimStrong, Color32? glow, bool shadow, Color32? rimColor = null)
    {
        var mb = new MeshBuilder();
        if (glow is Color32 g)
            mb.Rounded(Vector2.zero, size, radius, new[] { -30f, -18f, -8f, 0f },
                (p, n, d) => new Color32(g.r, g.g, g.b, (byte)(150 * Smooth(1f + d / 30f))), fill: false);
        if (shadow)
            mb.Rounded(new Vector2(0, -6), size, radius, new[] { -16f, -8f, 0f, 8f },
                (p, n, d) => new Color32(0, 0, 0, (byte)(56 * Smooth(1f + d / 16f))), fill: false);
        var top = size.y / 2f;
        mb.Rounded(Vector2.zero, size, radius, new[] { -0.8f, 0f, 3f, 12f, 40f },
            (p, n, d) => d < 0 ? new Color32(fill.r, fill.g, fill.b, 0)
                               : Mix(fill, Mathf.Clamp01(1f - (top - p.y) / (size.y * 0.5f)) * 0.11f));
        var rc = rimColor ?? White;
        mb.Rounded(Vector2.zero, size, radius, new[] { -0.7f, 0f, 1.6f, 2.6f },
            (p, n, d) => new Color32(rc.r, rc.g, rc.b, (byte)(d < 0 || d > 1.6f ? 0 : Mathf.Lerp(30, rimStrong, Mathf.Clamp01(0.5f + 0.5f * Vector2.Dot(n, new Vector2(-0.7071f, 0.7071f)))))),
            fill: false);
        return mb;
    }

    static Color32 Mix(Color32 c, float whiten) =>
        new((byte)Mathf.Lerp(c.r, 255, whiten), (byte)Mathf.Lerp(c.g, 255, whiten), (byte)Mathf.Lerp(c.b, 255, whiten),
            (byte)Mathf.Min(255, c.a + whiten * 255 * 0.6f));

    static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    // ---------------------------------------------------------------- widgets

    sealed class ChipStyle
    {
        public Color32 Fill = new(255, 255, 255, 26);
        public bool Active;
    }

    sealed class Card
    {
        readonly int slot;
        readonly ChipStyle style = new();
        readonly GlassShape shape;
        readonly Image icon, check, star;
        readonly RawImage custom;

        public Card(RectTransform parent, int slot, float x, float y)
        {
            this.slot = slot;
            var b = MakeButton(parent, $"Sticker{slot}", x, y, 214, 240, () => SelectSlot(slot));
            shape = AddShape(b.transform, "Glass", s => Chip(new Vector2(214, 240), 30,
                    new Color32(255, 255, 255, (byte)(style.Active ? 40 : 22)), (byte)(style.Active ? 255 : 140),
                    style.Active ? Green : null, true, style.Active ? Green : null), 0, 0, 214, 240, null, 214, 240);
            icon = NewImage(b.transform, "Icon", null, Color.white, 32, 22, 150, 150, 214, 240);
            icon.preserveAspect = true;
            custom = NewRaw(b.transform, "Custom", null, 32, 22, 150, 150, 214, 240);
            var t = StickerSlots.Tilt[slot - 1];
            custom.rectTransform.localEulerAngles = new Vector3(0, 0, -t);
            Label(b.transform, $"Sticker {slot}", Assets.SemiBold, 23, White, 0, 188, 214, 32, TextAlignmentOptions.Center, 214, 240);
            check = NewImage(b.transform, "Check", Assets.Circle, (Color)Green, 168, 14, 34, 34, 214, 240);
            NewImage(check.transform, "Tick", Assets.Icon("check"), new Color32(10, 40, 20, 255), 6, 6, 22, 22, 34, 34);
            star = NewImage(b.transform, "Custom", Assets.Icon("star"), new Color(1, 1, 1, 0.9f), 14, 14, 26, 26, 214, 240);
        }

        public void Refresh(bool selected)
        {
            style.Active = selected;
            if (StudioPanel.IsOpen) shape.Rebuild();
            check.enabled = selected;
            check.transform.GetChild(0).GetComponent<Image>().enabled = selected;
            var saved = StickerSlots.Saved(slot);
            var hasCustom = StickerStore.Get(slot) != null && saved != null;
            star.enabled = hasCustom;
            custom.enabled = hasCustom;
            custom.texture = saved;
            var (sprite, _) = hasCustom ? (null, null) : StickerSlots.GameSticker(slot);
            icon.sprite = sprite;
            icon.enabled = sprite != null;
        }
    }

    sealed class Recent
    {
        static readonly Dictionary<string, Texture2D> thumbs = new();
        readonly GameObject go;
        readonly RawImage thumb;
        readonly TextMeshProUGUI name, where;
        string path;

        public Recent(RectTransform parent, float x, float y)
        {
            var b = MakeButton(parent, "Recent", x, y, 340, 80, () => { if (path != null) Load(path, null); });
            go = b.gameObject;
            AddShape(b.transform, "Glass", s => Chip(new Vector2(80, 80), 20, new Color32(255, 255, 255, 26), 140, null, false), 0, 0, 80, 80, null, 340, 80);
            thumb = NewRaw(b.transform, "Thumb", null, 8, 8, 64, 64, 340, 80);
            name = Label(b.transform, "", Assets.Medium, 21, White, 96, 12, 240, 28, TextAlignmentOptions.Left, 340, 80);
            where = Label(b.transform, "", Assets.Regular, 17, new Color(1, 1, 1, 0.58f), 96, 42, 240, 24, TextAlignmentOptions.Left, 340, 80);
        }

        public void Set(string p)
        {
            path = p;
            go.SetActive(p != null);
            if (p == null) return;
            name.text = Path.GetFileName(p);
            where.text = p.StartsWith(StickerStore.Inbox, StringComparison.OrdinalIgnoreCase) ? "Inbox" : Path.GetFileName(Path.GetDirectoryName(p));
            thumb.texture = Thumb(p);
            foreach (var s in shapes) if (s.Rect.IsChildOf(go.transform)) s.Rebuild();
        }

        static Texture2D Thumb(string p)
        {
            if (thumbs.TryGetValue(p, out var t) && t != null) return t;
            try
            {
                var pic = Textures.ReadPicture(File.ReadAllBytes(p), p);
                if (pic == null) return null;
                var small = pic.Shrunk(Math.Max(1, Math.Max(pic.Width, pic.Height) / 128));
                var bytes = StickerBaker.Bake(small, 128, new SlotSettings { Border = false }, 0f);
                return thumbs[p] = Textures.Upload(null, 128, 128, bytes);
            }
            catch { return null; }
        }
    }

    static Button BigButton(RectTransform parent, string text, string iconName, float x, float y, float w, float h, Color32? tint, bool primary, Action click)
    {
        var b = MakeButton(parent, text, x, y, w, h, click);
        var fill = tint is Color32 t ? new Color32(t.r, t.g, t.b, 70) : new Color32(255, 255, 255, 30);
        AddShape(b.transform, "Glass", s => Chip(new Vector2(w, h), h / 2, fill, (byte)(primary ? 190 : 150), primary ? tint : null, true), 0, 0, w, h, null, w, h);
        var font = Assets.SemiBold;
        var label = Label(b.transform, text, font, 24, White, 0, 0, w, h, TextAlignmentOptions.Center, w, h);
        var textWidth = label.GetPreferredValues(text).x;
        var start = (w - textWidth - 38) / 2f;
        NewImage(b.transform, "Icon", Assets.Icon(iconName), Color.white, start, (h - 26) / 2, 26, 26, w, h);
        label.rectTransform.anchoredPosition = new Vector2(19, 0);
        return b;
    }

    static (Button, GlassShape) RoundButton(RectTransform parent, string iconName, float cx, float cy, float size, ChipStyle style, Action click)
    {
        var b = MakeButton(parent, iconName, cx - size / 2, cy - size / 2, size, size, click);
        var shape = AddShape(b.transform, "Glass", s => Chip(new Vector2(size, size), size / 2,
                style.Active ? new Color32(Green.r, Green.g, Green.b, 60) : style.Fill, 150, style.Active ? Green : null, true), 0, 0, size, size, null, size, size);
        NewImage(b.transform, "Icon", Assets.Icon(iconName), Color.white, (size - 26) / 2, (size - 26) / 2, 26, 26, size, size);
        return (b, shape);
    }

    static Slider SliderRow(RectTransform parent, float y, string iconName, string text, float min, float max, Action<float> changed, out TextMeshProUGUI value)
    {
        NewImage(parent, text + "Icon", Assets.Icon(iconName), new Color(1, 1, 1, 0.78f), 48, y - 12, 24, 24);
        Label(parent, text, Assets.Medium, 21, new Color(1, 1, 1, 0.84f), 86, y - 16, 120, 32);
        value = Label(parent, "", Assets.Medium, 20, new Color(1, 1, 1, 0.74f), 672, y - 16, 80, 32, TextAlignmentOptions.Right);

        var root = Node(parent, text + "Slider", 198, y - 16, 464, 32);
        var hit = root.gameObject.AddComponent<Image>();
        hit.color = new Color(1, 1, 1, 0);
        var track = NewImage(root, "Track", Assets.Rounded, new Color(1, 1, 1, 0.14f), 0, 11, 464, 10, 464, 32);
        track.type = Image.Type.Sliced; track.raycastTarget = false;
        var fillArea = Node(root, "Fill Area", 0, 11, 464, 10, 464, 32);
        var fill = NewImage(fillArea, "Fill", Assets.Rounded, (Color)new Color32(Green.r, Green.g, Green.b, 220), 0, 0, 0, 10, 464, 10);
        fill.type = Image.Type.Sliced; fill.raycastTarget = false;
        fill.rectTransform.anchorMin = Vector2.zero; fill.rectTransform.anchorMax = new Vector2(0, 1); fill.rectTransform.sizeDelta = Vector2.zero;
        var handleArea = Node(root, "Handle Area", 15, 0, 434, 32, 464, 32);
        var handle = NewImage(handleArea, "Knob", Assets.Circle, Color.white, 0, 1, 30, 30, 434, 32);
        handle.rectTransform.anchorMin = new Vector2(0, 0.5f); handle.rectTransform.anchorMax = new Vector2(0, 0.5f);
        handle.rectTransform.anchoredPosition = Vector2.zero;

        var slider = root.gameObject.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = min; slider.maxValue = max;
        slider.onValueChanged.AddListener((UnityAction<float>)new Action<float>(v => Safe(() => changed(v))));
        pressables.Add(handle.rectTransform);
        return slider;
    }

    static float KeyHint(RectTransform parent, float x, float y, string key, string text)
    {
        var keyLabel = Label(parent, key, Assets.Medium, 18, White, x, y - 15, 200, 30, TextAlignmentOptions.Center);
        var kw = keyLabel.GetPreferredValues(key).x + 22;
        keyLabel.rectTransform.sizeDelta = new Vector2(kw, 30);
        keyLabel.rectTransform.anchoredPosition = Center(x, y - 15, kw, 30);
        var cap = NewImage(parent, "Key", Assets.Rounded, new Color(1, 1, 1, 0.16f), x, y - 15, kw, 30);
        cap.type = Image.Type.Sliced; cap.raycastTarget = false;
        cap.transform.SetSiblingIndex(keyLabel.transform.GetSiblingIndex());
        var label = Label(parent, text, Assets.Medium, 18, new Color(1, 1, 1, 0.66f), x + kw + 10, y - 15, 200, 30);
        return x + kw + 10 + label.GetPreferredValues(text).x + 26;
    }

    // ---------------------------------------------------------------- plumbing

    static Button MakeButton(RectTransform parent, string name, float x, float y, float w, float h, Action click)
    {
        var rt = Node(parent, name, x, y, w, h);
        var hit = rt.gameObject.AddComponent<Image>();
        hit.color = new Color(1, 1, 1, 0); // invisible: only catches clicks
        var button = rt.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.targetGraphic = hit;
        button.onClick.AddListener((UnityAction)new Action(() => Safe(click)));
        pressables.Add(rt);
        return button;
    }

    static void SetEnabled(Button button, CanvasGroup g, bool on)
    {
        button.interactable = on;
        g.alpha = on ? 1f : 0.38f;
        g.blocksRaycasts = on;
    }

    static GlassShape AddShape(Transform parent, string name, Func<GlassShape, MeshBuilder> build, float x, float y, float w, float h,
                               Func<Texture> texture = null, float parentW = PW, float parentH = PH)
    {
        var shape = new GlassShape(parent, name, build, texture);
        shape.Rect.sizeDelta = new Vector2(w, h);
        shape.Rect.anchoredPosition = Center(x, y, w, h, parentW, parentH);
        shapes.Add(shape);
        return shape;
    }

    /// A RectTransform placed by its top-left corner inside a parent of the given size (both centre-anchored).
    static RectTransform Node(Transform parent, string name, float x, float y, float w, float h, float parentW = PW, float parentH = PH)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = Center(x, y, w, h, parentW, parentH);
        return rt;
    }

    static Vector2 Center(float x, float y, float w, float h, float parentW = PW, float parentH = PH) =>
        new(x + w / 2 - parentW / 2, parentH / 2 - (y + h / 2));

    static Image NewImage(Transform parent, string name, Sprite sprite, Color color, float x, float y, float w, float h, float parentW = PW, float parentH = PH)
    {
        var image = Node(parent, name, x, y, w, h, parentW, parentH).gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static RawImage NewRaw(Transform parent, string name, Texture texture, float x, float y, float w, float h, float parentSize)
        => NewRaw(parent, name, texture, x, y, w, h, parentSize, parentSize);

    static RawImage NewRaw(Transform parent, string name, Texture texture, float x, float y, float w, float h, float parentW, float parentH)
    {
        var raw = Node(parent, name, x, y, w, h, parentW, parentH).gameObject.AddComponent<RawImage>();
        raw.texture = texture;
        raw.raycastTarget = false;
        return raw;
    }

    static TextMeshProUGUI Label(Transform parent, string text, TMP_FontAsset font, float size, Color color, float x, float y, float w, float h,
                                 TextAlignmentOptions align = TextAlignmentOptions.Left, float parentW = PW, float parentH = PH)
    {
        var label = Node(parent, "Label", x, y, w, h, parentW, parentH).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.text = text;
        label.fontSize = size;
        label.color = color;
        label.alignment = align;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("StickerStudioEvents");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        Plugin.Logger.LogInfo("No EventSystem in this scene: added one for StickerSwap");
    }

    static float Wrap(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }

    static void Safe(Action action)
    {
        try { action(); }
        catch (Exception e) { Plugin.Logger.LogError($"panel: {e}"); }
    }
}

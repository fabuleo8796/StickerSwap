# StickerSwap

A **Flipping is Hard** mod by **Fabuleo8796**: put your own picture on the back of your phone as a sticker.

Press **K**, pick **Sticker 1, 2 or 3**, choose a picture, adjust it, and **Stick it!** Your picture takes that
sticker's place on your phone. You see it change live while you edit.

## Download

Get **`StickerSwap-v1.0.0.zip`** from the [Releases page](../../releases/latest).

## Install

**1. You need BepInEx 6 (IL2CPP).** If you already use other Flipping is Hard mods (like CharmReplacer), you have it:
skip to step 2. If not:
- Open the game folder: in Steam, right-click **Flipping is Hard** → **Manage** → **Browse local files**.
- Download **BepInEx 6 bleeding edge, IL2CPP, Windows x64** from <https://builds.bepinex.dev/projects/bepinex_be>
  (the normal BepInEx 5 does **not** work).
- Unzip it into the game folder, so the `BepInEx` folder sits next to `Flipping is Hard.exe`.
- Start the game once (the first start takes a while), then close it.

**2. Install StickerSwap:** unzip `StickerSwap-v1.0.0.zip` into the game folder.
`StickerSwap.dll` ends up in `BepInEx/plugins/`.

**3. Play:** start the game, wait a few seconds, and press **K**.

## How to use

- **Equip a game sticker first** in the slot you want to replace (customization screen). StickerSwap swaps that
  sticker's picture for yours. If the slot is empty, it borrows the sticker shape from another sticker on your phone.
- **Open picture**: PNG or JPG. Transparent PNGs become cut-out stickers.
- **Adjust**: drag to move, scroll to zoom, rotate, flip, Fit / Fill, white sticker border on/off and thickness.
- **Stick it!** saves it. **Restore original** brings the game's sticker back.
- Keys: **K** open/close · **Esc** back · **Enter** open picture / stick it · **1 2 3** pick a slot.

Only you see your stickers: nothing is sent to other players, and the game's own save files are never changed.
Your stickers are kept in `BepInEx/config/FabuleosStickerStudio/` (with a copy of each picture). If the Open
window doesn't appear, drop pictures into its `inbox` folder: they show up under **Recent**.

**Mac (Wine / CrossOver-style runners):** works too, and **Open picture** shows the normal macOS file picker.

## Settings

`BepInEx/config/com.fabuleo8796.stickerstudio.cfg`:
- `Hotkey`: the key that opens StickerSwap (default `K`).
- `StickerResolution`: sticker texture size, 256–1024 (default 512; the game's own stickers are 256).
- `FlipBackground`: set `Yes` if the blurred game behind the glass panel looks upside down.
- `MacFilePicker`: on a Mac, use the macOS file picker (default `true`).

## Build from source

`dotnet build StickerSwap.csproj -c Release` (needs the .NET 6+ SDK and a game folder with BepInEx installed, for
references; pass `-p:GameDir="/path/to/Flipping is Hard"`). `tests/BakerTest` runs the sticker maker on its own.

Font: Geist by Vercel, SIL Open Font License (`src/Resources/OFL-Geist.txt`).
Not affiliated with the developers of Flipping is Hard.

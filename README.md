# Overcooked AYCE Ultrawide Fix (MelonLoader)

A MelonLoader mod for **Overcooked! All You Can Eat** that runs the game at a super-ultrawide resolution
(5120x1440, 32:9, by default) without the resolution snapping back, with full-width cameras and correctly shaped,
correctly placed "+" prompts over the pots.

Version 3.x is a rewrite of [the original mod by Axiolus](https://github.com/Acsaeo/OvercookedAYCE-Ultrawide-Mod)
(1.0.3). It keeps the original's config section and keys, so an existing `MelonPreferences.cfg` still works.

## What it does
- **Holds the resolution:** a Harmony prefix on `Screen.SetResolution` rewrites every request to the target size
  before Unity applies it. The game asks for its own resolution on every launch and scene load; 1.x set it back
  afterwards, which showed as a visible snap.
- **Full-width cameras:** level cameras get the full screen rect and the screen's aspect ratio.
- **Hover icons:** the game lays out its hover icons in a 16:9 box, so on 32:9 the "+" rings were squashed and
  drawn at half the distance from the centre. The mod stretches that canvas and places each ring over its object.

## Requirements
- Windows, Overcooked! All You Can Eat (Steam)
- MelonLoader 0.7 or later installed in the game

## Install
1. Install MelonLoader into the game.
2. Copy `OvercookedAYCE_Ultrawide_Mod.dll` from the [latest release](../../releases/latest) into `<GameFolder>\Mods\`.
   Keep only one ultrawide mod DLL there; two versions fight over the same settings.
3. Start the game once; the settings appear in `<GameFolder>\UserData\MelonPreferences.cfg`.

## Settings (`[AYCEUltrawideFix]`)
| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Turns the whole mod on or off |
| `TargetWidth` / `TargetHeight` | `5120` / `1440` | Resolution to hold |
| `UseBorderless` | `true` | Borderless full screen instead of exclusive full screen |
| `ForceCameraRect` | `true` | Give level cameras the full screen rect |
| `ResetCameraAspect` | `true` | Give level cameras the screen's aspect ratio |
| `FixHoverIconAspect` | `true` | Fix the shape and position of the hover icons |
| `SkipHudCamera` | `false` | Leave the HUD camera alone |
| `Verbose` | `true` | Detailed lines in `MelonLoader\Latest.log` |

## Known issue
The lettered button prompt on control terminals (for example "A") is drawn about twice as wide as it should be.

## Build
```powershell
dotnet build "OvercookedAYCE Ultrawide Mod/OvercookedAYCE Ultrawide Mod.csproj" -c Release -p:GameDir="<GameFolder>"
```
The project references MelonLoader and the game's generated `Il2CppAssemblies`, so MelonLoader must have run once in
that game folder. The default `GameDir` is the standard Steam location.

## Uninstall
Remove the DLL from `<GameFolder>\Mods\`.

## Credits
Based on the original mod by Axiolus, who accepts donations at [ko-fi.com/axiolus](https://ko-fi.com/axiolus).

## License
MIT, see [LICENSE.txt](LICENSE.txt).

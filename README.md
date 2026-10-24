# MapIcons

ExileAPI plugin that draws configurable icons for monsters, chests, league mechanics, NPCs and the
game's own minimap icons on the minimap and large map, plus an overlay of the whole area layout.

## Features

- Per-type icons with color, hidden-state tint, size, name and health display.
- Custom icons by entity path, and an ignore list of paths.
- **Full Map**: the layout of the entire area, explored or not, drawn under the icons on the minimap
  and the large map. Built from the game's pathfinding grid when you enter an area (the approach the
  [Radar](https://github.com/instantsc/Radar) plugin uses). Outline color and width, optional fill of
  the walkable area, height adjustment and blockade clearing are in **Full Map** in the settings.

## Install

Requires ExileAPI built on .NET 10 (329.x).

```powershell
.\deploy.ps1            # build Release and copy MapIcons.dll + Media to <HUD>\Plugins\Compiled\MapIcons
.\deploy.ps1 -Source    # copy the source to <HUD>\Plugins\Source\MapIcons and compile-check it there
.\deploy.ps1 -DryRun    # show what would happen
```

The newest `ExileApi-Compiled-<version>` under `D:\bin\POE\ExileApi` is used; pass `-HudRoot` or
`-HudPath` to change that. Remove other MapIcons installs (e.g. `MapIcons_ExileAPI-master`) so only
one loads.

To build by hand: `dotnet build -p:exapiPackage=<HUD folder>`.

## Credits

- Original plugin by [diesal](https://github.com/diesal/MapIcons_ExileAPI).
- Fixes from [GodKratos](https://github.com/GodKratos/MapIcons_ExileAPI) (custom icons rendering,
  hidden minimap icons) and [Bene1ux](https://github.com/Bene1ux/MapIcons_ExileAPI) (icon size up to 80).
- `MapKey` fix from [chunn112](https://github.com/chunn112/MapIcons_ExileAPI).

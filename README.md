# emberville.multiplayer

Unofficial co-op for **Emberville** (Unity 6, Mono) as a BepInEx 5 plugin: `spike/EmbervilleGhost`.
Work in progress. Notes, findings and the roadmap are in [`todo.md`](todo.md) (top section: "Emberville").

## What works so far
- Connect over LAN with LiteNetLib (F8 host, F9 join, F10 disconnect, Insert = in-game ImGui menu).
- Other players as real characters (copy of your character with the game's scripts stripped), smoothed, with their
  look, gear, colours, animations, name, health bar, status ("in a chest", "talking"...) and [host]/[client] tags.
- Area owners: whoever has been in an area longest runs it (diamond after their tag; "X is the host here").
- Synced through the area owner: creatures (puppets + hit requests + damage replay), breakables, rocks/ore/trees,
  dropped items (dupe-proof pickup), chests (one player at a time), creature hits on other players, shared kill XP.
- Chat (Enter), death announcements.

## Build
Requirements: .NET SDK, Emberville installed with BepInEx 5.4.23 (copy `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version` and `BepInEx/core` into the game folder).

```
dotnet build spike/EmbervilleGhost -c Release -p:GamePath="<path to Emberville Demo>"
```
The build references the game's own DLLs from `<GamePath>/Emberville_Data/Managed` (not included here) and copies the
plugin into `<GamePath>/BepInEx/plugins/EmbervilleGhost`. Default `GamePath` is in `EmbervilleGhost.csproj`.

## Release zip (for players)
```
pwsh spike/EmbervilleGhost/package.ps1 [-GamePath "<path to Emberville Demo>"]
```
Builds Release and writes `spike/dist/EmbervilleMultiplayer-v<version>.zip` (version = `Plugin.Version`): BepInEx 5
(taken from the game folder), the mod in `BepInEx/plugins/EmbervilleMultiplayer`, a player README (install, host,
join, uninstall, credits). Console window off, log file on. Players unzip it into the game folder.

## Testing on one PC
Launch `Emberville.exe` twice. Copy #2 keeps its own options file (window size); characters are shared, so don't load
the same character in both. Windows arrange themselves side by side at 1280x720.

## Not included
No game code or assets: the decompiled reference used for research stays local (`reference/`, git-ignored).
`spike/libs/OverlayDearImGui`: ImGui.NET + cimgui (MIT).

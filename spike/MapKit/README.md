# Emberville Map Kit

Make a room in Unity and walk into it in the game.

## Once
1. Unity Hub > Installs > Install Editor > Archive: **Unity 6000.1.17f1** (exactly this version: it's the game's).
2. New project, template **Universal 2D**.
3. Copy everything in `Editor/` (with its `Core/` folder) into the project's `Assets/Editor/`, and `Runtime/` into
   `Assets/MapKit/`:
   - `CreateStarterRoom.cs` (menu **Emberville > Create Starter Room**)
   - `MapKitLinks.cs` (labels the markers in the Scene view and draws their links; menu **Emberville > Check Links**)
   - `BuildBundles.cs` (menu **Emberville > Build Bundles**; if the game isn't at
     `K:\__games\steamapps\common\Emberville Demo`, change `GamePath` at its top)
   - `GameArt.cs` + `Core/` (menus **Emberville > Settings** and **Emberville > Tile Sets**: the game's own tiles)
   - `Runtime/EmbervilleDoor.cs` (Add Component > Emberville > Door: tick boxes for a door)
4. For the game's own art: extract the game with **AssetRipper** (export as a Unity project), then **Emberville >
   Settings** and pick the export's `Assets` folder (the one with `MonoBehaviour`, `Sprite`, `Texture2D` in it).

## The starter room
**Emberville > Create Starter Room** makes `Assets/Emberville/`:
- `Tiles/`: the placeholder tiles `stone_floor`, `stone_wall`, `water`, `bridge` (plain colours; the game swaps in
  its own dungeon floor, wall, water and bridge art).
- `Emberville.prefab`: a tile palette with them (Window > 2D > Tile Palette, pick "Emberville").
- `MyRoom.unity`: a Grid with the game's room layers (see below) and a sample room on them: a
  river with a bridge, a wall with two doors (one opened by a lever, one by a floor plate), a teleporter pair, barrels,
  stairs down, an exit. AssetBundle `myroom`.
- `Cellar.unity`: where the stairs go. Stairs back up, and a pond with an island you only reach by a teleporter pair.
  AssetBundle `cellar`.

Then **Emberville > Build Bundles**, and in the game `/room myroom` (Enter for chat). `/room leave` goes back;
`/room` lists the rooms in `BepInEx/plugins/EmbervilleGhost/Maps`.

## Painting with the game's tiles
**Emberville > Tile Sets** lists the game's tiles by set ("Walls and Top_Zone1", "BrickBig_Floor",
"Water_Rocks_Dungeon"...). **Import** copies a set, its sprites and textures into `Assets/GameArt/` (only that set) and
makes it a palette (`Assets/GameArt/Palettes/`, pick it in Window > 2D > Tile Palette). Paint with them: they join up
(wall tops, shorelines) exactly as in the game, and the room carries them, so the game draws what you painted.

**The tilemap you paint on decides what it is**, using the game's own layer names and draw order (the same set every
one of the game's dungeon rooms has). The starter rooms have all of them; **Emberville > Update Room Layers** gives the
open room any that are missing (and renames our first names: Ground, Walls, WallTops):
| Tilemap | Drawn on | In the game |
|---|---|---|
| `Ground0` | Default 0 | the floor: you walk on it |
| `Ground1`, `Ground2` | Default 1, 2 | floor detail over it (paths, cracks, moss), still floor |
| `Water` | Default 3 | water: blocks (paint `Ground0` under it, or its edges show the dark) |
| `Bridge` | Default 4 | ours: walkable over water (the water is cut away under it) |
| `Wall` | Default 5 | wall faces (a wall set's face tile, e.g. `Walls and Top_Zone1_rules_1`): blocks |
| `WallCover` | Player 0, per tile | wall tops (its top tile, `..._rules_0`): blocks; characters walk behind them |
| `Fence` | Player 0, per tile | fences, railings: blocks; characters walk in front of / behind them |
| `Fog` | Player 10 | dark overhang drawn over everything |
| `Collision` | hidden (red in the editor) | invisible walls |
| `RemoveCollision` | hidden (green) | gaps cut through walls, fences and collision |
| `RemoveWater` | hidden (cyan) | water cut away there (not drawn, not solid) |
| any other name | over the floor | only drawn (rugs, decoration): doesn't block |

A game wall is a top (`WallCover`) everywhere, with a row of face (`Wall`) only where it looks down onto floor. The
"Default" / "Player" sorting layers are the game's: Default is flat ground, under every character; Player is anything
with height, sorted tile by tile against the characters (the project gets them from Update Room Layers).

## Sharing maps
**Players don't extract anything.** A map is one file (`myroom`, `cellar`...): put it in the game's
`BepInEx/plugins/EmbervilleGhost/Maps` and type `/room <name>`. The file carries the art it uses; live props, borrowed
breakables, doors and the placeholder tiles come from the player's own game by the game's names.

**Mapmakers: the same game build gives everyone the same art.** Extract the game with AssetRipper, point
**Emberville > Settings** at the export's `Assets`. The kit doesn't keep AssetRipper's names, folders or IDs: every file
it imports gets its ID, folder and name from what it is (`Texture2D/<name>.png`, `Sprite/<texture>/<sprite>.asset`,
`Tiles/<set>/<tile>.asset`, `Props/<scene>/<prop>.prefab`...). Two mapmakers on the same game build get identical
`Assets/GameArt` folders, so scene files, prefabs and palettes can be swapped between projects.
`Assets/GameArt/manifest.json` records the game's Steam build and every file's ID; each room remembers the build it was
made on, and opening it on another build says so in the Console.
Art imported before this (AssetRipper's layout): **Emberville > Re-file Game Art** once (it backs up first, moves the
files to their places and points every scene and prefab at them).

## Making it yours
**Placeholder tiles** (quick blocking-out; the game swaps in its own art. Keep the four apart so you can paint water
under a bridge):
| Tile | In the game |
|---|---|
| `stone_floor` | the game's floor; you walk on it |
| `stone_wall` | the game's wall; blocks |
| `water` | the game's water; blocks (floor is put under it for you) |
| `bridge` | over water: walkable, the water's collision cut away under it |

One tile = one of the game's tiles; paint whatever shape you like. The camera stays inside the room.

**Markers**: empty GameObjects (the starter's have a little coloured square so you can see them; it's hidden in the
game). The name is what counts:
| Name | Becomes |
|---|---|
| `PlayerStart` | where you arrive |
| `Breakable: Barrel` (or another of the game's breakables by name: Crate, Vase...) | the game's breakable |
| `Door: Vault` | one of the game's gate doors, closed. Put it in the middle of a doorway; it faces the right way by itself (from the walls on either side of it) |
| `Door: Vault (open)` | the same, starting open |
| `Door: Vault (locked: Rusty Key)` | the same, locked: it needs that item to open |
| `Lever: -> Vault` | a lever: on opens the door named Vault, off closes it |
| `Plate: -> Vault` | a floor plate: stood on opens Vault |
| `Exit: back` | a ring: stand on it to go back where you came from (out of all your rooms) |
| `Exit: -> otherroom` | a ring into another of your rooms |
| `Teleport: Blue` (two of them) | a pair: stand on one, you're at the other. Any name links them |
| `Teleport: -> Blue` + `Arrive: Blue` | one way, to the `Arrive` spot |
| `Stairs: down -> cellar` | down into your room `cellar`, arriving at its `Arrive: top` (else its `PlayerStart`) |
| `Stairs: up` | back up to the room you came down from, at its stairs (from the first room: out) |

Stand on a pad (exit, stairs, teleporter) a moment to use it. Teleporters are instant, with the game's own teleport
flash; stairs and exits fade like a door. A marker with art of its own under it (a staircase sprite, a rune) keeps that
art and gets no ring. Stairs into the game's own dungeon aren't in yet.

Lines in the Scene view (MapKitLinks) show what's linked to what; a red circle is a link that goes nowhere (a lever
naming a door that isn't there, a lone teleporter...).

Doors never touch your world's save. Breakables come back each time you enter.

Anything else you put in the scene (your own sprites, your own tiles) comes along as it is, drawn on the game's
layers. The scene's camera, global light and event system are left out (the game has its own).

More rooms: duplicate a scene, give the copy its own AssetBundle name (one scene per bundle; the name is what stairs
and exits use), build.

One player for now.

using System;
using System.Collections.Generic;

namespace EmbervilleGhost.Core;

/// <summary>What a custom room's tile is, from the tilemap it was painted on: the game's own dungeon-room layers
/// (notes/emberville-tile-layers.md), plus our Bridge (a deck over water in one layer: the game's Ground1 art and
/// RemoveWater together).</summary>
public enum RoomLayer
{
    Ground0, Ground1, Ground2, Water, Bridge, Wall, WallCover, Fence, Fog, Collision, RemoveCollision, RemoveWater, Decoration,
}

/// <summary>
/// A custom room's layers (CustomRoom): the tilemap a tile is on decides what it is in the game, by the game's own
/// layer names (Ground0-2 floor, Water, Wall faces, WallCover tops, Fence, Fog, and the hidden Collision,
/// RemoveCollision, RemoveWater); our first names (Ground, Walls, WallTops) still work; on a tilemap of another name, the
/// Map Kit's placeholder tiles still say what they are, and anything else is only drawn. Plain C#, tested on its own.
/// </summary>
public static class RoomLayers
{
    static readonly Dictionary<string, RoomLayer> ByMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ground0"] = RoomLayer.Ground0, ["Ground1"] = RoomLayer.Ground1, ["Ground2"] = RoomLayer.Ground2,
        ["Water"] = RoomLayer.Water, ["Bridge"] = RoomLayer.Bridge,
        ["Wall"] = RoomLayer.Wall, ["WallCover"] = RoomLayer.WallCover, ["Fence"] = RoomLayer.Fence, ["Fog"] = RoomLayer.Fog,
        ["Collision"] = RoomLayer.Collision, ["RemoveCollision"] = RoomLayer.RemoveCollision, ["RemoveWater"] = RoomLayer.RemoveWater,
        // (the names the game's hand-built scenes use: TutorialArea, the house interiors)
        ["Floor"] = RoomLayer.Ground0, ["Floor1"] = RoomLayer.Ground1, ["Floor2"] = RoomLayer.Ground2,
        ["Colliders"] = RoomLayer.Collision, ["Fences"] = RoomLayer.Fence,
        // (our first names)
        ["Ground"] = RoomLayer.Ground0, ["Walls"] = RoomLayer.Wall, ["WallTops"] = RoomLayer.WallCover,
    };

    static readonly Dictionary<string, RoomLayer> Placeholders = new()
    {
        ["stone_floor"] = RoomLayer.Ground0, ["stone_wall"] = RoomLayer.Wall, ["water"] = RoomLayer.Water, ["bridge"] = RoomLayer.Bridge,
    };

    public static RoomLayer Of(string tilemapName, string tileName)
    {
        if (tilemapName != null && ByMap.TryGetValue(tilemapName, out var layer)) return layer;
        return tileName != null && Placeholders.TryGetValue(tileName, out var p) ? p : RoomLayer.Decoration;
    }

    /// <summary>Floor you walk on.</summary>
    public static bool IsFloor(RoomLayer k) => k is RoomLayer.Ground0 or RoomLayer.Ground1 or RoomLayer.Ground2;

    /// <summary>Blocks you (water blocks too, but it has its own rules: bridges, RemoveWater).</summary>
    public static bool IsSolid(RoomLayer k) => k is RoomLayer.Wall or RoomLayer.WallCover or RoomLayer.Fence or RoomLayer.Collision;

    /// <summary>Not drawn in the game: only says where things block or don't.</summary>
    public static bool IsHidden(RoomLayer k) => k is RoomLayer.Collision or RoomLayer.RemoveCollision or RoomLayer.RemoveWater;

    /// <summary>One of the kit's plain-colour stand-ins (swapped for the game's art at load).</summary>
    public static bool IsPlaceholder(string tileName) => tileName != null && Placeholders.ContainsKey(tileName);

    /// <summary>Where the game's floor goes for the placeholders: their floor, and under their water and bridges (the shores
    /// sit on floor), but never where a real floor tile is painted already (two floors there would fight to be seen).</summary>
    public static HashSet<(int x, int y)> PlaceholderFloor(HashSet<(int x, int y)> ground, HashSet<(int x, int y)> water,
        HashSet<(int x, int y)> bridge, HashSet<(int x, int y)> realGround)
    {
        var floor = new HashSet<(int x, int y)>(ground);
        foreach (var c in water) if (!realGround.Contains(c)) floor.Add(c);
        foreach (var c in bridge) if (!realGround.Contains(c)) floor.Add(c);
        return floor;
    }

    /// <summary>A wall as the game's rooms draw it: its face where it looks down onto floor (no wall below), its top
    /// everywhere else.</summary>
    public static (HashSet<(int x, int y)> faces, HashSet<(int x, int y)> tops) SplitWalls(HashSet<(int x, int y)> walls)
    {
        var faces = new HashSet<(int x, int y)>();
        var tops = new HashSet<(int x, int y)>();
        foreach (var c in walls)
            (walls.Contains((c.x, c.y - 1)) ? tops : faces).Add(c);
        return (faces, tops);
    }
}

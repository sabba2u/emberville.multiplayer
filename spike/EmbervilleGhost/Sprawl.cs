using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// How spread out a descent floor is. The game's generator joins a dungeon's key rooms with paths
/// (ImprovedLinearPathGeneration) and then hangs a few extra rooms off random existing ones (toAddRandom): those
/// extras are the side branches. Region-based types pick a number of feature rooms (LinearRegionGeneration). Before a
/// floor is generated we raise those numbers (on the shared dungeon settings, put back right after), so the floor
/// branches out more: a trunk with side rooms becomes a web. Level 0 = the game's own numbers.
/// Also the floor's shape afterwards (rooms, dead ends, branch points, loops), for the log.
/// </summary>
internal static class Sprawl
{
    /// <summary>Extra rooms and features for this level, on the dungeon type's settings; the returned action undoes it.</summary>
    public static Action Apply(DungeonSettings settings, int level)
    {
        var undo = new List<Action>();
        if (settings == null || settings.steps == null) return () => { };
        var parts = new List<string>();
        foreach (var step in settings.steps)
        {
            switch (step)
            {
                case ImprovedLinearPathGeneration path:
                {
                    var was = path.toAddRandom;
                    if (level > 0)
                    {
                        float m = 1f + 0.6f * level;
                        int add = 3 * level;
                        path.toAddRandom = new Vector2Int(Mathf.RoundToInt(was.x * m) + add, Mathf.RoundToInt(was.y * m) + add);
                        undo.Add(() => path.toAddRandom = was);
                    }
                    parts.Add($"path + {was.x}-{was.y} side rooms" + (level > 0 ? $" -> {path.toAddRandom.x}-{path.toAddRandom.y}" : ""));
                    break;
                }
                case LinearRegionGeneration region:
                {
                    int lo = region.featureChunksCountMin, hi = region.featureChunksCountMax;
                    if (level > 0)
                    {
                        region.featureChunksCountMin = lo + level;
                        region.featureChunksCountMax = hi + 2 * level;
                        undo.Add(() => { region.featureChunksCountMin = lo; region.featureChunksCountMax = hi; });
                    }
                    parts.Add($"region {lo}-{hi} features" + (level > 0 ? $" -> {region.featureChunksCountMin}-{region.featureChunksCountMax}" : ""));
                    break;
                }
                default:
                    if (step != null) parts.Add(step.GetType().Name);
                    break;
            }
        }
        NetLog.Info($"Dungeon type {settings.name} (sprawl {level}): {string.Join(", ", parts)}", toast: false);
        return () => { foreach (var u in undo) u(); };
    }

    /// <summary>
    /// The floor's shape, from its walkable cells grouped by room (chunk-sized blocks): rooms joined where floor
    /// crosses from one block into the next. Dead ends = rooms with one way in; branch points = three or more;
    /// loops = the joins a tree wouldn't need.
    /// </summary>
    public static string Shape(HashSet<Vector3Int> cells, Vector2Int chunkSize, int toStairs)
    {
        if (cells == null || cells.Count == 0) return "no floor";
        int cx = Mathf.Max(1, chunkSize.x), cy = Mathf.Max(1, chunkSize.y);
        Vector2Int Room(Vector3Int c) => new Vector2Int(Mathf.FloorToInt((float)c.x / cx), Mathf.FloorToInt((float)c.y / cy));
        var rooms = new HashSet<Vector2Int>();
        var links = new HashSet<(Vector2Int, Vector2Int)>();
        foreach (var c in cells)
        {
            var r = Room(c);
            rooms.Add(r);
            foreach (var d in new[] { Vector3Int.right, Vector3Int.up })
            {
                var n = c + d;
                if (!cells.Contains(n)) continue;
                var rn = Room(n);
                if (rn == r) continue;
                var key = r.x < rn.x || r.x == rn.x && r.y < rn.y ? (r, rn) : (rn, r);
                links.Add(key);
            }
        }
        var degree = rooms.ToDictionary(r => r, r => 0);
        foreach (var (a, b) in links) { degree[a]++; degree[b]++; }
        int deadEnds = degree.Values.Count(d => d == 1), branches = degree.Values.Count(d => d >= 3);
        // loops = links - rooms + connected parts
        var parent = rooms.ToDictionary(r => r, r => r);
        Vector2Int Find(Vector2Int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (a, b) in links) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
        int parts = rooms.Select(Find).Distinct().Count();
        int loops = links.Count - rooms.Count + parts;
        return $"{rooms.Count} rooms, {deadEnds} dead ends, {branches} branch points, {loops} loops, stairs {toStairs} steps away";
    }
}

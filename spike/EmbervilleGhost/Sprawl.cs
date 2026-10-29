using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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
[HarmonyPatch]
internal static class Sprawl
{
    /// <summary>Extra rooms and features for this level, on the dungeon type's settings; the returned action undoes it.
    /// A footprint (rooms across, 0: none): the floor kept inside that square, its key rooms close together.</summary>
    public static Action Apply(DungeonSettings settings, int level, int footprint = 0)
    {
        var undo = new List<Action>();
        if (settings == null || settings.steps == null) return () => { };
        var parts = new List<string>();
        // a footprint: only for one-region layouts (a type with several regions spreads them over the map on purpose)
        box = footprint > 0 && settings.steps.Count(s => s is LinearRegionGeneration) <= 1 ? footprint : 0;
        calls.Clear();
        if (box > 0) parts.Add($"footprint {box}x{box} rooms");
        undo.Add(() => { box = 0; boxNow = false; calls.Clear(); });
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
                    if (box > 0 && path.features != null)   // (key rooms a short walk apart: their paths stay in the square)
                        foreach (var f in path.features)
                            if (f?.Connections != null)
                                foreach (var c in f.Connections)
                                {
                                    var d = c.DistanceMinMax;
                                    int hi = Mathf.Min(d.y, Mathf.Max(1, box / 2));
                                    c.DistanceMinMax = new Vector2Int(Mathf.Min(d.x, hi), hi);
                                    undo.Add(() => c.DistanceMinMax = d);
                                }
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
                    if (box > 0)
                    {
                        int dMin = region.featureChunksMinDistance, dMax = region.featureChunksMaxDistance;
                        region.featureChunksMaxDistance = Mathf.Min(dMax, Mathf.Max(1, box / 2));
                        region.featureChunksMinDistance = Mathf.Min(dMin, region.featureChunksMaxDistance);
                        undo.Add(() => { region.featureChunksMinDistance = dMin; region.featureChunksMaxDistance = dMax; });
                    }
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

    // ---- the footprint: while a step places its rooms, a spot that would stretch the floor past box x box rooms
    // counts as taken. A step that can't fit that way (35 misses in one go) finishes freely; tried again (it failed),
    // it's laid out freely from the start: a floor is never lost to the footprint, only less compact ----

    static int box;
    static bool boxNow;
    static int placing, misses;
    static readonly Dictionary<DungeonGenerationStep, int> calls = new Dictionary<DungeonGenerationStep, int>();
    static DungeonModel boundsOf;
    static int boundsCount;
    static Vector2Int lo, hi;

    static void StepStarts(DungeonGenerationStep step)
    {
        calls.TryGetValue(step, out int n);
        calls[step] = n + 1;
        boxNow = box > 0 && n == 0;
        misses = 0;
        if (box > 0 && n == 1) NetLog.Info($"Dungeon footprint: {step.GetType().Name} didn't fit in {box}x{box} rooms; laid out freely", toast: false);
    }

    [HarmonyPatch(typeof(ImprovedLinearPathGeneration), nameof(ImprovedLinearPathGeneration.Generate)), HarmonyPrefix]
    static void PathStarts(ImprovedLinearPathGeneration __instance) => StepStarts(__instance);

    [HarmonyPatch(typeof(LinearRegionGeneration), nameof(LinearRegionGeneration.Generate)), HarmonyPrefix]
    static void RegionStarts(LinearRegionGeneration __instance) => StepStarts(__instance);

    [HarmonyPatch(typeof(ImprovedLinearPathGeneration), "TryPlaceChunk"), HarmonyPrefix]
    static void PathPlacing() => placing++;
    [HarmonyPatch(typeof(ImprovedLinearPathGeneration), "TryPlaceChunk"), HarmonyPostfix]
    static void PathPlaced(ChunkModel __result) => Placed(__result);

    [HarmonyPatch(typeof(LinearRegionGeneration), "TryPlaceChunk"), HarmonyPrefix]
    static void RegionPlacing() => placing++;
    [HarmonyPatch(typeof(LinearRegionGeneration), "TryPlaceChunk"), HarmonyPostfix]
    static void RegionPlaced(ChunkModel __result) => Placed(__result);

    static void Placed(ChunkModel result)
    {
        placing = Mathf.Max(0, placing - 1);
        if (boxNow && result == null && ++misses >= 35) boxNow = false;
    }

    [HarmonyPatch(typeof(DungeonModel), nameof(DungeonModel.IsNodeOccupied), new[] { typeof(Vector2Int) }), HarmonyPostfix]
    static void OutsideIsTaken(DungeonModel __instance, Vector2Int pos, ref bool __result)
    {
        if (__result || !boxNow || placing == 0 || __instance.HasChunk == null || __instance.HasChunk.Count == 0) return;
        if (boundsOf != __instance || boundsCount != __instance.HasChunk.Count)
        {
            boundsOf = __instance;
            boundsCount = __instance.HasChunk.Count;
            lo = new Vector2Int(int.MaxValue, int.MaxValue);
            hi = new Vector2Int(int.MinValue, int.MinValue);
            foreach (var k in __instance.HasChunk.Keys) { lo = Vector2Int.Min(lo, k); hi = Vector2Int.Max(hi, k); }
        }
        if (Mathf.Max(hi.x, pos.x) - Mathf.Min(lo.x, pos.x) + 1 > box || Mathf.Max(hi.y, pos.y) - Mathf.Min(lo.y, pos.y) + 1 > box)
            __result = true;
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
        int across = rooms.Max(r => r.x) - rooms.Min(r => r.x) + 1, down = rooms.Max(r => r.y) - rooms.Min(r => r.y) + 1;
        return $"{rooms.Count} rooms ({across}x{down} across), {deadEnds} dead ends, {branches} branch points, {loops} loops, stairs {toStairs} steps away";
    }
}

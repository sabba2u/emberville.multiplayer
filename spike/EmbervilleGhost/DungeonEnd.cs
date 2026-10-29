using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace EmbervilleGhost;

/// <summary>
/// The end of each of Nagedra's dungeons (not Kaldhor's Throne: the Mad King is its end): in the last region (the lab:
/// its basement), at the walkable spot farthest from where you came in, a boss guards a big chest and the way home.
/// Nagedra's own forest: the Queen Bee; anywhere else a mini-boss picked by the visit's seed (half the time one of ours
/// made big, half the time one of the game's own). The chest won't open while the boss lives; it holds weapons and
/// armour as strong as gear 5+ levels above yours, asking only your level. The boss down, a portal home opens (beside
/// Nagedra, if that's where you came from).
/// </summary>
[HarmonyPatch]
internal static class DungeonEnd
{
    /// <summary>Nagedra's dungeons with an end of ours (KL has its King).</summary>
    static readonly HashSet<string> Places = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
    {
        "B01_DF_Negedra", "B01_Lab_Full", "Dungeon_B01_Library", "Dungeon_B01_MorchaHideout",
        "B01_SD_Mining_01", "B01_SD_Nature_01", "B01_DF_Tolcan", "DungeonGeneration_StoneBasement_First",
    };
    internal const string Forest = "B01_DF_Negedra";

    static readonly string[] Firsts = { "Grimjaw", "Bristle", "Morrow", "Skarn", "Hollowtooth", "Vesk", "Old Gnarl", "Ruska", "Thane", "Mabb" };
    static readonly string[] Lasts = { "the Keeper", "the Hoarder", "the Last", "the Warden", "the Greedy", "the Watcher", "the Unbowed" };

    static Enemy guard;
    static Chest chest;
    static Transform place;
    static Vector3 portalAt;
    static bool waiting;   // (a boss down will open the way home)
    static string guardName;

    /// <summary>That chest is a boss's, and the boss still stands.</summary>
    public static bool Guarded(Chest c) => c && c == chest && guard && !guard.IsDead;

    /// <summary>After a region is built and its creatures placed (Dungeon.Go): the end, if this is one of Nagedra's
    /// dungeons and its last region.</summary>
    public static void Built(Dungeon.Where where, int regions, GameObject root, Vector3 arrival, BiomeSettings biome)
    {
        guard = null; chest = null; waiting = false; place = root ? root.transform : null;
        if (where.Floor > 0 || !root || !Places.Contains(where.Settings) || where.Region != regions - 1) return;
        var cells = Dungeon.floorCells;
        if (cells.Count == 0) return;
        var cs = Dungeon.cellSize;
        var start = new Vector3Int(Mathf.FloorToInt(arrival.x / cs.x), Mathf.FloorToInt(arrival.y / cs.y), 0);
        if (!cells.Contains(start)) start = cells.OrderBy(c => (c - start).sqrMagnitude).First();
        var dist = Delve.Distances(start, cells);
        // the farthest spot with room around it (a boss, a chest and a portal side by side)
        Vector3Int end = start;
        for (int room = 3; room >= 0; room--)
        {
            var open = dist.Where(p => Delve.Open(p.Key, cells, room)).ToList();
            if (open.Count == 0) continue;
            end = open.OrderByDescending(p => p.Value).ThenBy(p => p.Key.x).ThenBy(p => p.Key.y).First().Key;
            break;
        }
        var endAt = new Vector3((end.x + 0.5f) * cs.x, (end.y + 0.5f) * cs.y, 0f);
        var rng = new System.Random(Delve.SeedOf(where) ^ 0x0E4D);
        // the portal and the chest each on open floor of their own (clear all round: not on a lake's edge, the portal's
        // big frame standing in the water)
        portalAt = Clear(cells, end, new Vector3(2f, 0f, 0f), cs, endAt);
        var chestAt = Clear(cells, end, new Vector3(-2f, 0.3f, 0f), cs, endAt);
        chest = DungeonLoot.MakeBossChest(root.transform, chestAt, $"EG_End_{where.Seed}_{where.Region}", rng.Next());
        // the boss (in co-op the area's owner makes it, as the creatures; the others see it through the creature sync)
        if (!(MiniBoss.Plugin != null && MiniBoss.Plugin.Connected && !MiniBoss.Plugin.IOwnHere))
            guard = Boss(where.Settings, rng, OnMesh(endAt + new Vector3(0f, -1.5f, 0f), endAt), biome, root.transform);
        if (guard)
        {
            waiting = true;
            NetLog.Warn($"Deep in here, {guardName} guards a treasure and the way home...", toast: true);
        }
        else Delve.HomePortal(portalAt, place, pressF: true);   // (no boss: the way home is open already)
        NetLog.Info($"Dungeon end: {(guard ? guardName : "no boss")} at {endAt.x:0.0}, {endAt.y:0.0} ({dist[end]} steps from the way in), a chest, {(guard ? "a portal once it falls" : "a portal")}", toast: false);
    }

    /// <summary>Near the end (that far off it, as near as it can be), a cell with open floor 2 cells all round (1, if none).</summary>
    static Vector3 Clear(HashSet<Vector3Int> cells, Vector3Int end, Vector3 off, Vector3 cs, Vector3 endAt)
    {
        var want = endAt + off;
        for (int room = 2; room >= 1; room--)
        {
            Vector3Int? best = null;
            float bestD = float.MaxValue;
            for (int dx = -7; dx <= 7; dx++)
                for (int dy = -7; dy <= 7; dy++)
                {
                    var c = new Vector3Int(end.x + dx, end.y + dy, 0);
                    if (!cells.Contains(c) || !Delve.Open(c, cells, room)) continue;
                    var at = new Vector3((c.x + 0.5f) * cs.x, (c.y + 0.5f) * cs.y, 0f);
                    if (((Vector2)(at - endAt)).sqrMagnitude < 1.2f * 1.2f) continue;   // (not on the boss's spot)
                    float d = ((Vector2)(at - want)).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
            if (best is Vector3Int b) return new Vector3((b.x + 0.5f) * cs.x, (b.y + 0.5f) * cs.y, 0f);
        }
        return OnMesh(want, endAt);
    }

    static Vector3 OnMesh(Vector3 at, Vector3 fallback) =>
        NavMesh.SamplePosition(at, out var h, 2f, NavMesh.AllAreas) ? h.position : NavMesh.SamplePosition(fallback, out var f, 3f, NavMesh.AllAreas) ? f.position : fallback;

    /// <summary>The end's boss: the Queen Bee in Nagedra's forest; else one of ours made big, or one of the game's.</summary>
    static Enemy Boss(string settings, System.Random rng, Vector3 at, BiomeSettings biome, Transform parent)
    {
        Enemy e = null;
        string title = $"{Firsts[rng.Next(Firsts.Length)]} {Lasts[rng.Next(Lasts.Length)]}";
        if (settings.Equals(Forest, System.StringComparison.OrdinalIgnoreCase))
        {
            guardName = "the Queen Bee";
            e = Monsters.SpawnBoss("Queen Bee", at, "The Queen Bee", "Queen of Nagedra's forest");
        }
        else if (rng.NextDouble() < 0.5 && Monsters.BossCandidate(rng) is string ours)
        {
            guardName = $"{title} ({ours})";
            e = Monsters.SpawnBoss(ours, at, title, $"{ours} mini-boss");
        }
        if (!e)
        {
            var look = MiniBoss.ForFloor(rng.Next(), 6, biome);
            look.Name = title;
            look.Health = 9f;
            look.Damage = 1.5f;
            look.LootFloor = 8;
            guardName = title;
            e = MiniBoss.Spawn(look, at, parent);
        }
        if (e) e.transform.SetParent(parent, true);
        return e;
    }

    /// <summary>Every frame (Plugin.Update): the boss down, the way home opens.</summary>
    public static void Tick()
    {
        if (!waiting) return;
        if (!place) { waiting = false; guard = null; chest = null; return; }   // (the region gone)
        if (guard && !guard.IsDead) return;
        waiting = false;
        Delve.HomePortal(portalAt, place, pressF: true);
        NetLog.Success($"{guardName} has fallen! Its treasure is yours, and a portal home is open.", toast: true);
    }

    /// <summary>A boss's chest stays shut while the boss stands.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.CanInteract)), HarmonyPostfix]
    static void Shut(Chest __instance, ref bool __result)
    {
        if (__result && Guarded(__instance)) __result = false;
    }
}

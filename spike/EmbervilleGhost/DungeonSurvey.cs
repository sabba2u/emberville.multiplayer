using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// /dungeon survey: what Nagedra's dungeons are made of, without going in. Each is generated (a random seed, in the
/// background, as entering would) and logged: its regions (the parts joined by doorways) and their rooms; the doorways
/// to other regions; the rooms' own teleporters (exits, sub-dungeons) with where they lead; the room with the way in
/// (its portal); the rooms holding chests; and the rooms named like a boss's, a treasure's or an end.
/// </summary>
internal static class DungeonSurvey
{
    /// <summary>The dungeons Nagedra sends you to (her dialogue's "command dungeon ..." lines).</summary>
    static readonly string[] Nagedras =
    {
        "B01_DF_Negedra", "B01_Lab_Full", "Dungeon_B01_Library", "Dungeon_B01_MorchaHideout", "Dungeon_B01_KL",
        "B01_SD_Mining_01", "B01_SD_Nature_01", "B01_DF_Tolcan", "DungeonGeneration_StoneBasement_First",
    };

    static readonly string[] Telling = { "boss", "treasure", "end", "exit", "throne", "reward", "vault", "chest", "portal", "stairs" };
    static bool busy;

    public static void Run() => Survey().Forget();

    static async UniTaskVoid Survey()
    {
        if (busy) { NetLog.Info("Dungeon survey: already running", toast: true); return; }
        busy = true;
        NetLog.Info($"Dungeon survey: generating Nagedra's {Nagedras.Length} dungeons (see the log)...", toast: true);
        try
        {
            var all = Dungeon.AllSettings();
            foreach (var name in Nagedras)
            {
                var settings = all.FirstOrDefault(s => s.name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!settings) { NetLog.Info($"Survey: {name}: no such dungeon settings", toast: false); continue; }
                int seed = UnityEngine.Random.Range(1, int.MaxValue);
                DungeonRender render;
                try
                {
                    var ctx = new GenerationContext(new SeededSystemRandomProvider(seed)) { NeighborSelection = new UniformNeighborSelection() };
                    GenRandom.Begin(seed);
                    DungeonModel model;
                    try { model = await settings.CreateDungeon(ctx, Dungeon.Plugin.destroyCancellationToken); }
                    finally { GenRandom.End(); }
                    if (model == null) { NetLog.Info($"Survey: {name}: the generator gave up", toast: false); continue; }
                    render = model.GetRender(ctx.RNG);
                }
                catch (Exception e) { NetLog.Info($"Survey: {name}: {e.GetType().Name}: {e.Message}", toast: false); continue; }
                Describe(name, seed, render);
                await UniTask.Yield();
            }
            NetLog.Info("Dungeon survey: done", toast: true);
        }
        finally { busy = false; }
    }

    static void Describe(string name, int seed, DungeonRender render)
    {
        NetLog.Info($"Survey: ==== {name} (seed {seed}): {render.Regions.Count} region(s) ====", toast: false);
        for (int r = 0; r < render.Regions.Count; r++)
        {
            var region = render.Regions[r];
            var rooms = region.Chunks.Values.Where(c => c != null && c.Type).ToList();
            var doors = region.Connections.Select(c => $"to region {c.Value.Index} ({c.Value.Direction}) at {c.Key}").ToList();
            NetLog.Info($"Survey:   region {r} \"{region.Name}\": {rooms.Count} rooms; doorways: {(doors.Count > 0 ? string.Join(", ", doors) : "none")}", toast: false);
            foreach (var c in rooms)
            {
                var prefab = c.Type;
                string n = prefab.name;
                int chests = prefab.GetComponentsInChildren<Chest>(true).Length;
                var tps = prefab.Teleporters?.Where(t => t).ToList() ?? new List<Teleporter>();
                c.Others.TryGetValue("Exit", out var exit);
                c.Others.TryGetValue("ID", out var id);
                bool telling = Telling.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!(prefab.Portal || chests > 0 || tps.Count > 0 || telling || exit != null)) continue;
                var bits = new List<string>();
                if (prefab.Portal) bits.Add("the way in (portal)");
                if (chests > 0) bits.Add($"{chests} chest(s)");
                if (tps.Count > 0) bits.Add($"teleporters: {string.Join(", ", tps.Select(t => $"{t.name}{(string.IsNullOrEmpty(t.teleporterID) ? "" : " id " + t.teleporterID)}{(t.TeleportResolver != null ? " via " + t.TeleportResolver.GetType().Name : "")}"))}");
                if (exit != null) bits.Add($"leads to \"{exit}\"");
                if (id != null) bits.Add($"id {id}");
                NetLog.Info($"Survey:     {n} at {c.Position}: {string.Join("; ", bits)}", toast: false);
            }
        }
    }
}

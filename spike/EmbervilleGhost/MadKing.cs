using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace EmbervilleGhost;

/// <summary>
/// The Mad King's dungeon: KL (Kaldhor's throne room is in it; the demo leaves it empty). Each region of it, once built,
/// gets groups of the King's soldiers (3-5 of one colour: Knights, Berserkers, Bowmen, Swordsmen, Lancers; one for every
/// ~200 walkable cells) and lone sentries (1-2), 45 at most, in the halls between, spread over it, never near where you came in nor in the throne room; and the region with Kaldhor's throne gets The Mad King,
/// waiting in front of it (the Invincible King: armour, his weapon sticking, his dash, his soldiers, his catapults).
/// He falls: the way home opens there, in front of the throne, and he drops the Cohhilition Sword. Only the one who runs the place spawns them.
/// </summary>
internal static class MadKing
{
    const string Title = "The Mad King";
    const string Sword = "Cohhilition Sword";   // (the game's spelling)
    const float SwordChance = 1f;   // (every time: the prize for beating him)
    static readonly string[] Colours = { "Black", "Blue", "Purple", "Red", "Yellow" };
    static readonly string[] Soldiers = { "Knight", "Berserker", "Bowman", "Swordsman", "Lancer" };
    const int MostSoldiers = 45;
    const float AwayFromYou = 10f, AwayFromThrone = 10f, GroupGap = 7f, SentryGap = 4.5f;

    static Enemy king;
    static Vector3 thronePos;
    static bool portalOpen;

    /// <summary>Is this a Mad King dungeon? KL, or any place with Kaldhor's throne in it.</summary>
    static bool Here(string settings, GameObject root) => settings.IndexOf("KL", System.StringComparison.OrdinalIgnoreCase) >= 0 || Throne(root);

    static Transform Throne(GameObject root) =>
        root ? root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith("Kaldhor_Throne")) : null;

    /// <summary>A region just built (Dungeon.Go, after its own creatures): the soldiers, and the King if his throne's here.</summary>
    public static void Built(string settings, GameObject root, Vector3 arrival)
    {
        if (!(king && kingPlace && kingPlace != (root ? root.transform : null))) king = null;   // (the throne room's King stays)
        portalOpen = portalOpen && king;
        if (!root || !Here(settings, root)) return;
        if (Dungeon.Plugin != null && Dungeon.Plugin.Connected && !Dungeon.Plugin.IOwnHere) return;   // (the host's world has them)
        var throne = Throne(root);
        thronePos = throne ? throne.position : Vector3.positiveInfinity;
        var floor = Dungeon.floorCells.ToList();   // (the floor's real tiles: never the dark between the rooms)
        if (floor.Count == 0) return;
        // groups: one for every ~200 walkable cells, 6 to 10; then lone sentries between, 3 to 6; at most MostSoldiers in
        // all (with the place's own creatures: some 50-60)
        int groups = Mathf.Clamp(Dungeon.floorCells.Count / 200, 6, 10);
        int sentries = Mathf.Clamp(Dungeon.floorCells.Count / 350, 3, 6);
        var spots = new List<Vector3>();
        var rng = new System.Random(root.GetInstanceID());
        for (int tries = 0; tries < 1200 && spots.Count < groups; tries++)
        {
            var p = OnFloor(floor[rng.Next(floor.Count)]);
            if (!NavMesh.SamplePosition(p, out var h, 0.5f, NavMesh.AllAreas) || !IsFloor(h.position)) continue;
            var at = h.position;
            if (Flat(at - arrival) < AwayFromYou || throne && Flat(at - thronePos) < AwayFromThrone || spots.Any(s => Flat(s - at) < GroupGap)) continue;
            var path = new NavMeshPath();   // (somewhere you can walk to)
            if (!NavMesh.CalculatePath(arrival, at, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            spots.Add(at);
        }
        // the sentries: 1-2 at a spot, away from the groups and each other (the halls between)
        var lone = new List<Vector3>();
        for (int tries = 0; tries < 1200 && lone.Count < sentries; tries++)
        {
            var p = OnFloor(floor[rng.Next(floor.Count)]);
            if (!NavMesh.SamplePosition(p, out var h, 0.5f, NavMesh.AllAreas) || !IsFloor(h.position)) continue;
            var at = h.position;
            if (Flat(at - arrival) < AwayFromYou || throne && Flat(at - thronePos) < AwayFromThrone ||
                spots.Any(s => Flat(s - at) < SentryGap) || lone.Any(s => Flat(s - at) < SentryGap * 1.5f)) continue;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(arrival, at, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            lone.Add(at);
        }
        int troops = 0;
        foreach (var at in spots.Concat(lone))
        {
            string colour = Colours[rng.Next(Colours.Length)];
            int n = lone.Contains(at) ? rng.Next(1, 3) : rng.Next(3, 6);
            for (int i = 0; i < n && troops < MostSoldiers; i++)
            {
                var near = at + (Vector3)(Random.insideUnitCircle * 1.6f);
                if (!IsFloor(near) || !NavMesh.SamplePosition(near, out var nh, 0.5f, NavMesh.AllAreas) || !IsFloor(nh.position)) near = at;
                else near = nh.position;
                var e = Monsters.Spawn($"{colour} {Soldiers[rng.Next(Soldiers.Length)]}", near);
                if (!e) continue;
                e.transform.SetParent(root.transform, true);   // (gone with the region)
                troops++;
            }
        }
        NetLog.Info($"Mad King: {troops} soldiers in {spots.Count} groups and {lone.Count} sentry posts ({settings}){(throne ? ", the throne's here" : "")}", toast: false);
        if (!throne) return;
        KingAt(root.transform, thronePos);
    }

    static Transform kingPlace;

    /// <summary>The King, waiting in front of his throne (in that place: KL's region, or the throne room).</summary>
    public static void KingAt(Transform place, Vector3 throne)
    {
        portalOpen = false;
        thronePos = throne;
        kingPlace = place;
        var front = throne + Vector3.down * 2f;
        if (NavMesh.SamplePosition(front, out var fh, 3f, NavMesh.AllAreas)) front = fh.position;
        king = Monsters.Spawn($"{Colours[Random.Range(0, Colours.Length)]} King", front, Title, "Lord of Kaldhor's Throne");
        if (king) { king.transform.SetParent(place, true); NetLog.Warn($"{Title} waits on his throne...", toast: true); }
    }

    /// <summary>Every frame (Plugin.Update): the King down, the way home opens before his throne.</summary>
    public static void Tick()
    {
        if (portalOpen || king == null) return;
        if (!king) { king = null; return; }   // (the region gone)
        if (!king.IsDead) return;
        portalOpen = true;
        var at = thronePos + Vector3.down * 3f;
        if (NavMesh.SamplePosition(at, out var h, 3f, NavMesh.AllAreas)) at = h.position;
        Delve.HomePortal(at, kingPlace, pressF: true);
        NetLog.Success($"{Title} has fallen! A portal home opens before his throne.", toast: true);
        // his prize, every time: the Cohhilition Sword (the game's own, unused in the demo), by his throne
        if (Random.value < SwordChance)
        {
            var by = thronePos + Vector3.down * 1.5f + Vector3.right * 1.2f;
            if (NavMesh.SamplePosition(by, out var sh, 2f, NavMesh.AllAreas)) by = sh.position;
            Arsenal.DropNamed(Sword, by);
            NetLog.Success($"{Title} dropped the {Sword}!", toast: true);
        }
    }

    static float Flat(Vector3 v) => new Vector2(v.x, v.y).magnitude;

    static Vector3 OnFloor(Vector3Int c) => new Vector3((c.x + 0.5f) * Dungeon.cellSize.x, (c.y + 0.5f) * Dungeon.cellSize.y, 0f);

    /// <summary>On one of the floor's walkable tiles.</summary>
    static bool IsFloor(Vector3 w) =>
        Dungeon.floorCells.Contains(new Vector3Int(Mathf.FloorToInt(w.x / Dungeon.cellSize.x), Mathf.FloorToInt(w.y / Dungeon.cellSize.y), 0));
}

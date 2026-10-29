using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace EmbervilleGhost;

/// <summary>
/// Kaldhor's throne room: a whole place the developers built and the demo never uses ("= Kaldhor_ThroneRoom Variant":
/// its tiles, the throne, 173 props, its doors). KL's way down: region 0's doorway (Throne_CaveA) leads to region 1,
/// region 1's (Throne_CaveB) to the throne room. The first time, it's put far off the map (as the arena is), with a walking map from its
/// floor and walls, and The Mad King waits before the throne (MadKing.KingAt). Its own doors take you back to KL where
/// you came from (the room stays as you left it); the King down, the way home opens before the throne. Leaving the
/// dungeon takes it away too.
/// </summary>
internal static class ThroneRoom
{
    const string PrefabName = "= Kaldhor_ThroneRoom Variant";
    static readonly Vector3 Origin = new Vector3(-6000f, 6000f, 0f);

    static GameObject root;
    static GameArea area;
    static bool busy;
    static Vector3 backPos;
    static GameArea backArea;

    public static bool Active => root && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.CurrentArea == area;

    /// <summary>A KL doorway's far end: the throne room? Only Cave B's (region 1's): Cave A's (region 0's) leads on to
    /// region 1 first (Dungeon's next-region way), so the throne is at the end of both.</summary>
    public static bool Leads(string doorId) => doorId == "Throne_CaveB";

    /// <summary>Is this door one of the throne room's own?</summary>
    public static bool Owns(Component door) => root && door && door.transform.IsChildOf(root.transform);

    /// <summary>Through a KL doorway: to the throne room (built the first time), arriving at the door it names.</summary>
    public static void Enter(string doorId, Teleporter klDoor = null) => Go(doorId, klDoor).Forget();

    static async UniTaskVoid Go(string doorId, Teleporter klDoor)
    {
        if (busy) return;
        var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
        var me = LocalPlayer.Get();
        if (!gm || !me) return;
        busy = true;
        try
        {
            // the way back: a few tiles into KL from the doorway you came by (not on it: that sent you straight back in)
            backPos = klDoor ? Dungeon.Inside(klDoor.DestPosition, klDoor) : me.transform.position;
            backArea = gm.CurrentArea;
            if (!root && !await Build()) return;
            // arrive at the door the doorway named (else any of its doors)
            var doors = root.GetComponentsInChildren<Teleporter>(true);
            var door = doors.FirstOrDefault(t => t.teleporterID == doorId) ?? doors.FirstOrDefault(t => t.name.Contains(doorId.Replace("Throne_", ""))) ?? doors.FirstOrDefault();
            var (at, dir) = Inside(door);
            await gm.TeleportPlayerFade(at, dir, area, area.CameraConfiner);
            NetLog.Warn("Kaldhor's throne room...", toast: true);
        }
        catch (System.Exception e) { NetLog.Warn($"Throne room: {e.Message}", toast: true); Debug.LogException(e); }
        finally { busy = false; }
    }

    /// <summary>The room, far off the map: its prefab (loaded with the dungeons' biomes), a walking map, the King.</summary>
    static async UniTask<bool> Build()
    {
        Dungeon.AllSettings();   // (the biomes: the Kaldhor places come in with them)
        var prefab = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g && g.name == PrefabName && !g.scene.IsValid());
        if (!prefab) { NetLog.Warn("Throne room: Kaldhor's throne room isn't in memory", toast: true); return false; }
        root = Object.Instantiate(prefab, Origin, Quaternion.identity);
        root.name = "EG_ThroneRoom";
        area = root.GetComponent<GameArea>() ?? root.GetComponentInChildren<GameArea>(true);
        if (!area) { NetLog.Warn("Throne room: it has no area", toast: true); Object.Destroy(root); root = null; return false; }
        // its own enter / exit events (the full game's: Kaldhor's intro, which hides you and turns your collisions
        // off for its cutscene): logged, and taken off: here it's just a room
        string Events(UnityEngine.Events.UnityEventBase ev) => ev == null ? "none" : string.Join(", ", Enumerable.Range(0, ev.GetPersistentEventCount())
            .Select(i => $"{(ev.GetPersistentTarget(i) is Object o && o ? o.name : "?")}.{ev.GetPersistentMethodName(i)}"));
        NetLog.Info($"Throne room: its own events taken off: enter [{Events(area.onEnter)}], faded in [{Events(area.onFinishFadeAnimation)}], exit [{Events(area.onExit)}]", toast: false);
        area.onEnter = new UnityEngine.Events.UnityEvent();
        area.onFinishFadeAnimation = new UnityEngine.Events.UnityEvent();
        area.onExit = new UnityEngine.Events.UnityEvent();
        foreach (var intro in root.GetComponentsInChildren<KaldhorsIntro>(true)) Object.Destroy(intro);   // (and the intro itself, if it's in the room)

        // the walking map: its floor tiles walkable, its walls (and their tops) solid
        var ground = new HashSet<Vector3Int>();
        var walls = new HashSet<Vector3Int>();
        var cell = Vector3.one;
        foreach (var tm in root.GetComponentsInChildren<Tilemap>(true))
        {
            var grid = tm.GetComponentInParent<Grid>(true);
            if (grid) cell = grid.cellSize;
            bool wall = tm.name.IndexOf("Wall", System.StringComparison.OrdinalIgnoreCase) >= 0 || tm.name.IndexOf("Collision", System.StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var c in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(c)) continue;
                var w = Dungeon.CellCenter(tm, c);
                var wc = new Vector3Int(Mathf.FloorToInt(w.x / cell.x), Mathf.FloorToInt(w.y / cell.y), 0);
                (wall ? walls : ground).Add(wc);
            }
        }
        ground.ExceptWith(walls);
        Dungeon.NavLayers(root.transform, cell, ground, walls, out var walkable, out var blocked);
        foreach (var r in walkable.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (var r in blocked.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        area.Setup();
        await Dungeon.BuildNavMesh(walkable, blocked);
        NetLog.Info($"Throne room: built at {Origin.x:0},{Origin.y:0} ({ground.Count} floor tiles, {walls.Count} wall tiles, cell {cell}); doors: " +
                    string.Join(", ", root.GetComponentsInChildren<Teleporter>(true).Select(t => $"{t.name} ({t.teleporterID})")), toast: false);

        // the King, before his throne
        var throne = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith("Kaldhor_Throne"));
        if (throne) { thronePos = throne.position; MadKing.KingAt(root.transform, throne.position); }
        else NetLog.Warn("Throne room: no throne in it", toast: false);
        return true;
    }

    static Vector3 thronePos;

    /// <summary>Where to arrive by a door: its own arrival point is on the side you'd come from in the full game (outside
    /// the room); so 3 tiles from the door, on the side with a walking path to the throne (the nearest such), facing in.</summary>
    static (Vector3, Direction) Inside(Teleporter door)
    {
        var from = door ? door.transform.position : root.transform.position;
        var goal = thronePos != Vector3.zero ? thronePos + Vector3.down * 2.5f : root.transform.position;
        UnityEngine.AI.NavMesh.SamplePosition(goal, out var g, 4f, UnityEngine.AI.NavMesh.AllAreas);
        (Vector3 at, Direction dir, float len)? best = null;
        foreach (var (v, d) in new[] { (Vector2.up, Direction.Up), (Vector2.down, Direction.Down), (Vector2.left, Direction.Left), (Vector2.right, Direction.Right) })
            for (float far = 3f; far <= 6f; far += 1.5f)
            {
                if (!UnityEngine.AI.NavMesh.SamplePosition(from + (Vector3)(v * far), out var h, 1f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                var path = new UnityEngine.AI.NavMeshPath();
                if (!UnityEngine.AI.NavMesh.CalculatePath(h.position, g.position, UnityEngine.AI.NavMesh.AllAreas, path) || path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) continue;
                float len = (h.position - from).magnitude;
                if (best == null || len < best.Value.len) best = (h.position, d, len);
                break;
            }
        if (best is { } b) { NetLog.Info($"Throne room: arriving {b.dir} of {(door ? door.name : "the room")} at {b.at.x:0.0},{b.at.y:0.0}", toast: false); return (b.at, b.dir); }
        NetLog.Warn("Throne room: no way in found by that door: arriving before the throne", toast: false);
        return (g.position != Vector3.zero ? g.position + Vector3.down * 3f : from, Direction.Up);
    }

    /// <summary>One of its own doors: back to KL, where you came from (the room stays as it is).</summary>
    public static void Back()
    {
        var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
        if (!gm || busy) return;
        gm.gameState = GameState.Playing;
        if (!backArea) { Dungeon.Leave(); return; }   // (KL's region gone meanwhile: home)
        gm.TeleportPlayerFade(backPos, Direction.Down, backArea, backArea.CameraConfiner).Forget();
    }

    /// <summary>The dungeon left (Dungeon.Leave / its places cleaned up): the room goes too.</summary>
    public static void Close()
    {
        thronePos = Vector3.zero;
        if (!root) return;
        DeathSafety.Forget(root);
        Object.Destroy(root);
        root = null;
        area = null;
    }

    internal static Transform Root => root ? root.transform : null;

    // ---- /throne: straight to the fight (KL's second region built first, if you're not in a dungeon) ----

    static bool pending;
    static float pendingSince;

    public static void Command()
    {
        if (Dungeon.Active && !Dungeon.Busy) { Enter("Throne_CaveB"); return; }
        pending = true;
        pendingSince = Time.unscaledTime;
        Chat.System("To the Mad King: KL first...");
        Dungeon.EnterNamed("Dungeon_B01_KL 1");
    }

    /// <summary>Every frame (Plugin.Update): KL built and you're in it: on into the throne room.</summary>
    public static void Tick()
    {
        if (!pending) return;
        if (Time.unscaledTime - pendingSince > 120f) { pending = false; return; }   // (it never got there)
        var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
        if (!Dungeon.Active || Dungeon.Busy || !gm || gm.gameState != GameState.Playing) return;
        pending = false;
        Enter("Throne_CaveB");
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The arena's hazards: the game's own spike traps (GroundTrap: spikes that come up every second and a half) in the
/// lanes between the pillars, firing in waves, and its arrow shooters (ShooterTrap) on the side walls, shooting
/// across. Copies of ones the game has loaded (the tutorial area's, the dungeon rooms'). They hurt whoever's in the
/// way, creatures included.
/// </summary>
internal static class ArenaTraps
{
    const string Prefix = "EG_ArenaTrap";
    static GameObject holder, spikeGroup, shooterGroup;

    /// <summary>In a boss rush the spikes join at this wave, the arrow shooters at this one.</summary>
    public const int SpikesFrom = 5, ShootersFrom = 10;

    /// <summary>Both kinds (the panel's checkbox, outside a rush). They start off.</summary>
    public static bool On
    {
        get => spikesOn || arrowsOn;
        set { Set(spikeGroup, value); Set(shooterGroup, value); }
    }

    static bool spikesOn, arrowsOn;

    /// <summary>Dormant, not hidden: the spikes stay down and the shooters quiet, but you can see where they are.</summary>
    static void Set(GameObject g, bool on)
    {
        if (!g) return;
        if (g == spikeGroup) spikesOn = on; else arrowsOn = on;
        foreach (var t in g.GetComponentsInChildren<GroundTrap>(true)) t.isActive = on;
        foreach (var t in g.GetComponentsInChildren<ShooterTrap>(true)) t.state = on ? SwitchState.On : SwitchState.Off;
    }

    /// <summary>The rush's wave: spikes from wave 5, arrows from wave 10 (announced when they come on).</summary>
    public static void ForWave(int wave)
    {
        bool spikes = wave >= SpikesFrom, arrows = wave >= ShootersFrom;
        if (spikes && spikeGroup && !spikesOn) NetLog.Warn("The spike traps wake up!", toast: true);
        if (arrows && shooterGroup && !arrowsOn) NetLog.Warn("The arrow shooters start firing!", toast: true);
        Set(spikeGroup, spikes);
        Set(shooterGroup, arrows);
    }

    /// <summary>Into a freshly built arena (W x H tiles from <paramref name="origin"/>).</summary>
    public static void Place(Transform root, Vector3 origin, int w, int h)
    {
        walls = root.Find("Tiles/Walls")?.GetComponent<UnityEngine.Tilemaps.Tilemap>();
        holder = new GameObject(Prefix + "s");
        holder.transform.SetParent(root, false);
        holder.transform.position = Vector3.zero;
        spikeGroup = new GameObject("Spikes");
        spikeGroup.transform.SetParent(holder.transform, false);
        shooterGroup = new GameObject("Shooters");
        shooterGroup.transform.SetParent(holder.transform, false);
        int spikes = 0, shooters = 0;

        var spike = Template<GroundTrap>();
        if (spike)
        {
            // three strips across the open lanes (top, middle, bottom), each a row of three; a ripple of start delays
            float[] rows = { 0.82f, 0.52f, 0.2f };
            for (int r = 0; r < rows.Length; r++)
                for (int i = -1; i <= 1; i++)
                {
                    var at = origin + new Vector3(w * 0.5f + i * 1.0f + 0.5f, h * rows[r] + 0.5f, 0f);
                    var t = Copy(spike.gameObject, at, spikeGroup);
                    var g = t.GetComponent<GroundTrap>();
                    if (g) { g.startDelay = (i + 1) * 0.25f + r * 0.5f; g.isActive = true; }
                    spikes++;
                }
        }

        var shooterList = Templates<ShooterTrap>();
        if (shooterList.Count > 0)
        {
            // on all four walls, in the lanes between the pillars: left (two) shooting right, right (one) shooting left,
            // the south wall (two) shooting up, the top (two) shooting down
            (float fx, float fy, Direction dir)[] spots =
            {
                (0f, 0.36f, Direction.Right), (0f, 0.68f, Direction.Right),
                (1f, 0.42f, Direction.Left),
                (0.23f, 0f, Direction.Up), (0.77f, 0f, Direction.Up),
                (0.42f, 1f, Direction.Down), (0.58f, 1f, Direction.Down),
            };
            int n = 0;
            foreach (var (fx, fy, dir) in spots)
            {
                var template = shooterList.FirstOrDefault(s => s.shotDirection == dir) ?? shooterList[0];
                var t = Copy(template.gameObject, origin + new Vector3(fx * w, fy * h, 0f), shooterGroup);
                var st = t.GetComponent<ShooterTrap>();
                if (st)
                {
                    st.shotDirection = dir;
                    st.state = SwitchState.Off;
                    // one arrow per shooter every 6 s, the walls taking turns (left, then south, then top + right),
                    // so a volley comes every 2 s
                    st.cycleTime = ShotCycle;
                    st.cycleShots = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.02f, 1f), new Keyframe(0.06f, 1f), new Keyframe(0.08f, 0f), new Keyframe(1f, 0f));
                    st.shotInSequence = true;
                    st.dephase = Turn(dir) / 3f;
                }
                Flush(t, origin, w, h, fx, fy);
                n++;
                shooters++;
            }
        }
        On = false;   // quiet until a rush reaches them (or the panel's checkbox)
        NetLog.Info($"Arena traps: {spikes} spike traps, {shooters} arrow shooters" + (spike ? "" : " (no spike trap loaded here)") + (shooterList.Count > 0 ? "" : " (no arrow shooter loaded here)"), toast: false);
    }

    /// <summary>
    /// Against its wall: the shooter's outer edge on the wall ART's inner edge (a little into it). The wall tiles' art
    /// doesn't end where the solid edge does (the top wall's face is drawn above its cells), so it's measured from the
    /// wall tilemap's sprites; the solid edge if there's no art to measure.
    /// </summary>
    static void Flush(GameObject t, Vector3 origin, int w, int h, float fx, float fy)
    {
        var rs = t.GetComponentsInChildren<Renderer>(true).Where(r => r is SpriteRenderer && r.bounds.size.sqrMagnitude > 0f).ToList();
        if (rs.Count == 0) return;
        var b = rs[0].bounds;
        foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
        const float Into = 0.25f;
        var p = t.transform.position;
        var shift = Vector3.zero;
        if (fx <= 0f) shift.x = WallEdge(new Vector3(origin.x - 0.5f, p.y), Side.Left, origin.x) - b.min.x - Into;
        else if (fx >= 1f) shift.x = WallEdge(new Vector3(origin.x + w + 0.5f, p.y), Side.Right, origin.x + w) - b.max.x + Into;
        if (fy <= 0f) shift.y = WallEdge(new Vector3(p.x, origin.y - 0.5f), Side.Bottom, origin.y) - b.min.y - Into;
        else if (fy >= 1f) shift.y = WallEdge(new Vector3(p.x, origin.y + h + 0.5f), Side.Top, origin.y + h) - b.max.y + Into;
        t.transform.position += shift;
        NetLog.Info($"Arena shooter at wall ({fx:0.##},{fy:0.##}): art {b.min.x:0.##}..{b.max.x:0.##} x {b.min.y:0.##}..{b.max.y:0.##}, moved {shift.x:0.##},{shift.y:0.##}", toast: false);
    }

    enum Side { Left, Right, Bottom, Top }

    /// <summary>Where the wall art at that wall cell ends towards the arena (world units).</summary>
    static float WallEdge(Vector3 cellWorld, Side side, float solidEdge)
    {
        if (!walls) return solidEdge;
        var local = walls.transform.InverseTransformPoint(cellWorld);   // (the arena's tilemaps sit at the origin, 1x1 cells)
        var cell = new Vector3Int(Mathf.FloorToInt(local.x), Mathf.FloorToInt(local.y), 0);
        var sprite = walls.GetSprite(cell);
        if (!sprite) return solidEdge;
        var c = Dungeon.CellCenter(walls, cell);
        var sb = sprite.bounds;   // around the sprite's pivot, which sits on the cell's centre
        return side switch
        {
            Side.Left => c.x + sb.max.x,
            Side.Right => c.x + sb.min.x,
            Side.Bottom => c.y + sb.max.y,
            _ => c.y + sb.min.y,
        };
    }

    static UnityEngine.Tilemaps.Tilemap walls;

    const float ShotCycle = 6f;

    /// <summary>Which turn a wall fires on (of three, 2 s apart).</summary>
    static int Turn(Direction shootsTowards) => shootsTowards switch
    {
        Direction.Right => 0,   // the left wall
        Direction.Up => 1,      // the south wall
        _ => 2,                 // the top wall and the right
    };

    /// <summary>The traps are for the players: creatures in the arena take nothing from them (spikes, arrows: hits with
    /// no attacker, or a trap's damage object, as the attacker).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    static class SparesCreatures
    {
        static bool Prefix(Enemy __instance, DamageData damageData)
        {
            if (!holder || !Arena.Active) return true;
            if (!__instance.transform.IsChildOf(holder.transform.parent)) return true;
            var a = damageData.Aggressor;
            return !(a == null || a is DamageInflictObject || (a as Object) == null);
        }
    }

    static GameObject Copy(GameObject template, Vector3 at, GameObject group)
    {
        var go = Object.Instantiate(template, group.transform);
        go.name = $"{Prefix}_{template.name}";
        go.transform.position = at;
        go.SetActive(true);
        return go;
    }

    static T Template<T>() where T : Component => Templates<T>().FirstOrDefault();

    /// <summary>The game's own (prefabs first, then ones in the scene), never our copies.</summary>
    static List<T> Templates<T>() where T : Component =>
        Resources.FindObjectsOfTypeAll<T>().Where(x => x && !x.name.StartsWith(Prefix))
            .OrderBy(x => x.gameObject.scene.IsValid() ? 1 : 0).ThenBy(x => x.name).ToList();

    /// <summary>A shooter clears the shared projectile pool when it goes; ours going with the arena mustn't take the
    /// tutorial area's arrows with it.</summary>
    [HarmonyPatch(typeof(ShooterTrap), "OnDestroy")]
    static class KeepPool
    {
        static bool Prefix(ShooterTrap __instance) => !__instance.name.StartsWith(ArenaTraps.Prefix);
    }
}

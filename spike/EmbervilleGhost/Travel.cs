using System.Linq;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Going somewhere in this world, wherever "somewhere" is: another of the scene's areas, or a spot on a generated
/// dungeon / descent floor (built again from its settings and seed, as joining a friend's floor does, and arriving at
/// that spot). The town portal uses it, and so do /tp (go to a player) and /summon (bring them to you: their game is
/// told where, and goes; no asking).
/// </summary>
internal static class Travel
{
    public static Plugin Plugin;

    /// <summary>A place: scene, area, or a dungeon floor, and the spot in it.</summary>
    public struct Spot
    {
        public string Scene, Area; public bool InDungeon; public Dungeon.Where Where; public Vector3 At;

        public void Write(NetDataWriter w)
        {
            w.Put(Scene ?? ""); w.Put(Area ?? ""); w.Put(InDungeon);
            w.Put(Where.Settings ?? ""); w.Put(Where.Seed); w.Put(Where.Region); w.Put(Where.Floor);
            w.Put(At.x); w.Put(At.y);
        }

        public static Spot Read(NetDataReader r) => new Spot
        {
            Scene = r.GetString(), Area = r.GetString(), InDungeon = r.GetBool(),
            Where = new Dungeon.Where(r.GetString(), r.GetInt(), r.GetInt(), r.GetInt()),
            At = new Vector3(r.GetFloat(), r.GetFloat(), 0f),
        };

        public bool IsHere => InDungeon
            ? Dungeon.Active && Dungeon.Current.HasValue && Dungeon.Current.Value == Where
            : !Dungeon.Active && LocalPlayer.Scene == Scene && LocalPlayer.Area == Area;
    }

    /// <summary>Where we are now (null if there's no player).</summary>
    public static Spot? Here()
    {
        var me = LocalPlayer.Get();
        if (!me) return null;
        var s = new Spot { Scene = LocalPlayer.Scene, Area = LocalPlayer.Area, InDungeon = Dungeon.Active && Dungeon.Current.HasValue, At = me.transform.position };
        if (s.InDungeon) s.Where = Dungeon.Current.Value;
        return s;
    }

    static GameArea AreaNamed(string name) =>
        Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => a && a.name == name);

    /// <summary>Go there. False (and why, on screen) if it can't be done from here.</summary>
    public static bool Go(Spot to, Direction facing = Direction.Down)
    {
        var me = LocalPlayer.Get();
        if (!me || !Singleton<GameManager>.HasMain) return false;
        if (Dungeon.Busy) { NetLog.Info("Still building a dungeon floor...", toast: true); return false; }
        if (to.Scene != LocalPlayer.Scene) return Load(to);
        var gm = Singleton<GameManager>.main;
        if (to.InDungeon)
        {
            if (to.IsHere)
            {
                var area = Dungeon.Root ? Dungeon.Root.GetComponent<GameArea>() : null;
                gm.TeleportPlayerFade(to.At, facing, area, area ? area.CameraConfiner : null).Forget();
                return true;
            }
            if (to.Where.Floor > 0) Delve.Follow(to.Where);   // (the descent's rules for that floor)
            Dungeon.ArriveNextAt(to.At);
            Dungeon.Enter(to.Where.Settings, to.Where.Seed, to.Where.Region, announce: false, floor: to.Where.Floor);
            return true;
        }
        // the area whose camera bounds hold the spot (an area's name can be shared; a wrong one leaves the camera behind)
        var byName = string.IsNullOrEmpty(to.Area) ? null : AreaNamed(to.Area);
        var target = Holds(byName, to.At) ? byName : AreaAt(to.At) ?? byName;
        if (!target) { NetLog.Warn($"Can't find {(string.IsNullOrEmpty(to.Area) ? "that place" : to.Area)}", toast: true); return false; }
        var all = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        NetLog.Info($"Travel: from {LocalPlayer.Area} at {me.transform.position} to {target.name} ({(target.gameObject.activeInHierarchy ? "active" : "inactive")}, " +
                    $"{all.Count(a => a && a.name == target.name)} areas by that name; the spot {(Holds(target, to.At) ? "inside" : "OUTSIDE")} its camera bounds) at {to.At}" +
                    $"; areas holding the spot: {string.Join(", ", all.Where(a => Holds(a, to.At)).Select(a => a.name))}", toast: false);
        if (Dungeon.Active) Dungeon.LeaveTo(to.At, target, to.Scene);
        else gm.TeleportPlayerFade(to.At, facing, target, target.CameraConfiner).Forget();
        return true;
    }

    /// <summary>The area whose camera bounds hold this spot (when we only know where, not the area's name).</summary>
    static GameArea AreaAt(Vector3 at) =>
        Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => Holds(a, at));

    /// <summary>Is the spot inside this area's camera bounds? (An area you're not in is switched off, and a switched-off
    /// collider answers no to everything: its shape is checked by hand then.)</summary>
    public static bool Holds(GameArea a, Vector3 at)
    {
        var c = a ? a.CameraConfiner : null;
        if (!c) return false;
        if (c.enabled && c.gameObject.activeInHierarchy) return c.OverlapPoint(at);
        var local = (UnityEngine.Vector2)c.transform.InverseTransformPoint(at) - c.offset;
        switch (c)
        {
            case PolygonCollider2D poly:
                for (int i = 0; i < poly.pathCount; i++) if (Inside(poly.GetPath(i), local)) return true;
                return false;
            case BoxCollider2D box:
                return Mathf.Abs(local.x) <= box.size.x / 2f && Mathf.Abs(local.y) <= box.size.y / 2f;
            default:
                return false;
        }
    }

    static bool Inside(UnityEngine.Vector2[] poly, UnityEngine.Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    // ---- another scene: the game's own loading, then the spot ----

    static Spot? pending;
    static float pendingSince;

    /// <summary>Into another scene (the game's loading screen), then on to the spot once we're standing in it.</summary>
    static bool Load(Spot to)
    {
        if (!Singleton<SceneLoader>.HasMain) { NetLog.Warn("Can't change scenes from here", toast: true); return false; }
        pending = to;
        pendingSince = Time.unscaledTime;
        Singleton<SceneLoader>.main.CallScene(to.Scene, null).Forget();
        NetLog.Info($"Travelling to {to.Scene}...", toast: true);
        return true;
    }

    /// <summary>Every frame: arrived in the scene we were going to? Then to the spot itself.</summary>
    public static void Tick()
    {
        if (!(pending is Spot to)) return;
        if (Time.unscaledTime - pendingSince > 60f) { pending = null; return; }   // (it never got there)
        var me = LocalPlayer.Get();
        if (!me || LocalPlayer.Scene != to.Scene || !Singleton<GameManager>.HasMain || Singleton<GameManager>.main.CurrentArea == null) return;
        if (Time.unscaledTime - pendingSince < 1.5f) return;   // (let the scene settle: the player placed, the fade done)
        pending = null;
        Go(to);
    }

    // ---- /tp and /summon ----

    /// <summary>The player called that (a part of the name will do), or null (and why, on screen).</summary>
    public static RemotePlayer Find(string name)
    {
        name = (name ?? "").Trim();
        if (Plugin == null || !Plugin.Connected) { NetLog.Info("Not playing together right now", toast: true); return null; }
        var all = Plugin.Players.ToList();
        var p = all.FirstOrDefault(x => string.Equals(x.Name, name, System.StringComparison.OrdinalIgnoreCase))
             ?? all.FirstOrDefault(x => x.Name != null && x.Name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (p == null) NetLog.Info(name.Length == 0 ? "Who? (/tp name, /summon name, /summon all)" : $"No player called {name}", toast: true);
        return p;
    }

    /// <summary>Go to that player (their game tells us exactly where they are, dungeon floor and all).</summary>
    public static void TeleportTo(RemotePlayer p)
    {
        if (p == null) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.TpAsk);
        Plugin.SendTo(p.Id, w);
        NetLog.Info($"Going to {p.Name}...", toast: true);
    }

    /// <summary>Bring that player here, now (no asking).</summary>
    public static void Summon(RemotePlayer p)
    {
        if (p == null || !(Here() is Spot here)) return;
        here.At += new Vector3(0.9f, 0f, 0f);   // beside us, not on top
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SummonTo);
        w.Put(GameData.HasPlayerData && !string.IsNullOrEmpty(GameData.playerData.playerName) ? GameData.playerData.playerName : "Someone");
        here.Write(w);
        Plugin.SendTo(p.Id, w);
        NetLog.Info($"Bringing {p.Name} over", toast: true);
    }

    public static void SummonAll()
    {
        if (Plugin == null || !Plugin.Connected) { NetLog.Info("Not playing together right now", toast: true); return; }
        foreach (var p in Plugin.Players.ToList()) Summon(p);
    }

    /// <summary>Someone wants to come to us: we bring them (the same as /summon them).</summary>
    public static void OnTpAsk(int from)
    {
        var p = Plugin.Players.FirstOrDefault(x => x.Id == from);
        if (p != null) Summon(p);
    }

    /// <summary>Someone brings us to them: off we go.</summary>
    public static void OnSummon(NetDataReader r)
    {
        string who = r.GetString();
        var to = Spot.Read(r);
        if (Go(to)) NetLog.Info($"{who} brought you over", toast: true);
    }
}

using System.Collections.Generic;
using BepInEx.Configuration;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The mismatch checker: every few seconds each player in an area that someone else runs sends the area owner a
/// summary of what their game sees there (StateDigest): creatures (alive + health), doors, switches, floor buttons,
/// repaired buildings / portals, opened item locks, story flags. The owner compares it with its own; a difference that
/// lasts (two summaries in a row) is written as "MISMATCH ..." (NetLog -> the host's session log, and the Insert
/// menu's count), and "resolved" once it goes away. Creature health only counts when it's far apart (it trails by a
/// moment) or one side has it dead.
/// </summary>
internal static class DesyncCheck
{
    public static Plugin Plugin;
    static ConfigEntry<bool> on;
    const float Every = 3f;
    static float next;

    enum Cat : byte { Creature, Door, Switch, Plate, Repaired, Portal, ItemLock, Flag }
    const int Missing = int.MinValue, Dead = -1;

    struct Open { public float Since; public bool Reported; public string Text; }
    static readonly Dictionary<(int player, Cat cat, uint id), Open> open = new Dictionary<(int, Cat, uint), Open>();

    public static int MismatchCount { get; private set; }

    public static void Bind(ConfigFile config) =>
        on = config.Bind("Diagnostics", "MismatchCheck", true, "Compare what each player's game sees in an area with the area owner's, and log differences");

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    public static void Tick()
    {
        if (!Connected || !on.Value) { open.Clear(); MismatchCount = 0; return; }
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + Every;
        if (Plugin.IOwnHere || !LocalPlayer.Get()) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StateDigest);
        w.Put(LocalPlayer.Scene ?? "");
        w.Put(LocalPlayer.Area ?? "");
        var items = Collect();
        w.Put(items.Count);
        foreach (var (cat, id, value) in items) { w.Put((byte)cat); w.Put(id); w.Put(value); }
        Plugin.SendTo(Plugin.OwnerHere, w, DeliveryMethod.ReliableUnordered);
    }

    /// <summary>What our game sees in this scene, as (category, id, value).</summary>
    static List<(Cat, uint, int)> Collect()
    {
        var list = new List<(Cat, uint, int)>();
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.gameObject.activeInHierarchy || EnemySync.LocalOnly(e)) continue;
            uint id = EnemySync.NetId(e);
            if (id == 0) continue;
            list.Add((Cat.Creature, id, e.IsDead ? Dead : Mathf.Max(0, Mathf.RoundToInt(e.CurrentHealth / Mathf.Max(1f, e.MaxHealth) * 100f))));
        }
        foreach (var d in Object.FindObjectsByType<GateDoor>(FindObjectsSortMode.None)) list.Add((Cat.Door, SceneIds.Of(d), (int)d.state));
        foreach (var s in Object.FindObjectsByType<InteractableSwitch>(FindObjectsSortMode.None)) list.Add((Cat.Switch, SceneIds.Of(s), (int)s.state));
        foreach (var b in Object.FindObjectsByType<GroundButton>(FindObjectsSortMode.None)) list.Add((Cat.Plate, SceneIds.Of(b), (int)b.state));
        foreach (var b in Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None)) list.Add((Cat.Repaired, SceneIds.Of(b), b.isRepaired ? 1 : 0));
        foreach (var p in Object.FindObjectsByType<DestructedPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None)) list.Add((Cat.Portal, SceneIds.Of(p), p.isRepaired ? 1 : 0));
        foreach (var r in Object.FindObjectsByType<ItemRequester>(FindObjectsInactive.Include, FindObjectsSortMode.None)) list.Add((Cat.ItemLock, SceneIds.Of(r), r.enabled ? 0 : 1));
        foreach (var pair in GameData.gameBools) list.Add((Cat.Flag, (uint)pair.Key, pair.Value ? 1 : 0));
        return list;
    }

    /// <summary>Area owner: a player's summary; compare with ours.</summary>
    public static void OnDigest(NetDataReader r, int from)
    {
        string scene = r.GetString(), area = r.GetString();
        int n = r.GetInt();
        var theirs = new Dictionary<(Cat, uint), int>(n);
        for (int i = 0; i < n; i++) theirs[((Cat)r.GetByte(), r.GetUInt())] = r.GetInt();
        if (!Plugin.IOwnHere || scene != LocalPlayer.Scene || area != LocalPlayer.Area) return;   // moved on since

        var ours = new Dictionary<(Cat, uint), int>();
        foreach (var (cat, id, value) in Collect()) ours[(cat, id)] = value;

        var seen = new HashSet<(Cat, uint)>();
        foreach (var pair in ours) { seen.Add(pair.Key); Compare(from, pair.Key.Item1, pair.Key.Item2, pair.Value, theirs.TryGetValue(pair.Key, out int t) ? t : Missing); }
        foreach (var pair in theirs) if (seen.Add(pair.Key)) Compare(from, pair.Key.Item1, pair.Key.Item2, Missing, pair.Value);

        // Whatever wasn't compared this time for this player (gone from both sides) is settled.
        foreach (var key in new List<(int, Cat, uint)>(open.Keys))
            if (key.Item1 == from && !seen.Contains((key.Item2, key.Item3))) open.Remove(key);
        Recount();
    }

    static void Compare(int player, Cat cat, uint id, int ours, int theirs)
    {
        var key = (player, cat, id);
        bool differs = Differs(cat, ours, theirs);
        if (!differs)
        {
            if (open.TryGetValue(key, out var was) && was.Reported)
                NetLog.Info($"Mismatch resolved after {Time.unscaledTime - was.Since:0} s: {was.Text}");
            open.Remove(key);
            return;
        }
        string text = $"{Describe(cat, id)} - {Plugin.NameOf(Plugin.MyId)}: {Value(cat, ours)}, {Plugin.NameOf(player)}: {Value(cat, theirs)}";
        if (!open.TryGetValue(key, out var o)) { open[key] = new Open { Since = Time.unscaledTime, Text = text }; return; }   // wait for the next summary
        o.Text = text;
        if (!o.Reported) { o.Reported = true; NetLog.Warn($"MISMATCH {text} (for {Time.unscaledTime - o.Since:0} s)", toast: false); }
        open[key] = o;
    }

    static bool Differs(Cat cat, int ours, int theirs)
    {
        if (ours == theirs) return false;
        if (cat == Cat.Flag && (ours == Missing || theirs == Missing)) return (ours == Missing ? theirs : ours) == 1;   // unset = false
        if (cat != Cat.Creature) return true;
        if (ours == Missing || theirs == Missing) return (ours == Missing ? theirs : ours) != Dead;   // gone vs dead: same thing
        if (ours == Dead || theirs == Dead) return true;
        return Mathf.Abs(ours - theirs) > 35;   // health trails by a moment: only far apart counts
    }

    static string Describe(Cat cat, uint id)
    {
        switch (cat)
        {
            case Cat.Creature: { var e = EnemySync.FindById(id); return e ? $"creature {e.name}" : $"creature #{id:X}"; }
            case Cat.Door: return "door " + NameOf<GateDoor>(id);
            case Cat.Switch: return "switch " + NameOf<InteractableSwitch>(id);
            case Cat.Plate: return "floor button " + NameOf<GroundButton>(id);
            case Cat.Repaired: return "building " + NameOf<RepairableBuilding>(id);
            case Cat.Portal: return "portal " + NameOf<DestructedPortal>(id);
            case Cat.ItemLock: return "item lock " + NameOf<ItemRequester>(id);
            default: return "story flag " + (BoolID)id;
        }
    }

    static string NameOf<T>(uint id) where T : Component
    {
        var c = SceneIds.Find<T>(id);
        return c ? SceneIds.PathOf(c.transform) : $"#{id:X}";
    }

    static string Value(Cat cat, int v)
    {
        if (v == Missing) return cat == Cat.Flag ? "off" : "not there";
        switch (cat)
        {
            case Cat.Creature: return v == Dead ? "dead" : $"alive {v}%";
            case Cat.Door: return ((DoorState)v).ToString();
            case Cat.Switch:
            case Cat.Plate: return ((SwitchState)v).ToString();
            case Cat.Repaired:
            case Cat.Portal: return v == 1 ? "repaired" : "broken";
            case Cat.ItemLock: return v == 1 ? "unlocked" : "locked";
            default: return v == 1 ? "on" : "off";
        }
    }

    static void Recount()
    {
        int n = 0;
        foreach (var o in open.Values) if (o.Reported) n++;
        MismatchCount = n;
    }
}

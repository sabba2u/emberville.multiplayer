using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Floor buttons (GroundButton: pressure plates in the tutorial's trap and puzzle rooms). The game only feels its own
/// player on them ("PlayerCenter"); teammates are stand-ins it can't feel. So whoever steps on one tells the others
/// (GroundPress), and on every copy a plate is down while anyone stands on it: up only when the last one steps off.
/// Standing players repeat it every couple of seconds (someone who walks in later sees it down; a lost "off" or a
/// player who left heals by itself after a few seconds).
/// </summary>
internal static class GroundSync
{
    public static Plugin Plugin;
    const float Repeat = 2f, Expire = 5f;

    // plate -> player id -> last heard (ours: while we stand on it)
    static readonly Dictionary<GroundButton, Dictionary<int, float>> pressing = new Dictionary<GroundButton, Dictionary<int, float>>();
    static float nextRepeat;
    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    static Dictionary<int, float> On(GroundButton b)
    {
        if (!pressing.TryGetValue(b, out var who)) pressing[b] = who = new Dictionary<int, float>();
        return who;
    }

    static void Send(GroundButton b, bool down)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.GroundPress);
        w.Put(SceneIds.Of(b));
        w.Put(down);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    [HarmonyPatch(typeof(GroundButton), "OnTriggerEnter2D"), HarmonyPrefix]
    static bool Entered(GroundButton __instance, Collider2D other)
    {
        if (!Connected || !other || !other.CompareTag("PlayerCenter")) return true;
        var who = On(__instance);
        bool othersOn = who.Count > 0;
        who[Plugin.MyId] = Time.unscaledTime;
        Send(__instance, true);
        return !othersOn;   // someone's already on it: it's down already
    }

    [HarmonyPatch(typeof(GroundButton), "OnTriggerExit2D"), HarmonyPrefix]
    static bool Left(GroundButton __instance, Collider2D other)
    {
        if (!Connected || !other || !other.CompareTag("PlayerCenter")) return true;
        var who = On(__instance);
        who.Remove(Plugin.MyId);
        Send(__instance, false);
        return who.Count == 0;   // someone else still stands on it: it stays down
    }

    /// <summary>A teammate stepped on / off a plate here.</summary>
    public static void OnPress(NetDataReader r, int from)
    {
        uint id = r.GetUInt();
        bool down = r.GetBool();
        var b = SceneIds.Find<GroundButton>(id);
        if (!b || from == Plugin.MyId) return;
        var who = On(b);
        if (down)
        {
            bool first = !who.ContainsKey(from);
            who[from] = Time.unscaledTime;
            if (first && b.state == SwitchState.Off) b.TurnOn();
        }
        else
        {
            who.Remove(from);
            if (who.Count == 0 && b.state == SwitchState.On) b.TurnOff();
        }
    }

    /// <summary>Every frame: repeat ours, forget teammates we stopped hearing from.</summary>
    public static void Tick()
    {
        if (pressing.Count == 0) return;
        if (!Connected) { pressing.Clear(); return; }
        if (Time.unscaledTime < nextRepeat) return;
        nextRepeat = Time.unscaledTime + Repeat;
        List<GroundButton> dead = null;
        foreach (var pair in pressing)
        {
            var b = pair.Key;
            if (!b) { (dead ??= new List<GroundButton>()).Add(b); continue; }
            var who = pair.Value;
            if (who.ContainsKey(Plugin.MyId)) Send(b, true);
            List<int> stale = null;
            foreach (var p in who) if (p.Key != Plugin.MyId && Time.unscaledTime - p.Value > Expire) (stale ??= new List<int>()).Add(p.Key);
            if (stale == null) continue;
            foreach (int p in stale) who.Remove(p);
            if (who.Count == 0 && b.state == SwitchState.On) b.TurnOff();
        }
        if (dead != null) foreach (var b in dead) pressing.Remove(b);
    }

    public static void Clear() => pressing.Clear();
}

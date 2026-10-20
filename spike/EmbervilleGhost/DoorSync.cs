using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Doors and gates (GateDoor: Closed / Opened / Locked / Blocked, animated Open() / Close(), ChangeState, ForceState)
/// and levers / switches (InteractableSwitch and subclasses: TurnOn / TurnOff / TurnDisabled, whose UnityEvents open
/// gates and start things).
///
/// A locked door is unlocked with a key from the interacting player's OWN inventory (GateDoor.TryToUnlock, maybe
/// consuming it), so these aren't requests: whoever uses one runs it on their copy (keys, sounds, animation, events)
/// and the result goes to everyone in the area. Changes that happen by themselves (gates opened by an event, auto-close
/// timers, switches flipped by scripts) only come from the area owner: on puppets they're blocked, so a puppet's own
/// (hidden) events can't lock anyone in. Receivers apply it through the same game methods (animation, colliders,
/// lever sprite), with echoes suppressed. Arrival snapshot: every door / switch state in the scene, applied quietly.
/// </summary>
[HarmonyPatch]
internal static class DoorSync
{
    public static Plugin Plugin;

    static int localUse;     // inside our own player's OnInteract on a door / switch (depth)
    static int mirroring;    // applying someone else's change (depth): allowed everywhere, never re-broadcast

    static bool Puppet => EnemySync.PuppetMode;
    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    static float ownUntil;   // a moment after our player used something that opens doors by its events (a key altar...)

    static bool OwnUse => localUse > 0 || Time.unscaledTime < ownUntil;

    /// <summary>May this state change happen here? Puppets only take their own player's use and mirrored changes.</summary>
    static bool Allowed() => !Connected || !Puppet || OwnUse || mirroring > 0;

    /// <summary>Should we tell the others? Our own use, or the owner's world changing (not a mirror of someone else).</summary>
    static bool ShouldSend() => Connected && mirroring == 0 && (OwnUse || !Puppet) && Plugin.AnyoneElseHere;

    /// <summary>
    /// Our player used something whose events open / close doors and flip switches (a key altar, a lever-like object,
    /// a repair): those changes count as our own use (allowed even where someone else runs the area, and sent to everyone)
    /// while it runs and for a few seconds after (events that open a gate after a short animation).
    /// </summary>
    public static void BeginOwnUse() => localUse++;
    public static void EndOwnUse(float graceSeconds = 4f)
    {
        localUse--;
        ownUntil = Mathf.Max(ownUntil, Time.unscaledTime + graceSeconds);
    }

    /// <summary>Run someone else's change here: doors and switches it touches follow, nothing is re-broadcast.</summary>
    public static void Mirror(System.Action change)
    {
        mirroring++;
        try { change(); }
        finally { mirroring--; }
    }

    // ---- doors ----

    enum DoorOp : byte { Open, Close, Change, Force }

    static void SendDoor(GateDoor d, DoorOp op, DoorState state)
    {
        if (!ShouldSend()) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DoorChange);
        w.Put(SceneIds.Of(d));
        w.Put((byte)op);
        w.Put((byte)state);
        w.Put((byte)d.relativePosition);   // which side it swings to (set by whoever opened it)
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnDoor(NetDataReader r)
    {
        var d = SceneIds.Find<GateDoor>(r.GetUInt());
        var op = (DoorOp)r.GetByte();
        var state = (DoorState)r.GetByte();
        var side = (Direction)r.GetByte();
        if (!d) return;
        mirroring++;
        try
        {
            d.relativePosition = side;
            switch (op)
            {
                case DoorOp.Open: d.Open(); break;
                case DoorOp.Close: d.Close(); break;
                case DoorOp.Change: d.ChangeState(state); break;
                default: d.ForceState(state); break;
            }
        }
        finally { mirroring--; }
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.OnInteract)), HarmonyPrefix]
    static void DoorUseStart() => localUse++;

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.OnInteract)), HarmonyFinalizer]
    static void DoorUseEnd() => localUse--;

    /// <summary>Doors our player unlocked with a key: the game opens them 0.5 s later (DelayedOpen), outside OnInteract.</summary>
    static readonly HashSet<GateDoor> unlockedByUs = new HashSet<GateDoor>();

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.DelayedOpen)), HarmonyPrefix]
    static void BeforeDelayedOpen(GateDoor __instance)
    {
        if (localUse > 0) unlockedByUs.Add(__instance);
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.Open)), HarmonyPrefix]
    static bool BeforeOpen(GateDoor __instance)
    {
        bool ours = unlockedByUs.Remove(__instance);   // our key, opening now: still our own use
        if (ours) localUse++;
        try
        {
            if (!Allowed()) return false;
            SendDoor(__instance, DoorOp.Open, DoorState.Opened);
            return true;
        }
        finally { if (ours) localUse--; }
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.Close)), HarmonyPrefix]
    static bool BeforeClose(GateDoor __instance)
    {
        if (!Allowed()) return false;
        SendDoor(__instance, DoorOp.Close, DoorState.Closed);
        return true;
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.ChangeState)), HarmonyPrefix]
    static bool BeforeChange(GateDoor __instance, DoorState newState)
    {
        if (!Allowed()) return false;
        mirroring++;   // ChangeState calls Open / Close itself: send this one message, not those too
        SendDoorOutside(__instance, DoorOp.Change, newState);
        return true;
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.ChangeState)), HarmonyFinalizer]
    static void AfterChange() => mirroring--;

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.ForceState)), HarmonyPrefix]
    static bool BeforeForce(GateDoor __instance, DoorState newState)
    {
        if (!Allowed()) return false;
        SendDoor(__instance, DoorOp.Force, newState);
        return true;
    }

    /// <summary>SendDoor as if one mirroring level up (ChangeState already counted itself).</summary>
    static void SendDoorOutside(GateDoor d, DoorOp op, DoorState state)
    {
        mirroring--;
        try { SendDoor(d, op, state); }
        finally { mirroring++; }
    }

    // ---- levers / switches ----

    static void SendSwitch(InteractableSwitch s, SwitchState state)
    {
        if (!ShouldSend()) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SwitchChange);
        w.Put(SceneIds.Of(s));
        w.Put((byte)state);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnSwitch(NetDataReader r)
    {
        var s = SceneIds.Find<InteractableSwitch>(r.GetUInt());
        var state = (SwitchState)r.GetByte();
        if (!s || s.state == state) return;
        mirroring++;
        try
        {
            if (state == SwitchState.On) s.TurnOn();       // virtual: the lever's own version (sprite) runs too
            else if (state == SwitchState.Off) s.TurnOff();
            else s.TurnDisabled();
        }
        finally { mirroring--; }
    }

    /// <summary>Every OnInteract / TurnOn / TurnOff / TurnDisabled of InteractableSwitch and its subclasses (levers...).</summary>
    static IEnumerable<MethodBase> SwitchMethods(params string[] names)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(InteractableSwitch).Assembly))
        {
            if (!typeof(InteractableSwitch).IsAssignableFrom(type)) continue;
            foreach (var name in names)
            {
                var m = AccessTools.DeclaredMethod(type, name);
                if (m != null) yield return m;
            }
        }
    }

    [HarmonyPatch]
    static class SwitchUse
    {
        static IEnumerable<MethodBase> TargetMethods() => SwitchMethods(nameof(InteractableSwitch.OnInteract));
        static void Prefix() => localUse++;
        static void Finalizer() => localUse--;
    }

    [HarmonyPatch]
    static class SwitchGate
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            SwitchMethods(nameof(InteractableSwitch.TurnOn), nameof(InteractableSwitch.TurnOff), nameof(InteractableSwitch.TurnDisabled));
        static bool Prefix() => Allowed();
    }

    // The base methods run once per change (overrides call them): that's where we tell the others.
    [HarmonyPatch(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnOn)), HarmonyPostfix]
    static void AfterOn(InteractableSwitch __instance) => SendSwitch(__instance, SwitchState.On);

    [HarmonyPatch(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnOff)), HarmonyPostfix]
    static void AfterOff(InteractableSwitch __instance) => SendSwitch(__instance, SwitchState.Off);

    [HarmonyPatch(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnDisabled)), HarmonyPostfix]
    static void AfterDisabled(InteractableSwitch __instance) => SendSwitch(__instance, SwitchState.Disabled);

    // ---- arrival snapshot ----

    /// <summary>Owner: every door and switch in this scene, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        var doors = Object.FindObjectsByType<GateDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var switches = Object.FindObjectsByType<InteractableSwitch>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DoorSnapshot);
        w.Put(doors.Length);
        foreach (var d in doors) { w.Put(SceneIds.Of(d)); w.Put((byte)d.state); }
        w.Put(switches.Length);
        foreach (var s in switches) { w.Put(SceneIds.Of(s)); w.Put((byte)s.state); }
        Plugin.SendTo(playerId, w);
    }

    /// <summary>Puppet: set them to how they are where the owner is, without animations or events.</summary>
    public static void OnSnapshot(NetDataReader r)
    {
        mirroring++;
        try
        {
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                var d = SceneIds.Find<GateDoor>(r.GetUInt());
                var state = (DoorState)r.GetByte();
                if (d && d.state != state) d.ForceState(state);
            }
            n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                var s = SceneIds.Find<InteractableSwitch>(r.GetUInt());
                var state = (SwitchState)r.GetByte();
                if (!s || s.state == state) continue;
                s.state = state;
                if (s is InteractableLever lever) { if (state == SwitchState.Disabled) lever.rend.sprite = lever.spriteOffState; else lever.UpdateVisual(); }
            }
        }
        finally { mirroring--; }
    }
}

using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Things a player uses whose result is the world's, not theirs:
///  - Key altars / item locks (ItemRequester: "Need X to unlock!"): the user's game checks and takes the item from their
///    own bag, then its events open the door. Those door changes count as the user's own (DoorSync.BeginOwnUse), so
///    they happen even where someone else runs the area and reach everyone; and the lock opens on every copy
///    (ItemUnlocked: its unlock events, mirrored, no item needed there).
///  - Simple interactables (SimpleInteractable: mechanisms that aren't switches): their events stay on the user's copy
///    (some only open a page or a text), but door / switch changes they cause are the user's own use too.
///  - Repairs: town buildings (RepairableBuilding) and the broken portal (DestructedPortal). The repairer pays; everyone
///    sees it repaired (Repaired: art, effect, its events). Players arriving later get what's repaired / unlocked here
///    from the area owner.
/// </summary>
[HarmonyPatch]
internal static class InteractSync
{
    public static Plugin Plugin;
    enum Kind : byte { Building, Portal }

    static readonly HashSet<uint> unlocked = new HashSet<uint>();   // item locks opened in this scene (for arrivals)
    static int mirroring;
    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    // ---- item locks ----

    [HarmonyPatch(typeof(ItemRequester), nameof(ItemRequester.OnInteract)), HarmonyPrefix]
    static void BeforeItemLock(ItemRequester __instance, out bool __state)
    {
        DoorSync.BeginOwnUse();
        __state = Has(__instance);
    }

    [HarmonyPatch(typeof(ItemRequester), nameof(ItemRequester.OnInteract)), HarmonyFinalizer]
    static void AfterItemLock(ItemRequester __instance, bool __state)
    {
        DoorSync.EndOwnUse();
        if (!__state || mirroring > 0) return;   // we didn't have the items: it stayed locked
        uint id = SceneIds.Of(__instance);
        unlocked.Add(id);
        if (!Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ItemUnlocked);
        w.Put(id);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        NetLog.Info($"Unlocked {__instance.name} for everyone here");
    }

    /// <summary>Does our bag have everything this lock wants (what ItemRequester.OnInteract checks)?</summary>
    static bool Has(ItemRequester lockObj)
    {
        if (Singleton<Inventory>.main == null || lockObj.requiredItems == null) return false;
        foreach (var item in lockObj.requiredItems)
            if (!Singleton<Inventory>.main.ContainsItem(item, out _)) return false;
        return true;
    }

    public static void OnItemUnlocked(NetDataReader r) => UnlockHere(SceneIds.Find<ItemRequester>(r.GetUInt()), quiet: false);

    static void UnlockHere(ItemRequester lockObj, bool quiet)
    {
        if (!lockObj || !lockObj.enabled) return;
        unlocked.Add(SceneIds.Of(lockObj));
        mirroring++;
        try
        {
            DoorSync.Mirror(() => lockObj.onUnlockEvent.Invoke());
            if (lockObj.titlePivot) lockObj.titlePivot.SetActive(false);
            if (!quiet) lockObj.sfxUnlock?.Post(lockObj.gameObject);
            lockObj.enabled = false;   // done: CanInteract is false, nobody needs the item again
        }
        finally { mirroring--; }
        NetLog.Info($"{lockObj.name} was unlocked by a teammate");
    }

    // ---- simple interactables ----

    [HarmonyPatch(typeof(SimpleInteractable), nameof(SimpleInteractable.OnInteract)), HarmonyPrefix]
    static void BeforeSimple() => DoorSync.BeginOwnUse();

    [HarmonyPatch(typeof(SimpleInteractable), nameof(SimpleInteractable.OnInteract)), HarmonyFinalizer]
    static void AfterSimple() => DoorSync.EndOwnUse();

    // ---- repairs ----

    [HarmonyPatch(typeof(RepairableBuilding), nameof(RepairableBuilding.Repair)), HarmonyPrefix]
    static void BeforeRepair(RepairableBuilding __instance, out bool __state) { __state = __instance.isRepaired; DoorSync.BeginOwnUse(); }

    [HarmonyPatch(typeof(RepairableBuilding), nameof(RepairableBuilding.Repair)), HarmonyFinalizer]
    static void AfterRepair(RepairableBuilding __instance, bool __state)
    {
        DoorSync.EndOwnUse(3f);
        if (!__state && __instance.isRepaired && mirroring == 0) SendRepaired(Kind.Building, SceneIds.Of(__instance));
    }

    [HarmonyPatch(typeof(DestructedPortal), nameof(DestructedPortal.Repair)), HarmonyPrefix]
    static void BeforePortal(DestructedPortal __instance, out bool __state) { __state = __instance.isRepaired; DoorSync.BeginOwnUse(); }

    [HarmonyPatch(typeof(DestructedPortal), nameof(DestructedPortal.Repair)), HarmonyFinalizer]
    static void AfterPortal(DestructedPortal __instance, bool __state)
    {
        DoorSync.EndOwnUse(3f);
        if (!__state && __instance.isRepaired && mirroring == 0) SendRepaired(Kind.Portal, SceneIds.Of(__instance));
    }

    static void SendRepaired(Kind kind, uint id)
    {
        if (!Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Repaired);
        w.Put((byte)kind);
        w.Put(id);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnRepaired(NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        uint id = r.GetUInt();
        RepairHere(kind, id, quiet: false);
    }

    static void RepairHere(Kind kind, uint id, bool quiet)
    {
        mirroring++;
        try
        {
            if (kind == Kind.Building)
            {
                var b = SceneIds.Find<RepairableBuilding>(id);
                if (!b || b.isRepaired) return;
                b.isRepaired = true;
                b.RemoveActionBinding();
                if (b.panelMain && b.panelMain.activeSelf) b.panelMain.AnimExit();
                if (!quiet && b.transitionVfx) b.transitionVfx.Play();
                b.UpdateVisuals();
                DoorSync.Mirror(() => b.onRepairCustomEvents.Invoke());
                NetLog.Info($"{b.name} was repaired by a teammate");
            }
            else
            {
                var p = SceneIds.Find<DestructedPortal>(id);
                if (!p || p.isRepaired) return;
                p.isRepaired = true;
                p.bindingDispose?.Dispose();
                if (p.panelMain && p.panelMain.activeSelf) p.panelMain.AnimExit();
                if (!quiet && p.transitionVfx) p.transitionVfx.Play();
                DoorSync.Mirror(() => p.onRepairEvent.Invoke());
                NetLog.Info($"{p.name} was repaired by a teammate");
            }
        }
        finally { mirroring--; }
    }

    // ---- arrival snapshot ----

    /// <summary>Owner: what's repaired and unlocked in this scene, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        var buildings = new List<uint>();
        foreach (var b in Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (b.isRepaired) buildings.Add(SceneIds.Of(b));
        var portals = new List<uint>();
        foreach (var p in Object.FindObjectsByType<DestructedPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p.isRepaired) portals.Add(SceneIds.Of(p));
        if (buildings.Count == 0 && portals.Count == 0 && unlocked.Count == 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.InteractSnapshot);
        w.Put(buildings.Count); foreach (var id in buildings) w.Put(id);
        w.Put(portals.Count); foreach (var id in portals) w.Put(id);
        w.Put(unlocked.Count); foreach (var id in unlocked) w.Put(id);
        Plugin.SendTo(playerId, w);
    }

    public static void OnSnapshot(NetDataReader r)
    {
        int n = r.GetInt();
        for (int i = 0; i < n; i++) RepairHere(Kind.Building, r.GetUInt(), quiet: true);
        n = r.GetInt();
        for (int i = 0; i < n; i++) RepairHere(Kind.Portal, r.GetUInt(), quiet: true);
        n = r.GetInt();
        for (int i = 0; i < n; i++) UnlockHere(SceneIds.Find<ItemRequester>(r.GetUInt()), quiet: true);
    }

    public static void Clear() => unlocked.Clear();
}

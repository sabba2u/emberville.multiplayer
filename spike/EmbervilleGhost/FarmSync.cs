using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Farm plots (SoilTile: one GameObject per plot, matched by its save id; the guest loads the host's estate so ids
/// match). Farming is a mode of the local player (FarmingManager), and each action ends in one SoilTile method on the
/// plot: Plow, Plant (rolls the growing time), Water, Fertilize, Harvest (replants or plows again).
///
/// Like doors, whoever farms does it on their copy with their own seeds / fertilizer and sends the plot's whole state
/// right after (the game's own save format, SavedSoilState); the others load it into their plot (SoilTile.OnLoad:
/// state, crop, progress, growing time, water / plow timers, visuals). Growth itself runs on every copy from the shared
/// clock (TimeSync), so plots only need a message when someone touches them. Harvest drops come from the harvester's
/// copy only (FarmingManager.HandleDrop) and travel as synced drops. Newcomers get every plot of the area.
/// </summary>
[HarmonyPatch]
internal static class FarmSync
{
    public static Plugin Plugin;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static int applying;
    static System.Collections.Generic.Dictionary<string, SoilTile> byId;

    public static void Clear()
    {
        byId = null;
        applying = 0;
    }

    static SoilTile Find(string id)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (byId == null || attempt == 1)
            {
                byId = new System.Collections.Generic.Dictionary<string, SoilTile>();
                foreach (var s in Object.FindObjectsByType<SoilTile>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!string.IsNullOrEmpty(s.saveID)) byId[s.saveID] = s;
            }
            if (byId.TryGetValue(id, out var found) && found) return found;
        }
        return null;
    }

    static NetDataWriter StateMessage(SoilTile s)
    {
        s.OnSave(null, null);   // the game writes the plot into the estate data...
        var saved = GameData.playerEstateData.savedSoilStates.Find(x => x.saveID == s.saveID);
        var w = new NetDataWriter();
        w.Put((byte)MsgType.SoilState);
        w.Put(s.saveID);
        w.Put(JsonUtility.ToJson(saved));   // ...and we send that entry
        return w;
    }

    static void Send(SoilTile s)
    {
        if (!Connected || applying > 0 || string.IsNullOrEmpty(s.saveID) || !Plugin.AnyoneElseHere) return;
        Plugin.SendToPlace(StateMessage(s), DeliveryMethod.ReliableOrdered);
    }

    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.Plow)), HarmonyPostfix]
    static void AfterPlow(SoilTile __instance) => Send(__instance);

    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.Plant)), HarmonyPostfix]
    static void AfterPlant(SoilTile __instance) => Send(__instance);

    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.Water)), HarmonyPostfix]
    static void AfterWater(SoilTile __instance) => Send(__instance);

    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.Fertilize)), HarmonyPostfix]
    static void AfterFertilize(SoilTile __instance) => Send(__instance);

    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.Harvest)), HarmonyPostfix]
    static void AfterHarvest(SoilTile __instance) => Send(__instance);

    public static void OnState(NetDataReader r)
    {
        var s = Find(r.GetString());
        var saved = JsonUtility.FromJson<SavedSoilState>(r.GetString());
        if (!s || saved == null) return;
        Apply(s, saved);
    }

    /// <summary>Load someone's plot state into ours, the way the game loads a save.</summary>
    static void Apply(SoilTile s, SavedSoilState saved)
    {
        var list = GameData.playerEstateData.savedSoilStates;
        int i = list.FindIndex(x => x.saveID == saved.saveID);
        if (i >= 0) list[i] = saved; else list.Add(saved);

        applying++;
        try
        {
            // OnLoad only reschedules the "plowed soil goes back to dirt" timer for plowed plots and never cancels the
            // old one: a plot planted since then would turn to dirt here when it fires.
            if (saved.state != SoilState.Plowed) TimeManager.RemoveScheduledEvent(s.resetSoilEvent);
            // OnLoad only ever adds: a plot that stopped growing (harvested -> plowed, dirt) must leave the growing list.
            if (saved.state != SoilState.Planted && s.manager) s.manager.RemoveGrowingSoil(s);
            if (saved.state == SoilState.Dirty) { s.SetDirty(); return; }
            s.canHarvest = saved.state == SoilState.Planted && saved.growingProgress >= 1f;
            s.OnLoad(null, null);
        }
        finally { applying--; }
    }

    /// <summary>Owner: every touched plot of the area, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        foreach (var s in Object.FindObjectsByType<SoilTile>(FindObjectsSortMode.None))
            if (!string.IsNullOrEmpty(s.saveID) && s.state != SoilState.Dirty) Plugin.SendTo(playerId, StateMessage(s));
    }
}

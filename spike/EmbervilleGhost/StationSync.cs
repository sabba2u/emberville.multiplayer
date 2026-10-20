using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Cooking pots, campfires, furnaces, anvils, sawmills... (CraftingStation: one class, stationType says which).
/// A station holds a queue of recipes (ingredients are taken when queued), cooks the first one in real time
/// (Update: producing.timer), and keeps what's finished (createdItems) until someone collects it.
///
/// The area owner's copy is the real one, like chests: only it cooks (puppets just count the bar down for show), so
/// food is made once. Queuing on a puppet takes the ingredients from that player as usual and asks the owner to queue
/// it (StationEnqueue); collecting on a puppet asks the owner (StationCollect), who hands the items over (StationGive),
/// so two players can't both take the same food. After any change the owner sends the whole station (StationState,
/// the game's own save format), and newcomers get every busy station. The XP for a finished recipe goes to whoever
/// queued it (StationXp). Stations match by their save id (estate placeables share it: the guest loads the host's
/// estate).
/// Not yet: ingredients a puppet takes from chests shared with stations (ChestShare.CraftingStations) aren't synced.
/// </summary>
[HarmonyPatch]
internal static class StationSync
{
    public static Plugin Plugin;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static bool Puppet => EnemySync.PuppetMode;

    static int applying;
    static readonly Dictionary<CraftingStation, List<int>> queuedBy = new Dictionary<CraftingStation, List<int>>();   // owner: who queued each recipe, in order
    static readonly Dictionary<CraftingStation, int> stamps = new Dictionary<CraftingStation, int>();
    static Dictionary<string, CraftingStation> byKey;
    static int finisher = -1;

    [Serializable] class Items { public List<SavedItem> items = new List<SavedItem>(); }

    public static void Clear()
    {
        queuedBy.Clear(); stamps.Clear();
        byKey = null;
        applying = 0; finisher = -1;
    }

    static string KeyOf(CraftingStation s) => !string.IsNullOrEmpty(s.saveID) ? s.saveID : "p" + SceneIds.Of(s);

    static CraftingStation Find(string key)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (byKey == null || attempt == 1)
            {
                byKey = new Dictionary<string, CraftingStation>();
                foreach (var s in UnityEngine.Object.FindObjectsByType<CraftingStation>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    byKey[KeyOf(s)] = s;
            }
            if (byKey.TryGetValue(key, out var found) && found) return found;
        }
        return null;
    }

    static List<int> QueuedBy(CraftingStation s)
    {
        if (!queuedBy.TryGetValue(s, out var q)) queuedBy[s] = q = new List<int>();
        return q;
    }

    // The station's queue is a Queue<EnqueuedRecipe> from the game's mscorlib; our net472 reference has Queue<> in
    // System.dll, so the compiler can't touch it: reach it as an object.
    static readonly FieldInfo demandField = AccessTools.Field(typeof(CraftingStation), "demand");
    static object DemandOf(CraftingStation s) => demandField.GetValue(s);
    static int DemandCount(CraftingStation s) => ((ICollection)DemandOf(s)).Count;
    static IEnumerable<EnqueuedRecipe> Demand(CraftingStation s) { foreach (var o in (IEnumerable)DemandOf(s)) yield return (EnqueuedRecipe)o; }
    static void DemandClear(CraftingStation s) { var q = DemandOf(s); q.GetType().GetMethod("Clear").Invoke(q, null); }
    static void DemandAdd(CraftingStation s, EnqueuedRecipe e) { var q = DemandOf(s); q.GetType().GetMethod("Enqueue").Invoke(q, new object[] { e }); }

    // ---- the station as data (the game's save format) ----

    static SavedCraftingStation Snapshot(CraftingStation s)
    {
        var data = new SavedCraftingStation { saveID = KeyOf(s), stationType = s.StationType };
        foreach (var e in Demand(s)) data.enqueuedRecipes.Add(e.recipe.RecipeID);
        foreach (var item in s.CreatedItems) data.createdItems.Add(item.ToSavedItem());
        data.producingTimer = s.Producing?.timer ?? 0f;
        return data;
    }

    static NetDataWriter StateMessage(CraftingStation s)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StationState);
        w.Put(KeyOf(s));
        w.Put(JsonUtility.ToJson(Snapshot(s)));
        return w;
    }

    /// <summary>Owner: tell the area how this station is now.</summary>
    static void Broadcast(CraftingStation s)
    {
        if (Connected && !Puppet && Plugin.AnyoneElseHere) Plugin.SendToPlace(StateMessage(s), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Owner: every busy station here, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        foreach (var s in UnityEngine.Object.FindObjectsByType<CraftingStation>(FindObjectsSortMode.None))
            if (DemandCount(s) > 0 || s.CreatedItems.Count > 0) Plugin.SendTo(playerId, StateMessage(s));
    }

    /// <summary>Puppet: become the owner's station (queue, progress, finished food).</summary>
    public static async void OnState(NetDataReader r)
    {
        var s = Find(r.GetString());
        var data = JsonUtility.FromJson<SavedCraftingStation>(r.GetString());
        if (!s || data == null || !Puppet) return;
        stamps.TryGetValue(s, out int stamp);
        stamps[s] = ++stamp;
        var items = await data.createdItems.ToInventoryItems();   // item assets load async
        if (!s || stamps[s] != stamp) return;                     // a newer state came meanwhile

        DemandClear(s);
        foreach (string id in data.enqueuedRecipes)
        {
            var recipe = s.availableRecipes.Find(x => x.RecipeID == id);
            if (recipe) DemandAdd(s, new EnqueuedRecipe { recipe = recipe, timer = recipe.CraftingTime });
        }
        s.createdItems = items;
        s.UpdateVisuals();
        if (s.producing != null) s.producing.timer = data.producingTimer;
        RefreshUi(s);
    }

    static void RefreshUi(CraftingStation s)
    {
        if (!Singleton<CraftingManager>.HasMain) return;
        var cm = Singleton<CraftingManager>.main;
        if (cm.activeStation == s) cm.UpdateQueue();
    }

    // ---- cooking: only the owner's copy ----

    /// <summary>Puppet: count the bar down for show; finishing is the owner's (its StationState brings the food).</summary>
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Update)), HarmonyPrefix]
    static bool BeforeUpdate(CraftingStation __instance)
    {
        if (!Connected || !Puppet) return true;
        var p = __instance.producing;
        if ((bool)__instance.isOn && p != null && p.recipe)
        {
            p.timer = Mathf.Max(0f, p.timer - Time.deltaTime);
            float left = p.timer / p.recipe.CraftingTime;
            __instance.iconMask.localPosition = new Vector3(0f, left * -2f, 0f);
            __instance.OnProducing?.Invoke(Mathf.CeilToInt(p.timer), left);
        }
        return false;
    }

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Finish)), HarmonyPrefix]
    static void BeforeFinish(CraftingStation __instance)
    {
        finisher = -1;
        if (!queuedBy.TryGetValue(__instance, out var q)) return;
        // Recipes queued before we started tracking (before anyone joined, before a scene change) are at the front
        // and untracked: those are the owner's. (Update has already taken the finishing one off the queue: +1.)
        int untracked = DemandCount(__instance) + 1 - q.Count;
        if (untracked > 0) { finisher = Plugin.MyId; return; }
        if (q.Count > 0) { finisher = q[0]; q.RemoveAt(0); }
    }

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Finish)), HarmonyPostfix]
    static void AfterFinish(CraftingStation __instance)
    {
        finisher = -1;
        Broadcast(__instance);
    }

    /// <summary>The cook's XP goes to whoever queued it.</summary>
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GiveXP)), HarmonyPrefix]
    static bool BeforeXp(CraftingStation __instance, CraftingRecipe recipe)
    {
        if (!Connected || finisher < 0 || finisher == Plugin.MyId) return true;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StationXp);
        w.Put(recipe.RecipeID);
        w.Put((int)recipe.RequiredCategory);
        w.Put(recipe.GetEarnedXP);
        Plugin.SendTo(finisher, w);
        return false;
    }

    public static void OnXp(NetDataReader r)
    {
        r.GetString();
        var job = (LifeSkillJob)r.GetInt();
        int xp = r.GetInt();
        GameData.playerData?.AddXP(job, xp);
    }

    // ---- queuing ----

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.TryToEnqueue)), HarmonyPostfix]
    static void AfterEnqueue(CraftingStation __instance, CraftingRecipe recipe, bool __result)
    {
        if (!__result || applying > 0 || !Connected) return;
        if (!Puppet)
        {
            QueuedBy(__instance).Add(Plugin.MyId);
            Broadcast(__instance);
            return;
        }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StationEnqueue);
        w.Put(KeyOf(__instance));
        w.Put(recipe.RecipeID);
        w.Put(recipe.CraftingTime);   // ours: our crafting speed
        Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Owner: a puppet queued a recipe (they already paid the ingredients).</summary>
    public static void OnEnqueue(NetDataReader r, int from)
    {
        var s = Find(r.GetString());
        string id = r.GetString();
        float time = r.GetFloat();
        if (!s) return;
        var recipe = s.availableRecipes.Find(x => x.RecipeID == id);
        if (!recipe) return;
        if (DemandCount(s) >= s.MaxStack) NetLog.Warn($"{Plugin.NameOf(from)} queued on a full {s.StationType}");
        DemandAdd(s, new EnqueuedRecipe { recipe = recipe, timer = time });
        QueuedBy(s).Add(from);
        s.UpdateVisuals();
        RefreshUi(s);
        Broadcast(s);
    }

    // ---- collecting ----

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.ForceCollect)), HarmonyPrefix]
    static bool BeforeCollectAll(CraftingStation __instance) => !AskToCollect(__instance, -1);

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.ForceCollect)), HarmonyPostfix]
    static void AfterCollectAll(CraftingStation __instance) { if (applying == 0) Broadcast(__instance); }

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CollectItem)), HarmonyPrefix]
    static bool BeforeCollectOne(CraftingStation __instance, InventoryItem item, bool removeItem)
    {
        if (!removeItem) return true;   // part of ForceCollect (already decided)
        return !AskToCollect(__instance, __instance.CreatedItems.IndexOf(item));
    }

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CollectItem)), HarmonyPostfix]
    static void AfterCollectOne(CraftingStation __instance, bool removeItem) { if (removeItem && applying == 0) Broadcast(__instance); }

    /// <summary>Puppet: ask the owner for the food instead of taking our copy of it. True = asked (skip the game's).</summary>
    static bool AskToCollect(CraftingStation s, int index)
    {
        if (!Connected || !Puppet || applying > 0) return false;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StationCollect);
        w.Put(KeyOf(s));
        w.Put(index);
        Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        return true;
    }

    /// <summary>Owner: a puppet takes finished items; they're theirs, gone from here, and everyone hears.</summary>
    public static void OnCollect(NetDataReader r, int from)
    {
        var s = Find(r.GetString());
        int index = r.GetInt();
        if (!s) return;
        var taken = new List<InventoryItem>();
        if (index < 0) taken.AddRange(s.CreatedItems);
        else if (index < s.CreatedItems.Count) taken.Add(s.CreatedItems[index]);
        if (taken.Count == 0) { Broadcast(s); return; }
        foreach (var item in taken) s.createdItems.Remove(item);
        s.UpdateVisuals();
        s.OnCollect?.Invoke();
        RefreshUi(s);

        var give = new Items();
        foreach (var item in taken) give.items.Add(item.ToSavedItem());
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StationGive);
        w.Put(KeyOf(s));
        w.Put(JsonUtility.ToJson(give));
        Plugin.SendTo(from, w);
        Broadcast(s);
    }

    /// <summary>Puppet: the owner handed us these; into our bag (or at our feet if it's full, like the game).</summary>
    public static async void OnGive(NetDataReader r)
    {
        var s = Find(r.GetString());
        var give = JsonUtility.FromJson<Items>(r.GetString());
        if (give == null || give.items.Count == 0) return;
        var items = await give.items.ToInventoryItems();
        var me = LocalPlayer.Get();
        foreach (var item in items)
        {
            int left = Singleton<Inventory>.main.AddItem(item.itemObject);
            if (left > 0) item.itemObject.DropToWorld(s ? s.transform.position : me ? me.transform.position : Vector3.zero, left, 1.2f);
        }
    }
}

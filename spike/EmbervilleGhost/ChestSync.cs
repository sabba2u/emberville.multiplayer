using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// Chests: the area owner's contents are the real ones, and one player at a time has a chest open (a lock), so
/// nothing can be duplicated however items get moved (drag, swap, split, stack, collect all, send all, sort, dispose...).
///
/// How the game does chests: ChestManager.allChests[chestID] holds each chest's InventoryItems (rolled randomly per
/// copy; chests without a designer-set id even get a random GUID per copy, so we match chests by scene path instead).
/// Opening hands that very list to the inventory UI; closing writes the chest section back into it. So:
///  - puppet opens: ChestOpen(id) to the owner -> owner locks it to that player and replies ChestContents -> the
///    puppet replaces its own roll with the owner's items and opens for real (the game's own UI does the rest);
///  - puppet closes: ChestClose(id, final contents) -> owner replaces its contents and unlocks;
///  - after any close the owner sends ChestState to everyone in the area (and to newcomers), so whoever runs the area
///    next has the real contents of every chest anyone has touched;
///  - the owner opening one a puppet has open: "in use". A player leaving unlocks their chests.
/// Not yet: chests built by players (placeables differ per copy), crafting that pulls from chests (ChestShare).
/// </summary>
[HarmonyPatch]
internal static class ChestSync
{
    public static Plugin Plugin;

    static readonly Dictionary<uint, int> lockedBy = new Dictionary<uint, int>();   // owner: chest -> player id with it open
    static readonly HashSet<uint> touched = new HashSet<uint>();                    // chests whose contents everyone shares (this scene)
    static string touchedScene = "";
    static uint openSynced;      // puppet: the chest the owner gave us (0 = none)
    static bool granted;         // puppet: opening the owner's chest for real
    static uint pending;         // puppet: asked, waiting for an answer

    static bool Puppet => EnemySync.PuppetMode;

    public static void Clear()
    {
        lockedBy.Clear(); touched.Clear();
        touchedScene = "";
        openSynced = pending = 0;
        granted = false;
    }

    /// <summary>Owner: a player left; whatever they had open is free again (their changes are lost, ours stay).</summary>
    public static void Unlock(int playerId)
    {
        var mine = new List<uint>();
        foreach (var pair in lockedBy) if (pair.Value == playerId) mine.Add(pair.Key);
        foreach (var id in mine) lockedBy.Remove(id);
    }

    static void Touch(uint id)
    {
        if (LocalPlayer.Scene != touchedScene) { touched.Clear(); touchedScene = LocalPlayer.Scene; }
        touched.Add(id);
    }

    // ---- items <-> bytes ----

    static void PutItems(NetDataWriter w, List<InventoryItem> items)
    {
        int n = items?.Count ?? 0;
        w.Put(n);
        for (int i = 0; i < n; i++)
        {
            var it = items[i];
            w.Put(it.itemObject.data.addressKey ?? "");
            w.Put(it.itemObject.amount);
            w.Put(JsonUtility.ToJson(it.itemObject.randomValues));
            w.Put(it.slotIndex);
        }
    }

    /// <summary>Read items and build them (Addressables, like the save loader); calls back on the main thread when all are ready.</summary>
    static void GetItems(NetDataReader r, System.Action<List<InventoryItem>> done)
    {
        int n = r.GetInt();
        var raw = new List<(string key, int amount, string values, int slot)>(n);
        for (int i = 0; i < n; i++) raw.Add((r.GetString(), r.GetInt(), r.GetString(), r.GetInt()));

        var result = new InventoryItem[n];
        int left = n;
        if (left == 0) { done(new List<InventoryItem>()); return; }
        for (int i = 0; i < n; i++)
        {
            int index = i;
            var (key, amount, values, slot) = raw[i];
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result)
                {
                    var rv = JsonUtility.FromJson<ItemRandomValues>(values) ?? new ItemRandomValues();
                    rv.SetData(h.Result);
                    result[index] = new InventoryItem(new ItemObject(h.Result, amount) { randomValues = rv }, slot, InventorySection.Chest);
                }
                else NetLog.Warn($"Unknown item '{key}' in a chest");
                if (--left == 0)
                {
                    var list = new List<InventoryItem>(n);
                    foreach (var it in result) if (it != null) list.Add(it);
                    done(list);
                }
            };
        }
    }

    /// <summary>Make this chest hold exactly these items (same list object: the UI may be holding it).</summary>
    static void SetContents(Chest chest, List<InventoryItem> items)
    {
        var manager = Singleton<ChestManager>.main;
        var list = manager.GetChestItems(chest.ID);
        if (list == null)
        {
            if (string.IsNullOrEmpty(chest.ID)) chest.chestID = System.Guid.NewGuid().ToString();
            manager.AddChest(chest.ID, items, chest.saveChest, chest.shareContent);
            return;
        }
        list.Clear();
        list.AddRange(items);
    }

    static NetDataWriter StateMessage(uint id, Chest chest)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ChestState); w.Put(id);
        PutItems(w, Singleton<ChestManager>.main.GetChestItems(chest.ID));
        return w;
    }

    // ---- owner ----

    public static void OnOpenRequest(NetDataReader r, int fromId)
    {
        uint id = r.GetUInt();
        var chest = SceneIds.Find<Chest>(id);
        if (!chest) return;
        var w = new NetDataWriter();
        if (lockedBy.TryGetValue(id, out int who) && who != fromId)
        {
            w.Put((byte)MsgType.ChestDenied); w.Put(id); w.Put(who);
            Plugin.SendTo(fromId, w);
            return;
        }
        lockedBy[id] = fromId;
        w.Put((byte)MsgType.ChestContents); w.Put(id);
        PutItems(w, Singleton<ChestManager>.main.GetChestItems(chest.ID));
        Plugin.SendTo(fromId, w);
    }

    public static void OnClose(NetDataReader r, int fromId)
    {
        uint id = r.GetUInt();
        var chest = SceneIds.Find<Chest>(id);
        if (!lockedBy.TryGetValue(id, out int who) || who != fromId || !chest) return;   // not theirs to write
        GetItems(r, items =>
        {
            if (!chest) return;
            SetContents(chest, items);
            if (lockedBy.TryGetValue(id, out int still) && still == fromId) lockedBy.Remove(id);
            Touch(id);
            Plugin.SendToPlace(StateMessage(id, chest), DeliveryMethod.ReliableOrdered);
        });
    }

    /// <summary>Owner: a player just arrived; give them the chests anyone has touched here.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (touchedScene != LocalPlayer.Scene) return;
        foreach (uint id in touched)
        {
            var chest = SceneIds.Find<Chest>(id);
            if (chest) Plugin.SendTo(playerId, StateMessage(id, chest));
        }
    }

    // ---- puppet ----

    public static void OnContents(NetDataReader r)
    {
        uint id = r.GetUInt();
        GetItems(r, items =>
        {
            pending = 0;
            var chest = SceneIds.Find<Chest>(id);
            if (!chest || !Puppet) { SendClose(id, items); return; }   // gone / we run the area now: give it straight back
            SetContents(chest, items);
            openSynced = id;
            granted = true;
            try { chest.OpenChest(); }
            finally { granted = false; }
        });
    }

    /// <summary>Puppet: a chest's real contents (after someone closed it, or on arrival).</summary>
    public static void OnState(NetDataReader r)
    {
        uint id = r.GetUInt();
        GetItems(r, items =>
        {
            var chest = SceneIds.Find<Chest>(id);
            if (!chest || openSynced == id) return;
            SetContents(chest, items);
            Touch(id);
        });
    }

    public static void OnDenied(NetDataReader r)
    {
        r.GetUInt();
        int who = r.GetInt();
        pending = 0;
        NetLog.Warn($"{Plugin.NameOf(who)} {(who == Plugin.MyId ? "are" : "is")} using that chest");
    }

    static void SendClose(uint id, List<InventoryItem> items)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ChestClose); w.Put(id);
        PutItems(w, items);
        Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
    }

    // ---- patches ----

    /// <summary>Puppet: opening is a request. Owner: a chest someone else has open is "in use"; otherwise we take the lock.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.OpenChest)), HarmonyPrefix]
    static bool BeforeOpen(Chest __instance)
    {
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0) return true;
        uint id = SceneIds.Of(__instance);
        if (!Puppet)
        {
            if (lockedBy.TryGetValue(id, out int who) && who != Plugin.MyId)
            {
                NetLog.Warn($"{Plugin.NameOf(who)} is using that chest");
                return false;
            }
            lockedBy[id] = Plugin.MyId;
            return true;
        }
        if (granted) return true;
        if (pending == id) return false;
        pending = id;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ChestOpen); w.Put(id);
        Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        return false;
    }

    /// <summary>After closing, the game has written the chest section back into its list: that's the final contents.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.CloseChest)), HarmonyPostfix]
    static void AfterClose(Chest __instance)
    {
        if (Plugin == null || !Plugin.Connected) return;
        uint id = SceneIds.Of(__instance);
        if (openSynced == id)
        {
            openSynced = 0;
            SendClose(id, Singleton<ChestManager>.main.GetChestItems(__instance.ID));
            return;
        }
        if (Puppet || !lockedBy.TryGetValue(id, out int who) || who != Plugin.MyId) return;
        lockedBy.Remove(id);
        Touch(id);
        if (Plugin.AnyoneElseHere) Plugin.SendToPlace(StateMessage(id, __instance), DeliveryMethod.ReliableOrdered);
    }
}

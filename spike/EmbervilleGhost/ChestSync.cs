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
///
/// Ids: a chest with a save id (built ones: a GUID in the estate save, the same on every copy since the guest loads
/// the host's estate) goes by that; placed ones by their scene path.
///
/// Shared chests (ChestShare: chests whose contents crafting stations, farming / building, repairs or shops can use
/// without opening them). The game takes from them inside Inventory.ConsumeItemTypeOf / ConsumeItem by editing the
/// chest's item list directly. We look at every shared chest before and after: whatever went missing is what was
/// taken. A puppet tells the owner (ChestTake: chest id, item key, amount); the owner takes the same from the real
/// chest. After any change the owner sends the shared chest to everyone here (ChestShared), so everyone's crafting
/// menus count the right amounts, and newcomers get them all. Taken while someone has the chest open: the owner takes
/// it again after their close writes the chest back (else the close would undo it).
/// Two players taking the last items at the same moment: the later one pays from their own bag (ChestShort).
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

    // ---- chest ids ----

    static Dictionary<uint, Chest> byId;

    static uint IdOf(Chest c)
    {
        if (string.IsNullOrEmpty(c.saveID)) return SceneIds.Of(c) & 0x7FFFFFFFu;
        uint h = 2166136261;
        foreach (char ch in c.saveID) { h ^= ch; h *= 16777619; }
        return h | 0x80000000u;
    }

    static Chest FindChest(uint id)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (byId == null || attempt == 1)
            {
                byId = new Dictionary<uint, Chest>();
                foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None)) byId[IdOf(c)] = c;
            }
            if (byId.TryGetValue(id, out var found) && found) return found;
        }
        return null;
    }

    public static void Clear()
    {
        lockedBy.Clear(); touched.Clear(); takenWhileOpen.Clear();
        byId = null;
        byChestId = null;
        touchedScene = "";
        openSynced = pending = quickPending = 0;
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
        var chest = FindChest(id);
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
        var chest = FindChest(id);
        if (!lockedBy.TryGetValue(id, out int who) || who != fromId || !chest) return;   // not theirs to write
        GetItems(r, items =>
        {
            if (!chest) return;
            SetContents(chest, items);
            RetakeAfterClose(chest);
            if (lockedBy.TryGetValue(id, out int still) && still == fromId) lockedBy.Remove(id);
            Touch(id);
            Plugin.SendToPlace(StateMessage(id, chest), DeliveryMethod.ReliableOrdered);
            if (chest.shareContent != ChestShare.None && !string.IsNullOrEmpty(chest.ID)) BroadcastShared(chest.ID);   // its crafting-menu counts too
        });
    }

    /// <summary>Owner: a player just arrived; give them the chests anyone has touched here.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (Singleton<ChestManager>.HasMain)
            foreach (var pair in Singleton<ChestManager>.main.allChests)
                if (pair.Value.shareContent != ChestShare.None) Plugin.SendTo(playerId, SharedMessage(pair.Key, pair.Value.mainItems));
        if (touchedScene != LocalPlayer.Scene) return;
        foreach (uint id in touched)
        {
            var chest = FindChest(id);
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
            var chest = FindChest(id);
            if (!chest || !Puppet) { SendClose(id, items); return; }   // gone / we run the area now: give it straight back
            if (quickPending == id)
            {
                // X on a chest: their contents, our matching items in, straight back (never opened on screen).
                quickPending = 0;
                SetContents(chest, items);
                StackInto(chest);
                SendClose(id, Singleton<ChestManager>.main.GetChestItems(chest.ID));
                return;
            }
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
            var chest = FindChest(id);
            if (!chest || openSynced == id) return;
            SetContents(chest, items);
            Touch(id);
        });
    }

    public static void OnDenied(NetDataReader r)
    {
        if (quickPending == r.PeekUInt()) quickPending = 0;
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
        uint id = IdOf(__instance);
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
        uint id = IdOf(__instance);
        if (openSynced == id)
        {
            openSynced = 0;
            SendClose(id, Singleton<ChestManager>.main.GetChestItems(__instance.ID));
            return;
        }
        if (Puppet || !lockedBy.TryGetValue(id, out int who) || who != Plugin.MyId) return;
        lockedBy.Remove(id);
        RetakeAfterClose(__instance);
        Touch(id);
        if (Plugin.AnyoneElseHere) Plugin.SendToPlace(StateMessage(id, __instance), DeliveryMethod.ReliableOrdered);
    }

    // ---- shared chests (taken from without opening: crafting, farming, building, repairs, shops) ----

    static readonly Dictionary<string, List<(string key, int amount)>> takenWhileOpen = new Dictionary<string, List<(string, int)>>();

    class Seen { public string Chest, Key; public int Amount; }

    /// <summary>
    /// The name a shared chest goes by on the wire. ChestManager keys chests by chestID, and a chest placed in the
    /// scene without a save id gets a random GUID on every copy: those go by their scene path instead.
    /// </summary>
    static string NetKey(string chestId)
    {
        var chest = ChestById(chestId);
        return chest && string.IsNullOrEmpty(chest.saveID) ? "p" + IdOf(chest) : chestId;
    }

    /// <summary>Our ChestManager id for a chest named on the wire (null = we don't have it).</summary>
    static string LocalId(string netKey)
    {
        if (netKey.Length > 1 && netKey[0] == 'p' && uint.TryParse(netKey.Substring(1), out uint id))
            return FindChest(id) is Chest c ? c.ID : null;
        return netKey;
    }

    static Dictionary<string, Chest> byChestId;

    static Chest ChestById(string chestId)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (byChestId == null || attempt == 1)
            {
                byChestId = new Dictionary<string, Chest>();
                foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!string.IsNullOrEmpty(c.ID)) byChestId[c.ID] = c;
            }
            if (byChestId.TryGetValue(chestId, out var found) && found) return found;
        }
        return null;
    }

    static bool Tracking(ChestShare share) =>
        share != ChestShare.None && Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && Singleton<ChestManager>.HasMain;

    /// <summary>Every item of every chest shared for this use, and how many there are.</summary>
    static Dictionary<InventoryItem, Seen> Look(ChestShare share)
    {
        var seen = new Dictionary<InventoryItem, Seen>();
        foreach (var pair in Singleton<ChestManager>.main.allChests)
        {
            if ((pair.Value.shareContent & share) == 0) continue;
            foreach (var it in pair.Value.mainItems)
                if (it?.itemObject?.data) seen[it] = new Seen { Chest = pair.Key, Key = it.itemObject.data.addressKey, Amount = it.itemObject.amount };
        }
        return seen;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItemTypeOf)), HarmonyPrefix]
    static void BeforeConsumeType(ChestShare sharingContent, out Dictionary<InventoryItem, Seen> __state) =>
        __state = Tracking(sharingContent) ? Look(sharingContent) : null;

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItemTypeOf)), HarmonyPostfix]
    static void AfterConsumeType(Dictionary<InventoryItem, Seen> __state) => Taken(__state);

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItem)), HarmonyPrefix]
    static void BeforeConsume(ChestShare sharingContent, out Dictionary<InventoryItem, Seen> __state) =>
        __state = Tracking(sharingContent) ? Look(sharingContent) : null;

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItem)), HarmonyPostfix]
    static void AfterConsume(Dictionary<InventoryItem, Seen> __state) => Taken(__state);

    /// <summary>What went missing from shared chests just now: tell the owner (puppet) or everyone (owner).</summary>
    static void Taken(Dictionary<InventoryItem, Seen> before)
    {
        if (before == null || before.Count == 0) return;
        var chests = Singleton<ChestManager>.main.allChests;
        var taken = new Dictionary<string, List<(string key, int amount)>>();
        foreach (var pair in before)
        {
            var was = pair.Value;
            bool still = chests.TryGetValue(was.Chest, out var col) && col.mainItems.Contains(pair.Key);
            int gone = was.Amount - (still ? pair.Key.itemObject.amount : 0);
            if (gone <= 0) continue;
            if (!taken.TryGetValue(was.Chest, out var list)) taken[was.Chest] = list = new List<(string, int)>();
            list.Add((was.Key, gone));
        }
        foreach (var pair in taken)
        {
            if (Puppet)
            {
                var w = new NetDataWriter();
                w.Put((byte)MsgType.ChestTake);
                w.Put(NetKey(pair.Key));
                w.Put(pair.Value.Count);
                foreach (var (key, amount) in pair.Value) { w.Put(key); w.Put(amount); }
                Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                NoteIfOpen(pair.Key, pair.Value);
                BroadcastShared(pair.Key);
            }
        }
    }

    /// <summary>Owner: a puppet used items from a shared chest; take the same from the real one.</summary>
    public static void OnTake(NetDataReader r, int fromId)
    {
        string netKey = r.GetString();
        string chestId = LocalId(netKey) ?? netKey;
        int n = r.GetInt();
        var list = new List<(string key, int amount)>(n);
        for (int i = 0; i < n; i++) list.Add((r.GetString(), r.GetInt()));
        if (!Singleton<ChestManager>.HasMain || !Singleton<ChestManager>.main.allChests.TryGetValue(chestId, out var col))
        {
            NetLog.Warn($"{Plugin.NameOf(fromId)} used items from a chest we don't have ({chestId})");
            return;
        }
        foreach (var (key, amount) in list)
        {
            int missing = Take(col.mainItems, key, amount);
            if (missing <= 0) continue;
            // Someone took the last of these a moment earlier: the later one pays from their own bag (no free items).
            NetLog.Warn($"{Plugin.NameOf(fromId)} used {missing} x {key} from a shared chest that were already gone");
            var w = new NetDataWriter();
            w.Put((byte)MsgType.ChestShort);
            w.Put(key);
            w.Put(missing);
            Plugin.SendTo(fromId, w);
        }
        NoteIfOpen(chestId, list);
        BroadcastShared(chestId);
    }

    /// <summary>
    /// Us: what we used from a shared chest was already gone (another player took it at the same moment). Pay it from
    /// our own bag if we can, else it's on the house (logged).
    /// </summary>
    public static void OnShort(NetDataReader r)
    {
        string key = r.GetString();
        int missing = r.GetInt();
        if (!Singleton<Inventory>.HasMain) return;
        var inv = Singleton<Inventory>.main;
        var have = inv.allItems.Find(x => x?.itemObject?.data != null && x.itemObject.data.addressKey == key);
        int left = have != null ? inv.ConsumeItemTypeOf(have.itemObject.data, missing) : missing;
        string what = have != null ? have.itemObject.data.name : key;
        if (left < missing) Chat.System($"Someone used the same {what} from the shared chest first: {missing - left} taken from your bag instead.");
        if (left > 0) NetLog.Warn($"{left} x {what} from a shared chest were already gone and you have none: free this time");
    }

    /// <summary>Take this many of an item from a chest's list, like Inventory.Consume. Returns how many weren't there.</summary>
    static int Take(List<InventoryItem> items, string key, int amount)
    {
        for (int i = 0; i < items.Count && amount > 0; i++)
        {
            var it = items[i];
            if (it?.itemObject?.data == null || it.itemObject.data.addressKey != key) continue;
            if (it.itemObject.amount <= amount) { amount -= it.itemObject.amount; items.RemoveAt(i--); }
            else { it.itemObject.amount -= amount; amount = 0; }
        }
        return amount;
    }

    /// <summary>Owner: taken from a chest someone has open; their close will write the chest back, so take it again then.</summary>
    static void NoteIfOpen(string chestId, List<(string key, int amount)> list)
    {
        foreach (var pair in lockedBy)
        {
            var chest = FindChest(pair.Key);
            if (!chest || chest.ID != chestId) continue;
            if (!takenWhileOpen.TryGetValue(chestId, out var later)) takenWhileOpen[chestId] = later = new List<(string, int)>();
            later.AddRange(list);
            return;
        }
    }

    static void RetakeAfterClose(Chest chest)
    {
        if (string.IsNullOrEmpty(chest.ID) || !takenWhileOpen.TryGetValue(chest.ID, out var list)) return;
        takenWhileOpen.Remove(chest.ID);
        var items = Singleton<ChestManager>.main.GetChestItems(chest.ID);
        if (items != null) foreach (var (key, amount) in list) Take(items, key, amount);
        BroadcastShared(chest.ID);
    }

    static NetDataWriter SharedMessage(string chestId, List<InventoryItem> items)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ChestShared);
        w.Put(NetKey(chestId));
        PutItems(w, items);
        return w;
    }

    static void BroadcastShared(string chestId)
    {
        if (Puppet || !Plugin.AnyoneElseHere || !Singleton<ChestManager>.main.allChests.TryGetValue(chestId, out var col)) return;
        Plugin.SendToPlace(SharedMessage(chestId, col.mainItems), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Puppet: a shared chest as it really is now (so our crafting menus count the right amounts).</summary>
    public static void OnShared(NetDataReader r)
    {
        string chestId = LocalId(r.GetString());
        GetItems(r, items =>
        {
            if (chestId == null || !Singleton<ChestManager>.HasMain || !Singleton<ChestManager>.main.allChests.TryGetValue(chestId, out var col)) return;
            if (openSynced != 0 && FindChest(openSynced) is Chest open && open.ID == chestId) return;   // ours to write back
            col.mainItems.Clear();
            col.mainItems.AddRange(items);
        });
    }

    // ---- X at a chest: the Better Chests mod, in a session ----
    //
    // Better Chests (BetterChests.QuickStack, a separate mod) does "X at a chest: put in what it already holds" on this
    // copy's contents. In a session that has to be an open + close as far as everyone else is concerned, so when it's
    // installed we patch its Run: a puppet asks the owner for the real contents and lock first (ChestOpen ->
    // ChestContents, then its StackInto, then ChestClose); the owner checks nobody else has the chest open, lets it run,
    // and sends the new contents to everyone.

    static uint quickPending;   // puppet: asked the owner for this chest to quick-stack into
    static System.Reflection.MethodInfo betterStackInto;

    /// <summary>From Plugin.Awake: hook Better Chests if it's installed (it loads first: soft dependency).</summary>
    public static void HookBetterChests(Harmony harmony)
    {
        var type = AccessTools.TypeByName("BetterChests.QuickStack");
        var run = type != null ? AccessTools.Method(type, "Run", new[] { typeof(Chest) }) : null;
        betterStackInto = type != null ? AccessTools.Method(type, "StackInto", new[] { typeof(Chest) }) : null;
        if (run == null || betterStackInto == null) return;
        harmony.Patch(run, prefix: new HarmonyMethod(typeof(ChestSync), nameof(BeforeQuickStack)),
                           postfix: new HarmonyMethod(typeof(ChestSync), nameof(AfterQuickStack)));
        NetLog.Info("Better Chests found: X at a chest works in multiplayer");
    }

    static bool BeforeQuickStack(Chest chest, ref int __result, out bool __state)
    {
        __state = false;
        bool connected = Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
        if (!connected || !chest) return true;
        uint id = IdOf(chest);
        if (Puppet)
        {
            __result = 0;
            if (quickPending != 0 || pending != 0) return false;
            quickPending = id;
            var w = new NetDataWriter();
            w.Put((byte)MsgType.ChestOpen); w.Put(id);
            Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
            return false;
        }
        if (lockedBy.TryGetValue(id, out int who) && who != Plugin.MyId)
        {
            NetLog.Warn($"{Plugin.NameOf(who)} is using that chest");
            __result = 0;
            return false;
        }
        __state = true;
        return true;
    }

    static void AfterQuickStack(Chest chest, int __result, bool __state)
    {
        if (!__state || __result == 0) return;
        uint id = IdOf(chest);
        Touch(id);
        if (Plugin.AnyoneElseHere) Plugin.SendToPlace(StateMessage(id, chest), DeliveryMethod.ReliableOrdered);
        if (chest.shareContent != ChestShare.None && !string.IsNullOrEmpty(chest.ID)) BroadcastShared(chest.ID);
    }

    /// <summary>Better Chests' deposit on this copy's contents (for a puppet, once the owner's contents are here).</summary>
    static void StackInto(Chest chest)
    {
        try { betterStackInto?.Invoke(null, new object[] { chest }); }
        catch (System.Exception e) { NetLog.Warn($"Better Chests: {e.InnerException?.Message ?? e.Message}"); }
    }
}

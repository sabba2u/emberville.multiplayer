using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// Items lying in the world (ItemDropObject), run by the area's owner and dupe-proof.
///
/// Every world drop goes through ItemDropHandler.DropToWorld (creature deaths, loot tables, breakables, inventory
/// overflow, dropping from the inventory), which ends in MoveToPosition(drop, from, to). The owner gives each drop an
/// id (unique per player: owner id in the top bits) and sends it (item address key + amount + random values, i.e.
/// what a save file stores) to everyone in the area. Everyone keeps the drops of the area they're in, so whoever runs
/// it next can serve pickups and tell newcomers.
///
/// Puppets (someone else runs our area):
///  - drops from synced things (an ItemDropper on/under an Enemy or DamageableObject) don't happen locally;
///  - any other drop coming from us (inventory overflow, dropping an item) becomes a request; the owner drops it;
///  - rocks/trees (GatherSync) don't drop on puppets at all; the owner's swing-for-you drops arrive as synced drops;
///  - drops from things that aren't synced yet still happen locally, per copy, as before;
///  - picking up a synced drop asks the owner first; only the player it says yes to gets it (Duckov loot rule).
/// </summary>
[HarmonyPatch]
internal static class DropSync
{
    public static Plugin Plugin;

    static int nextId = 1;
    static readonly Dictionary<int, ItemDropObject> drops = new Dictionary<int, ItemDropObject>();
    static readonly Dictionary<ItemDropObject, int> idOf = new Dictionary<ItemDropObject, int>();
    static readonly Dictionary<int, string> placeOf = new Dictionary<int, string>();   // drop id -> "scene/area"
    static readonly HashSet<int> pending = new HashSet<int>();   // puppet: asked the owner, waiting
    static bool allowLocal;        // puppet: an unsynced dropper (tree, rock) is dropping, keep it local
    static bool granted;           // puppet: the owner said this pickup is ours
    static string lastPlace = "";

    static bool Puppet => EnemySync.PuppetMode;
    public static int Count => drops.Count;

    public static void Clear()
    {
        drops.Clear(); idOf.Clear(); placeOf.Clear(); pending.Clear();
        nextId = 1; allowLocal = granted = false; lastPlace = "";
    }

    static void Track(int id, ItemDropObject d, string place)
    {
        if (idOf.TryGetValue(d, out int old)) Forget(old);   // pooled object reused
        drops[id] = d;
        idOf[d] = id;
        placeOf[id] = place;
    }

    static void Forget(int id)
    {
        if (drops.TryGetValue(id, out var d) && d) idOf.Remove(d);
        drops.Remove(id);
        placeOf.Remove(id);
        pending.Remove(id);
    }

    /// <summary>
    /// Every frame. When we leave a place someone else is still in, their drops there aren't ours to keep track of
    /// (we get a fresh list when we come back). If we were the last one there, we keep them: our world is the real one.
    /// </summary>
    public static void Tick()
    {
        string place = Plugin.Place;
        if (place == lastPlace) return;
        string left = lastPlace;
        lastPlace = place;
        if (left == "" || !SomeoneIn(left)) return;
        var gone = new List<int>();
        foreach (var pair in placeOf) if (pair.Value == left) gone.Add(pair.Key);
        foreach (int id in gone)
        {
            if (drops.TryGetValue(id, out var d) && d) d.gameObject.SetActive(false);
            Forget(id);
        }
    }

    static bool SomeoneIn(string place)
    {
        foreach (var p in Plugin.Players) if (p.HaveState && Plugin.PlaceOf(p.Scene, p.Area) == place) return true;
        return false;
    }

    // ---- item <-> bytes (the same fields a save file keeps) ----

    static void PutItem(NetDataWriter w, ItemObject item, int amount)
    {
        w.Put(item.data.addressKey ?? "");
        w.Put(amount);
        w.Put(JsonUtility.ToJson(item.randomValues));
    }

    static (string key, int amount, string values) GetItem(NetDataReader r) => (r.GetString(), r.GetInt(), r.GetString());

    /// <summary>Build the item like the save loader does (Addressables, then its random values). Calls back on the main thread.</summary>
    static void MakeItem(string key, int amount, string values, System.Action<ItemObject> done)
    {
        if (string.IsNullOrEmpty(key)) return;
        var handle = Addressables.LoadAssetAsync<ItemData>(key);
        handle.Completed += h =>
        {
            if (h.Status != AsyncOperationStatus.Succeeded || !h.Result) { NetLog.Warn($"Unknown item '{key}'"); return; }
            var rv = JsonUtility.FromJson<ItemRandomValues>(values) ?? new ItemRandomValues();
            rv.SetData(h.Result);
            done(new ItemObject(h.Result, amount) { randomValues = rv });
        };
    }

    static NetDataWriter SpawnMessage(int id, ItemObject item, int amount, Vector2 from, Vector2 to)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DropSpawn);
        w.Put(id);
        w.Put(from.x); w.Put(from.y); w.Put(to.x); w.Put(to.y);
        PutItem(w, item, amount);
        return w;
    }

    // ---- owner ----

    /// <summary>Owner: a player just arrived in our area; send them the drops lying here.</summary>
    public static void SendSnapshot(int playerId)
    {
        string here = Plugin.Place;
        int n = 0;
        foreach (var pair in drops)
        {
            var d = pair.Value;
            if (!d || !d.isActiveAndEnabled || d.itemObj == null || placeOf[pair.Key] != here) continue;
            Vector2 at = d.transform.position;
            Plugin.SendTo(playerId, SpawnMessage(pair.Key, d.itemObj, d.itemObj.amount, at, at));
            n++;
        }
        if (n > 0) NetLog.Info($"Sent {n} drops to {Plugin.NameOf(playerId)}");
    }

    /// <summary>Owner: another player wants to put an item in the world (overflow, dropped from its inventory).</summary>
    public static void OnDropRequest(NetDataReader r)
    {
        var (key, amount, values) = GetItem(r);
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        float radius = r.GetFloat();
        MakeItem(key, amount, values, item => Singleton<ItemDropHandler>.main.DropToWorld(pos, item, amount, radius));
    }

    /// <summary>Owner: another player wants to pick up drop <c>id</c>. First come, first served.</summary>
    public static void OnTake(NetDataReader r, int fromId)
    {
        int id = r.GetInt();
        var w = new NetDataWriter();
        if (drops.TryGetValue(id, out var d) && d && d.isActiveAndEnabled && d.isReady)
        {
            Forget(id);
            d.gameObject.SetActive(false);   // gone here, without us picking it up
            w.Put((byte)MsgType.DropTakeOk); w.Put(id);
            Plugin.SendTo(fromId, w);
            var gone = new NetDataWriter();
            gone.Put((byte)MsgType.DropRemoved); gone.Put(id);
            Plugin.SendToPlace(gone, DeliveryMethod.ReliableOrdered);   // the taker ignores it (already forgotten)
        }
        else
        {
            w.Put((byte)MsgType.DropDenied); w.Put(id);
            Plugin.SendTo(fromId, w);
        }
    }

    // ---- puppet ----

    public static void OnSpawn(NetDataReader r)
    {
        int id = r.GetInt();
        var from = new Vector2(r.GetFloat(), r.GetFloat());
        var to = new Vector2(r.GetFloat(), r.GetFloat());
        var (key, amount, values) = GetItem(r);
        if (drops.ContainsKey(id)) return;
        string place = Plugin.Place;
        MakeItem(key, amount, values, item =>
        {
            if (drops.ContainsKey(id) || Plugin.Place != place) return;
            var handler = Singleton<ItemDropHandler>.main;
            var d = PoolManager.Get(handler.itemDropObjectPrefab).GetComponent<ItemDropObject>();
            item.amount = amount;
            d.Setup(item);
            Track(id, d, place);
            handler.MoveToPosition(d, from, to).Forget();
        });
    }

    public static void OnTakeOk(NetDataReader r)
    {
        int id = r.GetInt();
        pending.Remove(id);
        if (!drops.TryGetValue(id, out var d) || !d) return;
        Forget(id);
        var me = LocalPlayer.Get();
        if (!me) return;
        granted = true;
        try { d.OnInteract(me.gameObject); }   // the game's own pickup: inventory, vfx, sound
        finally { granted = false; }
    }

    public static void OnDenied(NetDataReader r)
    {
        RemoveLocal(r.GetInt());
        NetLog.Info("Someone else picked that up first");
    }

    public static void OnRemoved(NetDataReader r) => RemoveLocal(r.GetInt());

    static void RemoveLocal(int id)
    {
        if (drops.TryGetValue(id, out var d) && d) d.gameObject.SetActive(false);
        Forget(id);
    }

    // ---- patches ----

    /// <summary>Puppet: drops from synced things never happen locally; unsynced droppers stay local.</summary>
    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.Drop)), HarmonyPrefix]
    static bool DropperDrop(ItemDropper __instance) => DropperAllowed(__instance);

    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.TryDrop)), HarmonyPrefix]
    static bool DropperTryDrop(ItemDropper __instance, ref bool __result)
    {
        __result = false;
        return DropperAllowed(__instance);
    }

    static bool DropperAllowed(ItemDropper dropper)
    {
        if (!Puppet) return true;
        // Synced things (creatures, breakables) only drop where they're run; those drops arrive as synced drops.
        if (dropper.GetComponentInParent<Enemy>(true) || dropper.GetComponentInParent<DamageableObject>(true)) return false;
        allowLocal = true;   // not synced yet (trees, rocks...): keep the old per-copy behaviour
        return true;
    }

    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.Drop)), HarmonyFinalizer]
    static void DropperDropDone() => allowLocal = false;

    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.TryDrop)), HarmonyFinalizer]
    static void DropperTryDropDone() => allowLocal = false;

    /// <summary>Puppet: our own drops (overflow, dropping from the inventory) go to the owner instead.</summary>
    [HarmonyPatch(typeof(ItemDropHandler), nameof(ItemDropHandler.DropToWorld)), HarmonyPrefix]
    static bool BeforeDropToWorld(Vector3 position, ItemObject itemObj, int stackAmount, float radius)
    {
        if (!Puppet || allowLocal) return true;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DropRequest);
        PutItem(w, itemObj, stackAmount);
        w.Put(position.x); w.Put(position.y);
        w.Put(radius);
        Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        return false;
    }

    /// <summary>Owner: every drop lands here; give it an id and tell the others in our area.</summary>
    [HarmonyPatch(typeof(ItemDropHandler), nameof(ItemDropHandler.MoveToPosition)), HarmonyPrefix]
    static void OnDropMoving(ItemDropObject drop, Vector2 from, Vector2 to)
    {
        if (Plugin == null || !Plugin.Connected || Puppet || allowLocal || !drop || drop.itemObj == null || idOf.ContainsKey(drop)) return;
        int id = ((Plugin.MyId + 1) << 20) | (nextId++ & 0xFFFFF);   // unique across owners
        Track(id, drop, Plugin.Place);
        if (Plugin.AnyoneElseHere) Plugin.SendToPlace(SpawnMessage(id, drop.itemObj, drop.itemObj.amount, from, to), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Puppet: picking up a synced drop is a request (no item until the owner says yes).
    /// Owner: we picked one up; tell the others it's gone.
    /// </summary>
    [HarmonyPatch(typeof(ItemDropObject), nameof(ItemDropObject.OnInteract)), HarmonyPrefix]
    static bool BeforePickup(ItemDropObject __instance)
    {
        if (Plugin == null || !idOf.TryGetValue(__instance, out int id)) return true;
        if (!Puppet)
        {
            Forget(id);
            var w = new NetDataWriter();
            w.Put((byte)MsgType.DropRemoved); w.Put(id);
            Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
            return true;
        }
        if (granted) return true;
        if (pending.Add(id))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.DropTake); w.Put(id);
            Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        }
        return false;
    }
}

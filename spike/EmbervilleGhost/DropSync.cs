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

    // Instanced loot (Session.InstancedLoot): weapons / armour / gear lie there once for every player. Everyone keeps a
    // record of each one (what, where, who already took their copy), so whoever runs the area (now or after a handover)
    // knows, and players arriving later still find theirs.
    class Instanced
    {
        public string Key, Name, Values, Place;
        public int Amount;
        public Vector2 At;
        public readonly HashSet<int> TakenBy = new HashSet<int>();
    }
    static readonly Dictionary<int, Instanced> instanced = new Dictionary<int, Instanced>();

    public static void Clear()
    {
        drops.Clear(); idOf.Clear(); placeOf.Clear(); pending.Clear(); instanced.Clear();
        nextId = 1; allowLocal = granted = false; lastPlace = "";
    }

    /// <summary>A place gone (a dungeon floor left): every item lying within it goes back to the pool, and is forgotten
    /// (the floors are all built in the same spot: the next one would have them).</summary>
    public static void ClearIn(Bounds b)
    {
        if (b.size == Vector3.zero) return;
        b.Expand(new Vector3(4f, 4f, 0f));
        bool Inside(Vector2 p) => p.x >= b.min.x && p.x <= b.max.x && p.y >= b.min.y && p.y <= b.max.y;
        int n = 0;
        foreach (var d in Object.FindObjectsOfType<ItemDropObject>())
            if (d && Inside(d.transform.position)) { if (idOf.TryGetValue(d, out int id)) Forget(id); d.gameObject.SetActive(false); n++; }
        var gone = new List<int>();
        foreach (var x in instanced) if (Inside(x.Value.At)) gone.Add(x.Key);
        foreach (var id in gone) instanced.Remove(id);
        if (n > 0) NetLog.Info($"Dungeon: {n} items left lying on the floor cleared away", toast: false);
    }

    static Instanced Remember(int id, string key, string name, int amount, string values, Vector2 at, string place)
    {
        if (!instanced.TryGetValue(id, out var rec))
            instanced[id] = rec = new Instanced { Key = key, Name = name, Amount = amount, Values = values, At = at, Place = place };
        return rec;
    }

    /// <summary>Owner: tell everyone in the area that this player has taken their copy (so the record survives a handover).</summary>
    static void SendTaken(int id, int player)
    {
        if (instanced.TryGetValue(id, out var rec)) rec.TakenBy.Add(player);
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DropTakenBy); w.Put(id); w.Put(player);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnTakenBy(NetDataReader r)
    {
        int id = r.GetInt(), player = r.GetInt();
        if (instanced.TryGetValue(id, out var rec)) rec.TakenBy.Add(player);
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
        var goneInst = new List<int>();
        foreach (var pair in instanced) if (pair.Value.Place == left) goneInst.Add(pair.Key);
        foreach (int id in goneInst) instanced.Remove(id);
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

    /// <summary>
    /// Build the item like the save loader does (Addressables by its address key, then its random values); if the key
    /// is empty or doesn't load, find the item among the loaded item assets by name. Calls back on the main thread.
    /// </summary>
    static void MakeItem(string key, int amount, string values, System.Action<ItemObject> done) => MakeItem(key, "", amount, values, done);

    static void MakeItem(string key, string name, int amount, string values, System.Action<ItemObject> done)
    {
        void Build(ItemData data)
        {
            if (!data) { NetLog.Warn($"Unknown item '{key}' / '{name}': not shown"); return; }
            var rv = JsonUtility.FromJson<ItemRandomValues>(values) ?? new ItemRandomValues();
            rv.SetData(data);
            done(new ItemObject(data, amount) { randomValues = rv });
        }
        if (string.IsNullOrEmpty(key)) { Build(ItemByName(name)); return; }
        // built AFTER the load's callback, not inside it: setting a drop up reads its translated name, a load of its
        // own, and Addressables refuses one load inside another's callback ("Reentering the Update method")
        Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
        {
            var data = h.Status == AsyncOperationStatus.Succeeded && h.Result ? h.Result : ItemByName(name);
            Cysharp.Threading.Tasks.UniTask.Post(() => Build(data));
        };
    }

    static Dictionary<string, ItemData> itemsByName;

    static ItemData ItemByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (itemsByName == null || attempt == 1)
            {
                itemsByName = new Dictionary<string, ItemData>();
                foreach (var d in Resources.FindObjectsOfTypeAll<ItemData>()) if (d && !itemsByName.ContainsKey(d.name)) itemsByName[d.name] = d;
            }
            if (itemsByName.TryGetValue(name, out var found) && found) return found;
        }
        return null;
    }

    static NetDataWriter SpawnMessage(int id, ItemObject item, int amount, Vector2 from, Vector2 to, bool inst) =>
        SpawnMessage(id, item.data.addressKey ?? "", item.data.name, amount, JsonUtility.ToJson(item.randomValues), from, to, inst);

    static NetDataWriter SpawnMessage(int id, string key, string name, int amount, string values, Vector2 from, Vector2 to, bool inst)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DropSpawn);
        w.Put(id);
        w.Put(from.x); w.Put(from.y); w.Put(to.x); w.Put(to.y);
        w.Put(key); w.Put(amount); w.Put(values);
        w.Put(inst);
        w.Put(name ?? "");   // fallback if the address key is empty / doesn't load
        return w;
    }

    // ---- owner ----

    /// <summary>Owner: a player just arrived in our area; send them the drops lying here (and the instanced copies still theirs).</summary>
    public static void SendSnapshot(int playerId)
    {
        string here = Plugin.Place;
        int n = 0;
        foreach (var pair in drops)
        {
            var d = pair.Value;
            if (!d || !d.isActiveAndEnabled || d.itemObj == null || placeOf[pair.Key] != here || instanced.ContainsKey(pair.Key)) continue;
            Vector2 at = d.transform.position;
            Plugin.SendTo(playerId, SpawnMessage(pair.Key, d.itemObj, d.itemObj.amount, at, at, false));
            n++;
        }
        foreach (var pair in instanced)
        {
            var rec = pair.Value;
            if (rec.Place != here || rec.TakenBy.Contains(playerId)) continue;
            Plugin.SendTo(playerId, SpawnMessage(pair.Key, rec.Key, rec.Name, rec.Amount, rec.Values, rec.At, rec.At, true));
            foreach (int who in rec.TakenBy)   // so they know too, should they run the area later
            {
                var w = new NetDataWriter();
                w.Put((byte)MsgType.DropTakenBy); w.Put(pair.Key); w.Put(who);
                Plugin.SendTo(playerId, w);
            }
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
        if (instanced.TryGetValue(id, out var rec))
        {
            // Instanced: everyone gets their own copy, once. It stays for the others (and for us).
            bool yes = !rec.TakenBy.Contains(fromId);
            w.Put((byte)(yes ? MsgType.DropTakeOk : MsgType.DropDenied)); w.Put(id);
            Plugin.SendTo(fromId, w);
            if (yes) SendTaken(id, fromId);
            return;
        }
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
        bool inst = r.GetBool();
        string name = r.GetString();
        string place = Plugin.Place;
        if (inst) Remember(id, key, name, amount, values, to, place);
        if (drops.TryGetValue(id, out var have) && have && have.isActiveAndEnabled) return;
        MakeItem(key, name, amount, values, item =>
        {
            if (drops.TryGetValue(id, out var already) && already && already.isActiveAndEnabled) return;
            if (Plugin.Place != place) { NetLog.Info($"Drop {name} skipped: we left {place}"); return; }
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
        int id = r.GetInt();
        RemoveLocal(id);
        NetLog.Info(instanced.ContainsKey(id) ? "You already took your copy of that" : "Someone else picked that up first");
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

    /// <summary>The game's own data: some loot tables hold an empty entry (a crate in the starting area), and dropping
    /// "nothing" threw, which also lost the rest of that dropper's drops. An empty one is skipped; the rest still fall.</summary>
    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.InternalDropToWorld)), HarmonyPrefix]
    static bool SkipEmptyDrop(ItemData item, float totalAngle, ref float __result)
    {
        if (item) return true;
        __result = totalAngle;
        return false;
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
        bool inst = Session.IsInstanced(drop.itemObj);   // weapons / armour / gear with instanced loot on: one per player
        if (inst) Remember(id, drop.itemObj.data.addressKey ?? "", drop.itemObj.data.name, drop.itemObj.amount, JsonUtility.ToJson(drop.itemObj.randomValues), to, Plugin.Place);
        if (Plugin.AnyoneElseHere) Plugin.SendToPlace(SpawnMessage(id, drop.itemObj, drop.itemObj.amount, from, to, inst), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Puppet: picking up a synced drop is a request (no item until the owner says yes).
    /// Owner: we picked one up; tell the others it's gone.
    /// </summary>
    [HarmonyPatch(typeof(ItemDropObject), nameof(ItemDropObject.OnInteract)), HarmonyPrefix]
    static bool BeforePickup(ItemDropObject __instance)
    {
        if (Plugin == null || !idOf.TryGetValue(__instance, out int id)) return true;
        if (!Puppet && instanced.ContainsKey(id))
        {
            // Our copy of an instanced drop: gone for us only, the others still have theirs.
            Forget(id);
            SendTaken(id, Plugin.MyId);
            return true;
        }
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

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace EmbervilleGhost;

/// <summary>
/// Building on the estate (PlaceableController, build mode): placing furniture, stations, chests, farm plots, roads,
/// fences, and taking them away again.
///
/// Placing: PlaceableController.PlaceObject -> PlaceableData.Place (a world object gets a new GUID save id; a farm
/// plot also paints its tile; roads / fences are only tiles) + SaveData. Whoever builds pays the resources on their
/// copy and sends what they placed: the placeable's id, where, which way, and the new GUID, so the object has the
/// same save id everywhere (stations, chests and plots sync by it). The others place it the way the game loads a save
/// (dataBase.Get(id).Place(pos, dir, guid) + SaveData), with its puff of smoke.
/// Removing: PlaceableController.RemoveSelectedObject -> per object PlacementObjectComponent.OnRemove (+ its saved
/// entry), per tile TilesetDrawerManager.RemoveTile. The others do the same (the resources go back to the remover).
///
/// Sent to everyone, wherever they are: a player in another scene keeps it and applies it when they walk into the
/// scene it happened in (the estate exists in one scene, the others don't have it loaded).
/// </summary>
[HarmonyPatch]
internal static class PlaceSync
{
    public static Plugin Plugin;

    enum Op : byte { Place, RemoveObject, RemoveTile }

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static int applying;   // doing someone else's: don't send it back
    static int removing;   // inside our own RemoveSelectedObject
    static readonly List<(string scene, byte[] msg)> later = new List<(string, byte[])>();

    public static void Clear()
    {
        later.Clear();
        applying = removing = 0;
    }

    // ---- ours ----

    struct Placing { public bool Valid; public PlaceableData Data; public Vector3 Pos; public Direction Dir; public int Before; }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.PlaceObject)), HarmonyPrefix]
    static void BeforePlace(PlaceableController __instance, out Placing __state)
    {
        __state = default;
        var c = __instance;
        if (!Connected || applying > 0 || !c.isPlacing || !c.canPlace || c.currentPlaceableData == null) return;
        __state = new Placing
        {
            Valid = true,
            Data = c.currentPlaceableData,
            Pos = c.lastSnapPosition + c.currentPlaceableData.anchor,   // as PlaceObject works it out
            Dir = c.turnable ? c.turnable.CurrentDirection : Direction.Down,
            Before = c.saved.Count,
        };
    }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.PlaceObject)), HarmonyPostfix]
    static void AfterPlace(PlaceableController __instance, Placing __state)
    {
        if (!__state.Valid) return;
        var saved = __instance.saved;
        string guid = saved.Count > __state.Before ? saved[saved.Count - 1].instanceSaveID ?? "" : "";
        var w = Message(Op.Place);
        w.Put(__state.Data.saveID);
        w.Put(__state.Pos.x); w.Put(__state.Pos.y);
        w.Put((byte)__state.Dir);
        w.Put(guid);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject)), HarmonyPrefix]
    static void BeforeRemove() => removing++;

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject)), HarmonyFinalizer]
    static void AfterRemove() => removing--;

    [HarmonyPatch(typeof(PlacementObjectComponent), nameof(PlacementObjectComponent.OnRemove)), HarmonyPrefix]
    static void ObjectRemoved(PlacementObjectComponent __instance)
    {
        if (removing == 0 || applying > 0 || !Connected) return;
        var w = Message(Op.RemoveObject);
        w.Put(__instance.RelatedPlaceableDataID ?? "");
        var p = __instance.transform.position;
        w.Put(p.x); w.Put(p.y);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    [HarmonyPatch]
    static class TileRemoved
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(TilesetDrawerManager), nameof(TilesetDrawerManager.RemoveTile),
            new[] { typeof(Tilemap), typeof(Vector3), typeof(string).MakeByRefType() });

        static void Postfix(TilesetDrawerManager __instance, Tilemap __0, Vector3 __1, bool __result)
        {
            if (!__result || removing == 0 || applying > 0 || !Connected) return;
            var w = Message(Op.RemoveTile);
            w.Put((int)__instance.GetLayer(__0));
            w.Put(__1.x); w.Put(__1.y); w.Put(__1.z);
            Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
        }
    }

    static NetDataWriter Message(Op op)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlaceChange);
        w.Put((byte)op);
        w.Put(LocalPlayer.Scene);
        return w;
    }

    // ---- theirs ----

    public static void OnChange(NetDataReader r)
    {
        var copy = new byte[r.AvailableBytes];
        System.Array.Copy(r.RawData, r.Position, copy, 0, copy.Length);
        var peek = new NetDataReader(copy);
        peek.GetByte();
        string scene = peek.GetString();
        if (Here(scene)) Apply(new NetDataReader(copy));
        else later.Add((scene, copy));   // the estate isn't loaded here: when we get there
    }

    static bool Here(string scene) => LocalPlayer.Scene == scene && LocalPlayer.Get() && Singleton<PlaceableController>.HasMain;

    /// <summary>Every frame: changes made in a scene we've just walked into.</summary>
    public static void Tick()
    {
        if (later.Count == 0) return;
        for (int i = 0; i < later.Count; i++)
        {
            if (!Here(later[i].scene)) continue;
            Apply(new NetDataReader(later[i].msg));
            later.RemoveAt(i--);
        }
    }

    static void Apply(NetDataReader r)
    {
        var op = (Op)r.GetByte();
        r.GetString();
        var c = Singleton<PlaceableController>.main;
        applying++;
        try
        {
            switch (op)
            {
                case Op.Place:
                {
                    string id = r.GetString();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                    var dir = (Direction)r.GetByte();
                    string guid = r.GetString();
                    var data = c.dataBase.Get(id);
                    if (data == null) { NetLog.Warn($"Can't place '{id}': unknown here"); return; }
                    var (_, saveId) = data.Place(pos, dir, guid.Length > 0 ? guid : null, c.placeablesParent);
                    data.SaveData(pos, dir, saveId);
                    c.HandleVFX(pos, data.VfxPlacePrefab, data.vfxPlaceOffset).Forget();
                    break;
                }
                case Op.RemoveObject:
                {
                    string id = r.GetString();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                    foreach (var comp in c.placeablesParent.GetComponentsInChildren<PlacementObjectComponent>(true))
                    {
                        if (comp.RelatedPlaceableDataID != id || ((Vector2)comp.transform.position - (Vector2)pos).sqrMagnitude > 0.01f) continue;
                        var data = c.dataBase.Get(id);
                        var at = comp.transform.position;
                        if (data != null)
                        {
                            data.RemoveSavedData(at);
                            c.HandleVFX(at, data.VfxRemovePrefab, data.vfxRemoveOffset).Forget();
                        }
                        comp.OnRemove();
                        Object.Destroy(comp.gameObject);
                        break;
                    }
                    break;
                }
                case Op.RemoveTile:
                {
                    var layer = (TilemapLayer)r.GetInt();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    Singleton<TilesetDrawerManager>.main.RemoveTile(layer, pos);
                    break;
                }
            }
        }
        finally { applying--; }
    }
}

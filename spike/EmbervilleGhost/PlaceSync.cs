using System.Collections.Generic;
using System.Linq;
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
        SendPlaced(__state.Data.saveID, __state.Pos, __state.Dir, guid, BuildMode.On && !BuildMode.InEstate(__state.Pos));
    }

    /// <summary>Placed (by the game's build mode or our own placer). anywhere: built anywhere (B), our list, not the estate's.</summary>
    public static void SendPlaced(string id, Vector3 pos, Direction dir, string guid, bool anywhere)
    {
        if (!Connected || applying > 0) return;
        var w = Message(Op.Place);
        w.Put(id);
        w.Put(pos.x); w.Put(pos.y);
        w.Put((byte)dir);
        w.Put(guid ?? "");
        w.Put(anywhere);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    public static void SendRemoved(string id, Vector3 pos)
    {
        if (!Connected || applying > 0) return;
        var w = Message(Op.RemoveObject);
        w.Put(id ?? "");
        w.Put(pos.x); w.Put(pos.y);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject)), HarmonyPrefix]
    static void BeforeRemove() => removing++;

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject)), HarmonyFinalizer]
    static void AfterRemove() => removing--;

    [HarmonyPatch(typeof(PlacementObjectComponent), nameof(PlacementObjectComponent.OnRemove)), HarmonyPrefix]
    static void ObjectRemoved(PlacementObjectComponent __instance)
    {
        if (removing == 0) return;
        SendRemoved(__instance.RelatedPlaceableDataID, __instance.transform.position);
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

    /// <summary>Host -> a player who just joined: everything built anywhere (B) in this world.</summary>
    public static void SendBuiltTo(int id)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.BuiltList);
        w.Put(BuildSave.List.Save());
        Plugin.SendTo(id, w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnBuiltList(NetDataReader r)
    {
        BuildSave.TakeHosts(EmbervilleGhost.Core.BuildList.Load(r.GetString()));
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
        else if (!RecordElsewhere(copy, scene)) later.Add((scene, copy));   // the estate isn't loaded here: when we get there
    }

    /// <summary>Built anywhere (B) in a scene we're not in: into the list now (saved, handed to joiners), the object
    /// appears when we walk in. False: an estate change, kept for when we get there.</summary>
    static bool RecordElsewhere(byte[] msg, string scene)
    {
        var r = new NetDataReader(msg);
        var op = (Op)r.GetByte();
        r.GetString();
        if (op == Op.Place)
        {
            string id = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var dir = (Direction)r.GetByte();
            string guid = r.GetString();
            if (!(r.AvailableBytes > 0 && r.GetBool())) return false;
            BuildSave.Record(scene, id, pos, dir, guid);
            return true;
        }
        if (op == Op.RemoveObject)
        {
            string id = r.GetString();
            return BuildSave.Unrecord(scene, id, new Vector3(r.GetFloat(), r.GetFloat(), 0f));
        }
        return false;
    }

    // (no build controller here: what was built anywhere still applies; Apply skips the estate's own changes)
    static bool Here(string scene) => LocalPlayer.Scene == scene && LocalPlayer.Get();

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
        var c = BuildMode.Controller;
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
                    bool anywhere = r.AvailableBytes > 0 && r.GetBool();
                    var data = BuildCatalog.Get(id);
                    if (data == null) { NetLog.Warn($"Can't place '{id}': unknown here"); return; }
                    if (!c && (!anywhere || BuildCatalog.NeedsController(data))) { NetLog.Warn($"Can't place '{id}' without a build controller here"); return; }
                    var (_, saveId) = data.Place(pos, dir, guid.Length > 0 ? guid : null, anywhere ? BuildSave.Parent : c.placeablesParent);
                    if (anywhere) BuildSave.Adopt(c, data, pos, dir, saveId);
                    else data.SaveData(pos, dir, saveId);
                    if (c) c.HandleVFX(pos, data.VfxPlacePrefab, data.vfxPlaceOffset).Forget();
                    break;
                }
                case Op.RemoveObject:
                {
                    string id = r.GetString();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                    var estate = c && c.placeablesParent ? c.placeablesParent.GetComponentsInChildren<PlacementObjectComponent>(true) : new PlacementObjectComponent[0];
                    foreach (var comp in estate.Concat(BuildSave.Parent.GetComponentsInChildren<PlacementObjectComponent>(true)))   // (and what was built anywhere)
                    {
                        if (comp.RelatedPlaceableDataID != id || ((Vector2)comp.transform.position - (Vector2)pos).sqrMagnitude > 0.01f) continue;
                        var data = BuildCatalog.Get(id);
                        var at = comp.transform.position;
                        if (data != null && c)
                        {
                            data.RemoveSavedData(at);
                            c.HandleVFX(at, data.VfxRemovePrefab, data.vfxRemoveOffset).Forget();
                        }
                        comp.OnRemove();
                        Object.Destroy(comp.gameObject);
                        break;
                    }
                    BuildSave.Unrecord(LocalPlayer.Scene, id, pos);   // (built anywhere but not put back yet: off the list all the same)
                    break;
                }
                case Op.RemoveTile:
                {
                    var layer = (TilemapLayer)r.GetInt();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    if (Singleton<TilesetDrawerManager>.HasMain) Singleton<TilesetDrawerManager>.main.RemoveTile(layer, pos);
                    break;
                }
            }
        }
        finally { applying--; }
    }
}

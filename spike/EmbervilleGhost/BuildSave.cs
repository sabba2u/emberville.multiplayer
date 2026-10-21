using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleGhost.Core;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// What build mode anywhere (B) puts down outside the estate: never left in the estate's own list (the game would
/// put it back in Town at the same spot), but in ours (BuildList), per world, in &lt;world&gt;.build next to the save,
/// written when the game saves. Each time a scene loads, what was built in it is put back (the game's own Place, with
/// the same save id, so a chest finds its contents). Destroy outside the estate only takes what was built this way.
/// As a guest the host's list is used (PlaceSync) and nothing is written.
/// </summary>
[HarmonyPatch]
internal static class BuildSave
{
    public static BuildList List = new BuildList();
    static string loadedFor;
    static bool hooked;
    static bool unreadable;   // the file was there but couldn't be read: never overwrite it
    static Transform parent;
    static string placedScene;
    static readonly HashSet<Built> placed = new HashSet<Built>();

    static bool Guest => Hud.Plugin != null && Hud.Plugin.Connected && !Hud.Plugin.IsHost;

    /// <summary>Where what we build hangs in the scene ("=== Built Anywhere", made per scene).</summary>
    public static Transform Parent
    {
        get
        {
            if (!parent) { parent = new GameObject("=== Built Anywhere").transform; placed.Clear(); }
            return parent;
        }
    }

    /// <summary>The parent if it's been made in this scene (null otherwise; Parent makes one).</summary>
    public static Transform Existing => parent;

    public static bool IsOurs(PlacementObjectComponent c) => c && parent && c.transform.IsChildOf(parent);

    public static bool IsOursAt(Vector3 at) =>
        List.InScene(LocalPlayer.Scene).Any(b => Mathf.Abs(b.X - at.x) <= 0.01f && Mathf.Abs(b.Y - at.y) <= 0.01f);

    static string FileFor(string world) => SaveLoad.GetPath(world + ".build");

    public static void Tick()
    {
        if (!hooked)
        {
            hooked = true;
            GameData.Register(GameData.Call.OnSave, (_, _) => Save());
            GameData.Register(GameData.Call.OnLoad, (_, _) => loadedFor = null);   // (the same world loaded again: back to what's on disk)
        }
        if (!Guest)
        {
            string world = GameData.HasWorldData ? GameData.GetWorldPrefix : null;
            if (world != loadedFor)
            {
                loadedFor = world;
                Load();
            }
        }
        PutBack();
    }

    static void Load()
    {
        string text = "";
        unreadable = false;
        if (loadedFor != null)
            try { string path = FileFor(loadedFor); if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (Exception e) { unreadable = true; NetLog.Warn($"Build mode: can't read the world's build file (it won't be overwritten): {e.Message}"); }
        Replace(BuildList.Load(text));
    }

    /// <summary>Joining: our own world's builds written first (nothing of ours is saved as a guest), then the host's
    /// list; ours is read again after leaving.</summary>
    public static void TakeHosts(BuildList hosts)
    {
        WriteOwn();
        Replace(hosts);
        loadedFor = null;
    }

    /// <summary>A new list (a world loaded, the host's arrived): what was put down from the old one goes.</summary>
    public static void Replace(BuildList list)
    {
        List = list;
        if (parent) UnityEngine.Object.Destroy(parent.gameObject);
        parent = null;
        placed.Clear();
    }

    static void Save()
    {
        if (Guest) return;
        WriteOwn();
    }

    /// <summary>Our own world's list to its file (also just before a host's list replaces it, on joining).</summary>
    public static void WriteOwn()
    {
        if (loadedFor == null || unreadable) return;
        try
        {
            string path = FileFor(loadedFor), tmp = path + ".tmp";
            File.WriteAllText(tmp, List.Save());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception e) { NetLog.Warn($"Build mode: can't save the world's build file: {e.Message}"); }
    }

    /// <summary>What was built in this scene, put back once it's loaded (and the chests' contents are).</summary>
    static void PutBack()
    {
        string scene = LocalPlayer.Scene;
        if (scene != placedScene) { placedScene = scene; placed.Clear(); }
        if (string.IsNullOrEmpty(scene) || scene == "MainMenu" || scene == "SplashScreen") return;
        if (!LocalPlayer.Get() || !Singleton<ChestManager>.HasMain || Singleton<ChestManager>.main.isLoading) return;
        foreach (var b in List.InScene(scene).ToList())
        {
            if (placed.Contains(b) && parent) continue;
            PlaceFromList(b);
        }
    }

    public static void PlaceFromList(Built b)
    {
        var data = BuildCatalog.Get(b.PlaceableId);
        placed.Add(b);
        if (data == null) { NetLog.Warn($"Build mode: can't put back '{b.PlaceableId}': unknown here"); return; }
        if (!BuildMode.Controller && BuildCatalog.NeedsController(data)) { NetLog.Warn($"Build mode: can't put back '{b.PlaceableId}' without the estate's controller"); return; }
        try { data.Place(new Vector3(b.X, b.Y, 0f), (Direction)b.Dir, string.IsNullOrEmpty(b.SaveId) ? null : b.SaveId, Parent); }
        catch (Exception e) { NetLog.Warn($"Build mode: putting back '{b.PlaceableId}': {e.Message}"); }
    }

    // ---- placing: out of the estate's list, into ours ----

    struct Placing { public bool Valid; public PlaceableData Data; public Vector3 Pos; public Direction Dir; public int Before; }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.PlaceObject)), HarmonyPrefix]
    static void BeforePlace(PlaceableController __instance, out Placing __state)
    {
        __state = default;
        var c = __instance;
        if (!BuildMode.On || !c.isPlacing || !c.canPlace || c.currentPlaceableData == null) return;
        var pos = c.lastSnapPosition + c.currentPlaceableData.anchor;   // as PlaceObject works it out
        if (BuildMode.InEstate(pos)) return;   // (an unlocked estate's own: the game keeps it)
        __state = new Placing { Valid = true, Data = c.currentPlaceableData, Pos = pos, Dir = c.turnable ? c.turnable.CurrentDirection : Direction.Down, Before = c.saved.Count };
    }

    // after PlaceSync's postfix (it reads the new entry's save id from the estate's list)
    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.PlaceObject)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
    static void AfterPlace(PlaceableController __instance, Placing __state)
    {
        if (!__state.Valid || __instance.saved.Count <= __state.Before) return;
        var entry = __instance.saved[__instance.saved.Count - 1];
        __instance.saved.RemoveAt(__instance.saved.Count - 1);
        Adopt(__instance, __state.Data, entry.worldPosition, entry.direction, entry.instanceSaveID);
    }

    /// <summary>Something just placed outside the estate (ours or a teammate's): into our list, under our parent.</summary>
    /// <summary>A teammate's build / removal in a scene we're not in: the list changes now (it's what gets saved and
    /// handed to players who join); the object itself appears when we walk in (PutBack).</summary>
    public static void Record(string scene, string placeableId, Vector3 pos, Direction dir, string saveId) =>
        List.Add(new Built { Scene = scene, PlaceableId = placeableId, SaveId = saveId ?? "", X = pos.x, Y = pos.y, Dir = (int)dir });

    public static bool Unrecord(string scene, string placeableId, Vector3 pos)
    {
        var b = List.Find(scene, placeableId, pos.x, pos.y);
        if (b == null) return false;
        List.Remove(b);
        placed.Remove(b);
        return true;
    }

    // ---- a deleted world: its build file goes aside too (a new world in the slot starts empty) ----

    [HarmonyPatch(typeof(GameData), nameof(GameData.DeleteWorldSave)), HarmonyPrefix]
    static void WorldDeleted() => SetAside();

    [HarmonyPatch(typeof(GameData), nameof(GameData.DeleteAllCurrentSave)), HarmonyPrefix]
    static void AllDeleted() => SetAside();

    static void SetAside()
    {
        try
        {
            string path = FileFor(GameData.GetWorldPrefix);
            if (!File.Exists(path)) return;
            string bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
        }
        catch (Exception e) { NetLog.Warn($"Build mode: setting the deleted world's build file aside: {e.Message}"); }
        loadedFor = null;
    }

    public static Built Adopt(PlaceableController c, PlaceableData data, Vector3 pos, Direction dir, string saveId)
    {
        var b = new Built { Scene = LocalPlayer.Scene, PlaceableId = data.saveID, SaveId = saveId ?? "", X = pos.x, Y = pos.y, Dir = (int)dir };
        List.Add(b);
        placed.Add(b);
        if (c && c.placeablesParent)
            foreach (var comp in c.placeablesParent.GetComponentsInChildren<PlacementObjectComponent>(true))
                if (comp.RelatedPlaceableDataID == data.saveID && ((Vector2)comp.transform.position - (Vector2)pos).sqrMagnitude <= 0.0001f)
                {
                    comp.transform.SetParent(Parent, true);
                    break;
                }
        return b;
    }

    // ---- removing: outside the estate, only what was built this way ----

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject)), HarmonyPrefix]
    static void OnlyOurs(PlaceableController __instance)
    {
        if (!BuildMode.On || BuildMode.EstateUnlocked && BuildMode.InEstate(__instance.lastSnapPosition)) return;
        __instance.objectResults.RemoveAll(col => !col || !col.transform.parent ||
            !col.transform.parent.TryGetComponent<PlacementObjectComponent>(out var comp) || !IsOurs(comp));
        __instance.tilemapResults.Clear();   // (the estate's roads and fences aren't ours)
    }

    [HarmonyPatch(typeof(PlaceableWorldObjectData), nameof(PlaceableWorldObjectData.RemoveSavedData)), HarmonyPrefix]
    static bool ForgetOurs(PlaceableWorldObjectData __instance, Vector3 position, ref bool __result)
    {
        var b = List.Find(LocalPlayer.Scene, __instance.saveID, position.x, position.y);
        if (b == null) return true;
        var c = BuildMode.Controller;
        if (c && !string.IsNullOrEmpty(b.SaveId) && c.instanceOnRemove.TryGetValue(b.SaveId, out var onRemove)) onRemove?.Invoke();
        List.Remove(b);
        placed.Remove(b);
        __result = true;
        return false;
    }
}

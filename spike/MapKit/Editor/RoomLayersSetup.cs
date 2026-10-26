// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/RoomLayersSetup.cs.
// A room's tilemaps, set up the way the game's own dungeon rooms are (notes: the game draws flat ground on the "Default"
// sorting layer, and things with height - wall tops, fences, fog - on "Player", sorted tile by tile against the
// characters). Menu "Emberville > Update Room Layers": the open scene's Grid gets every layer, in the game's order, with
// the game's sorting layers (added to the project if missing); our first names (Ground, Walls, WallTops) are renamed.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Tilemaps;

public static class EmbervilleRoomLayers
{
    /// <summary>One of a room's tilemaps: its name (the game's), the names it may have had, where it draws, and a tint
    /// for the hidden ones (only seen in the editor).</summary>
    struct Spec
    {
        public string Name, Sorting; public int Order; public bool Individual; public string[] Was; public Color? Tint;
        public Spec(string name, string sorting, int order, bool individual = false, string[] was = null, Color? tint = null)
        { Name = name; Sorting = sorting; Order = order; Individual = individual; Was = was ?? new string[0]; Tint = tint; }
    }

    static readonly Spec[] Specs =
    {
        new Spec("Ground0", "Default", 0, was: new[] { "Ground" }),         // the floor
        new Spec("Ground1", "Default", 1),                                  // floor detail over it
        new Spec("Ground2", "Default", 2),                                  // more floor detail
        new Spec("Water", "Default", 3),                                    // water (blocks)
        new Spec("Bridge", "Default", 4),                                   // ours: walkable over water
        new Spec("Wall", "Default", 5, was: new[] { "Walls" }),             // wall faces (block)
        new Spec("WallCover", "Player", 0, true, new[] { "WallTops" }),     // wall tops (block; characters walk behind them)
        new Spec("Fence", "Player", 0, true),                               // fences, railings (block)
        new Spec("Fog", "Player", 10),                                      // dark overhang over everything
        new Spec("Collision", "Default", 20, tint: new Color(1f, 0.2f, 0.2f, 0.45f)),         // invisible walls (hidden in the game)
        new Spec("RemoveCollision", "Default", 21, tint: new Color(0.2f, 1f, 0.3f, 0.45f)),   // gaps cut through walls (hidden)
        new Spec("RemoveWater", "Default", 22, tint: new Color(0.2f, 0.9f, 1f, 0.45f)),       // water cut away (hidden)
    };

    /// <summary>The game's sorting layers, in its order and with its IDs: PlayerPortraits, Default, Player, UI, WorldMap.</summary>
    static readonly (string name, uint id)[] SortingLayers =
        { ("PlayerPortraits", 2534410981), ("Default", 0), ("Player", 2326331859), ("UI", 3266674989), ("WorldMap", 2468839205) };

    /// <summary>The game's object layers, in its slots (what a thing is to physics and the game's code: BodyCollisions
    /// blocks bodies, IDamageables takes hits, Interactables answers F...; 0-2, 4, 5 are Unity's own).</summary>
    static readonly (int slot, string name)[] ObjectLayers =
    {
        (6, "Player"), (7, "SurfaceDetection"), (8, "Confiners"), (9, "FarmDetection"), (10, "BodyCollisions"),
        (11, "IDamageables"), (12, "HeightMaps"), (13, "Scene"), (14, "Interactables"), (15, "PlayerPortrait"),
        (16, "WorldMap"), (17, "Minimap"), (18, "Placements"),
    };

    /// <summary>The project gets the game's object layers in the same slots (a slot already used for something else is
    /// left alone, and said).</summary>
    public static void EnsureObjectLayers()
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        bool changed = false;
        foreach (var (slot, name) in ObjectLayers)
        {
            var e = layers.GetArrayElementAtIndex(slot);
            if (e.stringValue == name) continue;
            if (!string.IsNullOrEmpty(e.stringValue)) { Debug.LogWarning($"Emberville: object layer {slot} is \"{e.stringValue}\" here; the game's is \"{name}\" (left as it is)"); continue; }
            e.stringValue = name;
            changed = true;
        }
        if (changed) { tagManager.ApplyModifiedProperties(); Debug.Log("Emberville: added the game's object layers (6-18: Player ... Placements)"); }
    }

    /// <summary>The project gets the game's sorting layers (only the missing ones are added, in the game's order).</summary>
    public static void EnsureSortingLayers()
    {
        EnsureObjectLayers();
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("m_SortingLayers");
        bool changed = false;
        for (int want = 0; want < SortingLayers.Length; want++)
        {
            var (name, id) = SortingLayers[want];
            int at = -1;
            for (int i = 0; i < layers.arraySize; i++) if (layers.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == name) at = i;
            if (at >= 0) continue;
            // after the game's layer that comes before it
            int insert = 0;
            for (int i = 0; i < layers.arraySize; i++)
                if (SortingLayers.Take(want).Any(l => l.name == layers.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue)) insert = i + 1;
            layers.InsertArrayElementAtIndex(insert);
            var e = layers.GetArrayElementAtIndex(insert);
            e.FindPropertyRelative("name").stringValue = name;
            e.FindPropertyRelative("uniqueID").longValue = id;
            changed = true;
        }
        if (changed) { tagManager.ApplyModifiedProperties(); Debug.Log("Emberville: added the game's sorting layers (PlayerPortraits, Default, Player, UI, WorldMap)"); }
    }

    /// <summary>Every layer under this Grid, in the game's order (renamed from our first names, made if missing), drawn
    /// on the game's sorting layers. Returns them by name.</summary>
    public static Dictionary<string, Tilemap> EnsureLayers(GameObject grid)
    {
        EnsureSortingLayers();
        var maps = new Dictionary<string, Tilemap>();
        for (int i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            Transform t = grid.transform.Find(spec.Name);
            foreach (var old in spec.Was) if (!t && (t = grid.transform.Find(old))) t.name = spec.Name;
            if (!t)
            {
                t = new GameObject(spec.Name).transform;
                t.SetParent(grid.transform, false);
            }
            // (not "??": in the editor a missing component comes back as a stand-in that isn't null)
            var map = t.GetComponent<Tilemap>();
            if (!map) map = t.gameObject.AddComponent<Tilemap>();
            var r = t.GetComponent<TilemapRenderer>();
            if (!r) r = t.gameObject.AddComponent<TilemapRenderer>();
            r.sortingLayerName = spec.Sorting;
            r.sortingOrder = spec.Order;
            r.mode = spec.Individual ? TilemapRenderer.Mode.Individual : TilemapRenderer.Mode.Chunk;
            if (spec.Tint is Color c) map.color = c;
            t.SetSiblingIndex(i);
            maps[spec.Name] = map;
        }
        return maps;
    }

    /// <summary>Where a tilemap's name comes in the game's layer order (Ground0 first), or -1.</summary>
    public static int OrderOf(string tilemapName)
    {
        for (int i = 0; i < Specs.Length; i++)
            if (string.Equals(Specs[i].Name, tilemapName, System.StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The Tile Palette's "Active Tilemap" list in the game's layer order (Ground0, Ground1, Ground2, Water,
    /// Bridge, Wall, WallCover, Fence, Fog, then the hidden ones; other tilemaps after them, A-Z), turned on once
    /// (Preferences > 2D > Tile Palette > Active Targets Sorting Mode: "EmbervilleRoomLayerOrder").</summary>
    const string SortingPref = "TilePalette.ActiveTargetsSortingMode";

    /// <summary>A room made on another build of the game than the one installed: said when it's opened (its art may
    /// differ from what this build has).</summary>
    [InitializeOnLoadMethod]
    static void WarnOtherBuild() => EditorSceneManager.sceneOpened += (scene, _) =>
    {
        var info = scene.GetRootGameObjects().Select(g => g.GetComponent<EmbervilleRoomInfo>()).FirstOrDefault(i => i);
        var now = EmbervilleGameArt.GameBuild();
        if (info && !string.IsNullOrEmpty(info.gameBuild) && now != "unknown" && info.gameBuild != now)
            Debug.LogWarning($"Emberville: \"{scene.name}\" was made on game build {info.gameBuild}; this game is build {now}. Its art may differ: extract the game again and use Emberville > Re-file Game Art if anything looks wrong.");
    };

    [InitializeOnLoadMethod]
    static void UseRoomLayerOrder()
    {
        if (EditorPrefs.GetString(SortingPref, "None") == "None") EditorPrefs.SetString(SortingPref, typeof(EmbervilleRoomLayerOrder).FullName);
    }

    /// <summary>The room's groups, in the order the game's scenes have them under a place (NPCHouseDes_TinetiaAdeos:
    /// == Tilemap, == Props, == Doors, == Navmesh, == Lights; we build no navmesh, the game makes it; and our
    /// teleporters, stairs and exits go in == Teleporters, as the game's scenes keep theirs).</summary>
    static readonly string[] Groups = { "== Tilemap", "== Props", "== Doors", "== Teleporters", "== Lights" };

    static readonly System.Text.RegularExpressions.Regex MarkerKind =
        new System.Text.RegularExpressions.Regex(@"^\s*(Breakable|Prop|Door|Lever|Plate|Teleport|Arrive|Stairs|Exit)\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The scene laid out the way the game lays out a place: one root named after the room, the Grid as its
    /// "== Tilemap", and the markers sorted into "== Props", "== Doors", "== Teleporters", lights into "== Lights"
    /// (PlayerStart stays on the root). Things it doesn't know are left where they are. Returns the root.</summary>
    public static GameObject Organize(UnityEngine.SceneManagement.Scene scene, string roomName)
    {
        var tops = scene.GetRootGameObjects();
        var root = tops.FirstOrDefault(g => g.transform.Find("== Tilemap")) ?? tops.FirstOrDefault(g => g.name == roomName);
        if (!root) { root = new GameObject(roomName); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene); }
        root.transform.SetSiblingIndex(0);
        // which build of the game the room is made on (opening it on another build warns)
        var info = root.GetComponent<EmbervilleRoomInfo>();
        if (!info) info = root.AddComponent<EmbervilleRoomInfo>();
        var build = EmbervilleGameArt.GameBuild();
        if (build != "unknown") info.gameBuild = build;
        Transform Group(string name, int index)
        {
            var g = root.transform.Find(name);
            if (!g) { g = new GameObject(name).transform; g.SetParent(root.transform, false); }
            g.SetSiblingIndex(index);
            return g;
        }
        // the Grid is the "== Tilemap" (as in the game's scenes)
        var grid = root.GetComponentInChildren<Grid>(true);
        if (!grid) grid = tops.Select(t => t.GetComponentInChildren<Grid>(true)).FirstOrDefault(g => g);
        if (grid)
        {
            var old = root.transform.Find("== Tilemap");
            if (old && old != grid.transform && old.childCount == 0 && !old.GetComponent<Grid>()) Object.DestroyImmediate(old.gameObject);
            grid.name = "== Tilemap";
            grid.transform.SetParent(root.transform, true);
            grid.transform.SetSiblingIndex(0);
        }
        var groups = Groups.Select((n, i) => n == "== Tilemap" ? null : Group(n, i)).ToArray();
        Transform props = groups[1], doors = groups[2], teleporters = groups[3], lights = groups[4];
        // the game lights its places with a global light of its own; in the editor the room needs one too, or the first
        // prop with a light (a candle, a torch) leaves everything else black. The game drops it (it has its own).
        bool global = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<UnityEngine.Rendering.Universal.Light2D>(true))
            .Any(l => l.lightType == UnityEngine.Rendering.Universal.Light2D.LightType.Global);
        if (!global)
        {
            var go = new GameObject("Global Light 2D (editor only)");
            go.transform.SetParent(lights, false);
            var l = go.AddComponent<UnityEngine.Rendering.Universal.Light2D>();
            l.lightType = UnityEngine.Rendering.Universal.Light2D.LightType.Global;
            l.intensity = 1f;
        }
        foreach (var t in scene.GetRootGameObjects().Concat(root.GetComponentsInChildren<Transform>(true).Where(x => x.parent == root.transform).Select(x => x.gameObject)).Distinct().ToList())
        {
            if (!t || t == root || Groups.Contains(t.name) || t.GetComponent<Camera>() || t.GetComponent<UnityEngine.EventSystems.EventSystem>()) continue;
            var light = t.GetComponent<UnityEngine.Rendering.Universal.Light2D>();
            if (light) { if (light.lightType != UnityEngine.Rendering.Universal.Light2D.LightType.Global) t.transform.SetParent(lights, true); continue; }
            if (t.name == "PlayerStart") { t.transform.SetParent(root.transform, true); continue; }
            var m = MarkerKind.Match(t.name);
            if (!m.Success) continue;
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "breakable": case "prop": t.transform.SetParent(props, true); break;
                case "door": case "lever": case "plate": t.transform.SetParent(doors, true); break;
                default: t.transform.SetParent(teleporters, true); break;
            }
        }
        return root;
    }

    [MenuItem("Emberville/Update Room Layers")]
    public static void UpdateOpenRoom()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var grids = Object.FindObjectsByType<Grid>(FindObjectsSortMode.None).Where(g => g.GetComponentInChildren<Tilemap>(true)).ToList();
        if (grids.Count == 0) { Debug.LogWarning("Emberville: no Grid with tilemaps in the open scene"); return; }
        foreach (var g in grids) EnsureLayers(g.gameObject);
        var root = Organize(scene, string.IsNullOrEmpty(scene.name) ? "Room" : scene.name);
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"Emberville: \"{root.name}\" now has the game's room layers and layout (save the scene to keep it)");
    }
}

/// <summary>The Tile Palette's "Active Tilemap" list: each room's tilemaps together, in the game's layer order (Ground0,
/// Ground1, Ground2 next to each other), other tilemaps after them, A-Z.</summary>
[UnityEditor.Tilemaps.GridPaintSorting]
public class EmbervilleRoomLayerOrder : IComparer<GameObject>
{
    public int Compare(GameObject a, GameObject b)
    {
        if (a == b) return 0;
        if (!a || !b) return a ? -1 : 1;
        // (rooms side by side in one scene: each room's together)
        var ga = a.GetComponentInParent<Grid>(true); var gb = b.GetComponentInParent<Grid>(true);
        if (ga != gb && ga && gb) return string.CompareOrdinal(ga.transform.root.name + ga.GetInstanceID(), gb.transform.root.name + gb.GetInstanceID());
        int oa = EmbervilleRoomLayers.OrderOf(a.name), ob = EmbervilleRoomLayers.OrderOf(b.name);
        if (oa >= 0 && ob >= 0) return oa.CompareTo(ob);
        if (oa >= 0 || ob >= 0) return oa >= 0 ? -1 : 1;
        return string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase);
    }
}

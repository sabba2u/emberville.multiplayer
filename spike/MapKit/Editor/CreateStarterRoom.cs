// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/CreateStarterRoom.cs (Unity 6000.1.17f1,
// Universal 2D template). It adds the menu "Emberville > Create Starter Room", which makes, under Assets/Emberville:
//   Tiles/      the placeholder tiles, each a plain colour: stone_floor, stone_wall, water, bridge (the game swaps in its own)
//   Emberville  a tile palette with them (Window > 2D > Tile Palette)
//   MyRoom      a scene: a Grid with Ground / Water / Bridge / Walls tilemaps, a sample room painted on them (a river
//               with a bridge, a wall with two doors), and the markers the game turns into its own things:
//               PlayerStart, "Breakable: Barrel", "Door: Vault" + "Lever: -> Vault", "Door: Gate" + "Plate: -> Gate",
//               a teleporter pair "Teleport: Blue", "Stairs: down -> cellar", "Exit: back". AssetBundle "myroom".
//   Cellar      the room the stairs go down to: "Arrive: top" by "Stairs: up", and an island in a pond you only reach by
//               a teleporter pair ("Teleport: Island", one on the shore, one on the island). AssetBundle "cellar".
// Then: Emberville > Build Bundles, and in the game /room myroom. Paint over it, move the markers, rename them: see README.
// Running it again rebuilds the tiles and the palette, and asks before replacing the two scenes.
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Tilemaps;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

public static class EmbervilleCreateStarterRoom
{
    const string Root = "Assets/Emberville", TileDir = Root + "/Tiles", ScenePath = Root + "/MyRoom.unity", CellarPath = Root + "/Cellar.unity";
    const int Px = 32;   // pixels a tile (the game's art is 32 a tile; one tile = one unit)

    [MenuItem("Emberville/Create Starter Room")]
    public static void Create()
    {
        if ((File.Exists(ScenePath) || File.Exists(CellarPath)) && !EditorUtility.DisplayDialog("Emberville", ScenePath + " / " + CellarPath + " are already there. Replace them with fresh starter rooms?", "Replace", "Keep mine"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Directory.CreateDirectory(TileDir);

        var floor = MakeTile("stone_floor", new Color32(122, 116, 108, 255), new Color32(104, 98, 92, 255));
        var wall = MakeTile("stone_wall", new Color32(58, 52, 60, 255), new Color32(40, 36, 44, 255));
        var water = MakeTile("water", new Color32(52, 104, 168, 255), new Color32(70, 128, 196, 255));
        var bridge = MakeTile("bridge", new Color32(140, 96, 56, 255), new Color32(110, 72, 40, 255));
        MakeSprite("marker", 8, new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), 8);
        MakePalette(floor, wall, water, bridge);

        AssetDatabase.SaveAssets();   // (the tiles on disk: a new scene unloads what we're holding)
        Cellar();
        var grid = MyRoom();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = grid;
        Debug.Log("Emberville: made " + ScenePath + " (AssetBundle \"myroom\") and " + CellarPath + " (\"cellar\"). Paint on Ground / Water / Bridge / Walls with the Emberville palette, then Emberville > Build Bundles; in the game /room myroom.");
    }

    struct Tiles { public Tile floor, wall, water, bridge; public Sprite marker; }

    /// <summary>The tiles, loaded fresh from disk (after a new scene is opened: it unloads the ones loaded before).</summary>
    static Tiles Load() => new Tiles
    {
        floor = AssetDatabase.LoadAssetAtPath<Tile>(TileDir + "/stone_floor.asset"),
        wall = AssetDatabase.LoadAssetAtPath<Tile>(TileDir + "/stone_wall.asset"),
        water = AssetDatabase.LoadAssetAtPath<Tile>(TileDir + "/water.asset"),
        bridge = AssetDatabase.LoadAssetAtPath<Tile>(TileDir + "/bridge.asset"),
        marker = AssetDatabase.LoadAssetAtPath<Sprite>(TileDir + "/marker.png"),
    };

    /// <summary>The first room: 24 x 16 cells, x 0..23, y 0..15.
    ///   left: where you arrive; a river (x 8..9) with a bridge across it (y 7..8); a teleporter pair across the river
    ///   a wall at x 17 with two doorways: the Vault (y 10..11, a lever opens it) and the Gate (y 3..4, a floor plate)
    ///   right: the Vault room (top, the exit back) and the Gate room (bottom, a barrel and the stairs down), split at y 7</summary>
    static GameObject MyRoom()
    {
        var (scene, grid, ground, waterMap, bridgeMap, walls) = NewRoom();
        var t = Load();
        for (int x = 0; x < 24; x++)
            for (int y = 0; y < 16; y++)
            {
                var c = new Vector3Int(x, y, 0);
                bool outer = x == 0 || x == 23 || y == 0 || y == 15;
                bool split = x == 17 && !(y == 3 || y == 4 || y == 10 || y == 11);
                bool shelf = y == 7 && x > 17;
                if (outer || split || shelf) { walls.SetTile(c, t.wall); continue; }
                ground.SetTile(c, t.floor);
                if (x == 8 || x == 9)
                {
                    waterMap.SetTile(c, t.water);
                    if (y == 7 || y == 8) bridgeMap.SetTile(c, t.bridge);
                }
            }

        // markers: the cell's centre is (x + 0.5, y + 0.5); a doorway's two cells meet on a whole number
        Marker(t.marker, "PlayerStart", 3.5f, 7.5f, Start);
        Marker(t.marker, "Breakable: Barrel", 5.5f, 12.5f, Thing);
        Marker(t.marker, "Teleport: Blue", 3.5f, 2.5f, Teleport);
        Marker(t.marker, "Teleport: Blue", 13.5f, 2.5f, Teleport);
        DoorMarker(t.marker, "Vault", 17.5f, 10.5f);   // (either tile of the doorway: the door fills both)
        Marker(t.marker, "Lever: -> Vault", 14.5f, 13.5f, Switch);
        DoorMarker(t.marker, "Gate", 17.5f, 3.5f);
        Marker(t.marker, "Plate: -> Gate", 14.5f, 5.5f, Switch);
        Marker(t.marker, "Exit: back", 20.5f, 11.5f, Exit);
        Marker(t.marker, "Breakable: Barrel", 19.5f, 2.5f, Thing);
        Marker(t.marker, "Stairs: down -> cellar", 21.5f, 4.5f, Stairs);
        Save(scene, ScenePath, "myroom", new Vector3(12f, 8f, -10f), 9f);
        return grid;
    }

    /// <summary>The room down the stairs: 16 x 11 cells. Stairs back up by where you arrive; a pond (x 7..13, y 2..8) with
    /// an island in it (x 9..11, y 4..6) you reach by teleporter, and a barrel on it.</summary>
    static void Cellar()
    {
        var (scene, _, ground, waterMap, _, walls) = NewRoom();
        var t = Load();
        for (int x = 0; x < 16; x++)
            for (int y = 0; y < 11; y++)
            {
                var c = new Vector3Int(x, y, 0);
                if (x == 0 || x == 15 || y == 0 || y == 10) { walls.SetTile(c, t.wall); continue; }
                ground.SetTile(c, t.floor);
                bool pond = x >= 7 && x <= 13 && y >= 2 && y <= 8, island = x >= 9 && x <= 11 && y >= 4 && y <= 6;
                if (pond && !island) waterMap.SetTile(c, t.water);
            }
        Marker(t.marker, "Stairs: up", 2.5f, 8.5f, Stairs);
        Marker(t.marker, "Arrive: top", 3.5f, 6.5f, Start);
        Marker(t.marker, "Teleport: Island", 4.5f, 2.5f, Teleport);
        Marker(t.marker, "Teleport: Island", 9.5f, 4.5f, Teleport);
        Marker(t.marker, "Breakable: Barrel", 11.5f, 6.5f, Thing);
        Save(scene, CellarPath, "cellar", new Vector3(8f, 5.5f, -10f), 7f);
    }

    static readonly Color Start = new Color(0.3f, 1f, 0.4f), Thing = new Color(0.8f, 0.5f, 0.2f), Door = new Color(1f, 0.85f, 0.2f),
        Switch = new Color(1f, 0.6f, 0.1f), Exit = new Color(0.4f, 0.75f, 1f), Teleport = new Color(0.75f, 0.45f, 1f), Stairs = new Color(0.25f, 0.2f, 0.3f);

    /// <summary>A new scene with a Grid and the game's room layers (EmbervilleRoomLayers): returns its floor, water,
    /// bridge and wall-face tilemaps.</summary>
    static (Scene, GameObject, Tilemap, Tilemap, Tilemap, Tilemap) NewRoom()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var grid = new GameObject("Grid");
        grid.AddComponent<Grid>().cellSize = new Vector3(1f, 1f, 0f);
        var maps = EmbervilleRoomLayers.EnsureLayers(grid);
        return (scene, grid, maps["Ground0"], maps["Water"], maps["Bridge"], maps["Wall"]);
    }

    /// <summary>Saved, with its AssetBundle name, the template's camera looking at the room.</summary>
    static void Save(Scene scene, string path, string bundleName, Vector3 camAt, float camSize)
    {
        var cam = Camera.main;
        if (cam) { cam.transform.position = camAt; cam.orthographic = true; cam.orthographicSize = camSize; }
        EmbervilleRoomLayers.Organize(scene, Path.GetFileNameWithoutExtension(path));   // (laid out like the game's places: == Tilemap, == Props, == Doors...)
        EditorSceneManager.SaveScene(scene, path);
        var importer = AssetImporter.GetAtPath(path);
        if (importer) importer.assetBundleName = bundleName;
    }

    // ---- tiles ----

    /// <summary>A placeholder tile: a PNG (a plain colour, a darker rim so the cells show), its sprite, and a Tile asset
    /// of the same name (the name is what the game reads).</summary>
    static Tile MakeTile(string name, Color32 fill, Color32 rim)
    {
        var sprite = MakeSprite(name, Px, fill, rim, Px);
        string path = TileDir + "/" + name + ".asset";
        var tile = AssetDatabase.LoadAssetAtPath<Tile>(path);
        if (!tile)
        {
            tile = ScriptableObject.CreateInstance<Tile>();
            AssetDatabase.CreateAsset(tile, path);
        }
        tile.sprite = sprite;
        tile.color = Color.white;
        tile.colliderType = Tile.ColliderType.None;
        EditorUtility.SetDirty(tile);
        return tile;
    }

    static Sprite MakeSprite(string name, int size, Color32 fill, Color32 rim, int pixelsPerUnit)
    {
        string path = TileDir + "/" + name + ".png";
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1 ? rim : fill;
        tex.SetPixels32(px);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.spritePixelsPerUnit = pixelsPerUnit;
        ti.filterMode = FilterMode.Point;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.mipmapEnabled = false;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    /// <summary>The "Emberville" tile palette with the four tiles in a row (best effort: without it, drag the tiles from
    /// Assets/Emberville/Tiles into a palette of your own).</summary>
    static void MakePalette(params TileBase[] tiles)
    {
        try
        {
            string path = Root + "/Emberville.prefab";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var made = GridPaletteUtility.CreateNewPalette(Root, "Emberville", GridLayout.CellLayout.Rectangle,
                GridPalette.CellSizing.Automatic, Vector3.one, GridLayout.CellSwizzle.XYZ);
            path = AssetDatabase.GetAssetPath(made);
            var contents = PrefabUtility.LoadPrefabContents(path);
            var map = contents.GetComponentInChildren<Tilemap>();
            for (int i = 0; i < tiles.Length; i++) map.SetTile(new Vector3Int(i, 0, 0), tiles[i]);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
            PrefabUtility.UnloadPrefabContents(contents);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Emberville: couldn't make the tile palette (" + e.Message + "). Make one yourself: Window > 2D > Tile Palette > Create New Palette, and drag the tiles from " + TileDir + " into it.");
        }
    }

    // ---- the scene ----

    /// <summary>A marker: an empty object named for what it becomes, with a small coloured square so you can see it
    /// (hidden in the game).</summary>
    static GameObject Marker(Sprite sprite, string name, float x, float y, Color color)
    {
        var go = new GameObject(name);
        go.transform.position = new Vector3(x, y, 0f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = 10;
        go.transform.localScale = new Vector3(0.6f, 0.6f, 1f);
        return go;
    }

    /// <summary>A door marker in one of its doorway's tiles, with the Door tick boxes (EmbervilleDoor) on it.</summary>
    static void DoorMarker(Sprite sprite, string doorName, float x, float y)
    {
        var d = Marker(sprite, "Door: " + doorName, x, y, Door).AddComponent<EmbervilleDoor>();
        d.doorName = doorName;
        d.gameObject.name = d.Compose();
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using EmbervilleGhost.Core;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace EmbervilleGhost;

/// <summary>
/// A room made in Unity, played in the game: a scene packed in an asset bundle (Unity 6000.1.17f1, URP 2D; see
/// spike/MapKit), loaded on top of the scene you're in and walked into like the boss arena: /room &lt;name&gt; (the bundle
/// BepInEx/plugins/EmbervilleGhost/Maps/&lt;name&gt;), /room leave.
///
/// The layout comes from placeholder tiles, by name, on any tilemap: "stone_floor" (floor), "stone_wall" (wall),
/// "water", "bridge". It's built far off the map as the dungeon rooms are built: the game's own floor, wall and water
/// tiles (taken from its dungeon rooms; the bridge's art from a room that has one), walls and water you can't walk
/// through, bridges you can (the water's collision cut away under them: the rooms' "RemoveWater"), camera bounds.
/// Marker objects, by name, become the game's own things:
///   "PlayerStart"                         where you arrive
///   "Breakable: Barrel" (Crate, Vase...)  the game's breakable of that name
///   "Door: Vault", "Door: Vault (locked: Rusty Key)"   its gate door (closed / needing that item); never saved to your world
///   "Lever: -> Vault"                     a lever: on opens Vault, off closes it
///   "Plate: -> Vault"                     a floor plate: held down opens Vault
///   "Exit: back", "Exit: -> otherroom"    a pad: back where you came from / into another room
/// Anything else (your own sprites and tiles) comes along, drawn on the game's layers. The scene's camera, audio
/// listener, global light and event system (the template's) are dropped: the game has its own.
/// </summary>
internal static class CustomRoom
{
    /// <summary>Where a room is built: two spots far off the map, taken in turn (the next room loads beside the one you're in).</summary>
    static Vector3 Origin => new Vector3(-4000f - slot * 600f, -4000f, 0f);
    static int slot;
    const string Floor = "stone_floor", Wall = "stone_wall", Water = "water", Bridge = "bridge", Start = "PlayerStart";

    static AssetBundle bundle;
    static Scene scene;
    static GameObject root;
    static GameArea area, returnArea;
    static Vector3 returnPos;
    static bool busy;
    static string current;

    public static bool Active => root && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.CurrentArea == area;

    static string Folder => Path.Combine(Path.GetDirectoryName(typeof(CustomRoom).Assembly.Location), "Maps");

    /// <summary>/room name, /room leave, /room (the list).</summary>
    public static void Command(string args)
    {
        args = (args ?? "").Trim();
        if (args == "leave") { Leave(); return; }
        if (args.Length == 0)
        {
            var list = Directory.Exists(Folder) ? Directory.GetFiles(Folder).Where(f => !f.EndsWith(".manifest")).Select(Path.GetFileName).ToList() : new List<string>();
            Chat.System(list.Count == 0 ? $"No rooms yet: put a bundle in {Folder}" : "Rooms: " + string.Join(", ", list) + "   (/room name, /room leave)");
            return;
        }
        Enter(args).Forget();
    }

    /// <summary>Into the room called name: arriving at its "Arrive: arriveAt" marker if there is one, at the spot given,
    /// else at its PlayerStart. From another room, the old one goes once you're out of it.</summary>
    static async UniTaskVoid Enter(string name, string arriveAt = null, Vector3? arriveSpot = null)
    {
        if (busy || Dungeon.Active || Arena.Active || !LocalPlayer.Get() || !Singleton<GameManager>.HasMain) return;
        string path = Path.Combine(Folder, name);
        if (!File.Exists(path) && File.Exists(path + ".bundle")) path += ".bundle";
        if (!File.Exists(path)) { NetLog.Warn($"No room called {name} in {Folder}", toast: true); return; }
        busy = true;
        bool fromRoom = Active;   // (stairs or an exit into another room: keep where we first came from)
        var old = (root, scene, bundle);
        string oldName = current;
        try
        {
            var gm = Singleton<GameManager>.main;
            if (!fromRoom) { returnPos = LocalPlayer.Get().transform.position; returnArea = gm.CurrentArea; below.Clear(); }
            if (name.Equals(current, System.StringComparison.OrdinalIgnoreCase) || !fromRoom) { Unload(); old = default; }   // (one bundle can't load twice)
            root = null; scene = default; bundle = null;
            slot = 1 - slot;   // (built beside the room you're in, not on top of it)
            var req = AssetBundle.LoadFromFileAsync(path);
            while (!req.isDone) await UniTask.Yield();
            bundle = req.assetBundle;
            if (!bundle) throw new System.Exception("not an asset bundle (or built with another Unity version than 6000.1.17f1)");
            var scenePath = bundle.GetAllScenePaths().FirstOrDefault();
            if (scenePath == null) throw new System.Exception("the bundle has no scene in it");
            var load = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Additive);
            while (!load.isDone) await UniTask.Yield();
            scene = SceneManager.GetSceneByPath(scenePath);
            current = name;
            var start = Build(scene, arriveAt);
            if (arriveSpot is Vector3 spot) start = spot;
            await gm.TeleportPlayerFade(start, Direction.Down, area, area.CameraConfiner);
            Unload(old);
            if (!fromRoom) NetLog.Info($"The room {name}: /room leave to go back", toast: true);
        }
        catch (System.Exception e)
        {
            NetLog.Warn($"Room {name}: {e.Message}", toast: true);
            Debug.LogException(e);
            Unload();
            if (old.root) { (root, scene, bundle) = old; area = root.GetComponent<GameArea>(); current = oldName; slot = 1 - slot; }   // (still standing in it)
        }
        busy = false;
    }

    public static void Leave()
    {
        if (!Active || busy) return;
        busy = true;
        below.Clear();
        var gm = Singleton<GameManager>.main;
        if (!returnArea) { busy = false; Unload(); return; }
        gm.TeleportPlayerFade(returnPos, Direction.Down, returnArea, returnArea.CameraConfiner).ContinueWith(() => { busy = false; Unload(); }).Forget();
    }

    static void Unload()
    {
        Unload((root, scene, bundle));
        root = null; area = null; scene = default; bundle = null; current = null;
    }

    static void Unload((GameObject root, Scene scene, AssetBundle bundle) r)
    {
        if (r.root) Object.Destroy(r.root);
        if (r.scene.IsValid() && r.scene.isLoaded) SceneManager.UnloadSceneAsync(r.scene);
        if (r.bundle) r.bundle.Unload(true);
    }

    // ---- stairs ----

    /// <summary>The rooms above you, each with where its stairs down are (for "Stairs: up").</summary>
    static readonly List<(string room, Vector3 at)> below = new();

    static void Down(string to, Vector3 stairs)
    {
        if (string.Equals(to, "dungeon", System.StringComparison.OrdinalIgnoreCase)) { NetLog.Warn("Room: stairs into the game's dungeon aren't in yet", toast: true); return; }
        below.Add((current, stairs));
        Enter(to, "top").Forget();
    }

    static void Up()
    {
        if (below.Count == 0) { Leave(); return; }   // (the first room's stairs up: out)
        var (room, at) = below[below.Count - 1];
        below.RemoveAt(below.Count - 1);
        Enter(room, null, at).Forget();
    }

    // ---- teleporting: the game's own animation, no fade ----

    static bool blinking;
    static TilemapRenderer floorLike;   // (the room's floor: pads drawn just above it)

    /// <summary>Gone in the game's teleport flash, back at the spot in it: the player held still and unhurt meanwhile, the
    /// camera snapped there (not slid).</summary>
    static async UniTaskVoid Blink(Vector3 to)
    {
        var gm = Singleton<GameManager>.main;
        var p = gm ? gm.player : null;
        if (blinking || !p) return;
        blinking = true;
        Pad.DisarmAll();
        p.AddStaticStack(typeof(CustomRoom));
        p.Invincible = true;
        try
        {
            p.sfxComponent.PlaySfx("Teleport");
            UniTask.Delay(300).ContinueWith(() => { if (p) p.ShowPlayer(false); }).Forget();
            await p.PlayExtraVFXAsync("Teleport");
            gm.PlacePlayerAt(to, Direction.Down);
            Bus<CamEvent>.Invoke(new CamEvent { CamEventType = CamEventType.SetPosition, Position = to });
            Pad.DisarmAll();
            p.sfxComponent.PlaySfx("Teleport");
            UniTask.Delay(300).ContinueWith(() => { if (p) p.ShowPlayer(true); }).Forget();
            await p.PlayExtraVFXAsync("Teleport");
        }
        catch (System.Exception e) { NetLog.Warn($"Room: teleport: {e.Message}", toast: false); if (p) { p.ShowPlayer(true); gm.PlacePlayerAt(to, Direction.Down); } }
        finally
        {
            if (p) { p.RemoveStaticStack(typeof(CustomRoom)); p.Invincible = false; }
            blinking = false;
        }
    }

    // ---- the game's tiles ----

    /// <summary>The first dungeon-room tile layer of this kind: its most used tile and its renderer.</summary>
    static (TileBase tile, TilemapRenderer r) LayerTile(TilemapMergeLayer layer)
    {
        Dungeon.AllSettings();
        foreach (var chunk in Resources.FindObjectsOfTypeAll<DungeonChunk>().Where(c => c && !c.gameObject.scene.IsValid()).OrderBy(c => c.name))
        {
            if (chunk.Tilemaps == null) continue;
            foreach (var t in chunk.Tilemaps)
                if (t.Tilemap && t.Layer == layer && Arena.MostUsed(t.Tilemap) is TileBase tile) return (tile, t.Tilemap.GetComponent<TilemapRenderer>());
        }
        return (null, null);
    }

    /// <summary>A bridge's art: in a room that has a "RemoveWater" layer (where its bridge is), the tile most drawn there
    /// on its other layers.</summary>
    static (TileBase tile, TilemapRenderer r) BridgeTile()
    {
        // a tile called a bridge, anywhere in the rooms
        var buf = new TileBase[512];
        foreach (var chunk in Resources.FindObjectsOfTypeAll<DungeonChunk>().Where(c => c && !c.gameObject.scene.IsValid() && c.Tilemaps != null).OrderBy(c => c.name))
            foreach (var t in chunk.Tilemaps)
            {
                if (!t.Tilemap) continue;
                int n = t.Tilemap.GetUsedTilesNonAlloc(buf);
                for (int i = 0; i < n; i++)
                    if (buf[i] && buf[i].name.IndexOf("bridge", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return (buf[i], t.Tilemap.GetComponent<TilemapRenderer>());
            }
        // else: the tile most drawn where a room's water is cut away
        foreach (var chunk in Resources.FindObjectsOfTypeAll<DungeonChunk>().Where(c => c && !c.gameObject.scene.IsValid() && c.Tilemaps != null).OrderBy(c => c.name))
        {
            var remove = chunk.Tilemaps.FirstOrDefault(t => t.Tilemap && t.Layer == TilemapMergeLayer.RemoveWater).Tilemap;
            if (!remove) continue;
            var cells = new List<Vector3>();
            foreach (var c in remove.cellBounds.allPositionsWithin)
                if (remove.HasTile(c)) cells.Add(remove.CellToWorld(c) + remove.cellSize * 0.5f);
            var count = new Dictionary<TileBase, (int n, TilemapRenderer r)>();
            foreach (var t in chunk.Tilemaps)
            {
                if (!t.Tilemap || t.Layer == TilemapMergeLayer.RemoveWater || t.Layer == TilemapMergeLayer.Water || t.Layer == TilemapMergeLayer.WaterCollision || t.Layer == TilemapMergeLayer.Collision) continue;
                foreach (var w in cells)
                {
                    var tile = t.Tilemap.GetTile(t.Tilemap.WorldToCell(w));
                    if (tile) count[tile] = (count.TryGetValue(tile, out var c) ? c.n + 1 : 1, t.Tilemap.GetComponent<TilemapRenderer>());
                }
            }
            var best = count.OrderByDescending(p => p.Value.n).FirstOrDefault();
            if (best.Key) return (best.Key, best.Value.r);
        }
        return (null, null);
    }

    // ---- building it ----

    /// <summary>The room, far off the map. Returns where you arrive.</summary>
    static Vector3 Build(Scene s, string arriveAt)
    {
        var (groundTile, groundR, wallTile, wallR, cell) = Arena.RoomTiles();
        if (!groundTile) throw new System.Exception("the game's dungeon tiles aren't loaded");
        var (waterTile, waterR) = LayerTile(TilemapMergeLayer.Water);
        var (bridgeTile, bridgeR) = BridgeTile();
        NetLog.Info($"Room: the game's tiles: floor {groundTile.name}, wall {wallTile?.name}, wall top {LayerTile(TilemapMergeLayer.WallCover).tile?.name}, water {(waterTile ? $"{waterTile.name} ({waterTile.GetType().Name})" : "none")}, bridge {(bridgeTile ? bridgeTile.name : "none (your own art, or the floor)")}", toast: false);
        var roots = s.GetRootGameObjects();

        // what the scene template brings that the game already has
        foreach (var go in roots)
        {
            foreach (var c in go.GetComponentsInChildren<Camera>(true)) Object.Destroy(c.gameObject);
            foreach (var l in go.GetComponentsInChildren<AudioListener>(true)) Object.Destroy(l);
            foreach (var l in go.GetComponentsInChildren<UnityEngine.Rendering.Universal.Light2D>(true))
                if (l.lightType == UnityEngine.Rendering.Universal.Light2D.LightType.Global) Object.Destroy(l.gameObject);
            foreach (var es in go.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true)) Object.Destroy(es.gameObject);
        }

        // the layout, from the tilemap each tile is on (RoomLayers: the game's own layers Ground0-2, Water, Wall, WallCover,
        // Fence, Fog and the hidden Collision / RemoveCollision / RemoveWater, plus our Bridge; any other name is only
        // drawn), in cells of the game's grid from Origin. The game's own tiles painted in the editor stay as they are; the
        // kit's placeholders are taken out and the game's art painted in their place
        var floor = new HashSet<Vector3Int>();
        var water = new HashSet<Vector3Int>();
        var bridge = new HashSet<Vector3Int>();
        var solid = new HashSet<Vector3Int>();
        var open = new HashSet<Vector3Int>();      // (RemoveCollision: gaps cut through the solid)
        var dry = new HashSet<Vector3Int>();       // (RemoveWater: water cut away)
        var walls = new HashSet<Vector3Int>();     // (wall faces and tops: doors find their doorways by them)
        var stand = new Dictionary<RoomLayer, HashSet<Vector3Int>>();   // (placeholder cells, by what they stand for)
        var waterMaps = new List<Tilemap>();
        int ox = Mathf.FloorToInt(Origin.x / cell.x), oy = Mathf.FloorToInt(Origin.y / cell.y);
        var (coverTile, coverR) = LayerTile(TilemapMergeLayer.WallCover);
        var fenceR = LayerTile(TilemapMergeLayer.Fence).r;
        var fogR = LayerTile(TilemapMergeLayer.Fog).r;
        foreach (var tm in roots.SelectMany(r => r.GetComponentsInChildren<Tilemap>(true)))
        {
            var mapKind = RoomLayers.Of(tm.name, null);
            if (mapKind == RoomLayer.Water) waterMaps.Add(tm);
            foreach (var c in tm.cellBounds.allPositionsWithin)
            {
                var t = tm.GetTile(c);
                if (!t) continue;
                var at = new Vector3Int(ox + c.x, oy + c.y, 0);
                var kind = RoomLayers.Of(tm.name, t.name);
                if (RoomLayers.IsFloor(kind)) floor.Add(at);
                else if (kind == RoomLayer.Water) water.Add(at);
                else if (kind == RoomLayer.Bridge) bridge.Add(at);
                else if (kind == RoomLayer.RemoveCollision) open.Add(at);
                else if (kind == RoomLayer.RemoveWater) dry.Add(at);
                if (RoomLayers.IsSolid(kind)) solid.Add(at);
                if (kind is RoomLayer.Wall or RoomLayer.WallCover) walls.Add(at);
                if (!RoomLayers.IsPlaceholder(t.name)) continue;
                var standsFor = kind == RoomLayer.WallCover ? RoomLayer.Wall : kind;
                if (!stand.TryGetValue(standsFor, out var mine)) stand[standsFor] = mine = new HashSet<Vector3Int>();
                mine.Add(at);
                tm.SetTile(c, null);
            }
            // drawn like the game's own layer of that kind (its sorting layer, order, material and mode: wall tops and
            // fences sort against the characters); the hidden layers not at all
            var r = tm.GetComponent<TilemapRenderer>();
            if (!r) continue;
            if (RoomLayers.IsHidden(mapKind)) { r.enabled = false; continue; }
            var (like, bump) = mapKind switch
            {
                RoomLayer.Ground0 => (groundR, 0), RoomLayer.Ground1 => (groundR, 1), RoomLayer.Ground2 => (groundR, 2),
                RoomLayer.Water => (waterR ?? groundR, waterR ? 0 : 3), RoomLayer.Bridge => (groundR, 3),
                RoomLayer.Wall => (wallR ?? groundR, wallR ? 0 : 5), RoomLayer.WallCover => (coverR ?? wallR ?? groundR, coverR ? 0 : 6),
                RoomLayer.Fence => (fenceR ?? coverR ?? groundR, fenceR || coverR ? 0 : 6), RoomLayer.Fog => (fogR ?? coverR ?? groundR, fogR ? 0 : 10),
                _ => (groundR, 4),
            };
            if (like) { r.sortingLayerID = like.sortingLayerID; r.sortingOrder = like.sortingOrder + bump; r.sharedMaterial = like.sharedMaterial; r.mode = like.mode; }
        }
        if (floor.Count + water.Count + bridge.Count == 0) throw new System.Exception("no floor in the room (paint the Ground0 tilemap, or use the placeholder tile " + Floor + ")");
        // RemoveWater: as the game does it, the water there goes (drawn and solid)
        if (dry.Count > 0)
            foreach (var tm in waterMaps)
                foreach (var c in tm.cellBounds.allPositionsWithin)
                    if (dry.Contains(new Vector3Int(ox + c.x, oy + c.y, 0))) tm.SetTile(c, null);
        water.ExceptWith(dry);
        // a bridge's water: under it (cut away), so it's walkable there and blocks round it
        water.UnionWith(bridge.Where(b => water.Contains(b) || Next(water, b)));
        solid.ExceptWith(open);
        walls.ExceptWith(open);

        // our root: the game's art for the placeholders, walls, camera bounds; the scene's things moved under it, out to Origin
        root = new GameObject("EG_Room");
        SceneManager.MoveGameObjectToScene(root, s);
        root.SetActive(false);
        area = root.AddComponent<GameArea>();
        var gridGo = new GameObject("Tiles");
        gridGo.transform.SetParent(root.transform, false);
        gridGo.AddComponent<Grid>().cellSize = cell;
        HashSet<Vector3Int> Stand(RoomLayer k) => stand.TryGetValue(k, out var v) ? v : new HashSet<Vector3Int>();
        var pWater = Stand(RoomLayer.Water); var pBridge = Stand(RoomLayer.Bridge);
        HashSet<(int x, int y)> Cells(IEnumerable<Vector3Int> cs) => new HashSet<(int x, int y)>(cs.Select(c => (c.x, c.y)));
        var realGround = floor.Except(Stand(RoomLayer.Ground0));
        var pGround = new HashSet<Vector3Int>(RoomLayers.PlaceholderFloor(Cells(Stand(RoomLayer.Ground0)), Cells(pWater), Cells(pBridge), Cells(realGround))
            .Select(c => new Vector3Int(c.x, c.y, 0)));   // (the shores sit on floor; never a second floor over one painted)
        if (pGround.Count > 0) Arena.Paint(gridGo, "Ground0", pGround, groundTile, groundR, 0);
        // as the game's own rooms do it: the water cut away under a bridge (its banks drawn round the gap by the water's
        // rule tile), the bridge's art on the floor there
        var pWaterDrawn = new HashSet<Vector3Int>(pWater.Where(c => !bridge.Contains(c) && !dry.Contains(c)));
        if (pWaterDrawn.Count > 0) Arena.Paint(gridGo, "Water", pWaterDrawn, waterTile ?? groundTile, waterR ?? groundR, waterR ? 0 : 3);
        if (pBridge.Count > 0) Arena.Paint(gridGo, "Bridge", pBridge, bridgeTile ?? groundTile, groundR, 3);
        floorLike = groundR;
        // placeholder walls as the game's rooms draw them (RoomLayers.SplitWalls, against every wall, painted or not): the
        // face where the wall looks down onto floor, the top (its own layer, joining every way) everywhere else
        var pWalls = Stand(RoomLayer.Wall);
        var (faceCells, _) = RoomLayers.SplitWalls(Cells(walls));
        var faces = coverTile ? new HashSet<Vector3Int>(pWalls.Where(c => faceCells.Contains((c.x, c.y)))) : pWalls;
        if (faces.Count > 0) Arena.Paint(gridGo, "Wall", faces, wallTile ?? groundTile, wallR ?? groundR, wallR ? 0 : 5);
        if (coverTile) { var tops = new HashSet<Vector3Int>(pWalls.Where(c => !faces.Contains(c))); if (tops.Count > 0) Arena.Paint(gridGo, "WallCover", tops, coverTile, coverR ?? wallR ?? groundR, coverR ? 0 : 6); }
        // you walk on floor and bridges; walls, wall tops, fences, painted collision and water (not under a bridge, not
        // removed) block; anything else is only drawn
        var ground = new HashSet<Vector3Int>(floor); ground.UnionWith(water); ground.UnionWith(bridge);
        var walk = new HashSet<Vector3Int>(ground.Where(c => (!water.Contains(c) || bridge.Contains(c)) && !solid.Contains(c)));
        var block = new HashSet<Vector3Int>(solid.Concat(water.Where(c => !bridge.Contains(c))));
        Dungeon.NavLayers(root.transform, cell, walk, block, out _, out _);

        // markers and the rest of the scene, out to Origin
        var markers = new List<(string kind, string arg, Vector3 at, bool art)>();
        Vector3 arrive = new Vector3((float)walk.Average(c => c.x) * cell.x, (float)walk.Average(c => c.y) * cell.y, 0f);
        // one of the room's cells -> one of the game's (for everything in the scene, under the grid or not)
        var grid = roots.Where(r => r && r != root).Select(r => r.GetComponentInChildren<Grid>(true)).FirstOrDefault(g => g);
        float sx = grid && grid.cellSize.x > 0 ? cell.x / grid.cellSize.x : 1f, sy = grid && grid.cellSize.y > 0 ? cell.y / grid.cellSize.y : 1f;
        foreach (var go in roots)
        {
            if (!go || go == root) continue;
            go.transform.position = Origin + new Vector3(go.transform.position.x * sx, go.transform.position.y * sy, 0f);
            go.transform.localScale = new Vector3(go.transform.localScale.x * sx, go.transform.localScale.y * sy, 1f);
            go.transform.SetParent(root.transform, true);
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                var m = Regex.Match(t.name, @"^\s*(Breakable|Door|Lever|Plate|Exit|Teleport|Arrive|Stairs)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (t.name == Start) { arrive = new Vector3(t.position.x, t.position.y, 0f); Hide(t); }
                else if (m.Success) { markers.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), new Vector3(t.position.x, t.position.y, 0f), t.GetComponentsInChildren<Renderer>(true).Any(r => r.transform != t))); Hide(t); }
            }
            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
            {
                // the game's props (placed from the Map Kit's Props window) keep their own sorting (they sort with the
                // characters, as in the game's own places) and take the game's sprite material; anything else of the
                // room's own is drawn just over the floor
                if (InPropGroup(sr.transform))
                {
                    if (!SortingLayer.IsValid(sr.sortingLayerID) && groundR) sr.sortingLayerID = groundR.sortingLayerID;
                    if (groundR) sr.sharedMaterial = groundR.sharedMaterial;   // (the game's plain lit sprite material)
                }
                else if (groundR && sr.enabled) { sr.sortingLayerID = groundR.sortingLayerID; sr.sortingOrder = groundR.sortingOrder + 5; }
            }
        }
        LiveProps(root.transform);
        Place(markers, walls, cell);
        var named = markers.FirstOrDefault(m => m.kind == "arrive" && arriveAt != null && Arrow(m.arg).Equals(arriveAt, System.StringComparison.OrdinalIgnoreCase));
        if (named.kind != null) arrive = named.at;

        // camera bounds: the room and a little round it
        var all = walk.Concat(block).ToList();
        float minX = all.Min(c => c.x) * cell.x, minY = all.Min(c => c.y) * cell.y;
        float maxX = (all.Max(c => c.x) + 1) * cell.x, maxY = (all.Max(c => c.y) + 1) * cell.y;
        var confinerGo = new GameObject("CameraConfiner");
        confinerGo.transform.SetParent(root.transform, false);
        var confiner = confinerGo.AddComponent<PolygonCollider2D>();
        confiner.isTrigger = true;
        var sceneConfiner = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(a => a.CameraConfiner).FirstOrDefault(c => c);
        if (sceneConfiner) confinerGo.layer = sceneConfiner.gameObject.layer;
        const float pad = 4f;
        confiner.points = new[] { new Vector2(minX - pad, minY - pad), new Vector2(maxX + pad, minY - pad), new Vector2(maxX + pad, maxY + pad), new Vector2(minX - pad, maxY + pad) };
        area.cameraConfiner = confiner;
        root.SetActive(true);
        area.Setup();
        NetLog.Info($"Room: {walk.Count} walkable cells, {walls.Count} walls, {water.Count} water, {bridge.Count} bridge, {markers.Count} markers; arriving at {arrive}", toast: false);
        return arrive;
    }

    static bool Next(HashSet<Vector3Int> set, Vector3Int c) =>
        set.Contains(c + Vector3Int.left) || set.Contains(c + Vector3Int.right) || set.Contains(c + Vector3Int.up) || set.Contains(c + Vector3Int.down);

    /// <summary>A marker's own look (its editor icon) off: it's only a spot.</summary>
    static void Hide(Transform t)
    {
        foreach (var r in t.GetComponents<Renderer>()) r.enabled = false;
    }

    // ---- markers -> the game's things ----

    static void Place(List<(string kind, string arg, Vector3 at, bool art)> markers, HashSet<Vector3Int> walls, Vector3 cell)
    {
        var doors = new Dictionary<string, GateDoor>(System.StringComparer.OrdinalIgnoreCase);
        // doors first (the switches name them)
        foreach (var (kind, arg, at, _) in markers.Where(m => m.kind == "door"))
        {
            // "Door: Vault", "Door: Vault (open)", "Door: Vault (locked: Rusty Key)"
            var dm = Regex.Match(arg, @"^([^(]+?)\s*(\((.*)\))?\s*$");
            string name = dm.Groups[1].Value.Trim();
            string key = null; bool open = false;
            foreach (var opt in dm.Groups[3].Value.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0))
            {
                var lm = Regex.Match(opt, @"^locked\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (lm.Success) key = lm.Groups[1].Value.Trim();
                else if (opt.Equals("open", System.StringComparison.OrdinalIgnoreCase)) open = true;
                else if (!opt.Equals("closed", System.StringComparison.OrdinalIgnoreCase)) NetLog.Warn($"Room: door {name}: \"{opt}\"? (open, closed, locked: Item)", toast: false);
            }
            // which way it faces, from the walls round it: walls above and below -> you pass it left to right (the game's
            // "Horizontal" door, seen side-on); walls left and right -> you pass it up and down ("Vertical", seen front-on)
            // and where: in the middle of the opening the marker is in (put it in either of the doorway's tiles)
            var c = new Vector3Int(Mathf.FloorToInt(at.x / cell.x), Mathf.FloorToInt(at.y / cell.y), 0);
            int Run(int dx, int dy) { for (int i = 1; i <= 4; i++) if (walls.Contains(c + new Vector3Int(dx * i, dy * i, 0))) return i; return 0; }
            int up = Run(0, 1), down = Run(0, -1), right = Run(1, 0), left = Run(-1, 0);
            bool upDown = up > 0 && down > 0, leftRight = left > 0 && right > 0;
            if (upDown && leftRight) { if (up + down <= left + right) leftRight = false; else upDown = false; }
            var spot = at;
            int width = 0;
            // (a side-on door stands on its foot: at the bottom of its opening, not the middle)
            if (upDown) { width = up + down - 1; spot = new Vector3((c.x + 0.5f) * cell.x, (c.y - down + 1) * cell.y, 0f); }
            else if (leftRight) { width = left + right - 1; spot = new Vector3((c.x - left + 1 + width / 2f) * cell.x, (c.y + 0.5f) * cell.y, 0f); }
            if (width != 2) NetLog.Warn(width == 0 ? $"Room: door {name} isn't in a doorway (no walls either side of it)" : $"Room: door {name}: its doorway is {width} tiles (the game's doors are 2)", toast: false);
            var facing = upDown ? DoorOrientation.Horizontal : DoorOrientation.Vertical;
            var door = Copy<GateDoor>(DoorTemplate(facing), spot, d =>
            {
                d.saveDoorState = false;   // (never into your world's save: it would be another door's id)
                d.uniqueID = "";
                var item = key != null ? FindItem(key) : null;
                d.unlockRequirements = new List<ItemDataQuantified>();
                if (item) d.unlockRequirements.Add(new ItemDataQuantified { itemData = item, amount = 1 });
                d.state = item ? DoorState.Locked : open ? DoorState.Opened : DoorState.Closed;
                if (d.state == DoorState.Opened) Side(d, facing == DoorOrientation.Horizontal ? Direction.Left : Direction.Down);
                if (key != null && !item) NetLog.Warn($"Room: no item called {key} for the door {name} (it's an ordinary door)", toast: false);
            });
            if (door) doors[name] = door;
        }
        foreach (var (kind, arg, at, art) in markers)
        {
            switch (kind)
            {
                case "breakable":
                    Copy<DamageableObject>(Template<DamageableObject>(arg), at, null);
                    break;
                case "lever":
                {
                    var target = Target(arg, doors);
                    Copy<InteractableLever>(Template<InteractableLever>(null), at, l =>
                    {
                        l.OnTurnOnEvent = new UnityEvent(); l.OnTurnOffEvent = new UnityEvent();
                        if (target) { l.OnTurnOnEvent.AddListener(() => Swing(target, true)); l.OnTurnOffEvent.AddListener(() => Swing(target, false)); }
                    });
                    break;
                }
                case "plate":
                {
                    var target = Target(arg, doors);
                    Copy<GroundButton>(Template<GroundButton>(null), at, p =>
                    {
                        p.onTurnOnEvent = new UnityEvent(); p.onTurnOffEvent = new UnityEvent();
                        if (target) { p.onTurnOnEvent.AddListener(() => Swing(target, true)); p.onTurnOffEvent.AddListener(() => Swing(target, false)); }
                    });
                    break;
                }
                case "exit":
                {
                    string to = Arrow(arg);
                    bool back = to.Length == 0 || to.Equals("back", System.StringComparison.OrdinalIgnoreCase);
                    Pad.Make(at, back ? Pad.Back : Pad.Other, art, back ? Leave : () => Enter(to).Forget(), portal: true);
                    break;
                }
                case "stairs":
                {
                    // "Stairs: up", "Stairs: down -> cellar" (or just "Stairs: -> cellar")
                    var sm = Regex.Match(arg, @"^\s*(up|down)?\s*(->|→)?\s*(.*)$", RegexOptions.IgnoreCase);
                    string to = sm.Groups[3].Value.Trim();
                    bool up = sm.Groups[1].Value.Equals("up", System.StringComparison.OrdinalIgnoreCase) && to.Length == 0;
                    if (!up && to.Length == 0) { NetLog.Warn($"Room: stairs \"{arg}\" go nowhere (Stairs: up, or Stairs: down -> room)", toast: false); break; }
                    var spot = at;
                    Pad.Make(at, Pad.Hole, art, up ? Up : () => Down(to, spot));
                    break;
                }
                case "teleport":
                {
                    // "Teleport: Blue" (two of them: each takes you to the other), "Teleport: -> Blue" (to "Arrive: Blue")
                    bool oneWay = Regex.IsMatch(arg, @"^\s*(->|→)");
                    string name = Arrow(arg);
                    Vector3? dest = null;
                    if (oneWay)
                    {
                        var a = markers.FirstOrDefault(m => m.kind == "arrive" && Arrow(m.arg).Equals(name, System.StringComparison.OrdinalIgnoreCase));
                        if (a.kind != null) dest = a.at;
                    }
                    else
                    {
                        var pair = markers.Where(m => m.kind == "teleport" && !Regex.IsMatch(m.arg, @"^\s*(->|→)") && Arrow(m.arg).Equals(name, System.StringComparison.OrdinalIgnoreCase)).ToList();
                        if (pair.Count == 2) dest = pair[0].at == at ? pair[1].at : pair[0].at;
                        else NetLog.Warn($"Room: {pair.Count} teleporters called {name} (a pair is two)", toast: false);
                    }
                    if (oneWay && dest == null) NetLog.Warn($"Room: no \"Arrive: {name}\" for the teleporter -> {name}", toast: false);
                    if (dest is Vector3 d) Pad.Make(at, NameColour(name), art, () => Blink(d).Forget(), portal: true);
                    break;
                }
            }
        }
    }

    /// <summary>A switch opening (or closing) a door the game's way: its leaves swing away from you (as when you open it
    /// yourself), with its animation and sound.</summary>
    static void Swing(GateDoor d, bool open)
    {
        if (!d) return;
        if (!open) { if (d.state == DoorState.Opened) d.Close(); return; }
        if (d.state == DoorState.Opened) return;
        var me = LocalPlayer.Get();
        var at = me ? me.transform.position : d.transform.position;
        d.relativePosition = d.orientation == DoorOrientation.Horizontal
            ? (at.x > d.transform.position.x ? Direction.Right : Direction.Left)
            : (at.y > d.transform.position.y ? Direction.Up : Direction.Down);
        d.Open();
    }

    /// <summary>Only one side's leaves showing (the door's own Open picks them; a door made open needs it done).</summary>
    static void Side(GateDoor d, Direction side)
    {
        d.relativePosition = side;
        foreach (var o in d.playerDownOrLeftObjects) if (o) o.SetActive(side == Direction.Down || side == Direction.Left);
        foreach (var o in d.playerUpOrRightObjects) if (o) o.SetActive(side == Direction.Up || side == Direction.Right);
    }

    /// <summary>A teleporter's colour from its name: "Blue" is blue (the colour words anywhere in it), anything else a
    /// colour of its own, the same every time.</summary>
    static Color NameColour(string name)
    {
        var words = new (string word, Color c)[]
        {
            ("blue", new Color(0.35f, 0.6f, 1f)), ("red", new Color(1f, 0.3f, 0.3f)), ("green", new Color(0.4f, 1f, 0.45f)),
            ("yellow", new Color(1f, 0.92f, 0.3f)), ("gold", new Color(1f, 0.8f, 0.3f)), ("orange", new Color(1f, 0.6f, 0.25f)),
            ("purple", new Color(0.7f, 0.45f, 1f)), ("violet", new Color(0.7f, 0.45f, 1f)), ("pink", new Color(1f, 0.55f, 0.85f)),
            ("cyan", new Color(0.4f, 0.95f, 1f)), ("teal", new Color(0.3f, 0.85f, 0.8f)), ("white", new Color(0.95f, 0.95f, 1f)),
            ("black", new Color(0.35f, 0.3f, 0.4f)), ("silver", new Color(0.8f, 0.82f, 0.9f)),
        };
        string n = (name ?? "").ToLowerInvariant();
        foreach (var (word, c) in words) if (n.Contains(word)) return c;
        uint h = 2166136261;
        foreach (char ch in n) h = (h ^ ch) * 16777619;
        return Color.HSVToRGB((h % 360) / 360f, 0.55f, 1f);
    }

    static string Arrow(string arg) => Regex.Replace(arg ?? "", @"^\s*(->|→)?\s*", "").Trim();

    static GateDoor Target(string arg, Dictionary<string, GateDoor> doors)
    {
        string name = Arrow(arg);
        if (doors.TryGetValue(name, out var d)) return d;
        NetLog.Warn($"Room: no door called {name} (the switch does nothing)", toast: false);
        return null;
    }

    /// <summary>The game's own thing of this kind (one whose name has the word in it, if given): a prefab if one's loaded,
    /// else one standing in a scene.</summary>
    static T Template<T>(string word) where T : Component
    {
        Dungeon.AllSettings();   // (the dungeon rooms: their gates, levers, barrels...)
        var all = Resources.FindObjectsOfTypeAll<T>().Where(x => x && !x.name.StartsWith("EG_") && (word == null || x.name.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        var t = all.FirstOrDefault(x => !x.gameObject.scene.IsValid()) ?? all.FirstOrDefault();
        if (!t) NetLog.Warn($"Room: the game has no {typeof(T).Name}{(word != null ? " called " + word : "")} loaded here", toast: false);
        return t;
    }

    // ---- props (the Map Kit's Props window: under "== Props" / "== Doors") ----

    static bool InPropGroup(Transform t)
    {
        for (var p = t; p; p = p.parent) if (p.name == "== Props" || p.name == "== Doors") return true;
        return false;
    }

    /// <summary>Every placed prop the game has a loaded prefab of (by its name without the copy number) is swapped for the
    /// game's own (it breaks, burns, drops loot: "live"), set up before it wakes as doors are (a door never touches the
    /// save); the rest stay as placed ("look only"). Never the ones that save to the player's world (chests, buildables).</summary>
    static void LiveProps(Transform room)
    {
        Dungeon.AllSettings();   // (the dungeon rooms and what they hold)
        var game = new Dictionary<string, GameObject>();
        foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>())
            if (g && !g.scene.IsValid() && !g.transform.parent && !g.name.StartsWith("EG_") && !game.ContainsKey(g.name)) game[g.name] = g;
        int live = 0, borrowed = 0, look = 0;
        var placed = room.GetComponentsInChildren<Transform>(true).Where(t => t.parent && (t.parent.name == "== Props" || t.parent.name == "== Doors")).ToList();
        foreach (var mine in placed)
        {
            var name = PropRules.BaseName(mine.name);
            bool own = game.TryGetValue(name, out var real) || game.TryGetValue(mine.name, out real);
            if (own && PropRules.SaveTouching(name, real.GetComponents<MonoBehaviour>().Where(c => c).Select(c => c.GetType().Name))) { look++; continue; }
            if (!own)
            {
                // only in the game's scenes: if it could be hit there, the behaviour of a game breakable we can spawn,
                // with this prop's look
                var from = PropRules.BorrowFrom(name, mine.Find("DamageHitBox"));
                if (from == null || !game.TryGetValue(from, out real)) { look++; continue; }
            }
            var holder = new GameObject("EG_RoomHolder");
            holder.SetActive(false);
            try
            {
                var go = Object.Instantiate(real, holder.transform);
                go.name = name;
                foreach (var d in go.GetComponentsInChildren<GateDoor>(true)) { d.saveDoorState = false; d.uniqueID = ""; }
                go.transform.SetParent(mine.parent, false);
                go.transform.SetPositionAndRotation(mine.position, mine.rotation);
                go.transform.localScale = mine.localScale;   // (flips kept)
                if (!own) Dress(go.transform, mine);
                go.SetActive(true);
                Object.Destroy(mine.gameObject);
                if (own) live++; else borrowed++;
            }
            catch (System.Exception e) { NetLog.Warn($"Room: prop {name}: {e.Message} (kept as placed)", toast: false); look++; }
            finally { Object.Destroy(holder); }
        }
        if (live + borrowed + look > 0) NetLog.Info($"Room: props: {live} live, {borrowed} breakable (the game's barrel or table, their look), {look} look-only", toast: false);
    }

    /// <summary>A borrowed breakable given this prop's look and shape: its own sprites emptied and ours put in its art
    /// (its break hides its art, so ours go with it), our colliders in its "Colliders" (its own switched off), its hit box
    /// made our hit box's shape. Both stand at the same spot, so everything keeps its place.</summary>
    static void Dress(Transform game, Transform mine)
    {
        var art = game.Find("Art");
        var pivot = art ? (art.Find("Pivot") ?? art) : game;
        foreach (var sr in pivot.GetComponentsInChildren<SpriteRenderer>(true)) sr.sprite = null;
        var myArt = mine.Find("Art");
        if (myArt) foreach (var c in myArt.Cast<Transform>().ToList()) c.SetParent(pivot, true);
        var cols = game.Find("Colliders");
        var myCols = mine.Find("Colliders");
        if (cols && myCols)
        {
            foreach (var c in cols.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
            foreach (var c in myCols.Cast<Transform>().ToList()) c.SetParent(cols, true);
        }
        var box = game.Find("DamageHitBox") ? game.Find("DamageHitBox").GetComponent<BoxCollider2D>() : null;
        var myHit = mine.Find("DamageHitBox");
        var myBox = myHit ? myHit.GetComponent<Collider2D>() : null;
        if (box && myBox)
        {
            box.transform.SetPositionAndRotation(myHit.position, myHit.rotation);
            box.transform.localScale = myHit.localScale;
            if (myBox is BoxCollider2D b) { box.offset = b.offset; box.size = b.size; }
            else
            {
                var bounds = myBox.bounds;   // (another shape: its outline's box)
                box.offset = box.transform.InverseTransformPoint(bounds.center);
                var size = box.transform.InverseTransformVector(bounds.size);
                box.size = new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y));
            }
        }
    }

    /// <summary>One of the game's gate doors facing this way (a prefab if one's loaded); else any door, said so.</summary>
    static GateDoor DoorTemplate(DoorOrientation facing)
    {
        Dungeon.AllSettings();
        var all = Resources.FindObjectsOfTypeAll<GateDoor>().Where(d => d && !d.name.StartsWith("EG_")).ToList();
        if (!doorsListed)
        {
            doorsListed = true;
            NetLog.Info("Room: the game's gate doors: " + string.Join(", ", all.Select(d => $"{d.name} ({d.orientation}{(d.gameObject.scene.IsValid() ? ", in " + d.gameObject.scene.name : "")})").Distinct()), toast: false);
        }
        var right = all.Where(d => d.orientation == facing).ToList();
        var t = right.FirstOrDefault(d => !d.gameObject.scene.IsValid()) ?? right.FirstOrDefault();
        if (t) return t;
        NetLog.Warn($"Room: the game has no {facing} gate door loaded here; using another (it may face the wrong way)", toast: false);
        return all.FirstOrDefault(d => !d.gameObject.scene.IsValid()) ?? all.FirstOrDefault();
    }
    static bool doorsListed;

    /// <summary>A copy of it at the spot, set up before it wakes (so a door never reads or writes a save).</summary>
    static T Copy<T>(T template, Vector3 at, System.Action<T> setup) where T : Component
    {
        if (!template) return null;
        var holder = new GameObject("EG_RoomHolder");
        holder.SetActive(false);
        try
        {
            var go = Object.Instantiate(template.gameObject, holder.transform);
            go.name = "EG_Room_" + template.name;
            var c = go.GetComponent<T>();
            setup?.Invoke(c);
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(at.x, at.y, template.transform.position.z);
            go.SetActive(true);
            return c;
        }
        catch (System.Exception e) { NetLog.Warn($"Room: {typeof(T).Name}: {e.Message}", toast: false); return null; }
        finally { Object.Destroy(holder); }
    }

    static ItemData FindItem(string name)
    {
        string want = name.Replace(" ", "").ToLowerInvariant();
        return Resources.FindObjectsOfTypeAll<ItemData>().FirstOrDefault(i => i && i.name.Replace(" ", "").ToLowerInvariant().Contains(want));
    }

    /// <summary>A spot you stand on a moment to go somewhere: exits, stairs, teleporters. A ring (a dark hole for stairs)
    /// unless the marker brings its own art. Armed once you've been away from it, so you never bounce straight back off
    /// the pad you arrive on.</summary>
    sealed class Pad : MonoBehaviour
    {
        public static readonly Color Back = new Color(0.45f, 0.75f, 1f, 0.9f), Other = new Color(1f, 0.75f, 0.3f, 0.9f),
            Teleport = new Color(0.75f, 0.45f, 1f, 0.9f), Hole = new Color(0.08f, 0.06f, 0.1f, 0.85f);
        static readonly List<Pad> all = new();

        System.Action go;
        float stood;
        bool armed;

        public static void Make(Vector3 at, Color color, bool ownArt, System.Action go, bool portal = false)
        {
            var obj = new GameObject("EG_RoomPad");
            obj.transform.SetParent(root.transform, false);
            obj.transform.position = at;
            if (!ownArt && portal) TownPortal.Look(obj.transform, at, color);   // (the town portal's look, in this colour)
            else if (!ownArt)
            {
                var sr = obj.AddComponent<SpriteRenderer>();
                sr.sprite = color == Hole ? Disc() : Ring();
                sr.color = color;
                if (floorLike) { sr.sortingLayerID = floorLike.sortingLayerID; sr.sortingOrder = floorLike.sortingOrder + 6; }
                obj.transform.localScale = new Vector3(1.6f, 0.9f, 1f);
            }
            obj.AddComponent<Pad>().go = go;
        }

        /// <summary>Every pad waits till you've stepped off it again (after a teleport: the one you land on).</summary>
        public static void DisarmAll()
        {
            foreach (var p in all) if (p) { p.armed = false; p.stood = 0f; }
        }

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        void Update()
        {
            var me = LocalPlayer.Get();
            if (!me || busy || blinking) return;
            float d = ((Vector2)(me.transform.position - transform.position)).magnitude;
            if (d > 0.8f) { if (d > 1.6f) armed = true; stood = 0f; return; }
            if (!armed) return;
            stood += Time.deltaTime;
            if (stood < 0.8f) return;
            armed = false; stood = 0f;
            go();
        }

        static Sprite ring, disc;
        static Sprite Ring() => ring ? ring : ring = Circle(3.5f);
        static Sprite Disc() => disc ? disc : disc = Circle(99f);

        static Sprite Circle(float width)
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * n + x] = r <= n / 2f - 0.5f && r >= n / 2f - width ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }
    }
}

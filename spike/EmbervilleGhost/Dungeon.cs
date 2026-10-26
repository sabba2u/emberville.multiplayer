using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using NavMeshPlus.Components;
using NavMeshPlus.Extensions;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace EmbervilleGhost;

/// <summary>
/// The generated dungeons. The demo ships the full game's dungeon generator (DungeonSettings: regions, rooms, borders,
/// sub-dungeons), its 338 room pieces (DungeonChunk prefabs) and biomes, and even generates the world map from it,
/// but not the "DungeonScene" a dungeon is played in nor its renderer prefab (DungeonRegionRenderer). This builds a
/// region ourselves in the current scene, far away from the map: the rooms with their own tiles and props, a wall /
/// water collision layer and a navigation mesh made from the rooms' tiles, a camera box, and the biome's creatures;
/// then the game's own teleport takes us there.
/// Doorways to other regions / sub-dungeons are rerouted here (the game would load the missing scene); an exit with
/// nowhere to go, or the Insert menu's "Leave", brings us back to where we were.
/// Co-op: a dungeon is (settings, seed, region), so everyone builds the same one. Entering tells the others
/// (DungeonOpen); their Insert menu then offers to join.
/// </summary>
internal static class Dungeon
{
    public static Plugin Plugin;

    /// <summary>Where a generated dungeon region is built: well away from every map.</summary>
    static readonly Vector3 Origin = new Vector3(4000f, 4000f, 0f);

    /// <summary>A dungeon: settings, seed, region (-1 = pick one from the seed), and the descent's floor (0 = not a floor).</summary>
    internal record struct Where(string Settings, int Seed, int Region, int Floor = 0);

    static Where? current;                      // the region we're in
    static readonly Stack<Where> parents = new Stack<Where>();   // main regions under the sub-dungeon we're in
    static GameObject root;
    static GameArea area;
    static readonly List<GameObject> spawned = new List<GameObject>();
    static Vector3 returnPos;
    static GameArea returnArea;
    static string returnScene;
    static bool busy;
    static string status = "";

    // cache: the last generated layout (a whole world takes a few seconds)
    static (string settings, int seed) renderKey;
    static DungeonRender render;
    static int renderSprawl;
    /// <summary>The current place's room size (tiles), for the floor's shape report.</summary>
    internal static Vector2Int chunkSize = new Vector2Int(10, 10);

    // the latest invite from someone else
    static (int from, string scene, Where where)? invite;

    // menu state
    static int pick;
    static string seedText = "";
    static int regionPick;

    public static bool Active => current.HasValue && root;

    static float nextMeshCheck;

    /// <summary>
    /// Every second in a generated place: a creature off the navigation mesh (put back where it was last saved after a
    /// death, or spawned at the edge) goes onto the nearest walkable spot, or stands down if there's none near; else
    /// its AI asks the mesh every frame and Unity logs "GetRemainingDistance can only be called on an active agent".
    /// </summary>
    public static void Tick()
    {
        if (Active) Wake();
        if (!(Active || Arena.Active) || busy || Time.unscaledTime < nextMeshCheck) return;
        nextMeshCheck = Time.unscaledTime + 1f;
        int moved = 0, removed = 0;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || e.IsDead || !e.gameObject.activeInHierarchy) continue;
            var agent = e.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (!agent || agent.enabled && agent.isOnNavMesh) continue;
            if (!agent.enabled) agent.enabled = true;   // (an older build switched some off: its AI still asks it)
            var p = e.transform.position;
            if (UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 12f, UnityEngine.AI.NavMesh.AllAreas) && agent.Warp(hit.position)) { moved++; continue; }
            // nowhere to walk near it (put back somewhere off this place after a death): out of the fight, back to its pool
            e.gameObject.SetActive(false);
            removed++;
        }
        if (moved + removed > 0) NetLog.Info($"Creatures off the walking map: {moved} put back on it, {removed} taken out", toast: false);
    }

    public static int Floor => Active ? current.Value.Floor : 0;
    public static Where? Current => current;
    public static GameObject Root => root;

    // the region's floor cells (walkable, world cells) and bounds: for the descent's stairs and the creature scaling
    internal static HashSet<Vector3Int> floorCells = new HashSet<Vector3Int>();
    internal static HashSet<Vector3Int> wallCells = new HashSet<Vector3Int>();
    internal static Vector3 cellSize = Vector3.one;
    internal static Bounds bounds;
    public static bool Busy => busy;

    public static void Init()
    {
        SceneManager.sceneLoaded += (_, _) => { current = null; parents.Clear(); root = null; area = null; spawned.Clear(); sleeping.Clear(); busy = false; };
    }

    // ---- settings ----

    static List<DungeonSettings> all;

    /// <summary>Every DungeonSettings the game has: the world map's own first, then the sub-dungeons.</summary>
    public static List<DungeonSettings> AllSettings()
    {
        if (all != null && all.All(s => s)) return all;
        Resources.Load<AllBiomeSettings>("AllBiomes");   // pulls the biomes, their rooms and the sub-dungeons' settings in
        var main = MainSettings();
        all = Resources.FindObjectsOfTypeAll<DungeonSettings>().Where(s => s && s != main).OrderBy(s => s.name).ToList();
        if (main) all.Insert(0, main);
        NetLog.Info($"Dungeon types: {string.Join(", ", all.Select(x => x.name + (x == main ? " (world map)" : "")))}", toast: false);
        return all;
    }

    internal static DungeonSettings MainSettings() => Singleton<WorldMap>.HasMain ? Singleton<WorldMap>.main.settings : null;

    static DungeonSettings Find(string name) => AllSettings().FirstOrDefault(s => s.name == name);

    // ---- entering / leaving ----

    /// <summary>Generate (or reuse) the layout and take us to a region of it.</summary>
    public static void Enter(string settingsName, int seed, int region, bool announce = true, int floor = 0)
    {
        if (busy) { NetLog.Warn("A dungeon is still being generated...", toast: true); return; }
        var settings = Find(settingsName);
        if (!settings) { NetLog.Warn($"No dungeon settings called {settingsName}", toast: true); return; }
        if (!LocalPlayer.Get() || !Singleton<GameManager>.HasMain) { NetLog.Warn("Load a world first.", toast: true); return; }
        if (!Active)
        {
            returnPos = LocalPlayer.Get().transform.position;
            returnArea = Singleton<GameManager>.main.CurrentArea;
            returnScene = LocalPlayer.Scene;
        }
        Go(new Where(settings.name, seed, region, floor), null, announce).Forget();
    }

    /// <summary>The next floor down (from the descent's stairs).</summary>
    internal static void GoTo(Where where) => Go(where, null, true).Forget();

    /// <summary>The world map's own dungeon, the host's world seed (so it's the layout their world map shows).</summary>
    public static void EnterMain()
    {
        var main = MainSettings();
        if (!main) { NetLog.Warn("This scene has no world map.", toast: true); return; }
        int seed = GameData.HasWorldData && GameData.worldData.MapSeed != 0 ? GameData.worldData.MapSeed : 1;
        Enter(main.name, seed, 0);
    }

    /// <summary>Arrive at this spot next time a floor is built (the town portal's far end), not its usual arrival.</summary>
    internal static void ArriveNextAt(Vector3 at) => placeAt = at;
    static Vector3? placeAt;

    /// <summary>Leave, but to this spot (the town portal's town end), not back where you came in.</summary>
    internal static void LeaveTo(Vector3 at, GameArea to, string scene)
    {
        if (!Active || busy || !to) return;
        returnPos = at; returnArea = to; returnScene = scene;
        Leave();
    }

    public static void Leave()
    {
        if (!Active || busy) return;
        var gm = Singleton<GameManager>.main;
        var back = returnArea;
        var oldRoot = root;
        current = null;
        parents.Clear();
        Delve.Left();
        if (returnScene != LocalPlayer.Scene || !back)
        {
            Cleanup(oldRoot);
            return;
        }
        gm.TeleportPlayerFade(returnPos, Direction.Down, back, back.CameraConfiner).ContinueWith(() => Cleanup(oldRoot)).Forget();
        NetLog.Info("Leaving the dungeon", toast: true);
    }

    static void Cleanup(GameObject oldRoot)
    {
        foreach (var go in spawned) if (go) go.SetActive(false);   // pooled creatures: back to their pool
        spawned.Clear();
        DeathSafety.Forget(oldRoot);   // its bosses (the pooled creatures were only switched off)
        if (oldRoot) UnityEngine.Object.Destroy(oldRoot);
        if (oldRoot == root) { root = null; area = null; }
    }

    static async UniTaskVoid Go(Where where, string arriveAt, bool announce)
    {
        if (busy) return;   // one at a time: a doorway touched again (or a second door) while building would build two
        busy = true;
        status = "Generating...";
        var oldRoot = root;
        DungeonLoot.Preload();
        Loading.Begin(where.Floor > 0 ? $"Descending to floor {where.Floor}" : "Entering the dungeon");
        try
        {
            var settings = Find(where.Settings);
            NetLog.Info($"Generating the dungeon {settings.name}, seed {where.Seed}, region {where.Region}...", toast: true);
            int genSeed = Delve.SeedOf(where);
            int sprawlNow = where.Floor > 0 ? Delve.SprawlLevel(where.Floor) : 0;
            if (render == null || renderKey != (settings.name, genSeed) || renderSprawl != sprawlNow)
            {
                var rng = new SeededSystemRandomProvider(genSeed);
                var ctx = new GenerationContext(rng);
                if (settings != MainSettings()) ctx.NeighborSelection = new UniformNeighborSelection();
                // a descent floor: more side rooms / features than the game's own numbers (put back right after)
                var undo = Sprawl.Apply(settings, where.Floor > 0 ? Delve.SprawlLevel(where.Floor) : 0);
                DungeonModel model;
                Loading.Stage("Shaping the floor", 0.05f, 0.4f, creep: true);   // (one long step: the bar creeps meanwhile)
                GenRandom.Begin(genSeed);   // (its steps that roll Unity's own random number: from the seed too)
                try { model = await settings.CreateDungeon(ctx, Plugin.destroyCancellationToken); }
                finally { undo(); GenRandom.End(); }
                if (model == null) throw new Exception("the generator gave up (no layout)");
                render = model.GetRender(ctx.RNG);
                renderKey = (settings.name, genSeed);
                renderSprawl = sprawlNow;
            }
            if (where.Region < 0) where = where with { Region = (int)((uint)genSeed % (uint)render.Regions.Count) };
            if (where.Region >= render.Regions.Count)
                throw new Exception($"there are only {render.Regions.Count} regions");
            status = "Building...";
            Loading.Stage("Placing the rooms", 0.4f, 0.75f);
            var biome = render.Regions[where.Region].OverrideBiomeSettings ?? render.BiomeSettings ?? settings.BiomeSettings;
            var built = await Build(render, where, biome);

            // where we arrive: the doorway we came through, the region's portal, any doorway, or the middle
            Vector3 at = built.fallback;
            Direction dir = Direction.Down;
            if (arriveAt != null && Teleporter.GlobalTeleporters.TryGetValue(arriveAt, out var tp) && tp && tp.gameObject.scene.IsValid() && tp.transform.IsChildOf(root.transform))
            { at = tp.DestPosition; dir = tp.SpawnDirection; }
            else if (built.entry) { at = built.entry.DestPosition; dir = built.entry.SpawnDirection; }
            if (where.Floor > 0) at = Delve.SetUpFloor(where, at);   // the descent: arrival, stairs down, the way home
            if (placeAt is Vector3 there) { at = there; dir = Direction.Down; placeAt = null; }   // (through a town portal)
            DungeonLoot.Place(root, where, genSeed, at);   // chests
            Loading.Stage("Lighting the torches", 1f, 1f);
            await UniTask.Delay(250, ignoreTimeScale: true);

            current = where;
            // the loading screen goes once the teleport's fade has covered it (no glimpse of where you were)
            await Singleton<GameManager>.main.TeleportPlayerFade(at, dir, area, area.CameraConfiner, null, null, false, Loading.End);
            Loading.End();
            if (oldRoot && oldRoot != root) Cleanup(oldRoot);
            SpawnCreatures(biome, where);
            DebugWalls.Refresh();
            Pings.PingHere();   // where you are, on arrival
            DungeonMap.Built();   // the minimap: this floor, explored as you go
            if (where.Floor > 0) Delve.AfterSpawn(where, biome);   // a floor boss every few floors
            status = "";
            if (where.Floor > 0) NetLog.Success($"Floor {where.Floor} ({settings.name}): creatures {Delve.HealthMul(where.Floor):0.##}x health, {Delve.DamageMul(where.Floor):0.##}x damage. Find the stairs down", toast: true);
            else NetLog.Success($"Dungeon: {settings.name} region {where.Region} of {render.Regions.Count} ({built.rooms} rooms)", toast: true);
            if (announce) Announce(where);
        }
        catch (Exception e)
        {
            placeAt = null;
            Loading.End();
            status = "Failed: " + e.Message;
            NetLog.Warn($"Dungeon: couldn't build it: {e.Message}", toast: true);
            UnityEngine.Debug.LogException(e);
            if (root && root != oldRoot) { DeathSafety.Forget(root); UnityEngine.Object.Destroy(root); }
            root = oldRoot;
            area = oldRoot ? oldRoot.GetComponent<GameArea>() : null;
        }
        finally { busy = false; }
    }

    // ---- the game's loading screen while a place is built ----

    /// <summary>
    /// The game's own loading screen (the bar and spinner between scenes: SceneLoader.loadingProgress), faded in while a
    /// floor is generated and built, the bar moving through the stages; the stage's name drawn under it. The player
    /// stands still meanwhile.
    /// </summary>
    internal static class Loading
    {
        static LoadingProgress bar;
        static float from, to, creepUntil;
        static bool creeping;
        public static string Title = "", StageName = "";
        public static bool On { get; private set; }
        static readonly object holdKey = new object();

        public static void Begin(string title)
        {
            Title = title; StageName = ""; On = true;
            bar = Singleton<SceneLoader>.HasMain ? Singleton<SceneLoader>.main.loadingProgress : null;
            var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
            if (gm && gm.player) gm.player.AddStaticStack(holdKey);
            // as SceneLoader does: fade out, show the panel, fade back in onto it
            Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeOut, 0.35f, delegate
            {
                if (On && bar) bar.Show(true);
                Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeIn, 0.35f, null));
            }));
            Creep().Forget();
        }

        public static void Stage(string name, float start, float end, bool creep = false)
        {
            StageName = name; from = start; to = end;
            Set(start);
            creeping = creep;
            creepUntil = end;
        }

        /// <summary>How far through the current stage (0..1).</summary>
        public static void Fraction(float f) => Set(Mathf.Lerp(from, to, Mathf.Clamp01(f)));

        static float shown;
        static void Set(float v) { shown = Mathf.Max(shown, v); if (bar) bar.UpdateProgress(shown); }

        /// <summary>A long step with no progress of its own: the bar inches on (never past the stage's end).</summary>
        static async UniTaskVoid Creep()
        {
            shown = 0f;
            while (On)
            {
                if (creeping && shown < creepUntil - 0.01f) Set(shown + (creepUntil - shown) * 0.02f);
                await UniTask.Yield();
            }
        }

        public static void End()
        {
            if (!On) return;
            On = false; creeping = false;
            if (bar) bar.Show(false);
            var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
            if (gm && gm.player) gm.player.RemoveStaticStack(holdKey);
        }

        /// <summary>From Hud.Draw: what it's doing, under the game's bar.</summary>
        public static void Draw()
        {
            if (!On) return;
            var vp = ImGuiNET.ImGui.GetMainViewport();
            var dl = ImGuiNET.ImGui.GetForegroundDrawList();
            string line = string.IsNullOrEmpty(StageName) ? Title : $"{Title}: {StageName}...";
            var size = ImGuiNET.ImGui.CalcTextSize(line);
            float x = vp.WorkPos.x + vp.WorkSize.x / 2f - size.x / 2f, y = vp.WorkPos.y + vp.WorkSize.y * 0.62f;
            dl.AddText(new ImGuiNET.Vector2(x, y), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(1f, 0.8f, 0.5f, 1f)), line);
        }
    }

    // ---- building a region ----

    struct Built { public Teleporter entry; public Vector3 fallback; public int rooms; }

    /// <summary>
    /// Some of the game's room pieces have a gathering spot without its trigger (GatheringObject.triggerForward): it throws
    /// as it wakes and can't be gathered anyway. Given the one among its own pieces if it has one; else left off (logged).
    /// </summary>
    static void MendGathering(GameObject root)
    {
        int mended = 0, off = 0;
        foreach (var g in root.GetComponentsInChildren<GatheringObject>(true))
        {
            if (!g || g.triggerForward) continue;
            var trigger = g.GetComponentInChildren<TriggerForward>(true);
            if (trigger) { g.triggerForward = trigger; mended++; }
            else { g.gameObject.SetActive(false); off++; }
        }
        if (mended + off > 0) NetLog.Info($"Dungeon: {mended + off} gathering spot(s) without a trigger in the game's rooms: {mended} given theirs, {off} left off", toast: false);
    }

    static async UniTask<Built> Build(DungeonRender render, Where where, BiomeSettings biome)
    {
        var region = render.Regions[where.Region];
        var chunkSize = biome.ChunkSize;
        Dungeon.chunkSize = chunkSize;

        root = new GameObject($"EG_Dungeon_{where.Settings}_{where.Seed}_{where.Region}");
        root.SetActive(false);
        root.transform.position = Origin;
        area = root.AddComponent<GameArea>();
        var rooms = new GameObject("Rooms").transform;
        rooms.SetParent(root.transform, false);

        var chunks = new List<DungeonChunk>();
        Built result = default;
        int n = 0;
        DungeonChunk Place(DungeonChunk prefab, Vector2Int pos)
        {
            var c = UnityEngine.Object.Instantiate(prefab, rooms);
            c.transform.position = Origin + new Vector3(pos.x * chunkSize.x, pos.y * chunkSize.y, 0f);
            c.Position = pos;
            // the room's props (its RandomAsset spots: tables, barrels, crates and their nudges) roll Unity's own
            // random number: seeded from the floor and the room's place, the same props on every game
            var was = UnityEngine.Random.state;
            UnityEngine.Random.InitState(unchecked(Delve.SeedOf(where) * 486187739 + where.Region * 92821 + pos.x * 73856093 ^ pos.y * 19349663));
            try { c.AfterInScene(); }
            finally { UnityEngine.Random.state = was; }
            chunks.Add(c);
            return c;
        }

        // rooms (the same steps as the game's DungeonRegionRenderer.SetupChunks, without merging their tiles)
        foreach (var pair in region.Chunks)
        {
            var model = pair.Value;
            var pos = pair.Key;
            var chunk = Place(model.Type, pos);
            if (chunk.Portal && Singleton<WorldMap>.HasMain)
            {
                var state = Singleton<WorldMap>.main.WorldMapState.PortalStates.FirstOrDefault(x => x.Position == pos)
                            ?? new PortalState(PortalState.State.Active) { Position = pos, Region = where.Region };
                try { chunk.Portal.Setup(state); } catch { }
                var portalTp = chunk.Portal.GetComponentInChildren<Teleporter>(true);
                if (portalTp) { Teleporter.GlobalTeleporters[$"DungeonSceneP{pos}"] = portalTp; result.entry ??= portalTp; }
            }
            foreach (var teleporter in chunk.Teleporters)
            {
                if (model.Others.TryGetValue("ID", out var id)) teleporter.SetID(id);
                teleporter.SetArea(area);
                if (teleporter.TeleportResolver is TeleporterChunkResolver resolver && model.Others.TryGetValue("Exit", out var exit)) resolver.SetDestination(exit);
                if (!string.IsNullOrEmpty(teleporter.teleporterID)) Teleporter.GlobalTeleporters[teleporter.teleporterID] = teleporter;
            }
            if (region.Connections.TryGetValue(chunk.Position, out var connection) && chunk.Points.Length > 0 && chunk.Points[0].Options != null)
            {
                var point = chunk.Points[0];
                point.UseOptions = true;
                for (int k = 0; k < point.Options.Length; k++)
                    if (point.Options[k].Teleporters.Any(x => x && x.SpawnDirection == connection.Direction)) { point.ToUseOption = k; break; }
                var toRegion = point.Options[point.ToUseOption].Teleporters.FirstOrDefault();
                if (toRegion)
                {
                    toRegion.ToConnectTo = connection.Index;
                    Teleporter.GlobalTeleporters[$"DungeonSceneP{pos}"] = toRegion.Teleporter;
                }
            }
            foreach (var point in chunk.Points)
                if (point.UseOptions && point.Options != null && point.ToUseOption < point.Options.Length)
                    point.Options[point.ToUseOption].gameObject.SetActive(true);
            foreach (var point in chunk.Points)
                if (point.DungeonConnections != null && point.DungeonConnections.Count > 0 && region.Connections.TryGetValue(chunk.Position, out var c2))
                    point.DungeonConnections[0].ToConnectTo = c2.Index;
            if (++n % 4 == 0) { Loading.Fraction(n / (float)Mathf.Max(1, region.Chunks.Count)); await UniTask.Yield(); }
        }
        result.rooms = n;

        // borders between rooms and around the region (DungeonRegionRenderer.SetupBarriers)
        if (render.BorderPrefabs != null)
        {
            void Barrier(DungeonBarrierVariation prefab, Vector2Int pos)
            {
                var b = UnityEngine.Object.Instantiate(prefab, rooms);
                b.transform.position = Origin + new Vector3(pos.x * chunkSize.x, pos.y * chunkSize.y, 0f);
                b.Position = pos;
                barriers.Add(b);
            }
            foreach (var pos in region.InsideNodes)
            {
                if (render.BorderPrefabs.TryGetValue(pos, out var border)) Place(border, pos);
                if (render.VerticalCells.TryGetValue(pos, out var v)) Barrier(v, pos);
                var right = pos + Vector2Int.right;
                if (!region.InsideNodes.Contains(right) && render.VerticalCells.TryGetValue(right, out v)) Barrier(v, right);
                if (render.HorizontalCells.TryGetValue(pos, out var h)) Barrier(h, pos);
                var up = pos + Vector2Int.up;
                if (!region.InsideNodes.Contains(up) && render.HorizontalCells.TryGetValue(up, out h)) Barrier(h, up);
                if (++n % 8 == 0) await UniTask.Yield();
            }
            foreach (var pos in region.Edges)
                if (render.BorderPrefabs.TryGetValue(pos, out var border)) Place(border, pos);
        }

        // the rooms' tiles merged into one set of layers the way the game does it (DungeonRegionRenderer.MergeTilemaps:
        // each tile at (its room's slot + the layer's override) x room size + its own cell; where the layer object sits
        // in the room doesn't count), then collision + navigation from those
        var sources = new List<(TilemapData t, Vector2Int at)>();
        foreach (var c in chunks)
        {
            if (c.Tilemaps != null) foreach (var t in c.Tilemaps) if (t.Tilemap) sources.Add((t, c.Position));
            foreach (var p in c.Points)
                if (p.UseOptions && p.Options != null && p.ToUseOption < p.Options.Length && p.Options[p.ToUseOption].Tilemaps != null)
                    foreach (var t in p.Options[p.ToUseOption].Tilemaps) if (t.Tilemap) sources.Add((t, c.Position + p.Position));
        }
        foreach (var b in barriers)
            if (b && b.Tilemaps != null) foreach (var t in b.Tilemaps) if (t.Tilemap) sources.Add((t, b.Position));
        barriers.Clear();
        var bounds = BuildCollision(sources, chunkSize, out var walkable, out var blocked);
        await UniTask.Yield();

        // the camera box
        var confinerGo = new GameObject("CameraConfiner");
        confinerGo.transform.SetParent(root.transform, false);
        var confiner = confinerGo.AddComponent<PolygonCollider2D>();
        confiner.isTrigger = true;
        var sceneConfiner = UnityEngine.Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(a => a.CameraConfiner).FirstOrDefault(c => c);
        if (sceneConfiner) confinerGo.layer = sceneConfiner.gameObject.layer;
        var min = bounds.min - Origin; var max = bounds.max - Origin;
        confiner.points = new[] { new Vector2(min.x, min.y), new Vector2(max.x, min.y), new Vector2(max.x, max.y), new Vector2(min.x, max.y) };
        area.cameraConfiner = confiner;

        MendGathering(root);
        root.SetActive(true);
        area.Setup();
        area.gameObject.SetActive(false);   // GameManager turns it on when we arrive (CurrentArea)
        root.SetActive(true);
        Loading.Stage("Mapping the paths", 0.75f, 0.95f, creep: true);
        await BuildNavMesh(walkable, blocked);
        root.SetActive(false);

        result.fallback = bounds.center;
        if (!result.entry)
            result.entry = chunks.SelectMany(c => c.Teleporters ?? Array.Empty<Teleporter>()).FirstOrDefault(t => t && t.spawnPoint);
        return result;
    }

    /// <summary>The last built place's solid cells by the layer they came from (wall / wall top / water), for /walls.</summary>
    internal static readonly Dictionary<string, HashSet<Vector3Int>> DebugLayers = new Dictionary<string, HashSet<Vector3Int>>();

    static string FindRoomName(Transform t)
    {
        for (var p = t; p; p = p.parent) if (p.GetComponent<DungeonChunk>()) return p.name;
        return t.parent ? t.parent.name : t.name;
    }

    /// <summary>The last built place: every room tile in each world cell ("Layer: tile"), every layer (the ones we skip
    /// too), for /walls' probe under the mouse.</summary>
    internal static readonly Dictionary<Vector3Int, List<string>> DebugCells = new Dictionary<Vector3Int, List<string>>();

    static readonly List<DungeonBarrierVariation> barriers = new List<DungeonBarrierVariation>();
    static Tile blockTile;

    /// <summary>
    /// One collision tilemap for the region in world cells: walls and wall covers, minus the "remove collision" tiles,
    /// plus water, minus the "remove water" tiles (bridges); and the ground cells for the navigation mesh.
    /// Returns the region's bounds in the world.
    /// </summary>
    static Bounds BuildCollision(List<(TilemapData t, Vector2Int at)> sources, Vector2Int chunkSize, out Tilemap walkable, out Tilemap blocked)
    {
        var walls = new HashSet<Vector3Int>();
        var noWall = new HashSet<Vector3Int>();
        var water = new HashSet<Vector3Int>();
        var noWater = new HashSet<Vector3Int>();
        var ground = new HashSet<Vector3Int>();
        var covers = new HashSet<Vector3Int>();
        Vector3 cell = Vector3.one;
        var originCell = new Vector3Int(Mathf.RoundToInt(Origin.x), Mathf.RoundToInt(Origin.y), 0);

        // the drawn layers, merged (floor, walls, wall tops, water, fences); the rooms' own copies switched off
        var merged = new Dictionary<TilemapMergeLayer, Tilemap>();
        var mergedGrid = new GameObject("MergedTiles");
        mergedGrid.transform.SetParent(root.transform, false);
        mergedGrid.transform.position = Vector3.zero;
        mergedGrid.AddComponent<Grid>();
        var tiles = new Dictionary<TilemapMergeLayer, (List<Vector3Int> at, List<TileBase> tile, List<Matrix4x4> m, List<Color> col)>();

        DebugCells.Clear();
        foreach (var (t, at) in sources)
        {
            var tm = t.Tilemap;
            var grid = tm.GetComponentInParent<Grid>(true);   // (layoutGrid is null while the floor is switched off)
            if (grid) cell = grid.cellSize;
            HashSet<Vector3Int> into = t.Layer switch
            {
                // (a room's own "Collision" layer is NOT solid: the game's renderer switches it off and makes the
                // walls from the wall tiles)
                TilemapMergeLayer.Wall => walls,
                TilemapMergeLayer.WallCover => covers,
                TilemapMergeLayer.RemoveCollision => noWall,
                TilemapMergeLayer.Water or TilemapMergeLayer.WaterCollision => water,
                TilemapMergeLayer.RemoveWater => noWater,
                TilemapMergeLayer.Ground0 or TilemapMergeLayer.Ground1 or TilemapMergeLayer.Ground2 => ground,
                _ => null
            };
            bool draw = t.Layer is TilemapMergeLayer.Ground0 or TilemapMergeLayer.Ground1 or TilemapMergeLayer.Ground2
                        or TilemapMergeLayer.Wall or TilemapMergeLayer.WallCover or TilemapMergeLayer.Water or TilemapMergeLayer.Fence;
            var slot = at + (t.OverridePosition ?? Vector2Int.zero);
            var baseCell = originCell + new Vector3Int(slot.x * chunkSize.x, slot.y * chunkSize.y, 0);
            {   // (/walls' probe: every layer)
                tm.CompressBounds();
                foreach (var c in tm.cellBounds.allPositionsWithin)
                {
                    var tile = tm.GetTile(c);
                    if (!tile) continue;
                    var to = baseCell + new Vector3Int(c.x, c.y, 0);
                    if (!DebugCells.TryGetValue(to, out var here)) DebugCells[to] = here = new List<string>(3);
                    // (the game's layer tag, the tilemap's own name - a pit's is "Void" - the tile, the room)
                    here.Add($"{t.Layer} [tilemap {tm.name}]: {tile.name} (room {FindRoomName(tm.transform)})");
                }
            }
            if (into != null || draw)
            {
                tm.CompressBounds();
                if (draw && !merged.ContainsKey(t.Layer)) merged[t.Layer] = MergedLayer(mergedGrid.transform, tm, t.Layer);
                if (draw && !tiles.ContainsKey(t.Layer)) tiles[t.Layer] = (new List<Vector3Int>(), new List<TileBase>(), new List<Matrix4x4>(), new List<Color>());
                foreach (var c in tm.cellBounds.allPositionsWithin)
                {
                    var tile = tm.GetTile(c);
                    if (!tile) continue;
                    var to = baseCell + new Vector3Int(c.x, c.y, 0);
                    into?.Add(to);
                    if (!draw) continue;
                    var list = tiles[t.Layer];
                    list.at.Add(to); list.tile.Add(tile); list.m.Add(tm.GetTransformMatrix(c)); list.col.Add(tm.GetColor(c));
                }
            }
            // the room's own copy goes (drawn merged now), as do the helper layers
            if (draw || t.Layer is TilemapMergeLayer.Collision or TilemapMergeLayer.RemoveCollision or TilemapMergeLayer.RemoveWater or TilemapMergeLayer.WaterCollision)
                tm.gameObject.SetActive(false);
        }
        // water isn't drawn under walls / where a bridge takes it away (DungeonRegionRenderer.SetupLayers)
        var hideWater = new HashSet<Vector3Int>(walls); hideWater.UnionWith(covers); hideWater.UnionWith(noWater);
        foreach (var pair in tiles)
        {
            var tm = merged[pair.Key];
            var (at, tile, m, col) = pair.Value;
            for (int i = 0; i < at.Count; i++)
            {
                if (pair.Key == TilemapMergeLayer.Water && hideWater.Contains(at[i])) continue;
                tm.SetTile(at[i], tile[i]);
                tm.SetTileFlags(at[i], TileFlags.None);
                tm.SetTransformMatrix(at[i], m[i]);
                tm.SetColor(at[i], col[i]);
            }
        }

        // for /walls: which layer made each solid cell
        DebugLayers.Clear();
        DebugLayers["wall"] = new HashSet<Vector3Int>(walls);
        DebugLayers["wall top"] = new HashSet<Vector3Int>(covers);
        DebugLayers["water"] = new HashSet<Vector3Int>(water);
        // bridges placed as objects over the water (no "remove water" layer of their own): the water under the deck
        // goes (its railings keep their own colliders)
        int bridges = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t || t.name.IndexOf("bridge", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (t.parent && t.parent.name.IndexOf("bridge", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;   // counted with its parent
            var rs = t.GetComponentsInChildren<SpriteRenderer>(true);
            if (rs.Length == 0) continue;
            var deck = rs[0].bounds;
            foreach (var r in rs) if (r.bounds.size.sqrMagnitude > 0f) deck.Encapsulate(r.bounds);
            bridges++;
            for (int x = Mathf.FloorToInt(deck.min.x / cell.x); x <= Mathf.FloorToInt(deck.max.x / cell.x); x++)
                for (int y = Mathf.FloorToInt(deck.min.y / cell.y); y <= Mathf.FloorToInt(deck.max.y / cell.y); y++)
                {
                    var c = new Vector3Int(x, y, 0);
                    var centre = new Vector3((x + 0.5f) * cell.x, (y + 0.5f) * cell.y, deck.center.z);
                    if (deck.Contains(centre)) { noWater.Add(c); if (water.Contains(c)) ground.Add(c); }   // walkable deck
                }
        }
        if (bridges > 0) NetLog.Info($"Dungeon: {bridges} bridges over water made walkable", toast: false);
        walls.UnionWith(covers);
        walls.ExceptWith(noWall);
        water.ExceptWith(noWater);
        int wallOnly = walls.Count;
        // water is solid (the lakes; it has a lake-bed floor tile under it, so that doesn't make it walkable), except
        // where a bridge's "remove water" layer takes it away (subtracted above): with the tiles placed like the game
        // places them, those land on the bridge decks
        int groundUnderWater = ground.Count(c => water.Contains(c));
        bool waterSolid = true;
        walls.UnionWith(water);
        floorCells = new HashSet<Vector3Int>(ground);
        floorCells.ExceptWith(walls);
        if (ground.Count > 0 && floorCells.Count < Mathf.Max(30, ground.Count * 0.03f))   // corridors are mostly wall (86% here): only a floor of almost nothing is wrong
        {
            // still almost no floor: the walls layer must be covering it too (not just the walls) - keep the floor walkable
            NetLog.Warn($"Dungeon: walls cover {ground.Count - floorCells.Count} of {ground.Count} floor tiles; only the edges are solid now", toast: false);
            walls.ExceptWith(ground);
            floorCells = new HashSet<Vector3Int>(ground);
        }
        cellSize = cell;
        NetLog.Info($"Dungeon tiles: ground {ground.Count}, walls {wallOnly} (minus {noWall.Count} openings), water {water.Count} (minus {noWater.Count}, {groundUnderWater} under the floor, solid: {waterSolid}), walkable {floorCells.Count}, cell {cell}, from {sources.Count} tilemaps: " +
                    string.Join(", ", sources.GroupBy(s => s.t.Layer).Select(g => $"{g.Key} x{g.Count()}")), toast: false);

        wallCells = new HashSet<Vector3Int>(walls);   // (the minimap draws them)
        NavLayers(root.transform, cell, ground, walls, out walkable, out blocked);

        var all = new HashSet<Vector3Int>(ground); all.UnionWith(walls);
        if (all.Count == 0) return new Bounds(Origin, new Vector3(40f, 40f, 0f));
        int x0 = all.Min(c => c.x), x1 = all.Max(c => c.x), y0 = all.Min(c => c.y), y1 = all.Max(c => c.y);
        var b = new Bounds();
        b.SetMinMax(new Vector3(x0 * cell.x, y0 * cell.y, 0f), new Vector3((x1 + 1) * cell.x, (y1 + 1) * cell.y, 0f));
        bounds = b;
        return b;
    }

    /// <summary>
    /// The two tilemaps a built place needs, in world cells: "Walkable" (the floor, for the navigation mesh) and
    /// "Blocked" (walls and water: solid, and not walkable). Also used by the boss arena.
    /// </summary>
    internal static void NavLayers(Transform parent, Vector3 cell, HashSet<Vector3Int> ground, HashSet<Vector3Int> walls, out Tilemap walkable, out Tilemap blocked)
    {
        if (!blockTile)
        {
            blockTile = ScriptableObject.CreateInstance<Tile>();
            blockTile.colliderType = Tile.ColliderType.Grid;
            blockTile.hideFlags = HideFlags.HideAndDontSave;
        }

        // nav root: NavMeshPlus wants its surface turned to face the 2D plane; the grid under it is turned back
        var navGo = new GameObject("Navigation");
        navGo.transform.SetParent(parent, false);
        // the surface sits in the middle of the place: NavMeshPlus builds over the space from the surface out to the tiles,
        // so a surface at the world's origin would cover thousands of units (and the creatures' mesh never finishes)
        var cells = ground.Count > 0 ? ground : walls;
        var mid = cells.Count == 0 ? Vector3.zero : new Vector3((float)cells.Average(c => c.x) * cell.x, (float)cells.Average(c => c.y) * cell.y, 0f);
        navGo.transform.position = mid;
        navGo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        var gridGo = new GameObject("Grid");
        gridGo.transform.SetParent(navGo.transform, false);
        gridGo.transform.rotation = Quaternion.identity;
        gridGo.transform.position = Vector3.zero;
        gridGo.AddComponent<Grid>().cellSize = cell;

        walkable = MakeTilemap(gridGo, "Walkable", ground, 0);
        blocked = MakeTilemap(gridGo, "Blocked", walls, 1);
        blocked.gameObject.layer = WallLayer();
        var rb = blocked.gameObject.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Static;
        var col = blocked.gameObject.AddComponent<TilemapCollider2D>();
        var composite = blocked.gameObject.AddComponent<CompositeCollider2D>();
        composite.geometryType = CompositeCollider2D.GeometryType.Polygons;
        col.usedByComposite = true;

    }

    /// <summary>One merged layer, drawn like the rooms' own (sorting, material, mode, anchor, tint of the first of its kind).</summary>
    static Tilemap MergedLayer(Transform parent, Tilemap like, TilemapMergeLayer layer)
    {
        var go = new GameObject("Merged_" + layer);
        go.transform.SetParent(parent, false);
        go.layer = like.gameObject.layer;
        var tm = go.AddComponent<Tilemap>();
        tm.tileAnchor = like.tileAnchor;
        tm.color = like.color;
        tm.orientation = like.orientation;
        var r = go.AddComponent<TilemapRenderer>();
        var lr = like.GetComponent<TilemapRenderer>();
        if (lr)
        {
            r.sortingLayerID = lr.sortingLayerID;
            r.sortingOrder = lr.sortingOrder;
            r.sharedMaterial = lr.sharedMaterial;
            r.mode = lr.mode;
            r.sortOrder = lr.sortOrder;
        }
        return tm;
    }

    /// <summary>
    /// A tile's centre in the world, worked out from the transforms. Tilemap.GetCellCenterWorld can't be used while the
    /// floor is being built (switched off): it then puts every tile of a tilemap on the same spot.
    /// </summary>
    internal static Vector3 CellCenter(Tilemap tm, Vector3Int c)
    {
        var grid = tm.GetComponentInParent<Grid>(true);
        var cs = grid ? grid.cellSize : Vector3.one;
        var a = tm.tileAnchor;
        return tm.transform.TransformPoint(new Vector3((c.x + a.x) * cs.x, (c.y + a.y) * cs.y, 0f));
    }

    static Tilemap MakeTilemap(GameObject grid, string name, HashSet<Vector3Int> cells, int navArea)
    {
        var go = new GameObject(name);
        go.transform.SetParent(grid.transform, false);
        var tm = go.AddComponent<Tilemap>();
        var arr = cells.ToArray();
        tm.SetTiles(arr, Enumerable.Repeat((TileBase)blockTile, arr.Length).ToArray());
        tm.CompressBounds();   // else its bounds run from (0,0) out to the tiles: the navigation mesh would cover thousands of units and run out of memory
        var mod = go.AddComponent<NavMeshModifier>();
        mod.overrideArea = true;
        mod.area = navArea;
        return tm;
    }

    /// <summary>The layer the scene's walls use (the most common solid tile collider's layer).</summary>
    internal static int WallLayer()
    {
        var layer = UnityEngine.Object.FindObjectsByType<TilemapCollider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(c => c && !c.isTrigger).GroupBy(c => c.gameObject.layer).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        return layer ?? 0;
    }

    /// <summary>Creatures walk on a NavMeshPlus navigation mesh: build one from our two tilemaps, set up like the scene's.</summary>
    internal static async UniTask BuildNavMesh(Tilemap walkable, Tilemap blocked)
    {
        var navGo = walkable.transform.parent.parent.gameObject;
        var surface = navGo.AddComponent<NavMeshSurface>();
        var sceneSurface = Resources.FindObjectsOfTypeAll<NavMeshSurface>().FirstOrDefault(s => s && s.gameObject.scene.IsValid() && s != surface);
        if (sceneSurface)
        {
            surface.agentTypeID = sceneSurface.agentTypeID;
            surface.defaultArea = sceneSurface.defaultArea;
            surface.overrideTileSize = sceneSurface.overrideTileSize;
            surface.tileSize = sceneSurface.tileSize;
            surface.overrideVoxelSize = sceneSurface.overrideVoxelSize;
            surface.voxelSize = sceneSurface.voxelSize;
        }
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.RenderMeshes;
        surface.layerMask = ~0;
        var collect = navGo.AddComponent<CollectSources2d>();
        collect.overrideByGrid = true;
        try
        {
            var op = surface.BuildNavMeshAsync();
            while (op != null && !op.isDone) await UniTask.Yield();
        }
        catch (Exception e) { NetLog.Warn($"Dungeon: no navigation mesh ({e.Message}); creatures may stand still", toast: false); }
    }

    /// <summary>The biome's creatures in the rooms' spawn areas (the game's GameManager.SetupScene does the same in the dungeon scene).
    /// Connected: only whoever runs the area spawns; the others get them through the creature sync.</summary>
    /// <summary>
    /// The floor's creatures, Diablo-style: every spawn area gets its groups now (which creatures, from the floor's
    /// seed), but they only appear when a player comes near (Wake); at most MaxEnemies alive at once. So a big floor
    /// is full without hundreds of creatures running at the same time.
    /// Connected: only whoever runs the area spawns; the others get them through the creature sync.
    /// </summary>
    static void SpawnCreatures(BiomeSettings biome, Where where)
    {
        sleeping.Clear();
        if (!biome || !root) return;
        if (Plugin.Connected && !Plugin.IOwnHere) return;
        int genSeed = Delve.SeedOf(where);
        var rng = new System.Random(genSeed ^ where.Region);
        int groups = 0;
        foreach (var spawnArea in root.GetComponentsInChildren<EnemySpawnArea>(true))
        {
            if (rng.NextDouble() >= spawnArea.Chance + Delve.ExtraChance(where.Floor)) continue;
            var shape = spawnArea.GetComponent<PolygonCollider2D>();
            var collection = spawnArea.Type switch
            {
                SpawnAreaType.Big => biome.BigSpawnCollection,
                SpawnAreaType.Small => biome.SmallSpawnCollection,
                SpawnAreaType.Medium => biome.MediumSpawnCollection,
                SpawnAreaType.Custom => spawnArea.CustomGroups,
                _ => null
            };
            if (!shape || !collection || collection.Groups == null || collection.Groups.Count == 0) continue;
            var list = new List<Enemy>();
            int n = Delve.GroupsFor(where.Floor, rng);
            for (int g = 0; g < n; g++)
            {
                var group = collection.Groups[rng.Next(collection.Groups.Count)];
                if (group && group.Enemies != null) foreach (var e in group.Enemies) if (e) list.Add(e);
            }
            if (list.Count == 0) continue;
            sleeping.Add((shape, list, rng.Next()));
            groups++;
        }
        sleepingCreatures = sleeping.Sum(x => x.creatures.Count);
        NetLog.Info($"Dungeon: {groups} spawn areas, {sleepingCreatures} creatures waiting (they appear as you come near; at most {Delve.MaxEnemies} at once)", toast: false);
        nextWake = 0f;
    }

    static readonly List<(PolygonCollider2D shape, List<Enemy> creatures, int seed)> sleeping = new List<(PolygonCollider2D, List<Enemy>, int)>();
    static int sleepingCreatures;
    static float nextWake;
    const float WakeRange = 28f;

    /// <summary>Twice a second: spawn areas a player has come near fill up (while there's room under the limit).</summary>
    static void Wake()
    {
        if (sleeping.Count == 0 || Time.unscaledTime < nextWake || busy) return;
        nextWake = Time.unscaledTime + 0.5f;
        if (Plugin.Connected && !Plugin.IOwnHere) return;
        var near = new List<Vector3>();
        var me = LocalPlayer.Get();
        if (me) near.Add(me.transform.position);
        if (Plugin.Connected)
            foreach (var p in Plugin.Players)
                if (p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area)) near.Add(p.LastPosition);
        if (near.Count == 0) return;
        int alive = 0;
        foreach (var go in spawned) if (go && go.activeInHierarchy && go.GetComponent<Enemy>() is Enemy e && !e.IsDead) alive++;
        int max = Delve.MaxEnemies > 0 ? Delve.MaxEnemies : int.MaxValue;
        for (int i = sleeping.Count - 1; i >= 0 && alive < max; i--)
        {
            var (shape, creatures, seed) = sleeping[i];
            if (!shape) { sleeping.RemoveAt(i); continue; }
            var c = shape.bounds.center;
            bool close = false;
            foreach (var p in near) if (((Vector2)(p - c)).sqrMagnitude < WakeRange * WakeRange) { close = true; break; }
            if (!close) continue;
            sleeping.RemoveAt(i);
            var oldState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            int woke = 0;
            try
            {
                foreach (var enemy in creatures)
                {
                    if (alive >= max) break;
                    woke++;
                    PoolManager.CreatePool(enemy.gameObject, enemy.ID, 5, disableFirst: true);
                    var go = PoolManager.Get(enemy.gameObject);
                    go.transform.position = Utils.RandomInsidePolygon(shape);
                    go.GetComponent<Enemy>().ForceAlive();
                    go.SetActive(true);
                    spawned.Add(go);
                    alive++;
                }
            }
            catch (Exception e) { NetLog.Warn($"Dungeon: creatures: {e.Message}", toast: false); }
            finally { UnityEngine.Random.state = oldState; }
            // the limit was reached halfway through this area's group: the rest stay asleep there, and come once
            // there's room again (they were simply lost before, so busy floors ran dry)
            if (woke < creatures.Count) sleeping.Add((shape, creatures.GetRange(woke, creatures.Count - woke), seed + woke));
        }
    }

    // ---- doorways inside a dungeon (the game would load its missing DungeonScene) ----

    static string Name(DungeonSettings s) => s ? s.name : "";

    /// <summary>A doorway to another region of the same world.</summary>
    static void ToRegion(int region, Vector2Int fromChunk)
    {
        if (!current.HasValue) return;
        var c = current.Value;
        Go(new Where(c.Settings, c.Seed, region), $"DungeonSceneP{fromChunk}", true).Forget();
    }

    /// <summary>A doorway into a sub-dungeon (a cave...), or back out of one.</summary>
    static void ToSub(DungeonSettings settings, string entranceId, Vector2Int fromChunk)
    {
        if (!current.HasValue) return;
        var c = current.Value;
        if (parents.Count > 0) { Go(parents.Pop(), entranceId, true).Forget(); return; }   // in a sub-dungeon: this one leads back up
        if (!settings) return;
        parents.Push(c);
        int seed = unchecked(c.Seed * 31 + fromChunk.x * 7393 + fromChunk.y * 1931);
        Go(new Where(settings.name, seed, 0), entranceId, true).Forget();
    }

    /// <summary>
    /// The descent's floors are one place each: their doorways (to other regions, side caves, the full game's world)
    /// are sealed, and only the pads move you. Stopped before the game starts its teleport (it would freeze you in
    /// its loading state with the exit animation playing). In other generated dungeons, a doorway touched while one is
    /// being built is ignored the same way.
    /// </summary>
    [HarmonyPatch(typeof(Teleporter), nameof(Teleporter.Teleport))]
    static class SealedDoors
    {
        static float toldAt = -10f;

        static bool Prefix(Teleporter __instance, ref UniTask __result)
        {
            if (!Active || !root || !__instance.transform.IsChildOf(root.transform)) return true;
            if (current.Value.Floor <= 0 && !busy) return true;
            __result = UniTask.CompletedTask;
            if (Time.unscaledTime - toldAt > 4f)
            {
                toldAt = Time.unscaledTime;
                NetLog.Info(busy ? "Wait, the next place is still being built..." : "This doorway is sealed. Find the stairs down (or the blue pad home)", toast: true);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(TeleporterDungeonRegion), nameof(TeleporterDungeonRegion.Teleport))]
    static class RegionDoorPatch
    {
        static bool Prefix(TeleporterDungeonRegion __instance, ref UniTask __result)
        {
            if (!Active) return true;
            __result = UniTask.CompletedTask;
            var chunk = __instance.GetComponentInParent<DungeonChunk>(true);
            ToRegion(__instance.ToConnectTo, chunk ? chunk.Position : Vector2Int.zero);
            return false;
        }
    }

    [HarmonyPatch(typeof(TeleporterSubdungeonResolver), nameof(TeleporterSubdungeonResolver.Teleport))]
    static class SubDoorPatch
    {
        static bool Prefix(TeleporterSubdungeonResolver __instance, ref UniTask __result)
        {
            if (!Active) return true;
            __result = UniTask.CompletedTask;
            var chunk = __instance.GetComponentInParent<DungeonChunk>(true);
            ToSub(__instance.toUseSettings, __instance.EntranceTeleporterID, chunk ? chunk.Position : Vector2Int.zero);
            return false;
        }
    }

    /// <summary>An exit whose other end isn't in our region (it's in the scene the demo doesn't have): take us home.</summary>
    [HarmonyPatch(typeof(TeleporterChunkResolver), nameof(TeleporterChunkResolver.Teleport))]
    static class ExitPatch
    {
        static bool Prefix(TeleporterChunkResolver __instance, ref UniTask __result)
        {
            if (!Active) return true;
            if (!string.IsNullOrEmpty(__instance.otherTeleporterID) && Teleporter.GlobalTeleporters.TryGetValue(__instance.otherTeleporterID, out var t) && t) return true;
            __result = UniTask.CompletedTask;
            var gm = Singleton<GameManager>.main;
            if (gm) gm.gameState = GameState.Playing;
            if (parents.Count > 0) { Go(parents.Pop(), null, true).Forget(); return false; }
            NetLog.Info("That way out leads to the full game's world. Use the blue pad (or the Leave button) to go back", toast: true);
            return false;
        }
    }

    /// <summary>Never load the scene the demo doesn't ship (the game falls back to the tutorial area!).</summary>
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.CallScene))]
    static class NoDungeonScene
    {
        static bool Prefix(string scene, ref UniTaskVoid __result)
        {
            if (scene != TeleporterDungeonRegion.DUNGEON_SCENE) return true;
            __result = default;
            var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
            if (gm && gm.player) { gm.player.Static = false; gm.gameState = GameState.Playing; }
            NetLog.Warn("That doorway leads to the full game's dungeon scene, which the demo doesn't have", toast: true);
            return false;
        }
    }

    // ---- co-op ----

    static void Announce(Where where)
    {
        if (Plugin == null || !Plugin.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.DungeonOpen);
        w.Put(LocalPlayer.Scene);
        w.Put(where.Settings);
        w.Put(where.Seed);
        w.Put(where.Region);
        w.Put(where.Floor);
        Delve.WriteRules(w);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnOpen(NetDataReader r, int from)
    {
        string scene = r.GetString(), settings = r.GetString();
        int seed = r.GetInt(), region = r.GetInt(), floor = r.AvailableBytes >= 4 ? r.GetInt() : 0;
        Delve.ReadRules(r);
        invite = (from, scene, new Where(settings, seed, region, floor));
        inviteArena = false;
        if (!(current.HasValue && current.Value.Equals(invite.Value.where)))
        {
            inviteAt = Time.unscaledTime;   // the invite card (DungeonInvite) shows it, with Join / Not now
            Chat.System(InviteText(), new ImGuiNET.Vector4(1f, 0.75f, 0.4f, 1f));
        }
    }

    // ---- the invite card's side (DungeonInvite draws it) ----

    static float inviteAt = -999f;
    const float InviteSeconds = 45f;

    // the boss arena's invites share the card: the newest invite of either kind is the one shown
    static bool inviteArena, arenaRush;
    static int arenaFrom;
    static string arenaScene = "";

    /// <summary>We went into the boss arena (or started a boss rush there): everyone hears, and can come too.</summary>
    public static void AnnounceArena(bool rush)
    {
        if (Plugin == null || !Plugin.Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.ArenaOpen);
        w.Put(LocalPlayer.Scene);
        w.Put(rush);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnArena(NetDataReader r, int from)
    {
        arenaScene = r.GetString();
        arenaRush = r.GetBool();
        arenaFrom = from;
        inviteArena = true;
        if (Arena.Active) return;   // we're there already
        inviteAt = Time.unscaledTime;
        Chat.System(InviteText(), new ImGuiNET.Vector4(1f, 0.75f, 0.4f, 1f));
    }

    /// <summary>A live invite to show: someone else's dungeon (or the arena) we're not in, from the last 45 s, not waved away.</summary>
    public static bool HasInvite => Time.unscaledTime - inviteAt < InviteSeconds &&
                                    (inviteArena ? !Arena.Active
                                                 : invite.HasValue && !(current.HasValue && current.Value.Equals(invite.Value.where)));

    public static bool CanJoinInvite => HasInvite && !busy &&
                                        (inviteArena ? arenaScene == LocalPlayer.Scene && !Active : invite.Value.scene == LocalPlayer.Scene);

    public static string InviteText()
    {
        if (inviteArena)
        {
            string a = arenaRush ? $"{Plugin.NameOf(arenaFrom)} started a BOSS RUSH in the arena" : $"{Plugin.NameOf(arenaFrom)} went into the boss arena";
            if (arenaScene != LocalPlayer.Scene) return $"{a} from {arenaScene}: go there to join";
            return Active ? $"{a}: leave the dungeon to join" : a;
        }
        if (!invite.HasValue) return "";
        var inv = invite.Value;
        string who = Plugin.NameOf(inv.from);
        string what = inv.where.Floor > 0 ? $"{who} went down to floor {inv.where.Floor} of the descent" : $"{who} went into a dungeon ({inv.where.Settings})";
        return inv.scene == LocalPlayer.Scene ? what : $"{what} from {inv.scene}: go there to join";
    }

    public static void DismissInvite() => inviteAt = -999f;

    /// <summary>Go where the invite says (the same generated place: same settings, seed, region, floor).</summary>
    public static void JoinInvite()
    {
        if (!CanJoinInvite) return;
        inviteAt = -999f;
        if (inviteArena) { Arena.Enter(); return; }
        var inv = invite.Value;
        if (inv.where.Floor > 0) Delve.Follow(inv.where);
        Enter(inv.where.Settings, inv.where.Seed, inv.where.Region, announce: false, floor: inv.where.Floor);
    }

    // ---- the Insert menu ----

    public static void DrawMenu()
    {
        if (!GameData.HasWorldData || !LocalPlayer.Get()) return;
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.Text("Generated dungeons (the full game's generator; rooms from the game's files)");
        if (busy)
        {
            ImGuiNET.ImGui.TextDisabled("    " + status);
            return;
        }
        Delve.DrawMenu();
        if (Active)
        {
            var c = current.Value;
            ImGuiNET.ImGui.TextDisabled($"    In {c.Settings}, seed {c.Seed}, region {c.Region}{(render != null ? $" of {render.Regions.Count}" : "")}");
            if (ImGuiNET.ImGui.SmallButton("Leave the dungeon")) Leave();
            if (render != null && render.Regions.Count > 1)
            {
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.SmallButton("Next region")) Go(new Where(c.Settings, c.Seed, (c.Region + 1) % render.Regions.Count), null, true).Forget();
            }
        }
        else
        {
            if (ImGuiNET.ImGui.SmallButton("Enter the world map's dungeon")) EnterMain();
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextDisabled("(your world's seed, region 1)");
        }

        if (invite.HasValue && !(current.HasValue && current.Value.Equals(invite.Value.where)))
        {
            var inv = invite.Value;
            if (inv.scene != LocalPlayer.Scene)
                ImGuiNET.ImGui.TextDisabled($"    {Plugin.NameOf(inv.from)} is in a dungeon from {inv.scene}: go there to join");
            else if (ImGuiNET.ImGui.SmallButton(inv.where.Floor > 0 ? $"Follow {Plugin.NameOf(inv.from)} to floor {inv.where.Floor}"
                                                                   : $"Join {Plugin.NameOf(inv.from)}'s dungeon ({inv.where.Settings}, region {inv.where.Region})"))
            {
                inviteAt = Time.unscaledTime;
                JoinInvite();
            }
        }

        // any of the game's dungeon settings, any seed
        var list = AllSettings();
        if (list.Count == 0) return;
        pick = Mathf.Clamp(pick, 0, list.Count - 1);
        ImGuiNET.ImGui.SetNextItemWidth(220);
        if (ImGuiNET.ImGui.BeginCombo("##dungeonSettings", list[pick].name))
        {
            for (int i = 0; i < list.Count; i++)
                if (ImGuiNET.ImGui.Selectable(list[i].name + (i == 0 && list[i] == MainSettings() ? "  (world map)" : ""), i == pick)) pick = i;
            ImGuiNET.ImGui.EndCombo();
        }
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(90);
        ImGuiNET.ImGui.InputTextWithHint("##dungeonSeed", "random seed", ref seedText, 12);
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(70);
        ImGuiNET.ImGui.InputInt("##dungeonRegion", ref regionPick);
        if (regionPick < 0) regionPick = 0;
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Generate + go"))
        {
            int seed = int.TryParse(seedText, out int s) ? s : new System.Random().Next();
            seedText = seed.ToString();
            Enter(list[pick].name, seed, regionPick);
        }
    }

    /// <summary>/dungeon: in the world map's dungeon, or out of the one we're in.</summary>
    public static void Command()
    {
        if (Active) Leave(); else EnterMain();
    }
}


/// <summary>
/// The same floor on every game: most of the game's dungeon generator rolls the seeded number it's given, but a few of
/// its steps roll Unity's own random number (which room fills a spot first, side doors' barriers, the entrance and
/// exit, which far room starts a path). While a floor is generated, those steps get a random stream of their own,
/// started from the floor's seed and swapped in only while each one runs, so nothing else that rolls in between
/// (a flickering light, a creature) shifts it.
/// </summary>
[HarmonyPatch]
internal static class GenRandom
{
    static bool on;
    static UnityEngine.Random.State state;

    public static void Begin(int seed)
    {
        var was = UnityEngine.Random.state;
        UnityEngine.Random.InitState(unchecked(seed * 1103515245 + 12345));
        state = UnityEngine.Random.state;
        UnityEngine.Random.state = was;
        on = true;
    }

    public static void End() => on = false;

    static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(UniformNeighborSelection), nameof(UniformNeighborSelection.Select));
        yield return AccessTools.Method(typeof(RandomNeighborSelection), nameof(RandomNeighborSelection.Select));
        yield return AccessTools.Method(typeof(ConnectChunksStep), nameof(ConnectChunksStep.Generate));
        yield return AccessTools.Method(typeof(DungeonEntranceGeneration), nameof(DungeonEntranceGeneration.Generate));
        yield return AccessTools.Method(typeof(SelectFromDistanceRange), nameof(SelectFromDistanceRange.SelectStart));
        yield return AccessTools.Method(typeof(ChunkModel), nameof(ChunkModel.GetRandomAvailableBarrier));
    }

    static void Prefix(out UnityEngine.Random.State __state)
    {
        __state = UnityEngine.Random.state;
        if (on) UnityEngine.Random.state = state;
    }

    static void Postfix(UnityEngine.Random.State __state)
    {
        if (on) state = UnityEngine.Random.state;
        UnityEngine.Random.state = __state;
    }
}

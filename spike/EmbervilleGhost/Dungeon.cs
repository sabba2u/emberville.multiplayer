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
    static Teleporter entryDoor;   // (the door you arrived by in this region)
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

    /// <summary>/dungeons: every dungeon type the game has, by name.</summary>
    public static void ListNames()
    {
        var names = AllSettings().Select(s => s.name).ToList();
        if (MainSettings()) names.Insert(0, MainSettings().name);
        if (GameConsole.IsOpen) GameConsole.Links("Dungeons (click one, then Enter):", names.Select(n => (n, "/dungeon " + n)));
        else Chat.System("Dungeons: " + string.Join(", ", names) + "  (in the ~ console you can click them)");
    }

    /// <summary>/dungeon negedra: into the dungeon type whose name has that in it (a new seed each time); /dungeon kl 1: its
    /// region 1.</summary>
    public static void EnterNamed(string part)
    {
        part = (part ?? "").Trim();
        // a region number after it: /dungeon kl 1 (KL's second part: Kaldhor's throne room)
        int region = 0;
        var words = part.Split(' ');
        if (words.Length > 1 && int.TryParse(words[words.Length - 1], out int r) && r >= 0) { region = r; part = string.Join(" ", words.Take(words.Length - 1)); }
        var list = AllSettings().ToList();
        if (MainSettings()) list.Insert(0, MainSettings());   // (the world map's own too: "fullmap")
        var s = list.FirstOrDefault(x => x.name.Equals(part, System.StringComparison.OrdinalIgnoreCase))
             ?? list.FirstOrDefault(x => x.name.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (!s) { NetLog.Warn($"No dungeon called \"{part}\" (/dungeons lists them)", toast: true); return; }
        Enter(s.name, UnityEngine.Random.Range(1, int.MaxValue), region);
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

    /// <summary>Out, to Nagedra (the Mad King's portal): beside her in the tutorial area, if that's where you came in
    /// from; else back where you came in.</summary>
    public static void LeaveToNagedra()
    {
        if (!Active || busy) return;
        if (returnScene == "TutorialArea" && returnArea)
        {
            // her spot, in the area she's in (you may have come in from another: the starting house; its area at her
            // spot left you unseen and walking through walls)
            var spot = new Vector2(NagedraAt.x, NagedraAt.y);
            var hers = UnityEngine.Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(a => a && a.CameraConfiner && a.gameObject.scene.name == returnScene && Inside(a.CameraConfiner, spot))
                .OrderBy(a => Size(a.CameraConfiner)).FirstOrDefault();
            if (hers)
            {
                returnArea = hers;
                returnPos = new Vector3(NagedraAt.x, NagedraAt.y, returnPos.z);
            }
            else NetLog.Warn("Dungeon: no area found around Nagedra: back where you came in", toast: false);
        }
        Leave();
    }

    static readonly Vector2 NagedraAt = new Vector2(-38.6f, 65.6f);

    /// <summary>Is the point inside the area's camera bounds? Worked out from its shape (an area not current is switched
    /// off: then the physics check says no for everything).</summary>
    static bool Inside(Collider2D c, Vector2 p)
    {
        var local = (Vector2)c.transform.InverseTransformPoint(p) - c.offset;
        if (c is BoxCollider2D box) return Mathf.Abs(local.x) <= box.size.x / 2f && Mathf.Abs(local.y) <= box.size.y / 2f;
        if (c is PolygonCollider2D poly)
            for (int i = 0; i < poly.pathCount; i++)
            {
                var pts = poly.GetPath(i);
                bool inside = false;
                for (int j = 0, k = pts.Length - 1; j < pts.Length; k = j++)
                    if ((pts[j].y > local.y) != (pts[k].y > local.y) && local.x < (pts[k].x - pts[j].x) * (local.y - pts[j].y) / (pts[k].y - pts[j].y) + pts[j].x)
                        inside = !inside;
                if (inside) return true;
            }
        return false;
    }

    /// <summary>Roughly how big an area's bounds are (smallest first: the room you're in, not the whole map).</summary>
    static float Size(Collider2D c)
    {
        var scale = c.transform.lossyScale;
        if (c is BoxCollider2D box) return box.size.x * box.size.y * Mathf.Abs(scale.x * scale.y);
        if (c is PolygonCollider2D poly && poly.pathCount > 0)
        {
            var pts = poly.GetPath(0);
            float w = pts.Max(q => q.x) - pts.Min(q => q.x), h = pts.Max(q => q.y) - pts.Min(q => q.y);
            return w * h * Mathf.Abs(scale.x * scale.y);
        }
        return float.MaxValue;
    }   // (just below her: Better NPCs and Quests' Nagedra stands at -38.6, 67.2)

    public static void Leave()
    {
        if (!Active || busy) return;
        var gm = Singleton<GameManager>.main;
        var back = returnArea;
        var oldRoot = root;
        current = null;
        parents.Clear();
        Delve.Left();
        var oldBounds = bounds;
        if (returnScene != LocalPlayer.Scene || !back)
        {
            Cleanup(oldRoot, oldBounds);
            ThroneRoom.Close();
            return;
        }
        // (the places go once you've arrived: the one you stand in taken away mid-fade left the screen black)
        gm.TeleportPlayerFade(returnPos, Direction.Down, back, back.CameraConfiner).ContinueWith(() => { Cleanup(oldRoot, oldBounds); ThroneRoom.Close(); }).Forget();
        NetLog.Info("Leaving the dungeon", toast: true);
    }

    static void Cleanup(GameObject oldRoot, Bounds oldBounds)
    {
        // what lay on its floor (creatures' drops, things you threw down): loose in the world, not under the floor, and
        // every floor is built in the same spot: the next floor had the last one's leftovers lying about
        DropSync.ClearIn(oldBounds);
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
        var oldBounds = bounds;
        DungeonLoot.Preload();
        Loading.Begin(where.Floor > 0 ? $"Descending to floor {where.Floor}" : "Entering the dungeon");
        try
        {
            var settings = Find(where.Settings);
            NetLog.Info($"Generating the dungeon {settings.name}, seed {where.Seed}, region {where.Region}...", toast: true);
            int genSeed = Delve.SeedOf(where);
            int footprint = where.Floor > 0 && settings != MainSettings() ? Delve.Footprint : 0;   // (the descent's floors: kept within that many rooms across)
            int sprawlNow = (where.Floor > 0 ? Delve.SprawlLevel(where.Floor) : 0) * 100 + footprint;
            if (render == null || renderKey != (settings.name, genSeed) || renderSprawl != sprawlNow)
            {
                var rng = new SeededSystemRandomProvider(genSeed);
                var ctx = new GenerationContext(rng);
                if (settings != MainSettings()) ctx.NeighborSelection = new UniformNeighborSelection();
                // a descent floor: more side rooms / features than the game's own numbers (put back right after)
                var undo = Sprawl.Apply(settings, where.Floor > 0 ? Delve.SprawlLevel(where.Floor) : 0, footprint);
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
            Teleporter came = arriveAt != null && Teleporter.GlobalTeleporters.TryGetValue(arriveAt, out var c0) && c0 && c0.transform.IsChildOf(root.transform) ? c0 : built.entry;
            if (where.Floor == 0 && came) at = Inside(at, came);
            entryDoor = built.entry;   // (the region's own entrance, whichever door you came by: in a dungeon's first region, the way home)
            if (where.Floor > 0) at = Delve.SetUpFloor(where, at);   // the descent: arrival, stairs down, the way home
            if (placeAt is Vector3 there) { at = there; dir = Direction.Down; placeAt = null; }   // (through a town portal)
            DungeonLoot.Place(root, where, genSeed, at);   // chests
            Loading.Stage("Lighting the torches", 1f, 1f);
            await UniTask.Delay(250, ignoreTimeScale: true);

            current = where;
            // the loading screen goes once the teleport's fade has covered it (no glimpse of where you were)
            await Singleton<GameManager>.main.TeleportPlayerFade(at, dir, area, area.CameraConfiner, null, null, false, Loading.End);
            Loading.End();
            if (oldRoot && oldRoot != root) Cleanup(oldRoot, oldBounds);
            Monsters.Favour = where.Floor == 0 && settings.name.Equals(DungeonEnd.Forest, StringComparison.OrdinalIgnoreCase)
                ? (new[] { "Bumblebee", "Gnome", "Slingshot Gnome" }, 0.75) : null;   // (Nagedra's forest: mostly bees and gnomes, soldiers still about)
            try { SpawnCreatures(biome, where, at); }
            finally { Monsters.Favour = null; }
            if (where.Floor == 0) MadKing.Built(settings.name, root, at);
            DungeonEnd.Built(where, render.Regions.Count, root, at, biome);   // (Nagedra's dungeons: a boss, its chest, the way home)   // (KL: the King's soldiers; his throne room: the King)
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
        var pitFloor = new HashSet<Vector3Int>();   // (bottom-layer tiles drawn as a pit: solid unless a floor layer is over them)
        var overFloor = new HashSet<Vector3Int>();
        var bottomTiles = new Dictionary<string, int>();
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
                    // (a cut-down bush's stump on the wall-top layer, "Bush_Top_Destroyed": drawn, but you walk over it)
                    bool stump = t.Layer == TilemapMergeLayer.WallCover && tile.name.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!stump) into?.Add(to);
                    if (t.Layer is TilemapMergeLayer.Ground0 or TilemapMergeLayer.Ground1 or TilemapMergeLayer.Ground2)
                    {
                        string key = $"{t.Layer}:{tile.name}";
                        bottomTiles[key] = bottomTiles.TryGetValue(key, out int k) ? k + 1 : 1;
                    }
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
        // the pits: a pit's bottom tile with no floor drawn over it (Nature's entrance rooms: the moss is the floor, the
        // bare "MainFloor_SubDungeonC" under it is the drop; the game itself left them walkable)
        // the pits: the rooms' own "Floor_hollow" tilemaps (Nature's entrance rooms: dark holes with rocky rims), not one
        // of the layers the game merges, so nothing made them solid (the game leaves them walkable too)
        foreach (var tm in root.GetComponentsInChildren<Tilemap>(true))
        {
            if (!tm || tm.transform.IsChildOf(mergedGrid.transform) || tm.name.IndexOf("hollow", StringComparison.OrdinalIgnoreCase) < 0) continue;
            tm.CompressBounds();
            foreach (var c in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(c)) continue;
                var w = CellCenter(tm, c);
                pitFloor.Add(new Vector3Int(Mathf.FloorToInt(w.x / cell.x), Mathf.FloorToInt(w.y / cell.y), 0));
            }
        }
        walls.UnionWith(pitFloor);
        DebugLayers["pit"] = new HashSet<Vector3Int>(pitFloor);
        NetLog.Info($"Dungeon: {pitFloor.Count} hollow (pit) cells made solid; floor tiles: " + string.Join(", ", bottomTiles.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}")), toast: false);
        walls.UnionWith(covers);
        walls.ExceptWith(noWall);
        water.ExceptWith(noWater);
        int wallOnly = walls.Count;
        // water is solid (the lakes; it has a lake-bed floor tile under it, so that doesn't make it walkable), except
        // where a bridge's "remove water" layer takes it away (subtracted above): with the tiles placed like the game
        // places them, those land on the bridge decks
        // the rooms' own floors: a set piece (Nagedra's forest's "Nagedra" room) draws its floor on tilemaps of its own,
        // not on the layers the game merges, so it counted as nothing and the hole's edge walled it off. Their floor
        // tiles (named so: "..._Floor_...", "...Ground...") are floor too; pits ("hollow") and the void aren't
        var ownFloor = new HashSet<Vector3Int>();
        var ownWater = new HashSet<Vector3Int>();   // (and their own water: the same set piece's lakes, walked on before)
        var ownWall = new HashSet<Vector3Int>();    // (and their own walls: the lab's hallways draw a wall top of their own, "Walls_Top")
        foreach (var tm in root.GetComponentsInChildren<Tilemap>(true))
        {
            if (!tm || tm.transform.IsChildOf(mergedGrid.transform) || tm.GetComponent<TilemapCollider2D>()) continue;
            string tn = tm.name.ToLowerInvariant();
            if (tn.Contains("hollow") || tn.Contains("void")) continue;
            var tr = tm.GetComponent<TilemapRenderer>();
            if (!tr || !tr.enabled || !tm.gameObject.activeSelf) continue;
            tm.CompressBounds();
            foreach (var c in tm.cellBounds.allPositionsWithin)
            {
                var tile = tm.GetTile(c);
                if (!tile) continue;
                string n = tile.name.ToLowerInvariant();
                bool wet = n.Contains("water") || n.Contains("lake") || n.Contains("river") || tn.Contains("water");
                bool wall = !wet && !n.Contains("floor") && !n.Contains("ground") && (n.Contains("wall") || n.Contains("ceiling") || tn.Contains("wall"));
                if (wall)
                {
                    var ww = CellCenter(tm, c);
                    ownWall.Add(new Vector3Int(Mathf.FloorToInt(ww.x / cell.x), Mathf.FloorToInt(ww.y / cell.y), 0));
                    continue;
                }
                if (!wet && (!(n.Contains("floor") || n.Contains("ground")) || n.Contains("hollow") || n.Contains("void"))) continue;
                var w = CellCenter(tm, c);
                var at = new Vector3Int(Mathf.FloorToInt(w.x / cell.x), Mathf.FloorToInt(w.y / cell.y), 0);
                if (wet) ownWater.Add(at);
                else if (!ground.Contains(at) && !walls.Contains(at)) ownFloor.Add(at);
            }
        }
        ownWater.ExceptWith(noWater);   // (a bridge over it: walkable)
        ownWall.ExceptWith(noWall);     // (a doorway's "remove collision": open)
        walls.UnionWith(ownWall);
        DebugLayers["room's own wall"] = ownWall;
        ownFloor.ExceptWith(ownWater);
        ground.UnionWith(ownFloor);
        ground.UnionWith(ownWater);   // (water has ground under it: solid by the water rule below, not a hole's edge)
        water.UnionWith(ownWater);
        DebugLayers["room's own floor"] = ownFloor;
        DebugLayers["room's own water"] = ownWater;
        if (ownFloor.Count + ownWater.Count + ownWall.Count > 0) NetLog.Info($"Dungeon: the rooms' own tilemaps: {ownFloor.Count} floor cells, {ownWater.Count} water cells, {ownWall.Count} wall cells (solid)", toast: false);
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
        // the holes: a cell with no tile at all (the pits in the rooms, the gaps between them) had no collider either, so
        // you walked into the dark: every empty cell touching the floor (corners too) is solid
        var rim = new HashSet<Vector3Int>();
        foreach (var c in ground)
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var n = new Vector3Int(c.x + dx, c.y + dy, 0);
                    if (!ground.Contains(n) && !walls.Contains(n)) rim.Add(n);
                }
        walls.UnionWith(rim);
        DebugLayers["edge of a hole"] = rim;
        cellSize = cell;
        NetLog.Info($"Dungeon tiles: {rim.Count} empty cells at the floor's edge made solid; ground {ground.Count}, walls {wallOnly} (minus {noWall.Count} openings), water {water.Count} (minus {noWater.Count}, {groundUnderWater} under the floor, solid: {waterSolid}), walkable {floorCells.Count}, cell {cell}, from {sources.Count} tilemaps: " +
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
    /// <summary>A bottom-layer tile that's drawn as a drop (the dark chasm floor of the Nature sub-dungeon rooms).</summary>
    const float HoleShade = 0.1f;
    static readonly Dictionary<Sprite, float> shadeOf = new Dictionary<Sprite, float>();
    static readonly Dictionary<Texture2D, Texture2D> readable = new Dictionary<Texture2D, Texture2D>();

    /// <summary>How bright a sprite's drawn pixels are on average (0 black, 1 white; -1: couldn't read it). The atlases
    /// aren't readable: each is copied once through a render texture.</summary>
    static float Shade(Sprite s)
    {
        if (shadeOf.TryGetValue(s, out float v)) return v;
        v = -1f;
        try
        {
            var tex = s.texture;
            if (!readable.TryGetValue(tex, out var copy) || !copy)
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                var was = RenderTexture.active;
                RenderTexture.active = rt;
                copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                readable[tex] = copy;
            }
            var r = s.textureRect;
            var px = copy.GetPixels(Mathf.FloorToInt(r.x), Mathf.FloorToInt(r.y), Mathf.Max(1, Mathf.FloorToInt(r.width)), Mathf.Max(1, Mathf.FloorToInt(r.height)));
            double sum = 0, alpha = 0;
            foreach (var p in px) { sum += (0.299 * p.r + 0.587 * p.g + 0.114 * p.b) * p.a; alpha += p.a; }
            v = alpha > 0 ? (float)(sum / alpha) : -1f;
        }
        catch (Exception e) { NetLog.Warn($"Dungeon: couldn't read {s.name}: {e.Message}", toast: false); }
        return shadeOf[s] = v;
    }

    static bool PitTile(string name) => name.IndexOf("Floor_Moss2_SubDungeon", StringComparison.OrdinalIgnoreCase) >= 0;

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
    static void SpawnCreatures(BiomeSettings biome, Where where, Vector3 arrival)
    {
        sleeping.Clear();
        if (!biome || !root) return;
        if (Plugin.Connected && !Plugin.IOwnHere) return;
        int genSeed = Delve.SeedOf(where);
        var rng = new System.Random(genSeed ^ where.Region);
        int groups = 0;
        // each spawn area rolls its chance (as the game does); but a floor with few areas could roll none at all and
        // stand empty: at least 60% of them (all, when there are two or fewer) get creatures, the skipped ones by lot
        var areas = root.GetComponentsInChildren<EnemySpawnArea>(true).Where(a => a && a.GetComponent<PolygonCollider2D>()).ToList();
        var chosen = areas.Where(a => rng.NextDouble() < a.Chance + Delve.ExtraChance(where.Floor)).ToList();
        int least = areas.Count <= 2 ? areas.Count : Mathf.CeilToInt(areas.Count * 0.6f);
        var skipped = areas.Except(chosen).OrderBy(_ => rng.Next()).ToList();
        int added = 0;
        while (chosen.Count < least && skipped.Count > 0) { chosen.Add(skipped[0]); skipped.RemoveAt(0); added++; }
        foreach (var spawnArea in chosen)
        {
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
            var list = new List<Foe>();
            int n = Delve.GroupsFor(where.Floor, rng);
            for (int g = 0; g < n; g++)
            {
                if (Monsters.DungeonGroup(rng) is List<string> ours) { foreach (var m in ours) list.Add(new Foe(null, m)); continue; }   // (ours: a squad of troops, or a pack of creatures)
                var group = collection.Groups[rng.Next(collection.Groups.Count)];
                if (group && group.Enemies != null) foreach (var e in group.Enemies) if (e) list.Add(new Foe(e, null));
            }
            if (list.Count == 0) continue;
            sleeping.Add((shape, Vector3.zero, list, rng.Next()));
            groups++;
        }
        int extra = MoreGroups(biome, rng, arrival);
        // Nagedra's dungeons (not the descent): half as many (you couldn't take a step without running into ten): every
        // group halved (an odd one rounds up or down by chance), the empty ones gone; spread as before
        if (where.Floor == 0)
        {
            int before = sleeping.Sum(x => x.creatures.Count);
            for (int g = sleeping.Count - 1; g >= 0; g--)
            {
                var foes = sleeping[g].creatures;
                int keep = (foes.Count + rng.Next(2)) / 2;
                foes.RemoveRange(keep, foes.Count - keep);
                if (foes.Count == 0) sleeping.RemoveAt(g);
            }
            NetLog.Info($"Dungeon: creatures halved for a dungeon (not a descent floor): {before} -> {sleeping.Sum(x => x.creatures.Count)}", toast: false);
        }
        sleepingCreatures = sleeping.Sum(x => x.creatures.Count);
        if (extra > 0) NetLog.Info($"Dungeon: {extra} more groups spread over the floor (too few in its spawn areas)", toast: false);
        NetLog.Info($"Dungeon: {groups} of {areas.Count} spawn areas{(added > 0 ? $" ({added} added: too few rolled)" : "")}, {sleepingCreatures} creatures waiting (they appear as you come near; at most {Delve.MaxEnemies} at once)", toast: false);
        if (sleepingCreatures == 0) NetLog.Warn($"Dungeon: floor {where.Floor} has no creatures ({areas.Count} spawn areas, biome {biome.name})", toast: false);
        nextWake = 0f;
    }

    /// <summary>One creature waiting in a spawn area: the game's (its prefab) or ours (MonsterArt's name).</summary>
    readonly struct Foe
    {
        public readonly Enemy Prefab;
        public readonly string Monster;
        public Foe(Enemy prefab, string monster) { Prefab = prefab; Monster = monster; }
    }

    /// <summary>
    /// A floor with too few creatures in its spawn areas (few areas, or none: once just one creature on a whole floor):
    /// more groups out on the floor itself, away from where you came in and from each other, until it has the floor's
    /// number (MaxEnemiesAlive; 60 if that's off). Ours or the biome's, as the spawn areas mix them. The groups added.
    /// </summary>
    static int MoreGroups(BiomeSettings biome, System.Random rng, Vector3 arrival)
    {
        int want = Delve.MaxEnemies > 0 ? Delve.MaxEnemies : 60;
        // no more than the place has room for: one per 30 walkable cells (a one-room dungeon, the library, had all 100
        // crammed into its single room)
        want = Mathf.Min(want, Mathf.Max(6, floorCells.Count / 30));
        int have = sleeping.Sum(x => x.creatures.Count);
        if (have >= want || floorCells.Count == 0) return 0;
        var cells = floorCells.OrderBy(c => c.x).ThenBy(c => c.y).ToList();
        var game = new[] { biome.MediumSpawnCollection, biome.SmallSpawnCollection, biome.BigSpawnCollection }
            .Where(c => c && c.Groups != null && c.Groups.Count > 0).ToList();
        var spots = new List<Vector3>();
        int added = 0;
        for (int tries = 0; tries < 2000 && have < want; tries++)
        {
            var c = cells[rng.Next(cells.Count)];
            var at = new Vector3((c.x + 0.5f) * cellSize.x, (c.y + 0.5f) * cellSize.y, 0f);
            float gap = tries < 1000 ? 8f : 3f;   // (spread out; closer together if the floor is small)
            if (((Vector2)(at - arrival)).sqrMagnitude < 12f * 12f || spots.Any(s => ((Vector2)(s - at)).sqrMagnitude < gap * gap)) continue;
            if (!UnityEngine.AI.NavMesh.SamplePosition(at, out var h, 0.6f, UnityEngine.AI.NavMesh.AllAreas)) continue;
            var list = new List<Foe>();
            if (Monsters.DungeonGroup(rng) is List<string> ours) list.AddRange(ours.Select(m => new Foe(null, m)));
            else if (game.Count > 0)
            {
                var g = game[rng.Next(game.Count)];
                var group = g.Groups[rng.Next(g.Groups.Count)];
                if (group && group.Enemies != null) foreach (var e in group.Enemies) if (e) list.Add(new Foe(e, null));
            }
            if (list.Count == 0 && Monsters.DungeonGroup(rng, always: true) is List<string> any) list.AddRange(any.Select(m => new Foe(null, m)));
            if (list.Count == 0) { if (game.Count == 0) break; continue; }   // (nothing at all to put there)
            spots.Add(h.position);
            sleeping.Add((null, h.position, list, rng.Next()));
            have += list.Count;
            added++;
        }
        return added;
    }

    /// <summary>Spawn areas still waiting: the room's area (its shape), or a spot out on the floor (MoreGroups: no shape).</summary>
    static readonly List<(PolygonCollider2D shape, Vector3 spot, List<Foe> creatures, int seed)> sleeping = new List<(PolygonCollider2D, Vector3, List<Foe>, int)>();

    static Vector3 PlaceIn(PolygonCollider2D shape, Vector3 spot)
    {
        if (shape) return Utils.RandomInsidePolygon(shape);
        var p = spot + (Vector3)(UnityEngine.Random.insideUnitCircle * 1.6f);
        return UnityEngine.AI.NavMesh.SamplePosition(p, out var h, 0.6f, UnityEngine.AI.NavMesh.AllAreas) ? h.position : spot;
    }
    static int leftCount;
    static float leftAt = -10f;

    /// <summary>Creatures still on this floor: the ones alive on it now (anyone's: the creature sync's too), and the ones
    /// still waiting in spawn areas nobody has come near (known where this copy spawns them). Counted twice a second.</summary>
    public static int CreaturesLeft
    {
        get
        {
            if (Time.unscaledTime - leftAt < 0.5f) return leftCount;
            leftAt = Time.unscaledTime;
            int n = 0;
            var b = bounds;
            b.Expand(new Vector3(4f, 4f, 1000f));
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (!e || e.IsDead || !e.gameObject.activeInHierarchy || e.enemyType == EnemyType.None || e.name.StartsWith("NPC_")) continue;
                var p = e.transform.position;
                if (b.Contains(new Vector3(p.x, p.y, b.center.z))) n++;
            }
            foreach (var s in sleeping) n += s.creatures.Count;
            return leftCount = n;
        }
    }
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
        // (each creature once: the pool hands a killed one out again, so it can be on the list more than once, and
        // counting it each time filled the limit with ghosts: nothing more came)
        spawned.RemoveAll(go => !go);
        int alive = 0;
        foreach (var go in spawned.Distinct()) if (go.activeInHierarchy && go.GetComponent<Enemy>() is Enemy e && !e.IsDead) alive++;
        int max = Delve.MaxEnemies > 0 ? Delve.MaxEnemies : int.MaxValue;
        for (int i = sleeping.Count - 1; i >= 0 && alive < max; i--)
        {
            var (shape, spot, creatures, seed) = sleeping[i];
            if ((object)shape != null && !shape) { sleeping.RemoveAt(i); continue; }   // (its room's gone)
            var c = shape ? shape.bounds.center : spot;
            bool close = false;
            foreach (var p in near) if (((Vector2)(p - c)).sqrMagnitude < WakeRange * WakeRange) { close = true; break; }
            if (!close) continue;
            sleeping.RemoveAt(i);
            var oldState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            int woke = 0;
            try
            {
                foreach (var foe in creatures)
                {
                    if (alive >= max) break;
                    woke++;
                    if (foe.Monster != null)   // (ours: made fresh, and gone with the floor)
                    {
                        var m = Monsters.Spawn(foe.Monster, PlaceIn(shape, spot));
                        if (!m) continue;
                        if (root) m.transform.SetParent(root.transform, true);
                        spawned.Add(m.gameObject);
                        alive++;
                        continue;
                    }
                    var enemy = foe.Prefab;
                    PoolManager.CreatePool(enemy.gameObject, enemy.ID, 5, disableFirst: true);
                    var go = PoolManager.Get(enemy.gameObject);
                    go.transform.position = PlaceIn(shape, spot);
                    go.GetComponent<Enemy>().ForceAlive();
                    go.SetActive(true);
                    if (!spawned.Contains(go)) spawned.Add(go);
                    alive++;
                }
            }
            catch (Exception e) { NetLog.Warn($"Dungeon: creatures: {e.Message}", toast: false); }
            finally { UnityEngine.Random.state = oldState; }
            // the limit was reached halfway through this area's group: the rest stay asleep there, and come once
            // there's room again (they were simply lost before, so busy floors ran dry)
            if (woke < creatures.Count) sleeping.Add((shape, spot, creatures.GetRange(woke, creatures.Count - woke), seed + woke));
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
        // a dungeon you came straight into (/dungeon negedra, Nagedra): its own entrance leads to itself; it's the way out
        if (settings.name == c.Settings) { NetLog.Info("Dungeon: out through the way you came in", toast: false); Leave(); return; }
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
            if (Active && ThroneRoom.Owns(__instance)) { __result = UniTask.CompletedTask; ThroneRoom.Back(); return false; }   // (the throne room's doors: back to KL)
            if (!Active || !root || !__instance.transform.IsChildOf(root.transform)) return true;
            // the door you came in by, in a dungeon's first region (not a descent floor, not a cave inside): home
            if (!busy && current.Value.Floor <= 0 && current.Value.Region == 0 && parents.Count == 0 && __instance == entryDoor)
            {
                __result = UniTask.CompletedTask;
                var gm = Singleton<GameManager>.main;
                if (gm) gm.gameState = GameState.Playing;
                NetLog.Info("Dungeon: out the way you came in: home", toast: false);
                Leave();
                return false;
            }
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

    /// <summary>An entrance's arrival point can sit outside the map (Negedra's forest, Morcha's hideout: north of the
    /// doorway, on dark ground that still counts as walkable, so the first step goes back through the door): instead,
    /// 3 squares from the door the way it faces (the way you face arriving through it, its glow spreading into the
    /// room), on the walkable ground there.</summary>
    internal static Vector3 Inside(Vector3 at, Teleporter door)
    {
        var d = door.SpawnDirection;
        var into = d == Direction.Up ? Vector2.up : d == Direction.Down ? Vector2.down : d == Direction.Left ? Vector2.left : Vector2.right;
        var from = door.transform.position;
        var want = from + (Vector3)(into * 3f);
        var p = want;
        if (UnityEngine.AI.NavMesh.SamplePosition(new Vector3(want.x, want.y, 0f), out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) p = hit.position;
        var glow = string.Join(", ", door.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.sprite).Take(4)
            .Select(r => $"{r.sprite.name} at {r.transform.position.x - from.x:+0.0;-0.0},{r.transform.position.y - from.y:+0.0;-0.0}{(r.flipY ? " flipped" : "")} rot {r.transform.eulerAngles.z:0}"));
        NetLog.Info($"Dungeon: arrival {d} of the door {door.name} at {from.x:0.0},{from.y:0.0} (its own spot {at.x:0.0},{at.y:0.0}) -> {p.x:0.0},{p.y:0.0}; door sprites: {glow}", toast: false);
        return new Vector3(p.x, p.y, at.z);
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

    /// <summary>An exit whose other end isn't in our region: in a dungeon of several regions, on to the next one (KL's way
    /// down); else it's in the scene the demo doesn't have: take us home.</summary>
    [HarmonyPatch(typeof(TeleporterChunkResolver), nameof(TeleporterChunkResolver.Teleport))]
    static class ExitPatch
    {
        static bool Prefix(TeleporterChunkResolver __instance, ref UniTask __result)
        {
            if (!Active) return true;
            // KL's doorways to Kaldhor's throne room (a place of its own: ThroneRoom); its own doors lead back
            if (ThroneRoom.Owns(__instance)) { __result = UniTask.CompletedTask; ThroneRoom.Back(); return false; }
            if (ThroneRoom.Leads(__instance.otherTeleporterID)) { __result = UniTask.CompletedTask; ThroneRoom.Enter(__instance.otherTeleporterID, __instance.teleporter); return false; }
            if (!string.IsNullOrEmpty(__instance.otherTeleporterID) && Teleporter.GlobalTeleporters.TryGetValue(__instance.otherTeleporterID, out var t) && t) return true;
            __result = UniTask.CompletedTask;
            var gm = Singleton<GameManager>.main;
            if (gm) gm.gameState = GameState.Playing;
            if (parents.Count > 0) { Go(parents.Pop(), null, true).Forget(); return false; }
            // a dungeon of more than one region (KL: its way down to the throne): its far end is in the next region,
            // not built yet; there, arriving at that end (the door it names) if it's in it
            var c = current.Value;
            // the last region's way out (the lab's basement; a one-region dungeon's exit): home, beside Nagedra if you
            // came from her (not KL: its regions lead on to the throne)
            bool last = render == null || c.Region >= render.Regions.Count - 1;
            if (c.Floor <= 0 && last && c.Settings.IndexOf("_KL", StringComparison.OrdinalIgnoreCase) < 0)
            {
                NetLog.Success("You found the way out!", toast: true);
                LeaveToNagedra();
                return false;
            }
            if (c.Floor <= 0 && render != null && render.Regions.Count > 1)
            {
                int next = (c.Region + 1) % render.Regions.Count;
                NetLog.Info($"Dungeon: the way on, to region {next} of {render.Regions.Count} (its door {__instance.otherTeleporterID})", toast: false);
                Go(c with { Region = next }, __instance.otherTeleporterID, true).Forget();
                return false;
            }
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
/// Some of the dungeons the demo never opens (Negedra's forest) have gathering spots with parts missing: their Start
/// throws. In a generated dungeon such a spot is taken away, quietly, instead of standing there half made.
/// </summary>
[HarmonyPatch(typeof(GatheringObject), "Start")]
internal static class BrokenGathering
{
    static int told;

    /// <summary>Part of a dungeon we built (it can wake as the floor is switched on, before we count as in it).</summary>
    static bool InOurs(Transform t)
    {
        for (; t; t = t.parent) if (t.name.StartsWith("EG_Dungeon_")) return true;
        return false;
    }

    static System.Exception Finalizer(GatheringObject __instance, System.Exception __exception)
    {
        if (__exception == null || !__instance || !(Dungeon.Active || InOurs(__instance.transform))) return __exception;
        if (told++ < 3) NetLog.Info($"Dungeon: a gathering spot with parts missing taken away ({__instance.name})", toast: false);
        __instance.gameObject.SetActive(false);
        return null;
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

using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using ImGuiNET;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The boss arena: a walled stone floor of our own, far off the map (the floor and wall tiles are the dungeon rooms'
/// own), with a panel to make mini-bosses: any of the game's creatures, a preset or your own name, colour, size,
/// health and damage; spawn it (or plain creatures), clear them, leave. Reached from the second pad next to the
/// descent's pad. Just for you (it's somewhere nobody else is).
/// </summary>
internal static partial class Arena
{
    static readonly Vector3 Origin = new Vector3(-4000f, 4000f, 0f);
    static readonly (float x, float y)[] Pillars = { (0.13f, 0.82f), (0.87f, 0.82f), (0.33f, 0.52f), (0.67f, 0.52f), (0.13f, 0.2f), (0.87f, 0.2f) };
    const int W = 30, H = 20;

    static GameObject root;
    static GameArea area;
    static Vector3 returnPos;
    static GameArea returnArea;
    static bool busy;
    static readonly List<GameObject> spawned = new List<GameObject>();

    public static bool Active => root && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.CurrentArea == area;

    public static void Init() => SceneManager.sceneLoaded += (_, _) => { root = null; area = null; spawned.Clear(); busy = false; };

    public static void Enter()
    {
        if (busy || Active || Dungeon.Active || !LocalPlayer.Get() || !Singleton<GameManager>.HasMain) return;
        busy = true;
        try
        {
            var gm = Singleton<GameManager>.main;
            returnPos = LocalPlayer.Get().transform.position;
            returnArea = gm.CurrentArea;
            Build();
            gm.TeleportPlayerFade(Origin + new Vector3(W / 2f, 3f, 0f), Direction.Up, area, area.CameraConfiner).ContinueWith(() => busy = false).Forget();
            NetLog.Info("The boss arena: make a mini-boss in the panel on the right", toast: true);
            Dungeon.AnnounceArena(rush: false);   // teammates get an invite card
        }
        catch (System.Exception e)
        {
            busy = false;
            NetLog.Warn($"Arena: {e.Message}", toast: true);
            Debug.LogException(e);
            if (root) { DeathSafety.Forget(root); Object.Destroy(root); }
        }
    }

    public static void Leave()
    {
        if (!Active || busy) return;
        EndChickens("over (you left)");
        var gm = Singleton<GameManager>.main;
        var old = root;
        DeathSafety.Forget(old);   // the creatures go with the arena, after the teleport (removing them mid-fade froze it)
        int gone = ClearLoot();
        if (gone > 0) NetLog.Info($"Left the arena: {gone} items left lying there are gone", toast: true);
        spawned.Clear();
        if (!returnArea) { Object.Destroy(old); return; }
        busy = true;
        gm.TeleportPlayerFade(returnPos, Direction.Down, returnArea, returnArea.CameraConfiner).ContinueWith(() => { busy = false; if (old) Object.Destroy(old); }).Forget();
    }

    /// <summary>Loot on the arena's floor (bosses' drops, creatures' drops): back to the game's pool, i.e. gone.</summary>
    static int ClearLoot()
    {
        var area = new Rect(Origin.x - 3f, Origin.y - 3f, W + 6f, H + 6f);
        int n = 0;
        foreach (var drop in Object.FindObjectsByType<ItemDropObject>(FindObjectsSortMode.None))
        {
            if (!drop || !drop.gameObject.activeInHierarchy || !area.Contains((UnityEngine.Vector2)drop.transform.position)) continue;
            drop.gameObject.SetActive(false);
            n++;
        }
        return n;
    }

    static void ClearSpawned()
    {
        foreach (var go in spawned)
        {
            if (!go) continue;
            DeathSafety.Forget(go);
            Object.Destroy(go);
        }
        spawned.Clear();
    }

    // ---- the place ----

    static void Build()
    {
        if (root) { DeathSafety.Forget(root); Object.Destroy(root); }
        root = new GameObject("EG_BossArena");
        root.SetActive(false);
        area = root.AddComponent<GameArea>();
        var gridGo = new GameObject("Tiles");
        gridGo.transform.SetParent(root.transform, false);
        var grid = gridGo.AddComponent<Grid>();

        // the floor and wall tiles of a dungeon room, with their renderers' settings
        var (groundTile, groundR, wallTile, wallR, cell) = RoomTiles();
        grid.cellSize = cell;
        var ground = new HashSet<Vector3Int>();
        var walls = new HashSet<Vector3Int>();
        int ox = Mathf.FloorToInt(Origin.x / cell.x), oy = Mathf.FloorToInt(Origin.y / cell.y);
        for (int x = -1; x <= W; x++)
            for (int y = -1; y <= H; y++)
            {
                var c = new Vector3Int(ox + x, oy + y, 0);
                if (x < 0 || y < 0 || x >= W || y >= H) walls.Add(c); else ground.Add(c);
            }
        // six pillars to dodge behind: a column at each side top and bottom, two in the middle
        foreach (var (fx, fy) in Pillars)
        {
            int px = Mathf.RoundToInt(fx * W), py = Mathf.RoundToInt(fy * H);
            for (int x = px - 1; x <= px; x++)
                for (int y = py - 1; y <= py; y++)
                {
                    var c = new Vector3Int(ox + x, oy + y, 0);
                    ground.Remove(c);
                    walls.Add(c);
                }
        }
        Paint(gridGo, "Ground", ground, groundTile, groundR, 0);
        Paint(gridGo, "Walls", walls, wallTile ?? groundTile, wallR ?? groundR, 1);

        Dungeon.NavLayers(root.transform, cell, ground, walls, out var walkable, out var blocked);
        try { ArenaTraps.Place(root.transform, Origin, W, H); }   // spikes and arrow shooters
        catch (System.Exception e) { NetLog.Warn($"Arena traps: {e.Message}", toast: false); }

        var confinerGo = new GameObject("CameraConfiner");
        confinerGo.transform.SetParent(root.transform, false);
        var confiner = confinerGo.AddComponent<PolygonCollider2D>();
        confiner.isTrigger = true;
        var sceneConfiner = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(a => a.CameraConfiner).FirstOrDefault(c => c);
        if (sceneConfiner) confinerGo.layer = sceneConfiner.gameObject.layer;
        float m = 4f;
        confiner.points = new[]
        {
            new UnityEngine.Vector2(Origin.x - m, Origin.y - m), new UnityEngine.Vector2(Origin.x + W * cell.x + m, Origin.y - m),
            new UnityEngine.Vector2(Origin.x + W * cell.x + m, Origin.y + H * cell.y + m), new UnityEngine.Vector2(Origin.x - m, Origin.y + H * cell.y + m)
        };
        area.cameraConfiner = confiner;
        root.SetActive(true);
        area.Setup();
        Dungeon.BuildNavMesh(walkable, blocked).ContinueWith(() => { if (root && Singleton<GameManager>.main.CurrentArea != area) root.SetActive(false); }).Forget();
    }

    internal static void Paint(GameObject grid, string name, HashSet<Vector3Int> cells, TileBase tile, TilemapRenderer like, int orderBump)
    {
        if (!tile) return;
        var go = new GameObject(name);
        go.transform.SetParent(grid.transform, false);
        var tm = go.AddComponent<Tilemap>();
        var r = go.AddComponent<TilemapRenderer>();
        if (like)
        {
            r.sortingLayerID = like.sortingLayerID;
            r.sortingOrder = like.sortingOrder + orderBump;
            r.sharedMaterial = like.sharedMaterial;
            r.mode = like.mode;
        }
        var arr = cells.ToArray();
        tm.SetTiles(arr, Enumerable.Repeat(tile, arr.Length).ToArray());
        tm.CompressBounds();
    }

    /// <summary>The most used floor tile and wall tile of the dungeon rooms (and the renderers they're drawn with).</summary>
    internal static (TileBase ground, TilemapRenderer groundR, TileBase wall, TilemapRenderer wallR, Vector3 cell) RoomTiles()
    {
        Dungeon.AllSettings();
        TileBase g = null, w = null; TilemapRenderer gr = null, wr = null; Vector3 cell = Vector3.one;
        foreach (var chunk in Resources.FindObjectsOfTypeAll<DungeonChunk>().Where(c => c && !c.gameObject.scene.IsValid()).OrderBy(c => c.name))
        {
            if (chunk.Tilemaps == null) continue;
            foreach (var t in chunk.Tilemaps)
            {
                if (!t.Tilemap) continue;
                if (t.Layer == TilemapMergeLayer.Ground0 && !g) { g = MostUsed(t.Tilemap); gr = t.Tilemap.GetComponent<TilemapRenderer>(); if (t.Tilemap.layoutGrid) cell = t.Tilemap.layoutGrid.cellSize; }
                if (t.Layer == TilemapMergeLayer.Wall && !w) { w = MostUsed(t.Tilemap); wr = t.Tilemap.GetComponent<TilemapRenderer>(); }
            }
            if (g && w) break;
        }
        return (g, gr, w, wr, cell);
    }

    internal static TileBase MostUsed(Tilemap tm)
    {
        var count = new Dictionary<TileBase, int>();
        foreach (var c in tm.cellBounds.allPositionsWithin)
        {
            var t = tm.GetTile(c);
            if (t) count[t] = count.TryGetValue(t, out int n) ? n + 1 : 1;
        }
        return count.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
    }

    // ---- the panel ----

    static int preset, basePick, plainPick;
    static string bossName = "Rosalind the Pink";
    static ImGuiNET.Vector3 tint = new ImGuiNET.Vector3(1f, 0.45f, 0.85f);
    static float scale = 1.7f, health = 2f, damage = 1.2f;
    static int count = 1;
    static Rect panelRect;
    static bool hovered;

    public static bool WantsMouse => Active && hovered;

    /// <summary>Every frame: is the mouse over the panel (then ImGui gets it and the game's controls pause)?</summary>
    public static void Tick()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        hovered = false;
        if (Active && mouse != null && Application.isFocused && panelRect.width > 0 && !PlayerStatusUtil.LocalBusy)
        {
            var p = mouse.position.ReadValue();
            hovered = panelRect.Contains(new UnityEngine.Vector2(p.x, Screen.height - p.y));
        }
        if (hovered || ImGuiUnity.Typing && Active) GameInput.Block("arena"); else GameInput.Unblock("arena");
        Rush();
        Chickens();
        Engage();
    }

    static float nextEngage;

    /// <summary>The arena's creatures always know where you are (on their own they only notice what they see).</summary>
    static void Engage()
    {
        if (!Active || Time.unscaledTime < nextEngage) return;
        nextEngage = Time.unscaledTime + 0.5f;
        var me = LocalPlayer.Get();
        if (!me) return;
        foreach (var go in spawned)
        {
            var e = go ? go.GetComponent<Enemy>() : null;
            if (!e || e.IsDead || e.Group == null) continue;
            e.Group.ForceEngage = true;
            e.Group.LastPlayerSeenPosition = me.transform.position;
        }
    }

    // ---- boss rush: the roster in order, each one tougher, the next once the last is down ----

    static int rushWave, bestRush;
    static Enemy rushBoss;
    static float rushNextAt;
    const float RushGap = 4f;

    /// <summary>On top of the roster's own numbers: each wave +35% health and +10% damage, and a bit bigger.</summary>
    /// <summary>The rush's own curve (not the roster's numbers): 2x health at wave 1, +0.3x a wave (about 10.7x by wave
    /// 30); damage from 1x, +5% a wave (2.45x by wave 30).</summary>
    static float RushHealth(int wave) => 2f + 0.3f * (wave - 1);
    static float RushDamage(int wave) => 1f + 0.05f * (wave - 1);

    static void StartRush()
    {
        EndChickens("stopped");
        ClearSpawned();
        rushWave = 0;
        rushBoss = null;
        rushNextAt = Time.unscaledTime + 2f;
        NetLog.Warn($"BOSS RUSH: {MiniBoss.Presets.Length} bosses. First one in 2...", toast: true);
        folded = true;   // out of the way while fighting
        rushWave = 1;
        rushPending = true;
        Dungeon.AnnounceArena(rush: true);
    }

    static bool rushPending;

    static void EndRush(string why)
    {
        if (rushWave == 0) return;
        int reached = rushBoss && !rushBoss.IsDead ? rushWave - 1 : rushWave;
        if (why == "won") reached = MiniBoss.Presets.Length;
        bestRush = Mathf.Max(bestRush, reached);
        NetLog.Info($"Boss rush {why}: {reached} of {MiniBoss.Presets.Length} beaten (best {bestRush})", toast: true);
        rushWave = 0;
        rushBoss = null;
        rushPending = false;
        ArenaTraps.On = false;
    }

    static void Rush()
    {
        if (rushWave == 0) return;
        if (!Active) { EndRush("left"); return; }
        var me = LocalPlayer.Get();
        if (me && me.IsDead) { EndRush("over: you fell"); return; }
        if (rushPending)
        {
            if (Time.unscaledTime < rushNextAt) return;
            rushPending = false;
            SpawnWave(rushWave);
            return;
        }
        if (rushBoss && !rushBoss.IsDead) return;
        // down: the next one, or the end
        if (rushWave >= MiniBoss.Presets.Length) { EndRush("won"); NetLog.Success("You beat every boss in the rush!", toast: true); return; }
        bestRush = Mathf.Max(bestRush, rushWave);
        rushWave++;
        rushPending = true;
        rushNextAt = Time.unscaledTime + RushGap;
        NetLog.Info($"Wave {rushWave - 1} down! Wave {rushWave} in {RushGap:0}...", toast: true);
    }


    static void SpawnWave(int wave)
    {
        ArenaTraps.ForWave(wave);
        var p = MiniBoss.Presets[wave - 1];
        var baseE = MiniBoss.ForPreset(wave - 1);
        if (!baseE) { EndRush("stopped (no creatures)"); return; }
        var look = new MiniBoss.Look
        {
            Name = $"{p.label}  (wave {wave})",
            Base = baseE.name,
            Tint = p.tint,
            Scale = p.scale * (1f + 0.01f * wave),
            Health = RushHealth(wave),
            Damage = RushDamage(wave),
            LootFloor = 1 + wave / 2,
        };
        // the far side of the arena from you
        var me = LocalPlayer.Get();
        var center = Origin + new Vector3(W / 2f, H / 2f, 0f);
        var at = me ? center + (center - me.transform.position).normalized * 7f : center + Vector3.up * 6f;
        at.x = Mathf.Clamp(at.x, Origin.x + 3f, Origin.x + W - 3f);
        at.y = Mathf.Clamp(at.y, Origin.y + 3f, Origin.y + H - 3f);
        rushBoss = MiniBoss.Spawn(look, at, root.transform);
        if (rushBoss) spawned.Add(rushBoss.gameObject);
        NetLog.Warn($"Wave {wave}: {p.label}!", toast: true);
    }

    public static void Draw()
    {
        if (!Active) { panelRect = default; return; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x - 12f, vp.WorkPos.y + 90f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.88f);
        var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
        if (folded) flags |= ImGuiWindowFlags.NoTitleBar;
        if (!ImGui.Begin("Boss arena##arena", flags))
        { ImGui.End(); return; }

        // folded: one small line (the rush, and the way out); unfold for the boss maker
        if (ImGui.SmallButton(folded ? "+##fold" : "-##fold")) folded = !folded;
        ImGui.SameLine();
        if (folded)
        {
            if (chickensOn)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"No chicken level: {Clock(Survived)}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop##chickens")) EndChickens("stopped");
            }
            else if (rushWave > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), $"Rush: wave {rushWave}/{MiniBoss.Presets.Length}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Stop")) EndRush("stopped");
            }
            else
            {
                ImGui.TextDisabled("Boss arena");
                ImGui.SameLine();
                if (ImGui.SmallButton("Boss rush")) StartRush();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Leave")) Leave();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to the pads. Loot still on the floor here is lost!");
            if (!chickensOn && rushWave == 0 && ImGui.SmallButton("There is no chicken level")) StartChickens();   // (its own row)
            FoldEnd();
            return;
        }

        var list = MiniBoss.Prefabs();
        if (list.Count == 0) { ImGui.TextDisabled("No creatures loaded yet..."); FoldEnd(); return; }

        ImGui.TextColored(new Vector4(1f, 0.7f, 0.35f, 1f), "Make a mini-boss");
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Preset", MiniBoss.Presets[preset].label))
        {
            for (int i = 0; i < MiniBoss.Presets.Length; i++)
                if (ImGui.Selectable(MiniBoss.Presets[i].label, i == preset)) UsePreset(i, list);
            ImGui.EndCombo();
        }
        basePick = Mathf.Clamp(basePick, 0, list.Count - 1);
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Creature", Label(list[basePick])))
        {
            for (int i = 0; i < list.Count; i++) if (ImGui.Selectable(Label(list[i]) + "##b" + i, i == basePick)) basePick = i;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(220); ImGui.InputText("Name", ref bossName, 40);
        ImGui.SetNextItemWidth(220); ImGui.ColorEdit3("Colour", ref tint);
        ImGui.SetNextItemWidth(220); ImGui.SliderFloat("Size", ref scale, 0.5f, 4f, "%.1fx");
        ImGui.SetNextItemWidth(220); ImGui.SliderFloat("Health", ref health, 1f, 60f, "%.1fx");
        ImGui.SetNextItemWidth(220); ImGui.SliderFloat("Damage", ref damage, 0.25f, 10f, "%.2fx");
        if (ImGui.Button("Spawn the boss")) SpawnBoss(list[basePick]);
        ImGui.SameLine();
        ImGui.TextDisabled("(in front of you)");

        ImGui.Separator();
        if (chickensOn)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"There is no chicken level: {Clock(Survived)} (wave {chickenWave})");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##chickens2")) EndChickens("stopped");
        }
        else
        {
            if (ImGui.Button("There is no chicken level")) StartChickens();
            ImGui.SameLine();
            ImGui.TextDisabled(bestChickens.Value > 0f ? $"survive the chickens (best: {Clock(bestChickens.Value)})" : "survive the chickens as long as you can");
        }
        if (rushWave == 0)
        {
            if (ImGui.Button("  BOSS RUSH  ")) StartRush();
            ImGui.SameLine();
            ImGui.TextDisabled(bestRush > 0 ? $"all {MiniBoss.Presets.Length}, one after another (best: wave {bestRush})" : $"all {MiniBoss.Presets.Length}, one after another, harder each time");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), $"Boss rush: wave {rushWave} of {MiniBoss.Presets.Length}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop")) EndRush("stopped");
        }

        ImGui.Separator();
        ImGui.Text("Plain creatures");
        plainPick = Mathf.Clamp(plainPick, 0, list.Count - 1);
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("##plain", Label(list[plainPick])))
        {
            for (int i = 0; i < list.Count; i++) if (ImGui.Selectable(Label(list[i]) + "##p" + i, i == plainPick)) plainPick = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine(); ImGui.SetNextItemWidth(70); ImGui.InputInt("##count", ref count);
        count = Mathf.Clamp(count, 1, 20);
        ImGui.SameLine();
        if (ImGui.SmallButton("Spawn")) SpawnPlain(list[plainPick], count);

        ImGui.Separator();
        bool traps = ArenaTraps.On;
        if (rushWave > 0) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Traps", ref traps)) ArenaTraps.On = traps;
        if (rushWave > 0) { ImGui.EndDisabled(); if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip($"In a rush: spikes from wave {ArenaTraps.SpikesFrom}, arrows from wave {ArenaTraps.ShootersFrom}"); }
        ImGui.SameLine();
        if (ImGui.Button("Clear all")) ClearSpawned();
        ImGui.SameLine();
        if (ImGui.Button("Leave the arena")) Leave();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to the pads. Loot still on the floor here is lost!");
        ImGui.TextDisabled($"{spawned.Count(s => s)} spawned. Bosses drop loot when they die.");

        var wp = ImGui.GetWindowPos(); var ws = ImGui.GetWindowSize();
        panelRect = new Rect(wp.x, wp.y, ws.x, ws.y);
        ImGui.End();
    }

    static bool folded = true;   // starts small; "+" opens the boss maker

    static void FoldEnd()
    {
        var wp = ImGui.GetWindowPos(); var ws = ImGui.GetWindowSize();
        panelRect = new Rect(wp.x, wp.y, ws.x, ws.y);
        ImGui.End();
    }

    static string Label(Enemy e) => $"{e.name} ({e.enemyType})";

    static void UsePreset(int i, List<Enemy> list)
    {
        preset = i;
        var p = MiniBoss.Presets[i];
        var e = MiniBoss.ForPreset(i);
        if (e) basePick = Mathf.Max(0, list.IndexOf(e));
        bossName = p.label;
        tint = new ImGuiNET.Vector3(p.tint.r, p.tint.g, p.tint.b);
        scale = p.scale; health = MiniBoss.PresetHealth(i); damage = MiniBoss.PresetDamage(i);
    }

    static Vector3 InFront()
    {
        var me = LocalPlayer.Get();
        var at = me ? me.transform.position + Vector3.up * 4f : Origin + new Vector3(W / 2f, H / 2f, 0f);
        at.x = Mathf.Clamp(at.x, Origin.x + 2f, Origin.x + W - 2f);
        at.y = Mathf.Clamp(at.y, Origin.y + 2f, Origin.y + H - 2f);
        return at;
    }

    static void SpawnBoss(Enemy baseE)
    {
        var look = new MiniBoss.Look
        {
            Name = string.IsNullOrWhiteSpace(bossName) ? baseE.name : bossName.Trim(),
            Base = baseE.name,
            Tint = new Color(tint.x, tint.y, tint.z),
            Scale = scale, Health = health, Damage = damage, LootFloor = 5,
        };
        var e = MiniBoss.Spawn(look, InFront(), root.transform);
        if (e) spawned.Add(e.gameObject);
    }

    static void SpawnPlain(Enemy baseE, int n)
    {
        var center = InFront();
        for (int i = 0; i < n; i++)
        {
            var at = center + (Vector3)(Random.insideUnitCircle * 2.5f);
            var e = MiniBoss.SpawnPlain(baseE.ID, at, root.transform);
            if (e) spawned.Add(e.gameObject);
        }
    }
}

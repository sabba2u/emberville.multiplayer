using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace BetterWeather;

/// <summary>
/// Snow: pixel flakes drifting down over everything in view, swaying, pushed along by a share of the wind; where one
/// lands is only to look at. The settling is a tilemap of our own laid over the map's ground (the same grid, on the
/// layer things stand on, under them), following one depth for the whole map (how long it has snowed; melting back
/// when it stops): each tile, a fixed delay behind it (from where it is: the same on every game), goes up a stage, from a few white pixels to clumps, patches and finally white all over (32 stages, the first few see-through, 3 drawings of
/// each so the ground doesn't tile, thinning out toward water and cliffs; lit like the map's own tiles, so it's blue at night). Water swallows flakes;
/// walls, cliffs and pits get none. When it stops (or you're indoors), the snow slowly melts back, stage by stage.
/// Each game settles its own (the same storm, not the same flakes).
/// </summary>
internal static class Snow
{
    const int MaxFlakes = 1400, Stages = 32, Variants = 3;
    const int SeeThrough = 6;          // the first stages: a dusting the ground shows through
    const float Medium = 350f;          // flakes in the air at density 1
    const float SecondsPerStage = 13f;  // of snowing, for a tile to go up a stage (at settle 1): ~7 min to white
    const float Spread = 60f;           // seconds: how much later some tiles start than others (uneven cover)
    const float MeltSeconds = 90f;      // from full cover to bare, once it stops
    const float Margin = 6f;            // world units past the screen's edge kept up to date (it's ready as you walk)

    sealed class Flake { public SpriteRenderer Sr; public Vector3 From, To; public float Born, Life, Phase, Sway, Freq; public bool Live; }
    sealed class Cell { public int Stage, Variant, Edges = -1; public bool Looked, Ok; }   // Edges: which neighbours take no snow (bits, -1 = not looked yet)

    static readonly List<Flake> flakes = new();
    static GameObject root;
    static Sprite[] flakeSprites;
    static Material unlit;
    static float owed, nextSettle, nextScan;
    /// <summary>How much it has snowed (seconds of full snow, at the settle speed), melting back when it stops: the
    /// same for the whole map, so every tile follows it, seen or not.</summary>
    static float depth;

    // the settled snow
    static GameObject gridGo;
    static Tilemap snowMap;
    static readonly Dictionary<int, Tile> tiles = new();   // by stage, variant and edges (made as they're needed)
    static float[][] noise;             // per variant
    static float[] drift;               // the shading: one pattern that wraps from tile to tile (no seams)
    static int tileW, tileH;
    static string mapFor;
    static int mapSig;
    static readonly Dictionary<Vector3Int, Cell> cells = new();
    static readonly List<Tilemap> floors = new(), blockers = new(), covers = new(), walls = new();

    public static int Covered => cells.Count(c => c.Value.Stage > 0);

    public static void Tick(float strength, bool outside)
    {
        bool airborne = flakes.Any(f => f.Live);
        if (strength > 0f || airborne) Fall(strength);
        else if (root && root.activeSelf) root.SetActive(false);
        Deepen();
        if (depth > 0f && outside)
        {
            Rain.Layers();
            Scan();
            Settle();
        }
        Steps();
    }

    // ---- footprints: where you walk on settled snow, left and right, gone after a while ----

    const float Stride = 0.38f, StepStays = 10f, StepFades = 1.5f;
    const int StepFrom = 8;             // the stage the snow must be at to take a print

    sealed class Print { public SpriteRenderer Sr; public float Born; public bool Live; }
    static readonly List<Print> prints = new();
    static Sprite[] printSprites;       // up, down, side
    static Vector3 lastStep;
    static bool leftFoot, stepping;

    static void Steps()
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        float now = Time.time;
        foreach (var p in prints)
        {
            if (!p.Live) continue;
            float age = now - p.Born;
            if (age >= StepStays + StepFades || !p.Sr) { p.Live = false; if (p.Sr) p.Sr.enabled = false; continue; }
            var col = p.Sr.color; col.a = 0.75f * (age < StepStays ? 1f : 1f - (age - StepStays) / StepFades); p.Sr.color = col;
        }
        if (!me || !snowMap || cells.Count == 0) { stepping = false; return; }
        var feet = me.transform.position;
        if (!stepping) { lastStep = feet; stepping = true; return; }
        var moved = feet - lastStep; moved.z = 0f;
        if (moved.magnitude < Stride) return;
        lastStep = feet;
        if (!cells.TryGetValue(snowMap.WorldToCell(feet), out var c) || c.Stage < StepFrom) return;
        if (printSprites == null) Prints();
        var p2 = prints.FirstOrDefault(x => !x.Live);
        if (p2 == null)
        {
            if (prints.Count >= 160) return;
            var go = new GameObject("BW_Footprint");
            go.transform.SetParent(snowMap.transform.parent, false);   // (with the snow: gone with the scene)
            var sr = go.AddComponent<SpriteRenderer>();
            var mapR = snowMap.GetComponent<TilemapRenderer>();
            if (mapR) sr.sharedMaterial = mapR.sharedMaterial;          // (lit like the snow)
            prints.Add(p2 = new Print { Sr = sr });
        }
        if (!p2.Sr) { prints.Remove(p2); return; }
        // the way you're going: up / down / sideways prints, each foot a little to its side
        var dir = moved.normalized;
        bool side = Mathf.Abs(dir.x) > Mathf.Abs(dir.y);
        p2.Sr.sprite = side ? printSprites[2] : dir.y > 0 ? printSprites[0] : printSprites[1];
        p2.Sr.flipX = side && dir.x < 0f;
        var across = new Vector3(-dir.y, dir.x, 0f) * (leftFoot ? 1f : -1f) * (2.5f / Rain.Ppu);
        leftFoot = !leftFoot;
        var at = feet + across;
        p2.Sr.transform.position = new Vector3(Mathf.Round(at.x * Rain.Ppu) / Rain.Ppu, Mathf.Round(at.y * Rain.Ppu) / Rain.Ppu, 0f);
        p2.Sr.sortingLayerID = Rain.GroundLayer;
        p2.Sr.sortingOrder = Rain.GroundOrder + 1;   // (on the snow, under you)
        p2.Sr.color = new Color(0.62f, 0.68f, 0.8f, 0.75f);
        p2.Sr.enabled = true;
        p2.Born = Time.time; p2.Live = true;
    }

    static void Prints()
    {
        var mid = new Vector2(0.5f, 0.5f);
        printSprites = new[]
        {
            Rain.Pixels("BW_PrintUp", new[] { ".#.", "###", "###", ".#.", "...", ".#.", ".#." }, mid),
            Rain.Pixels("BW_PrintDown", new[] { ".#.", ".#.", "...", ".#.", "###", "###", ".#." }, mid),
            Rain.Pixels("BW_PrintSide", new[] { "##..###.", "##.#####", "....###." }, mid),
        };
    }

    // ---- the flakes ----

    static void Fall(float strength)
    {
        var cam = Camera.main;
        if (!cam) return;
        if (!root) Build();
        if (!root.activeSelf) root.SetActive(true);
        Rain.Layers();
        Scan();
        if (snowMap && snowMap.GetComponent<TilemapRenderer>() is TilemapRenderer sr && (sr.sortingLayerID != Rain.GroundLayer || sr.sortingOrder != Rain.GroundOrder))
        { sr.sortingLayerID = Rain.GroundLayer; sr.sortingOrder = Rain.GroundOrder; }   // (the ground's layer found again: new map pieces)

        float now = Time.time, dt = Time.deltaTime;
        float avgLife = 2.6f;
        owed += Medium * Plugin.snowDensity.Value * strength / avgLife * dt;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        while (owed >= 1f)
        {
            owed -= 1f;
            var f = flakes.FirstOrDefault(x => !x.Live);
            if (f == null) { owed = 0f; break; }
            float life = Random.Range(1.8f, 3.4f);
            var to = new Vector3(c.x + Random.Range(-w - 1f, w + 1f), c.y + Random.Range(-h - 0.5f, h + 0.5f), 0f);
            var v = new Vector3(Weather.Wind * 0.35f, -Random.Range(1.1f, 1.8f), 0f);
            f.To = to; f.From = to - v * life; f.Born = now; f.Life = life; f.Live = true;
            f.Phase = Random.Range(0f, 6.28f); f.Sway = Random.Range(0.15f, 0.45f); f.Freq = Random.Range(0.6f, 1.3f);
            f.Sr.sprite = flakeSprites[Random.value < 0.6f ? 0 : Random.value < 0.85f ? 1 : 2];
            f.Sr.sortingLayerID = Rain.SkyLayer; f.Sr.sortingOrder = 31999;
            f.Sr.enabled = true;
        }

        foreach (var f in flakes)
        {
            if (!f.Live) continue;
            float age = now - f.Born, k = age / f.Life;
            if (k >= 1f)
            {
                f.Live = false; f.Sr.enabled = false;
                if (Mathf.Abs(f.To.x - c.x) < w + 0.5f && Mathf.Abs(f.To.y - c.y) < h + 0.5f) Land(f.To);
                continue;
            }
            // drifting side to side, settling exactly where it was going
            var p = Vector3.LerpUnclamped(f.From, f.To, k);
            p.x += Mathf.Sin(f.Phase + age * f.Freq * 6.28f) * f.Sway * (1f - k);
            f.Sr.transform.position = p;
        }
    }

    static void Land(Vector3 at) { }   // (the flakes are only to look at: the cover follows the depth)

    // ---- how deep: one number for the map ----

    static float Full => (Stages + 1) * SecondsPerStage + Spread;

    static void Deepen()
    {
        float dt = Time.deltaTime;
        if (Weather.Now == Kind.Snow) depth = Mathf.Min(Full, depth + Weather.SnowStrength * Plugin.snowSettle.Value * dt);
        else if (depth > 0f) depth = Mathf.Max(0f, depth - Full / MeltSeconds * dt);
    }

    /// <summary>What a tile should be at now: its own delay (from where it is, the same on every game) behind the depth.</summary>
    static int StageOf(Vector3Int cell) =>
        Mathf.Clamp(Mathf.FloorToInt((depth - Hash(cell.x * 7 + 13, cell.y * 11 + 5) * Spread) / SecondsPerStage), 0, Stages);

    /// <summary>Every tile on screen (and a margin around it) brought up to the depth, a few times a second.</summary>
    static void Settle()
    {
        if (!snowMap || Time.unscaledTime < nextSettle) return;
        nextSettle = Time.unscaledTime + 0.25f;
        var cam = Camera.main;
        if (!cam) return;
        float h = cam.orthographicSize + Margin, w = cam.orthographicSize * cam.aspect + Margin;
        var c = cam.transform.position;
        var lo = snowMap.WorldToCell(new Vector3(c.x - w, c.y - h, 0f));
        var hi = snowMap.WorldToCell(new Vector3(c.x + w, c.y + h, 0f));
        for (int y = Mathf.Min(lo.y, hi.y); y <= Mathf.Max(lo.y, hi.y); y++)
            for (int x = Mathf.Min(lo.x, hi.x); x <= Mathf.Max(lo.x, hi.x); x++)
            {
                var cell = new Vector3Int(x, y, 0);
                int want = StageOf(cell);
                if (!cells.TryGetValue(cell, out var s))
                {
                    if (want == 0) continue;
                    cells[cell] = s = new Cell { Variant = Mathf.Abs(cell.x * 73856093 ^ cell.y * 19349663) % Variants };
                }
                if (!s.Looked)
                {
                    var at = snowMap.GetCellCenterWorld(cell);
                    s.Ok = Snowable(at) && !Rain.IsWater(at);
                    s.Looked = true;
                }
                if (!s.Ok || s.Stage == want) continue;
                s.Stage = want;
                if (want == 0) { snowMap.SetTile(cell, null); continue; }
                if (s.Edges < 0) s.Edges = Edges(cell);
                snowMap.SetTile(cell, TileFor(s));
            }
    }

    /// <summary>All of it gone, now (the Insert menu's button).</summary>
    public static void MeltAll()
    {
        depth = 0f;
        if (snowMap) snowMap.ClearAllTiles();
        foreach (var c in cells.Values) c.Stage = 0;
    }

    // ---- where snow can lie: the map's ground, not walls, cliffs or pits ----

    static bool Snowable(Vector3 at)
    {
        // floor tiles, and cliff tops (the "cover" layers: the raised floors you walk on); not pits (fill, roofs),
        // nor a cliff's face (a wall tile with no cover over it)
        bool cover = On(covers, at);
        if (!cover && !On(floors, at)) return false;
        if (On(blockers, at)) return false;
        return cover || !On(walls, at);
    }

    static bool On(List<Tilemap> maps, Vector3 at)
    {
        foreach (var t in maps) if (t && t.isActiveAndEnabled && t.HasTile(t.WorldToCell(at))) return true;
        return false;
    }

    static readonly string[] FloorWords = { "floor", "ground", "grass", "path", "dirt", "road" };
    static readonly string[] CoverWords = { "cover" };
    static readonly string[] WallWords = { "wall", "cliff" };
    static readonly string[] BlockWords = { "fill", "roof" };
    static readonly string[] WaterWords = { "water", "river", "lake", "pond", "sea", "stream" };
    static readonly string[] Ignore = { "collision", "collider", "fog" };

    static bool Has(string name, string[] words) => words.Any(w => name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The map's tilemaps, sorted into ground and what blocks snow (again every few seconds: the map loads
    /// in pieces); our snow grid made on the ground's grid.</summary>
    static void Scan()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (mapFor == scene && Time.unscaledTime < nextScan && snowMap) return;
        nextScan = Time.unscaledTime + 3f;
        var all = Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None).Where(t => t && t != snowMap && !Has(t.name, Ignore) && t.GetComponent<TilemapRenderer>()).ToList();
        int sig = all.Count;
        foreach (var t in all) sig = sig * 31 + t.GetInstanceID();
        if (mapFor == scene && sig == mapSig && snowMap) return;
        if (mapFor != scene) { cells.Clear(); prints.Clear(); if (gridGo) Object.Destroy(gridGo); gridGo = null; snowMap = null; }
        mapFor = scene; mapSig = sig;
        foreach (var c in cells.Values) { c.Looked = false; c.Edges = -1; }
        floors.Clear(); blockers.Clear(); covers.Clear(); walls.Clear();
        foreach (var t in all)
        {
            if (Has(t.name, WaterWords)) continue;   // (the water's its own: Rain.IsWater)
            if (Has(t.name, BlockWords)) blockers.Add(t);
            else if (Has(t.name, CoverWords)) covers.Add(t);
            else if (Has(t.name, WallWords)) walls.Add(t);
            else if (Has(t.name, FloorWords)) floors.Add(t);
        }
        Log.Info($"Better Weather: snow lies on {string.Join(", ", floors.Concat(covers).Select(t => t.name))}; not on {string.Join(", ", blockers.Select(t => t.name))}, nor walls without cover ({string.Join(", ", walls.Select(t => t.name))})");
        if (!snowMap && floors.Count > 0) MakeMap(floors[0]);
    }

    static void MakeMap(Tilemap like)
    {
        var grid = like.layoutGrid;
        gridGo = new GameObject("BW_SnowGrid");   // (in the scene: gone with it)
        gridGo.transform.SetPositionAndRotation(grid ? grid.transform.position : Vector3.zero, grid ? grid.transform.rotation : Quaternion.identity);
        var g = gridGo.AddComponent<Grid>();
        if (grid) { g.cellSize = grid.cellSize; g.cellGap = grid.cellGap; g.cellLayout = grid.cellLayout; g.cellSwizzle = grid.cellSwizzle; }
        var mapGo = new GameObject("BW_Snow");
        mapGo.transform.SetParent(gridGo.transform, false);
        mapGo.transform.localPosition = like.transform.localPosition;
        snowMap = mapGo.AddComponent<Tilemap>();
        var r = mapGo.AddComponent<TilemapRenderer>();
        var likeR = like.GetComponent<TilemapRenderer>();
        if (likeR) r.sharedMaterial = likeR.sharedMaterial;   // (lit like the ground: blue at night)
        r.sortingLayerID = Rain.GroundLayer;
        r.sortingOrder = Rain.GroundOrder;
        r.mode = TilemapRenderer.Mode.Individual;
        if (noise == null || tilesFor != g.cellSize) Tiles(g.cellSize);
        Log.Info($"Better Weather: snow grid {g.cellSize.x}x{g.cellSize.y} on {SortingLayer.IDToName(r.sortingLayerID)} at {r.sortingOrder}");
    }

    // ---- the drawings ----

    static Vector3 tilesFor;

    // the 8 neighbours, as bits: N, NE, E, SE, S, SW, W, NW
    static readonly Vector3Int[] Around =
    {
        new(0, 1, 0), new(1, 1, 0), new(1, 0, 0), new(1, -1, 0), new(0, -1, 0), new(-1, -1, 0), new(-1, 0, 0), new(-1, 1, 0),
    };

    /// <summary>Which neighbours take no snow (water, walls, cliffs, pits, the map's end).</summary>
    static int Edges(Vector3Int cell)
    {
        int bits = 0;
        for (int i = 0; i < 8; i++)
        {
            var at = snowMap.GetCellCenterWorld(cell + Around[i]);
            if (!Snowable(at) || Rain.IsWater(at)) bits |= 1 << i;
        }
        return bits;
    }

    /// <summary>The noise each drawing grows from: lumps (Perlin) and 2x2 clumps.</summary>
    static void Tiles(Vector3 cellSize)
    {
        tilesFor = cellSize;
        tiles.Clear();
        Rain.EnsurePpu();
        tileW = Mathf.Max(4, Mathf.RoundToInt(cellSize.x * Rain.Ppu)); tileH = Mathf.Max(4, Mathf.RoundToInt(cellSize.y * Rain.Ppu));
        noise = new float[Variants][];
        var rng = new System.Random(810);
        // drifts: a Perlin made seamless across the tile (the four shifted copies blended), so neighbouring tiles' shades meet
        drift = new float[tileW * tileH];
        float W = tileW, H = tileH, sc = 9f;
        for (int y = 0; y < tileH; y++)
            for (int x = 0; x < tileW; x++)
            {
                float a = Mathf.PerlinNoise(x / sc, y / sc), b = Mathf.PerlinNoise((x - W) / sc + 50f, y / sc);
                float c2 = Mathf.PerlinNoise((x - W) / sc + 50f, (y - H) / sc + 50f), d = Mathf.PerlinNoise(x / sc, (y - H) / sc + 50f);
                drift[y * tileW + x] = (a * (W - x) * (H - y) + b * x * (H - y) + c2 * x * y + d * (W - x) * y) / (W * H);
            }
        for (int v = 0; v < Variants; v++)
        {
            float ox = (float)rng.NextDouble() * 100f, oy = (float)rng.NextDouble() * 100f;
            var nz = noise[v] = new float[tileW * tileH];
            for (int y = 0; y < tileH; y++)
                for (int x = 0; x < tileW; x++)
                    nz[y * tileW + x] = Mathf.PerlinNoise(ox + x / 7f, oy + y / 7f) * 0.55f + Hash(x / 2 + v * 31, y / 2 + v * 17) * 0.45f;
        }
    }

    /// <summary>
    /// Snow for one tile: white where its noise is under the stage's level (so it grows from specks to clumps to
    /// cover), a pale blue-grey just inside the edges and in the hollows of full cover. Toward a side (or corner) whose
    /// neighbour takes no snow, the level drops: specks right at that edge, filling in further in, so the snow thins
    /// out at the water's and the cliffs' edge instead of stopping in a straight white line.
    /// </summary>
    static Tile TileFor(Cell c)
    {
        int key = (c.Stage * Variants + c.Variant) * 256 + Mathf.Max(0, c.Edges);
        if (tiles.TryGetValue(key, out var made)) return made;
        int n = tileW, m = tileH, edges = Mathf.Max(0, c.Edges);
        var nz = noise[c.Variant];
        var shade = new Color32(205, 216, 236, 255);
        float level = c.Stage >= Stages ? 2f : 0.12f + 0.76f * (c.Stage - 1) / (Stages - 2);   // (the first: a few specks)
        const float Reach = 0.6f;   // how far in (of the tile) the thinning goes
        var px = new Color32[n * m];
        for (int y = 0; y < m; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = (y + 0.5f) / m;   // 0..1 across the tile, v up
                // how far this pixel is from the nearest side / corner with no snow beyond it (1 = far)
                float d = 1f;
                if ((edges & 1) != 0) d = Mathf.Min(d, 1f - v);
                if ((edges & 4) != 0) d = Mathf.Min(d, 1f - u);
                if ((edges & 16) != 0) d = Mathf.Min(d, v);
                if ((edges & 64) != 0) d = Mathf.Min(d, u);
                if ((edges & 2) != 0) d = Mathf.Min(d, Mathf.Max(1f - u, 1f - v));
                if ((edges & 8) != 0) d = Mathf.Min(d, Mathf.Max(1f - u, v));
                if ((edges & 32) != 0) d = Mathf.Min(d, Mathf.Max(u, v));
                if ((edges & 128) != 0) d = Mathf.Min(d, Mathf.Max(u, 1f - v));
                float cap = Mathf.Lerp(0.14f, 2f, Mathf.Clamp01(d / Reach));
                float lv = Mathf.Min(level, cap);
                int i = y * n + x;
                float over = lv - nz[i];
                if (over < 0f) px[i] = new Color32(0, 0, 0, 0);
                else if (lv >= 1.5f ? nz[i] > 0.86f : over < 0.035f) px[i] = shade;
                else px[i] = Whites(drift[i], Hash(x + c.Variant * 57, y + c.Variant * 91));
                if (c.Stage <= SeeThrough && px[i].a > 0) px[i].a = (byte)(255 * Mathf.Lerp(0.3f, 0.95f, (c.Stage - 1) / (float)SeeThrough));
            }
        var tex = new Texture2D(n, m, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = $"BW_Snow{c.Stage}_{c.Variant}_{edges}" };
        tex.SetPixels32(px);
        tex.Apply();
        var tile = ScriptableObject.CreateInstance<Tile>();
        tile.sprite = Sprite.Create(tex, new Rect(0, 0, n, m), new Vector2(0.5f, 0.5f), Rain.Ppu);
        tile.colliderType = Tile.ColliderType.None;
        tiles[key] = tile;
        return tile;
    }

    // the snow's whites: cool blue-white drifts, bright white, light grey-white hollows, and the odd sparkle
    static readonly Color32 Cool = new(226, 234, 250, 255), Bright = new(243, 247, 255, 255), Soft = new(234, 236, 242, 255), Sparkle = new(255, 255, 255, 255);

    static Color32 Whites(float drift, float speck)
    {
        if (speck > 0.985f) return Sparkle;
        float v = drift * 0.75f + speck * 0.25f;
        return v < 0.38f ? Cool : v > 0.62f ? Soft : Bright;
    }

    static float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }

    static void Build()
    {
        Rain.EnsurePpu();
        root = new GameObject("BW_Snowfall");
        Object.DontDestroyOnLoad(root);
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        unlit = new Material(shader) { name = "BW_Snowflakes" };
        var mid = new Vector2(0.5f, 0.5f);
        flakeSprites = new[]
        {
            Rain.Pixels("BW_Flake1", new[] { "#" }, mid),
            Rain.Pixels("BW_Flake2", new[] { "##", "##" }, mid),
            Rain.Pixels("BW_Flake3", new[] { ".#.", "###", ".#." }, mid),
        };
        for (int i = 0; i < MaxFlakes; i++)
        {
            var go = new GameObject("BW_Flake");
            go.transform.SetParent(root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = flakeSprites[0];
            sr.sharedMaterial = unlit;
            sr.color = new Color(1f, 1f, 1f, 0.9f);
            sr.enabled = false;
            flakes.Add(new Flake { Sr = sr });
        }
    }
}

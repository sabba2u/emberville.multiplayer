using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace BetterWeather;

/// <summary>
/// The rain: pixel streaks falling over everything the camera sees, slanted by the wind. Each drop is picked by where
/// it lands (anywhere in view, a little past the edges) and how long it falls; it starts up-wind of that spot and
/// ends on it, then leaves a tiny ring there (a wider, slower ripple on water: the map's water tiles). Pooled
/// SpriteRenderers, unlit (rain catches the light even in a dark storm), over the map (the highest sorting layer the
/// map uses, like the Hunter's Hook's rope).
/// </summary>
internal static class Rain
{
    const int MaxDrops = 1100, MaxSplashes = 420;
    const float Medium = 260f;          // drops in the air at density 1
    const float FallSpeed = 9f;         // world units / s, down the screen
    internal static float Ppu = 16f;            // the game's pixels per world unit (read off the player's sprites when we build)

    sealed class Drop { public SpriteRenderer Sr; public Vector3 From, To; public float Born, Life; public bool Live; }
    sealed class Splash { public SpriteRenderer Sr; public Vector3 At; public float Born, Life; public Sprite[] Frames; public float Alpha, From, To; public bool Live; }

    static readonly List<Drop> drops = new();
    static readonly List<Splash> splashes = new();
    static GameObject root;
    static Sprite streak;
    static Sprite ripple;
    static Sprite[][] splashes3;   // a few kinds of splash on the ground, picked at random
    static Material unlit;
    static float owed;
    static string layeredFor, waterFor;
    static int sortingLayer;
    static readonly List<Tilemap> water = new();

    public static void Tick(float strength)
    {
        bool anything = strength > 0f || drops.Any(d => d.Live) || splashes.Any(s => s.Live);
        if (!anything) { if (root && root.activeSelf) root.SetActive(false); return; }
        var cam = Camera.main;
        if (!cam) return;
        if (!root) Build();
        if (!root.activeSelf) root.SetActive(true);
        Layers();

        float now = Time.time, dt = Time.deltaTime;
        // new drops: enough to keep (Medium x density x strength) in the air
        float avgLife = 0.375f;
        owed += Medium * Weather.RainAmount * strength / avgLife * dt;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        while (owed >= 1f)
        {
            owed -= 1f;
            var d = Free(drops);
            if (d == null) { owed = 0f; break; }
            float life = Random.Range(0.25f, 0.5f);
            var to = new Vector3(c.x + Random.Range(-w - 1f, w + 1f), c.y + Random.Range(-h - 0.5f, h + 0.5f), 0f);
            var v = new Vector3(Weather.Wind, -FallSpeed, 0f);
            d.To = to; d.From = to - v * life; d.Born = now; d.Life = life; d.Live = true;
            d.Sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg + 90f);
            d.Sr.enabled = true;
        }

        foreach (var d in drops)
        {
            if (!d.Live) continue;
            float k = (now - d.Born) / d.Life;
            if (k >= 1f)
            {
                d.Live = false; d.Sr.enabled = false;
                // only where it's in view now (the camera moves): no splashes for nobody
                if (Mathf.Abs(d.To.x - c.x) < w + 0.5f && Mathf.Abs(d.To.y - c.y) < h + 0.5f) Land(d.To, now);
                continue;
            }
            d.Sr.transform.position = Vector3.LerpUnclamped(d.From, d.To, k);
        }

        foreach (var s in splashes)
        {
            if (!s.Live) continue;
            if (now < s.Born) continue;   // (a ripple's second ring, not started yet)
            float k = (now - s.Born) / s.Life;
            if (k >= 1f) { s.Live = false; s.Sr.enabled = false; continue; }
            s.Sr.enabled = true;
            var col = s.Sr.color;
            if (s.Frames != null)
            {
                // a splash: frame by frame (crisp pixels), fading over the last half
                s.Sr.sprite = s.Frames[Mathf.Min(s.Frames.Length - 1, (int)(k * s.Frames.Length))];
                col.a = s.Alpha * Mathf.Clamp01((1f - k) * 2f);
            }
            else
            {
                // a ripple: one ring spreading out and fading
                float scale = Mathf.Lerp(s.From, s.To, Mathf.Sqrt(k));
                s.Sr.transform.localScale = new Vector3(scale, scale, 1f);
                col.a = s.Alpha * (1f - k);
            }
            s.Sr.color = col;
        }
    }

    static void Land(Vector3 at, float now)
    {
        if (IsWater(at)) Start(at, now, 0.7f, null, 0.6f, 0.5f, 1.5f);   // a ring on the water
        else Start(at, now, Random.Range(0.2f, 0.28f), splashes3[Random.Range(0, splashes3.Length)], 0.8f, 1f, 1f);
    }

    static void Start(Vector3 at, float born, float life, Sprite[] frames, float alpha, float from, float to)
    {
        var s = Free(splashes);
        if (s == null) return;
        s.At = at; s.Born = born; s.Life = life; s.Frames = frames; s.Alpha = alpha; s.From = from; s.To = to; s.Live = true;
        s.Sr.sprite = frames != null ? frames[0] : ripple;
        s.Sr.flipX = Random.value < 0.5f;
        s.Sr.transform.position = at;
        s.Sr.transform.localScale = Vector3.one * from;
        s.Sr.enabled = false;   // (on at its first frame)
    }

    static T Free<T>(List<T> pool) where T : class
    {
        foreach (var x in pool)
            if (x is Drop d && !d.Live || x is Splash s && !s.Live) return x;
        return null;
    }

    // ---- water ----

    // the maps' water layers: "River", "River_SecondFloor" (the starting area), "Water"...; not their colliders
    static readonly string[] WaterWords = { "water", "river", "lake", "pond", "sea", "stream" };
    static bool IsWaterName(string name) =>
        WaterWords.Any(w => name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) &&
        name.IndexOf("collision", System.StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("collider", System.StringComparison.OrdinalIgnoreCase) < 0;

    // what's drawn over each water layer (its banks, islands, cliffs and pits: tiles on top of the river, which runs
    // on under them): a drop there landed on that, not in the water
    static readonly Dictionary<Tilemap, List<Tilemap>> over = new();
    static float nextWaterScan;
    static int waterSig;

    internal static bool IsWater(Vector3 at)
    {
        string scene = SceneManager.GetActiveScene().name;
        // (looked at again every few seconds: the map loads in pieces as you go, the river's with its own)
        if (waterFor != scene || Time.unscaledTime >= nextWaterScan)
        {
            nextWaterScan = Time.unscaledTime + 3f;
            var all = Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None).Where(t => t).ToList();
            int sig = all.Count;
            foreach (var t in all) sig = sig * 31 + t.GetInstanceID();
            if (waterFor == scene && sig == waterSig) goto Check;
            waterFor = scene; waterSig = sig;
            water.Clear(); over.Clear();
            water.AddRange(all.Where(t => IsWaterName(t.name)));
            foreach (var w in water)
                over[w] = all.Where(t => t != w && !IsWaterName(t.name) && Drawn(t) && Above(t, w)).ToList();
            Log.Info($"Better Weather: {scene}: water = {(water.Count > 0 ? string.Join(", ", water.Select(t => t.name + " (under " + string.Join("/", over[t].Select(o => o.name)) + ")")) : "none found")} (tilemaps: {string.Join(", ", all.Select(t => t.name).Distinct().Take(40))})");
            layeredFor = null;   // (and the splashes' layer again: new tiles)
        }
        Check:
        foreach (var w in water)
        {
            if (!w || !w.isActiveAndEnabled || !w.HasTile(w.WorldToCell(at))) continue;
            bool covered = false;
            foreach (var o in over[w]) if (o && o.isActiveAndEnabled && o.HasTile(o.WorldToCell(at))) { covered = true; break; }
            if (!covered) return true;
        }
        return false;
    }

    /// <summary>A tilemap you see (not collision, colliders or fog).</summary>
    static bool Drawn(Tilemap t)
    {
        string n = t.name.ToLowerInvariant();
        if (n.Contains("collision") || n.Contains("collider") || n.Contains("fog")) return false;
        var r = t.GetComponent<TilemapRenderer>();
        return r && r.enabled;
    }

    /// <summary>Is a drawn on top of b (sorting layer, then order in it)?</summary>
    static bool Above(Tilemap a, Tilemap b)
    {
        var ra = a.GetComponent<TilemapRenderer>(); var rb = b.GetComponent<TilemapRenderer>();
        if (!ra || !rb) return false;
        int la = SortingLayer.GetLayerValueFromID(ra.sortingLayerID), lb = SortingLayer.GetLayerValueFromID(rb.sortingLayerID);
        return la != lb ? la > lb : ra.sortingOrder > rb.sortingOrder;
    }

    // ---- building ----

    /// <summary>Where things fall from the sky (over everything) and where they land (over the ground, under what
    /// stands on it): for the snow too.</summary>
    internal static int SkyLayer, GroundLayer, GroundOrder;

    internal static void Layers()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (scene == layeredFor) return;
        layeredFor = scene;
        // over the map: the highest sorting layer its sprites and tiles use
        int best = 0, bestValue = int.MinValue;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r || !r.enabled || !(r is SpriteRenderer || r is TilemapRenderer) || r.name.StartsWith("BW_")) continue;
            int v = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
            if (v > bestValue) { bestValue = v; best = r.sortingLayerID; }
        }
        sortingLayer = best;
        foreach (var d in drops) { d.Sr.sortingLayerID = best; d.Sr.sortingOrder = 31999; }
        // splashes and ripples: on the layer the player, trees and props stand on, before all of them (the lowest
        // order in it): over the ground and the water, under anything you can walk behind
        int ground = StandingLayer(best, out int standing);
        // the ground's tiles can share that layer: just over the highest of them that's under the player
        int groundTop = int.MinValue;
        var tiles = Object.FindObjectsByType<TilemapRenderer>(FindObjectsSortMode.None)
            .Where(r => r && r.enabled && r.sortingLayerID == ground && r.sortingOrder < standing).ToList();
        foreach (var r in tiles) groundTop = Mathf.Max(groundTop, r.sortingOrder);
        int order = groundTop == int.MinValue ? standing - 1 : Mathf.Min(groundTop + 1, standing - 1);
        SkyLayer = best; GroundLayer = ground; GroundOrder = order;
        foreach (var s in splashes) { s.Sr.sortingLayerID = ground; s.Sr.sortingOrder = order; }
        Log.Info($"Better Weather: rain over {SortingLayer.IDToName(best)}, splashes on {SortingLayer.IDToName(ground)} at {order} " +
                 $"(the player at {standing}; tiles there: {string.Join(", ", Object.FindObjectsByType<TilemapRenderer>(FindObjectsSortMode.None).Where(r => r && r.sortingLayerID == ground).Select(r => r.name + " " + r.sortingOrder))})");
    }

    static void Build()
    {
        EnsurePpu();
        root = new GameObject("BW_Rain");
        Object.DontDestroyOnLoad(root);
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        unlit = new Material(shader) { name = "BW_Rain" };
        streak = Pixels("BW_Drop", new[] { "#", "#", "#", "#", "#" }, new Vector2(0.5f, 0f));
        var mid = new Vector2(0.5f, 0.5f);
        // on water: a ring that spreads out (scaled)
        ripple = Pixels("BW_Ripple", new[] { "..#####..", ".#.....#.", "#.......#", ".#.....#.", "..#####.." }, mid);
        // on the ground: the drop bursts, bits fly out, gone (a few kinds, mirrored at random too)
        splashes3 = new[]
        {
            new[]   // a crown: up and out to both sides
            {
                Pixels("BW_SplashA1", new[] { ".#.", "#.#" }, mid),
                Pixels("BW_SplashA2", new[] { "#...#", ".....", ".#.#." }, mid),
                Pixels("BW_SplashA3", new[] { "#.....#", ".......", ".......", ".#...#." }, mid),
            },
            new[]   // a spray to one side
            {
                Pixels("BW_SplashB1", new[] { "##", "#." }, mid),
                Pixels("BW_SplashB2", new[] { "..#.", "#...", ".#.#" }, mid),
                Pixels("BW_SplashB3", new[] { "....#", "#....", ".....", "..#.." }, mid),
            },
            new[]   // a flat burst along the ground
            {
                Pixels("BW_SplashC1", new[] { "###" }, mid),
                Pixels("BW_SplashC2", new[] { "#.#.#", "....." }, mid),
                Pixels("BW_SplashC3", new[] { "#.....#", "...#..." }, mid),
            },
            new[]   // a single droplet bouncing up
            {
                Pixels("BW_SplashD1", new[] { "#", "#" }, mid),
                Pixels("BW_SplashD2", new[] { "#", ".", "#" }, mid),
                Pixels("BW_SplashD3", new[] { "#", ".", ".", ".#" }, mid),
            },
        };
        var dropColour = new Color(0.78f, 0.85f, 1f, 0.55f);
        for (int i = 0; i < MaxDrops; i++)
        {
            var sr = Renderer("BW_Drop", streak);
            sr.color = dropColour;
            drops.Add(new Drop { Sr = sr });
        }
        for (int i = 0; i < MaxSplashes; i++)
        {
            var sr = Renderer("BW_Splash", ripple);
            sr.color = new Color(0.82f, 0.88f, 1f, 0.75f);
            splashes.Add(new Splash { Sr = sr });
        }
        layeredFor = null;
    }

    /// <summary>The sorting layer the player's sprites are on (most of them), else the given one.</summary>
    static int StandingLayer(int fallback, out int order)
    {
        order = 0;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me) return fallback;
        var rs = me.GetComponentsInChildren<SpriteRenderer>().Where(r => r.enabled && r.sprite).ToList();
        if (rs.Count == 0) return fallback;
        int id = rs.GroupBy(r => r.sortingLayerID).OrderByDescending(g => g.Count()).First().Key;
        order = rs.Where(r => r.sortingLayerID == id).Min(r => r.sortingOrder);   // (its lowest part)
        return id;
    }

    static bool ppuRead;
    internal static void EnsurePpu()
    {
        if (ppuRead) return;
        Ppu = GamePpu();
        ppuRead = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player;
        Log.Info($"Better Weather: pixels per unit {Ppu}");
    }

    /// <summary>The game's pixel size: the player's own sprites' pixels per unit (most common), else 16.</summary>
    static float GamePpu()
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me) return 16f;
        var ppus = me.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.sprite).Select(r => r.sprite.pixelsPerUnit).ToList();
        return ppus.Count > 0 ? ppus.GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key : 16f;
    }

    static SpriteRenderer Renderer(string name, Sprite sprite)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sharedMaterial = unlit;
        sr.enabled = false;
        return sr;
    }

    /// <summary>A white pixel-art sprite from rows of '#' (top row first).</summary>
    public static Sprite Pixels(string name, string[] rows, Vector2 pivot)
    {
        int h = rows.Length, w = rows.Max(r => r.Length);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = name };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[(h - 1 - y) * w + x] = x < rows[y].Length && rows[y][x] == '#' ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, Ppu);
    }
}

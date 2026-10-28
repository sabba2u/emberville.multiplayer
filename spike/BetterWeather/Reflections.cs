using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace BetterWeather;

/// <summary>
/// Reflections on water (as Core Keeper does them): everything that stands by the water (players, companions, pets,
/// creatures, NPCs, torches: anything animated or giving light) gets a copy of its sprites, upside down under its feet,
/// faded and a little blue, swaying sideways like moving water. The game has no water shader (its water is plain tiles
/// on the usual sprite shader), so it's sprites: each copy uses the original's own material, so the game's 2D lights
/// light it (dim in a dark cave, bright by a torch). One mask over the water near the camera (a texture, a pixel a
/// tile: the tiles Rain.IsWater calls water, not under a bank) keeps them on the water. Indoors and out.
/// </summary>
internal static class Reflections
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> opacity, sway;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Water", "Reflections", true, "Reflections on water: what stands by it, upside down in it");
        opacity = config.Bind("Water", "Opacity", 0.35f, "How strong the reflections are (0.05 - 0.8)");
        sway = config.Bind("Water", "Sway", 1f, "How far the reflections sway sideways, in the game's pixels (0 = still, up to 3)");
    }

    sealed class Actor
    {
        public Transform Root;
        public float FeetOffset;   // (the bottom of its sprites, from its root)
        public TilemapRenderer Water;   // (the water it shows in: drawn just over that, so the banks and cliffs drawn
                                        // over the water cover it too)
        public GameObject Group;
        public readonly List<(SpriteRenderer Src, SpriteRenderer Copy)> Parts = new();
    }

    static readonly Dictionary<Transform, Actor> actors = new();
    static readonly Stack<SpriteRenderer> spare = new();
    static readonly MaterialPropertyBlock block = new();
    static readonly Color Tint = new(0.72f, 0.84f, 1f, 1f);
    static GameObject root;
    static SpriteMask mask;
    static Texture2D maskTex;
    static Rect maskArea;           // (world: what the mask covers)
    static float nextScan, nextMask;
    static string maskScene;
    static bool anyWater;
    const int Sub = 8;   // (the mask's pixels a tile)
    static System.Type light2D;
    static int logged, lastWet = -1;
    static string loggedScene;

    /// <summary>Every frame, after the animations (LateUpdate).</summary>
    public static void Tick()
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        var cam = Camera.main;
        if (!on.Value || !me || !cam) { Hide(); return; }
        if (!root) { root = new GameObject("BW_Reflections"); Object.DontDestroyOnLoad(root); }
        if (!root.activeSelf) root.SetActive(true);
        Rain.EnsurePpu();
        if (!Rain.Layered) Rain.Layers();
        Mask(cam);
        if (!anyWater) { Clear(); return; }
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; Scan(cam); }
        float t = Time.time, alpha = Mathf.Clamp(opacity.Value, 0.05f, 0.8f), px = Mathf.Clamp(sway.Value, 0f, 3f) / Rain.Ppu;
        foreach (var a in actors.Values)
        {
            if (!a.Root) continue;
            int layer = a.Water ? a.Water.sortingLayerID : Rain.GroundLayer, order = a.Water ? a.Water.sortingOrder + 1 : Rain.GroundOrder;
            if (a.Group.TryGetComponent<SortingGroup>(out var g)) { g.sortingLayerID = layer; g.sortingOrder = order; }
            float feet = a.Root.position.y + a.FeetOffset;
            a.Group.transform.position = new Vector3(0f, feet, 0f);
            a.Group.transform.localScale = new Vector3(1f, -1f, 1f);
            foreach (var (src, copy) in a.Parts)
            {
                if (!src || !src.enabled || !src.gameObject.activeInHierarchy || !src.sprite) { copy.enabled = false; continue; }
                // the copy as the original is (turned, tilted, scaled: the game turns a character round its upright
                // axis), inside its group, which is upside down at the feet: that does the mirroring
                var p = src.transform.position;
                float depth = p.y - feet;
                // the sway: a slow wave down the water (deeper parts of the reflection a step behind)
                float x = p.x + Mathf.Sin(t * 2.2f + depth * 2.5f) * px;
                var ct = copy.transform;
                ct.localPosition = new Vector3(x, depth, p.z);
                ct.localRotation = src.transform.rotation;
                ct.localScale = src.transform.lossyScale;
                copy.sprite = src.sprite;
                copy.flipX = src.flipX; copy.flipY = src.flipY;
                copy.drawMode = src.drawMode;
                if (src.drawMode != SpriteDrawMode.Simple) copy.size = src.size;
                var mat = Plain(src.sharedMaterial);
                if (copy.sharedMaterial != mat) copy.sharedMaterial = mat;
                src.GetPropertyBlock(block);
                copy.SetPropertyBlock(block);
                var c = src.color * Tint;
                c.a *= alpha;
                copy.color = c;
                copy.sortingLayerID = layer;              // (the water's layer: the lights that light the water light it)
                copy.sortingOrder = src.sortingOrder;     // (its parts in the same order, inside the group)
                copy.enabled = true;
            }
        }
    }

    // ---- what's by the water ----

    static void Scan(Camera cam)
    {
        float h = cam.orthographicSize + 3f, w = cam.orthographicSize * cam.aspect + 3f;
        var c = cam.transform.position;
        bool InView(Vector3 p) => Mathf.Abs(p.x - c.x) < w && Mathf.Abs(p.y - c.y) < h;

        var roots = new HashSet<Transform>();
        foreach (var an in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
            if (an && an.isActiveAndEnabled && InView(an.transform.position)) roots.Add(Top(an.transform));
        light2D ??= System.Type.GetType("UnityEngine.Rendering.Universal.Light2D, Unity.RenderPipelines.Universal.2D.Runtime")
                    ?? System.Type.GetType("UnityEngine.Rendering.Universal.Light2D, Unity.RenderPipelines.Universal.Runtime");
        if (light2D != null)
            foreach (var l in Object.FindObjectsByType(light2D, FindObjectsSortMode.None).OfType<Behaviour>())
                if (l && l.isActiveAndEnabled && l.transform.parent && InView(l.transform.position)) roots.Add(Top(l.transform.parent));

        var keep = new HashSet<Transform>();
        foreach (var r in roots)
        {
            if (!r || r.GetComponentInParent<Canvas>() || r.name.StartsWith("BW_")) continue;
            if (!actors.TryGetValue(r, out var a))
            {
                var srs = Sprites(r);
                if (srs.Count == 0) continue;
                float bottom = srs.Min(s => s.bounds.min.y);
                // (a root at its feet: mirror there; else the bottom of its sprites)
                float off = Mathf.Abs(bottom - r.position.y) < 0.5f ? 0f : bottom - r.position.y;
                var under = WaterBelow(r.position.x, r.position.y + off);
                if (!under) continue;
                a = new Actor { Root = r, FeetOffset = off, Water = under.GetComponent<TilemapRenderer>() };
                a.Group = new GameObject("BW_Reflection " + r.name);
                a.Group.transform.SetParent(root.transform, false);
                var g = a.Group.AddComponent<SortingGroup>();
                g.sortingLayerID = Rain.GroundLayer;
                g.sortingOrder = Rain.GroundOrder;
                actors[r] = a;
                if (logged++ < 30) Log.Info($"Better Weather: reflecting {r.name} ({srs.Count} sprites, feet {off:+0.00;-0.00;0} from its root): " +
                    string.Join(", ", r.GetComponentsInChildren<SpriteRenderer>(false).Where(x => x.sprite).Select(x =>
                        $"{x.name} [{x.sprite.name} {x.bounds.size.x:0.0}x{x.bounds.size.y:0.0}, {x.sharedMaterial?.shader?.name}, mask {x.maskInteraction}, {x.color}{(srs.Contains(x) ? "" : ", skipped")}]")));
            }
            else
            {
                var under = WaterBelow(r.position.x, r.position.y + a.FeetOffset);
                if (!under) continue;
                a.Water = under.GetComponent<TilemapRenderer>();
            }
            keep.Add(r);
            Parts(a);
        }
        foreach (var r in actors.Keys.Where(k => !keep.Contains(k)).ToList()) Drop(r);
    }

    /// <summary>Its topmost animated ancestor (a character's parts animate on their own: the whole character).</summary>
    static Transform Top(Transform t)
    {
        var best = t;
        for (var p = t.parent; p; p = p.parent)
            if (p.GetComponent<Animator>()) best = p;
        return best;
    }

    static List<SpriteRenderer> Sprites(Transform r) =>
        r.GetComponentsInChildren<SpriteRenderer>(false)
         .Where(s => s.sprite && s.enabled && !s.name.StartsWith("BW_") && s.name.IndexOf("shadow", System.StringComparison.OrdinalIgnoreCase) < 0
                     // (a sprite the game shows only through / outside a mask: the silhouette behind a wall and the
                     // like; copied, it's a pale shape in the water)
                     && s.maskInteraction == SpriteMaskInteraction.None
                     // (only what the game's camera draws: not the minimap's marker on you, a white box)
                     && Seen(s.gameObject.layer))
         .ToList();

    static bool Seen(int layer)
    {
        var cam = Camera.main;
        return !cam || (cam.cullingMask & (1 << layer)) != 0;
    }

    /// <summary>Water just below its feet (or under them): close enough to show in it.</summary>
    static Tilemap WaterBelow(float x, float feet)
    {
        for (float d = 0.25f; d <= 3f; d += 0.5f)
        {
            var w = Rain.WaterAt(new Vector3(x, feet - d, 0f));
            if (w) return w;
        }
        return null;
    }

    /// <summary>Its sprites now (gear changes, things appear): a copy for each, the copies of the gone ones back.</summary>
    static void Parts(Actor a)
    {
        var now = Sprites(a.Root);
        for (int i = a.Parts.Count - 1; i >= 0; i--)
            if (!a.Parts[i].Src || !now.Contains(a.Parts[i].Src)) { Free(a.Parts[i].Copy); a.Parts.RemoveAt(i); }
        foreach (var s in now)
        {
            if (a.Parts.Any(p => p.Src == s)) continue;
            var copy = spare.Count > 0 ? spare.Pop() : NewCopy();
            copy.transform.SetParent(a.Group.transform, false);
            copy.gameObject.SetActive(true);
            copy.enabled = false;
            a.Parts.Add((s, copy));
        }
    }

    static SpriteRenderer NewCopy()
    {
        var go = new GameObject("BW_ReflectionPart");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        return sr;
    }

    static void Free(SpriteRenderer copy)
    {
        if (!copy) return;
        copy.enabled = false;
        copy.gameObject.SetActive(false);
        copy.transform.SetParent(root.transform, false);
        spare.Push(copy);
    }

    static void Drop(Transform r)
    {
        if (!actors.TryGetValue(r, out var a)) return;
        foreach (var (_, copy) in a.Parts) Free(copy);
        if (a.Group) Object.Destroy(a.Group);
        actors.Remove(r);
    }

    static void Clear()
    {
        foreach (var r in actors.Keys.ToList()) Drop(r);
    }

    static void Hide()
    {
        if (actors.Count > 0) Clear();
        if (root && root.activeSelf) root.SetActive(false);
    }

    // ---- a tile's pixels: water (its colour in the open water's palette), something else, or see-through ----

    static readonly HashSet<int> palette = new HashSet<int>();
    static string paletteScene;
    static readonly Dictionary<(int, bool, bool), sbyte[]> cells = new Dictionary<(int, bool, bool), sbyte[]>();
    static readonly Dictionary<int, (Color32[] px, int w)> textures = new Dictionary<int, (Color32[], int)>();

    static int Quant(Color32 c) => (c.r >> 3) << 10 | (c.g >> 3) << 5 | c.b >> 3;   // (a little slack: the copy back from the GPU)

    // Sub x Sub per tile, bottom row first: 1 water, 0 something else, -1 see-through (mostly, in each block)
    static sbyte[] Cells(Sprite s, bool fx, bool fy)
    {
        var key = (s.GetInstanceID(), fx, fy);
        if (cells.TryGetValue(key, out var got)) return got;
        var px = SpritePixels(s, out int w, out int h);
        var r = new sbyte[Sub * Sub];
        for (int sy = 0; sy < Sub; sy++)
            for (int sx = 0; sx < Sub; sx++)
            {
                int x0 = sx * w / Sub, x1 = Mathf.Max(x0 + 1, (sx + 1) * w / Sub), y0 = sy * h / Sub, y1 = Mathf.Max(y0 + 1, (sy + 1) * h / Sub);
                int wetN = 0, solid = 0, all = 0;
                for (int py = y0; py < y1 && py < h; py++)
                    for (int qx = x0; qx < x1 && qx < w; qx++)
                    {
                        var c = px[py * w + qx];
                        all++;
                        if (c.a <= 127) continue;
                        solid++;
                        if (palette.Contains(Quant(c))) wetN++;
                    }
                int tx = fx ? Sub - 1 - sx : sx, ty = fy ? Sub - 1 - sy : sy;
                r[ty * Sub + tx] = (sbyte)(all == 0 || solid * 2 < all ? -1 : wetN * 4 >= all * 3 ? 1 : 0);
            }
        return cells[key] = r;
    }

    // a sprite's own pixels (w x h, bottom row first), copied off the GPU once per texture (the game's aren't readable)
    static Color32[] SpritePixels(Sprite s, out int w, out int h)
    {
        var t = s.texture;
        Rect rect;
        try { rect = s.textureRect; } catch { rect = s.rect; }
        int rx = Mathf.RoundToInt(rect.x), ry = Mathf.RoundToInt(rect.y);
        w = Mathf.Max(1, Mathf.RoundToInt(rect.width)); h = Mathf.Max(1, Mathf.RoundToInt(rect.height));
        if (!t) return new Color32[w * h];
        if (!textures.TryGetValue(t.GetInstanceID(), out var all))
        {
            Color32[] got;
            if (t.isReadable) got = t.GetPixels32();
            else
            {
                var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var was = RenderTexture.active;
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                got = copy.GetPixels32();
                Object.Destroy(copy);
            }
            textures[t.GetInstanceID()] = all = (got, t.width);
        }
        var r = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (ry + y) * all.w + rx + x;
                if (i >= 0 && i < all.px.Length) r[y * w + x] = all.px[i];
            }
        return r;
    }

    // (testing: the mask as a picture, BepInEx/BW_WaterMask.png, water white, you red; and the tile stack)
    static void Dump(byte[] px, int tw, int th, Vector3 origin, Vector3 cell, List<Tilemap> stack, bool log)
    {
        try
        {
            var img = new Texture2D(tw, th, TextureFormat.RGBA32, false);
            var cols = new Color32[tw * th];
            for (int i = 0; i < cols.Length; i++) cols[i] = px[i] > 0 ? new Color32(255, 255, 255, 255) : new Color32(40, 40, 40, 255);
            var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            if (me)
            {
                int fx = Mathf.RoundToInt((me.transform.position.x - origin.x) / cell.x * Sub), fy = Mathf.RoundToInt((me.transform.position.y - origin.y) / cell.y * Sub);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    if (fx + dx >= 0 && fx + dx < tw && fy + dy >= 0 && fy + dy < th) cols[(fy + dy) * tw + fx + dx] = new Color32(255, 0, 0, 255);
            }
            img.SetPixels32(cols);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "BW_WaterMask.png"), img.EncodeToPNG());
            Object.Destroy(img);
            if (log) Log.Info($"Better Weather: mask picture saved (origin {origin.x:0.##},{origin.y:0.##}; you at {(me ? me.transform.position.ToString() : "?")}); stack top first: {string.Join(", ", stack.Select(m => { var r = m.GetComponent<TilemapRenderer>(); return r ? $"{m.name} {SortingLayer.IDToName(r.sortingLayerID)} {r.sortingOrder}" : m.name; }))}");
        }
        catch (System.Exception e) { Log.Info("Better Weather: mask picture failed: " + e.Message); }
    }

    // ---- the mask: the water near the camera ----

    static void Mask(Camera cam)
    {
        string scene = SceneManager.GetActiveScene().name;
        var c = cam.transform.position;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var view = new Rect(c.x - w, c.y - h, 2f * w, 2f * h);
        bool inside = maskScene == scene && maskArea.xMin <= view.xMin && maskArea.xMax >= view.xMax && maskArea.yMin <= view.yMin && maskArea.yMax >= view.yMax;
        if (inside && Time.unscaledTime < nextMask) return;
        nextMask = Time.unscaledTime + 3f;   // (and again every few seconds: the map loads in pieces)
        maskScene = scene;

        Rain.IsWater(c);   // (finds the water layers)
        var maps = Rain.WaterMaps.Where(t => t && t.isActiveAndEnabled).ToList();
        anyWater = maps.Count > 0;
        if (!anyWater) { if (mask) mask.enabled = false; return; }
        var grid = maps[0];
        var cell = grid.cellSize;
        if (cell.x <= 0f || cell.y <= 0f) { anyWater = false; return; }
        const float Margin = 8f;
        var from = grid.WorldToCell(new Vector3(view.xMin - Margin, view.yMin - Margin, 0f));
        var to = grid.WorldToCell(new Vector3(view.xMax + Margin, view.yMax + Margin, 0f));
        int cw = to.x - from.x + 1, ch = to.y - from.y + 1;
        if (cw <= 0 || ch <= 0 || cw * ch > 40000) { anyWater = false; return; }

        int tw = cw * Sub, th = ch * Sub;
        if (!maskTex || maskTex.width != tw || maskTex.height != th)
        {
            if (maskTex) Object.Destroy(maskTex);
            maskTex = new Texture2D(tw, th, TextureFormat.Alpha8, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "BW_WaterMask" };
        }
        // which tiles are water, down to the pixel: the common looks (tile and sprite) of the water layers are the
        // open water, their colours its palette; every water tile's pixels in that palette are water, the pebble
        // rim and the land the shore tiles show aren't (the rim is painted into the water tiles themselves, so no
        // sorting order can put a reflection between the water and it). The land drawn over the water is in the
        // stack too: the river runs on under the rock floors, and what's on top at a pixel decides
        var looks = new List<(Sprite s, bool fx, bool fy, bool wet)>[cw, ch];
        var counts = new Dictionary<string, int>();
        var bySprite = new Dictionary<string, Sprite>();
        var wetMaps = new HashSet<Tilemap>(maps);
        var top = maps.Concat(maps.SelectMany(Rain.Over)).Where(t => t && t.isActiveAndEnabled).Distinct()
            .OrderByDescending(m => { var r = m.GetComponent<TilemapRenderer>(); return r ? SortingLayer.GetLayerValueFromID(r.sortingLayerID) * 100000 + r.sortingOrder : 0; }).ToList();
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                var at = grid.GetCellCenterWorld(new Vector3Int(from.x + x, from.y + y, 0));
                if (!maps.Any(m => m.HasTile(m.WorldToCell(at)))) continue;
                bool open1 = Rain.IsWater(at), counted = false;
                foreach (var m in top)
                {
                    var cellAt = m.WorldToCell(at);
                    var sp = m.GetSprite(cellAt);
                    if (!sp) continue;
                    var tm = m.GetTransformMatrix(cellAt);
                    bool isWet = wetMaps.Contains(m);
                    (looks[x, y] ??= new List<(Sprite, bool, bool, bool)>()).Add((sp, tm.m00 < 0f, tm.m11 < 0f, isWet));
                    if (!isWet || counted || !open1) continue;
                    counted = true;
                    string key = m.GetTile(cellAt)?.name + "/" + sp.name;
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                    bySprite[key] = sp;
                }
            }
        int most = counts.Count > 0 ? counts.Values.Max() : 0;
        var open = new HashSet<string>(counts.Where(kv => kv.Value >= 4 && kv.Value >= most * 0.3f).Select(kv => kv.Key));
        if (scene != paletteScene || palette.Count == 0)
        {
            paletteScene = scene; palette.Clear(); cells.Clear();
            foreach (var k in open) foreach (var c32 in SpritePixels(bySprite[k], out _, out _)) if (c32.a > 127) palette.Add(Quant(c32));
        }
        int wet = 0;
        var px = new byte[tw * th];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                if (looks[x, y] == null) continue;
                bool any = false;
                for (int sy = 0; sy < Sub; sy++)
                    for (int sx = 0; sx < Sub; sx++)
                    {
                        // the topmost tile with something drawn there decides (see-through: the one under it)
                        sbyte v = -1;
                        foreach (var l in looks[x, y])
                            if ((v = Cells(l.s, l.fx, l.fy)[sy * Sub + sx]) >= 0) { if (!l.wet) v = 0; break; }
                        if (v != 1) continue;
                        px[(y * Sub + sy) * tw + x * Sub + sx] = 255;
                        any = true;
                    }
                if (any) wet++;
            }
        if (scene != loggedScene || wet != lastWet)
            Log.Info($"Better Weather: water tiles here: {string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key} x{kv.Value}{(open.Contains(kv.Key) ? " (open)" : "")}"))}; palette {palette.Count} colours");
        anyWater = wet > 0;
        maskTex.LoadRawTextureData(px);
        maskTex.Apply();

        if (!mask)
        {
            var go = new GameObject("BW_WaterMask");
            go.transform.SetParent(root.transform, false);
            mask = go.AddComponent<SpriteMask>();
            mask.alphaCutoff = 0.5f;
        }
        var old = mask.sprite;
        mask.sprite = Sprite.Create(maskTex, new Rect(0, 0, tw, th), Vector2.zero, Sub / cell.x);
        if (old) Object.Destroy(old);
        mask.transform.position = grid.CellToWorld(from);
        mask.transform.localScale = new Vector3(1f, cell.y / cell.x, 1f);
        // (no custom range: the copies inside a reflection's group keep their own orders, which a range by the
        // group's order missed: every copy hidden)
        mask.isCustomRangeActive = false;
        mask.enabled = true;
        if (showMask && !maskView)
        {
            var mv = new GameObject("BW_MaskView");
            mv.transform.SetParent(root.transform, false);
            maskView = mv.AddComponent<SpriteRenderer>();
            maskView.sortingLayerID = SortingLayer.layers[SortingLayer.layers.Length - 1].id;
            maskView.sortingOrder = 32000;
            maskView.color = new Color(1f, 0f, 0f, 0.35f);
        }
        if (maskView)
        {
            maskView.enabled = showMask;
            maskView.sprite = mask.sprite;
            maskView.transform.position = mask.transform.position;
            maskView.transform.localScale = mask.transform.localScale;
        }
        if (wet != lastWet || scene != loggedScene) Dump(px, tw, th, grid.CellToWorld(from), cell, top, true);
        if (wet != lastWet || scene != loggedScene)
        {
            lastWet = wet; loggedScene = scene;
            Log.Info($"Better Weather: water mask {cw}x{ch} tiles, {wet} of them water (cell {cell.x:0.##}x{cell.y:0.##}, from {grid.name}); reflections just over the water ({string.Join(", ", maps.Select(m => { var r = m.GetComponent<TilemapRenderer>(); return r ? $"{m.name} {SortingLayer.IDToName(r.sortingLayerID)} {r.sortingOrder}" : m.name; }))})");
        }
        var bl = mask.transform.position;
        maskArea = new Rect(bl.x, bl.y, cw * cell.x, ch * cell.y);
    }

    // ---- the menu (the Weather tab) ----

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Water");
        bool v = on.Value;
        if (ImGui.Checkbox("Reflections on water", ref v)) on.Value = v;
        ImGui.SameLine(); ImGui.TextDisabled(on.Value ? $"{actors.Count} reflected here now" : "off");
        if (!on.Value) ImGui.BeginDisabled();
        float o = opacity.Value;
        if (ImGui.SliderFloat("Reflection strength", ref o, 0.05f, 0.8f, "%.2f")) opacity.Value = o;
        float s = sway.Value;
        if (ImGui.SliderFloat("Reflection sway (pixels)", ref s, 0f, 3f, s <= 0f ? "still" : "%.1f")) sway.Value = s;
        if (!on.Value) ImGui.EndDisabled();
    }

    // the game's own sprite shaders (skin colour, dyed clothes) ignore the mask and the see-through colour: their
    // copies drawn with the plain lit sprite shader instead (taken from a part that has it)
    const string PlainShader = "Universal Render Pipeline/2D/Sprite-Lit-Default";
    static Material plain;
    static Material Plain(Material m)
    {
        if (m && m.shader && m.shader.name == PlainShader) { if (!plain) plain = m; return m; }
        if (!plain)
        {
            var sh = Shader.Find(PlainShader);
            if (sh) plain = new Material(sh) { name = "BW_PlainSprite", hideFlags = HideFlags.DontSave };
        }
        return plain ? plain : m;
    }

    static bool showMask;
    static SpriteRenderer maskView;
}

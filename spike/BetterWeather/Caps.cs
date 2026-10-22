using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterWeather;

/// <summary>
/// Snow on what stands on the map (trees, the bridge, fences, roofs, rocks, barrels...): the map's snow is tiles, these
/// are sprites, so each gets a cap of its own. Its picture is read once (copied back from the graphics card, without
/// stalling: AsyncGPUReadback, one texture at a time, cached), its top edges found (a solid pixel with nothing above
/// it), and a white crust drawn on them: specks first, then a pixel, then two or three thick, in the snow's own
/// whites. The cap is a child sprite drawn just over the object (same layer, one order up, same flip, lit like it),
/// so it moves and sorts with it and you still walk behind the tree. It follows the map's snow depth (Snow.Amount) up
/// and back down as it melts. Nothing that moves or animates gets one: characters, creatures, our own things, or a
/// sprite that changes (an animation).
/// </summary>
internal static class Caps
{
    const int Levels = 4;               // specks, a thin crust, thicker, thickest
    const int MinHeight = 10;           // pixels: smaller sprites (grass, pebbles, sparks) get none
    const float ScanEvery = 2f, Margin = 6f;

    sealed class Capped { public SpriteRenderer Src, Cap; public Sprite Sprite; }
    sealed class Picture { public int W, H; public Color32[] Px; }

    static readonly Dictionary<SpriteRenderer, Capped> capped = new();
    static readonly HashSet<SpriteRenderer> skip = new();
    static readonly Dictionary<Texture, Picture> pictures = new();
    static readonly HashSet<Texture> asked = new();
    static readonly Dictionary<(Sprite, int), Sprite> caps = new();
    static float nextScan;
    static int level;
    static bool reading;

    public static void Tick(float amount)
    {
        int want = amount < 0.12f ? 0 : amount < 0.35f ? 1 : amount < 0.6f ? 2 : amount < 0.85f ? 3 : 4;
        bool changed = want != level;
        level = want;
        if (level == 0)
        {
            if (changed) foreach (var c in capped.Values) if (c.Cap) c.Cap.enabled = false;
            return;
        }
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + ScanEvery; Scan(); }
        // keep each cap matching its object (and off if the object changed picture or went away)
        foreach (var c in capped.Values)
        {
            if (!c.Cap) continue;
            if (!c.Src || !c.Src.enabled || !c.Src.gameObject.activeInHierarchy || c.Src.sprite != c.Sprite) { c.Cap.enabled = false; continue; }
            var sp = CapFor(c.Sprite, level);
            if (sp == null) { c.Cap.enabled = false; continue; }
            if (c.Cap.sprite != sp) c.Cap.sprite = sp;
            c.Cap.flipX = c.Src.flipX; c.Cap.flipY = c.Src.flipY;
            c.Cap.enabled = true;
        }
    }

    // ---- what gets a cap ----

    static void Scan()
    {
        var cam = Camera.main;
        if (!cam) return;
        foreach (var dead in capped.Keys.Where(k => !k).ToList()) capped.Remove(dead);
        skip.RemoveWhere(k => !k);
        float h = cam.orthographicSize + Margin, w = cam.orthographicSize * cam.aspect + Margin;
        var c = cam.transform.position;
        int groundValue = SortingLayer.GetLayerValueFromID(Rain.GroundLayer);
        foreach (var sr in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
        {
            if (!sr || capped.ContainsKey(sr) || skip.Contains(sr) || !sr.enabled || !sr.sprite) continue;
            var p = sr.transform.position;
            if (Mathf.Abs(p.x - c.x) > w || Mathf.Abs(p.y - c.y) > h) continue;
            string why = Why(sr, groundValue);
            if (why != null) { skip.Add(sr); Note(why, sr.name); continue; }
            var picture = PictureOf(sr.sprite.texture);
            if (picture == null) continue;   // (being read: next time)
            var go = new GameObject("BW_Cap");
            go.transform.SetParent(sr.transform, false);
            var cap = go.AddComponent<SpriteRenderer>();
            cap.sharedMaterial = sr.sharedMaterial;   // (lit like it)
            cap.sortingLayerID = sr.sortingLayerID;
            cap.sortingOrder = sr.sortingOrder + 1;
            cap.enabled = false;
            capped[sr] = new Capped { Src = sr, Cap = cap, Sprite = sr.sprite };
        }
        if (capped.Count != logged)
        {
            logged = capped.Count;
            Log.Info($"Better Weather: snow caps on {capped.Count} things (e.g. {string.Join(", ", capped.Keys.Where(k => k).Select(k => k.name).Distinct().Take(12))}); " +
                     $"left out {skip.Count}: {string.Join("; ", reasons.Select(kv => $"{kv.Key} {kv.Value.count} ({string.Join(", ", kv.Value.names)})"))}");
        }
    }

    static int logged = -1;

    // not things: glows, shadows, the game's range circles (a lamp's "DisplayTrigger"), effects
    static readonly string[] NotThings = { "trigger", "display", "range", "light", "glow", "shadow", "halo", "indicator", "vfx", "fx", "decal", "minimap", "area" };

    /// <summary>Why this sprite gets no cap (null: it gets one).</summary>
    static string Why(SpriteRenderer sr, int groundValue)
    {
        string n = sr.name, low = n.ToLowerInvariant();
        if (n.StartsWith("BW_") || n.StartsWith("EG_") || n.StartsWith("BNQ_") || n.StartsWith("EmbervilleGhost")) return "ours";
        if (NotThings.Any(w => low.Contains(w)) || NotThings.Any(w => sr.transform.parent && sr.transform.parent.name.ToLowerInvariant().Contains(w))) return "not a thing";
        if (sr.color.a < 0.6f) return "see-through";
        var mat = sr.sharedMaterial ? sr.sharedMaterial.name.ToLowerInvariant() : "";
        if (mat.Contains("add") || mat.Contains("glow") || mat.Contains("light")) return "a glow";
        if (sr.sprite.rect.height < MinHeight) return "small";
        // on the ground's layer or above (not the ground's own decals), and not something that moves
        int v = SortingLayer.GetLayerValueFromID(sr.sortingLayerID);
        if (v < groundValue) return "under the ground layer";
        if (v == groundValue && sr.sortingOrder <= Rain.GroundOrder) return "flat on the ground";
        if (sr.GetComponentInParent<PlayerMaster>() || sr.GetComponentInParent<Enemy>() || sr.GetComponentInParent<Rigidbody2D>()) return "moves";
        if (sr.GetComponentInParent<Animator>()) return "animated";
        var root = sr.transform.root.name;
        if (root.StartsWith("EmbervilleGhost") || root.StartsWith("BNQ_") || root.StartsWith("EG_") || root.StartsWith("BW_")) return "ours";
        try { _ = sr.sprite.textureRect; } catch { return "packed"; }   // (tightly packed sprites: no rectangle to read)
        return null;
    }

    static readonly Dictionary<string, (int count, HashSet<string> names)> reasons = new();

    static void Note(string why, string name)
    {
        if (!reasons.TryGetValue(why, out var r)) reasons[why] = r = (0, new HashSet<string>());
        r.count++;
        if (r.names.Count < 8) r.names.Add(name);
        reasons[why] = r;
    }

    // ---- reading a picture back from the graphics card (once a texture, one at a time) ----

    static Picture PictureOf(Texture tex)
    {
        if (!tex) return null;
        if (pictures.TryGetValue(tex, out var p)) return p;
        if (reading || asked.Contains(tex)) return null;
        asked.Add(tex);
        reading = true;
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
        {
            reading = false;
            RenderTexture.ReleaseTemporary(rt);
            if (req.hasError || !tex) { Log.Warn($"Better Weather: couldn't read {(tex ? tex.name : "a texture")} for the snow caps"); return; }
            pictures[tex] = new Picture { W = tex.width, H = tex.height, Px = req.GetData<Color32>().ToArray() };
        });
        return null;
    }

    // ---- the cap's picture ----

    /// <summary>The crust for this sprite at this level: on every solid pixel with nothing solid above it, going down
    /// a few pixels (1 at level 2, 2 at 3, 3 at 4; at level 1 only some of them, specks).</summary>
    static Sprite CapFor(Sprite s, int lvl)
    {
        if (caps.TryGetValue((s, lvl), out var made)) return made;
        if (!pictures.TryGetValue(s.texture, out var pic)) return null;
        var tr = s.textureRect;
        int w = Mathf.RoundToInt(s.rect.width), h = Mathf.RoundToInt(s.rect.height);
        int ox = Mathf.RoundToInt(s.textureRectOffset.x), oy = Mathf.RoundToInt(s.textureRectOffset.y);
        int tx = Mathf.RoundToInt(tr.x), ty = Mathf.RoundToInt(tr.y), tw = Mathf.RoundToInt(tr.width), th = Mathf.RoundToInt(tr.height);
        bool Solid(int x, int y)
        {
            x -= ox; y -= oy;
            if (x < 0 || y < 0 || x >= tw || y >= th) return false;
            int i = (ty + y) * pic.W + tx + x;
            return i >= 0 && i < pic.Px.Length && pic.Px[i].a > 140;
        }
        int depth = lvl <= 1 ? 1 : lvl - 1;
        var px = new Color32[w * h];
        bool any = false;
        for (int x = 0; x < w; x++)
            for (int y = h - 1; y >= 0; y--)
            {
                if (!Solid(x, y) || Solid(x, y + 1)) continue;   // a top edge
                for (int d = 0; d < depth && y - d >= 0 && Solid(x, y - d); d++)
                {
                    float speck = Snow.Hash(x * 3 + s.GetInstanceID(), y * 5 + d);
                    if (lvl == 1 && speck > 0.4f) continue;                 // specks only
                    if (lvl == 2 && d == 0 && speck > 0.85f) continue;      // a crust with gaps
                    var col = Snow.Whites(Snow.Hash(x / 3, y / 3), speck);
                    if (d == depth - 1 && depth > 1) col = new Color32(205, 216, 236, 255);   // its shadowed underside
                    px[y * w + x] = col;
                    any = true;
                }
            }
        Sprite sprite = null;
        if (any)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = $"BW_Cap_{s.name}_{lvl}" };
            tex.SetPixels32(px);
            tex.Apply();
            sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(s.pivot.x / w, s.pivot.y / h), s.pixelsPerUnit);
        }
        caps[(s, lvl)] = sprite;
        return sprite;
    }
}

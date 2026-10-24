using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Tilemaps;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The walls raised as blocks for the chase and first-person views (TiltView): the game's walls are flat tiles painted on
/// the ground, so seen from low down you'd walk over a painting of them. Around the player, each tile cell you bump into
/// (a tilemap's collider on the BodyCollisions layer) becomes a block WallHeight tall: its top the tile drawn on top
/// there (the wall top), its sides the wall face drawn there on the ground layer. The blocks write depth, so whatever is
/// behind a wall is hidden behind it. Drawn only in those views' frames; while they draw, the flat wall tops and the fog
/// (the "Player" layer tilemaps the blocks replace) are hidden. Rebuilt as the player moves.
/// </summary>
internal static class Walls3D
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> height;

    const int Radius = 22;   // cells around the player
    static GameObject root;
    static readonly List<MeshRenderer> parts = new();
    static readonly List<TilemapRenderer> covered = new();   // the flat tilemaps the blocks stand in for
    static readonly List<TilemapRenderer> hiddenNow = new();
    static readonly Dictionary<Texture, Material> materials = new();
    static Vector2Int builtAt;
    static float builtTime, builtHeight;
    static string builtScene;
    static bool reported;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("View", "Walls3D", true, "Chase and first person: the walls raised as blocks (else they stay flat tiles)");
        height = config.Bind("View", "WallHeight", 2f, "Chase and first person: the raised walls' height (0.5-5)");
    }

    public static void Tick(bool turning)
    {
        if (on == null) return;
        if (!turning || !on.Value) { Clear(); return; }
        var p = LocalPlayer.Get();
        if (!p) return;
        var here = new Vector2Int(Mathf.FloorToInt(p.transform.position.x), Mathf.FloorToInt(p.transform.position.y));
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        bool stale = !root || scene != builtScene || (here - builtAt).sqrMagnitude > 36 || Time.unscaledTime - builtTime > 6f
                     || !Mathf.Approximately(builtHeight, height.Value);
        if (stale) Build(here, scene);
    }

    /// <summary>The frame of a turned view: the blocks drawn, the flat tops they replace not.</summary>
    public static void Begin()
    {
        if (!root) return;
        foreach (var r in parts) if (r) r.enabled = true;
        hiddenNow.Clear();
        foreach (var t in covered) if (t && t.enabled) { t.enabled = false; hiddenNow.Add(t); }
    }

    public static void End()
    {
        foreach (var r in parts) if (r) r.enabled = false;
        foreach (var t in hiddenNow) if (t) t.enabled = true;
        hiddenNow.Clear();
    }

    static void Clear()
    {
        if (root) Object.Destroy(root);
        root = null;
        parts.Clear();
        covered.Clear();
    }

    class Piece { public readonly List<Vector3> v = new(); public readonly List<Vector2> uv = new(); public readonly List<int> tri = new(); }

    static void Build(Vector2Int at, string scene)
    {
        Clear();
        builtAt = at; builtTime = Time.unscaledTime; builtScene = scene; builtHeight = height.Value;
        root = new GameObject("EG_Walls3D");
        int wallLayer = LayerMask.NameToLayer("BodyCollisions");
        int player = SortingLayer.NameToID("Player");
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = 1 << wallLayer };
        var hits = new Collider2D[8];
        var maps = Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None)
            .Select(t => (t, r: t.GetComponent<TilemapRenderer>()))
            .Where(x => x.r && x.r.enabled && x.t.gameObject.activeInHierarchy).ToList();

        // the solid cells: a tilemap's collider there
        int n = Radius * 2 + 1;
        var solid = new bool[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var c = new Vector2(at.x - Radius + i + 0.5f, at.y - Radius + j + 0.5f);
                int k = Physics2D.OverlapPoint(c, filter, hits);
                for (int h = 0; h < k; h++)
                    if (hits[h] && hits[h].GetComponent<Tilemap>()) { solid[i, j] = true; break; }
            }
        bool Solid(int i, int j) => i >= 0 && j >= 0 && i < n && j < n && solid[i, j];

        float H = Mathf.Clamp(height.Value, 0.5f, 5f);
        var pieces = new Dictionary<Texture, Piece>();
        var cover = new HashSet<TilemapRenderer>();
        int blocks = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (!solid[i, j]) continue;
                var c = new Vector3(at.x - Radius + i + 0.5f, at.y - Radius + j + 0.5f, 0f);
                // the top: the tile drawn on top there; the sides: the top-most one on the ground layer (a wall face)
                Sprite top = null, face = null;
                (int layer, int order) topRank = (int.MinValue, int.MinValue), faceRank = (int.MinValue, int.MinValue);
                foreach (var (t, r) in maps)
                {
                    var s = t.GetSprite(t.WorldToCell(c));
                    if (!s || !s.texture) continue;
                    var rank = (SortingLayer.GetLayerValueFromID(r.sortingLayerID), r.sortingOrder);
                    if (rank.CompareTo(topRank) > 0) { topRank = rank; top = s; if (r.sortingLayerID == player) cover.Add(r); }
                    if (r.sortingLayerID != player && rank.CompareTo(faceRank) > 0) { faceRank = rank; face = s; }
                }
                face ??= top;
                if (!top) continue;
                blocks++;
                float x0 = c.x - 0.5f, x1 = c.x + 0.5f, y0 = c.y - 0.5f, y1 = c.y + 0.5f, z = -H;
                Quad(pieces, top, new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z));
                if (!Solid(i, j - 1)) Quad(pieces, face, new Vector3(x0, y0, 0), new Vector3(x1, y0, 0), new Vector3(x1, y0, z), new Vector3(x0, y0, z));
                if (!Solid(i, j + 1)) Quad(pieces, face, new Vector3(x1, y1, 0), new Vector3(x0, y1, 0), new Vector3(x0, y1, z), new Vector3(x1, y1, z));
                if (!Solid(i - 1, j)) Quad(pieces, face, new Vector3(x0, y1, 0), new Vector3(x0, y0, 0), new Vector3(x0, y0, z), new Vector3(x0, y1, z));
                if (!Solid(i + 1, j)) Quad(pieces, face, new Vector3(x1, y0, 0), new Vector3(x1, y1, 0), new Vector3(x1, y1, z), new Vector3(x1, y0, z));
            }
        // the fog (a dark overhang drawn flat over everything) goes too: it'd lie over the floor
        foreach (var (t, r) in maps)
            if (r.sortingLayerID == player && t.name.IndexOf("Fog", System.StringComparison.OrdinalIgnoreCase) >= 0) cover.Add(r);
        covered.AddRange(cover);

        foreach (var kv in pieces)
        {
            var go = new GameObject("Walls_" + kv.Key.name);
            go.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = go.name };
            if (kv.Value.v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(kv.Value.v);
            mesh.SetUVs(0, kv.Value.uv);
            mesh.SetColors(Enumerable.Repeat(Color.white, kv.Value.v.Count).ToList());
            mesh.SetTriangles(kv.Value.tri, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MaterialFor(kv.Key);
            mr.sortingLayerName = "Default";
            mr.sortingOrder = -32000;   // (drawn first: everything after is hidden behind it by depth)
            mr.enabled = false;
            parts.Add(mr);
        }
        if (!reported) { reported = true; NetLog.Info($"Walls3D: {blocks} blocks, {pieces.Count} textures, {covered.Count} flat tilemaps covered (shader {Shader?.name ?? "none"})"); }
    }

    static void Quad(Dictionary<Texture, Piece> pieces, Sprite s, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (!pieces.TryGetValue(s.texture, out var p)) pieces[s.texture] = p = new Piece();
        // (the sprite's corner UVs: its smallest and largest)
        var uv = s.uv;
        float u0 = uv.Min(q => q.x), u1 = uv.Max(q => q.x), v0 = uv.Min(q => q.y), v1 = uv.Max(q => q.y);
        int k = p.v.Count;
        p.v.Add(a); p.v.Add(b); p.v.Add(c); p.v.Add(d);
        p.uv.Add(new Vector2(u0, v0)); p.uv.Add(new Vector2(u1, v0)); p.uv.Add(new Vector2(u1, v1)); p.uv.Add(new Vector2(u0, v1));
        p.tri.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
    }

    static Shader shader;
    static Shader Shader => shader ? shader : shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");

    /// <summary>The game's own sprite shader (lit by its 2D lights, both sides drawn), told to write depth.</summary>
    static Material MaterialFor(Texture tex)
    {
        if (materials.TryGetValue(tex, out var m) && m) return m;
        m = new Material(Shader) { name = "EG_Walls3D_" + tex.name, mainTexture = tex };
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
        return materials[tex] = m;
    }

    public static void DrawMenu()
    {
        if (on == null) return;
        bool v = on.Value;
        if (ImGui.Checkbox("Raised walls (chase and first person)", ref v)) on.Value = v;
        float h = height.Value;
        if (ImGui.SliderFloat("Wall height##walls", ref h, 0.5f, 5f, "%.1f")) height.Value = h;
        TiltView.Reset(height, "wallh");
    }
}

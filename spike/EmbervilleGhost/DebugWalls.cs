using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Tilemaps;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// /walls: what stops you in a generated dungeon or the arena. The cells we made solid (our "Blocked" tilemap: the
/// rooms' wall / wall-cover / water tiles) tinted red on the ground, and every other solid collider near you (props,
/// rocks, the rooms' own colliders) outlined in orange, so you can tell a wrongly-solid tile from a prop in the way.
/// Not saved.
/// </summary>
internal static class DebugWalls
{
    public static bool On { get; private set; }
    static readonly List<GameObject> overlays = new List<GameObject>();
    static Tile red;

    public static void Toggle()
    {
        On = !On;
        Clear();
        if (On) Build();
        NetLog.Info(On ? "Walls view: on. Solid tiles: red = wall, yellow = wall top, blue = water; orange boxes = other colliders (/walls again to hide)" : "Walls view: off", toast: true);
    }

    /// <summary>A new place was built: show its solid layer too.</summary>
    public static void Refresh() { if (!On) return; Clear(); Build(); }

    static void Clear()
    {
        foreach (var o in overlays) if (o) Object.Destroy(o);
        overlays.Clear();
    }

    /// <summary>A red copy of each of our solid layers (in the dungeon / arena that exist now).</summary>
    static void Build()
    {
        if (!red)
        {
            var tex = new Texture2D(4, 4) { filterMode = FilterMode.Point };
            var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
            tex.SetPixels(px); tex.Apply();
            red = ScriptableObject.CreateInstance<Tile>();
            red.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new UnityEngine.Vector2(0.5f, 0.5f), 4f);
            red.color = Color.white;   // tinted per layer by its tilemap
            red.colliderType = Tile.ColliderType.None;
        }
        var me = LocalPlayer.Get();
        var like = me ? me.GetComponentInChildren<SpriteRenderer>() : null;
        foreach (var tm in Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!tm || tm.name != "Blocked" || !tm.transform.root.name.StartsWith("EG_")) continue;
            // the solid cells, coloured by the layer that made them (wall red, wall top yellow, water blue); anything solid
            // from none of them (the arena's ring) red
            var solid = new HashSet<Vector3Int>();
            foreach (var c in tm.cellBounds.allPositionsWithin) if (tm.HasTile(c)) solid.Add(c);
            var colours = new (string layer, Color colour)[] { ("water", new Color(0.2f, 0.5f, 1f, 0.4f)), ("wall top", new Color(1f, 0.85f, 0.1f, 0.4f)), ("wall", new Color(1f, 0.15f, 0.1f, 0.38f)) };
            var left = new HashSet<Vector3Int>(solid);
            bool fromDungeon = tm.transform.root.name.StartsWith("EG_Dungeon");
            foreach (var (layer, colour) in colours)
            {
                if (!fromDungeon || !Dungeon.DebugLayers.TryGetValue(layer, out var cells)) continue;
                var mine = new List<Vector3Int>();
                foreach (var c in cells) if (left.Remove(c)) mine.Add(c);
                Overlay(tm, mine, colour, like);
            }
            Overlay(tm, new List<Vector3Int>(left), new Color(1f, 0.15f, 0.1f, 0.38f), like);
        }
    }

    static void Overlay(Tilemap under, List<Vector3Int> cells, Color colour, SpriteRenderer like)
    {
        if (cells.Count == 0) return;
        var go = new GameObject("EG_WallsView");
        go.transform.SetParent(under.transform.parent, false);
        var copy = go.AddComponent<Tilemap>();
        copy.color = colour;
        var r = go.AddComponent<TilemapRenderer>();
        if (like) { r.sortingLayerID = like.sortingLayerID; r.sortingOrder = 900; }
        var arr = cells.ToArray();
        var tiles = new TileBase[arr.Length];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = red;
        copy.SetTiles(arr, tiles);
        overlays.Add(go);
    }

    /// <summary>From Hud.Draw: orange outlines on the other solid colliders around you.</summary>
    public static void Draw()
    {
        if (!On) return;
        var cam = Camera.main;
        var me = LocalPlayer.Get();
        if (!cam || !me) return;
        var dl = ImGui.GetBackgroundDrawList();
        uint orange = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.6f, 0.1f, 0.9f));
        foreach (var c in Physics2D.OverlapCircleAll(me.transform.position, 14f))
        {
            if (!c || c.isTrigger || !c.enabled) continue;
            if (c is TilemapCollider2D || c is CompositeCollider2D) continue;   // the tiles: red already (or the scene's)
            if (c.attachedRigidbody && c.attachedRigidbody.gameObject == me.gameObject) continue;
            var b = c.bounds;
            var a = cam.WorldToScreenPoint(b.min);
            var z = cam.WorldToScreenPoint(b.max);
            dl.AddRect(new Vector2(a.x, Screen.height - z.y), new Vector2(z.x, Screen.height - a.y), orange, 0f, ImDrawFlags.None, 1.5f);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
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
        NetLog.Info(On ? "Walls view: on. Solid tiles: red = wall, yellow = wall top, blue = water; orange boxes = other colliders; under the mouse: the tiles and colliders there (Shift+P logs it; /walls again to hide)" : "Walls view: off", toast: true);
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

    /// <summary>From Hud.Draw: Shift+P pins what's under the mouse (any time); with /walls on, orange outlines on the
    /// other solid colliders around you and the probe box beside the mouse.</summary>
    public static void Draw()
    {
        var cam = Camera.main;
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (cam && kb != null && kb.shiftKey.isPressed && kb.pKey.wasPressedThisFrame && !ImGuiUnity.Typing && MouseWorld(cam, out var at))
        {
            pinned = LinesAt(at);
            pinnedWhen = System.DateTime.Now;
            NetLog.Info("Under the mouse: " + string.Join(" | ", pinned), toast: false);
            NetLog.Info("Pinned what's under the mouse: Insert > Under the mouse", toast: true);
        }
        if (!On) return;
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
        if (!MouseWorld(cam, out var w)) return;
        var mp = UnityEngine.InputSystem.Mouse.current.position.ReadValue();
        string text = string.Join("\n", LinesAt(w));
        var box = new Vector2(mp.x + 18f, Screen.height - mp.y + 12f);
        var size = ImGui.CalcTextSize(text);
        dl.AddRectFilled(box - new Vector2(4f, 3f), box + size + new Vector2(4f, 3f), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.75f)), 3f);
        dl.AddText(box, 0xFFFFFFFF, text);
    }

    static List<string> pinned;
    static System.DateTime pinnedWhen;

    static bool MouseWorld(Camera cam, out Vector3 w)
    {
        w = default;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var mp = mouse.position.ReadValue();
        w = cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, -cam.transform.position.z));
        w.z = 0f;
        return true;
    }

    /// <summary>Everything at this spot: the cell, whether our collision made it solid or walkable, the dungeon rooms'
    /// tiles there layer by layer (the game's layer tag, the tilemap's own name, the tile, the room; the layers we skip
    /// too), the colliders there (triggers too) and the things drawn there (props, decals).</summary>
    static List<string> LinesAt(Vector3 w)
    {
        var cs = Dungeon.cellSize;
        var cell = new Vector3Int(Mathf.FloorToInt(w.x / cs.x), Mathf.FloorToInt(w.y / cs.y), 0);
        string ours = Dungeon.wallCells.Contains(cell) ? "SOLID" : Dungeon.floorCells.Contains(cell) ? "walkable" : "not ours (no room tile)";
        var why = new List<string>();
        foreach (var layer in new[] { "wall", "wall top", "water", "edge of a hole", "pit", "room's own water", "room's own wall" })
            if (Dungeon.DebugLayers.TryGetValue(layer, out var set) && set.Contains(cell)) why.Add(layer);
        var lines = new List<string> { $"cell {cell.x},{cell.y} at {w.x:0.0},{w.y:0.0}: our collision says {ours}{(why.Count > 0 ? " (from " + string.Join(", ", why) + ")" : "")}" };
        if (Dungeon.DebugCells.TryGetValue(cell, out var here)) lines.AddRange(here.Select(h => "  tile: " + h));
        else lines.Add("  (no dungeon room tiles here)");
        var p = new UnityEngine.Vector2(w.x, w.y);
        foreach (var c in Physics2D.OverlapPointAll(p))
            if (c) lines.Add($"  collider: {c.GetType().Name} on {Path(c.transform)}{(c.isTrigger ? " (trigger)" : "")}{(c.enabled ? "" : " (off)")}, layer {LayerMask.LayerToName(c.gameObject.layer)}");
        // every tilemap drawing a tile here (ours merged, the rooms' own still on, anything else)
        int maps = 0;
        foreach (var tm in Object.FindObjectsByType<UnityEngine.Tilemaps.Tilemap>(FindObjectsSortMode.None))
        {
            if (!tm || !tm.gameObject.activeInHierarchy) continue;
            var tr = tm.GetComponent<UnityEngine.Tilemaps.TilemapRenderer>();
            var at = tm.WorldToCell(new Vector3(w.x, w.y, tm.transform.position.z));
            var tile = tm.GetTile(at);
            if (!tile) continue;
            if (++maps > 10) { lines.Add("  ... more tilemaps here"); break; }
            var drawnSprite = tm.GetSprite(at);
            lines.Add($"  tilemap: {tile.name}{(drawnSprite ? $" (picture {drawnSprite.name})" : "")} on {Path(tm.transform)}{(tr ? $" ({SortingLayer.IDToName(tr.sortingLayerID)}/{tr.sortingOrder}{(tr.enabled ? "" : ", off")})" : " (not drawn)")}");
        }
        int drawn = 0;
        foreach (var r in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
        {
            if (!r || !r.enabled || !r.sprite || !r.gameObject.activeInHierarchy) continue;
            var b = r.bounds;
            if (p.x < b.min.x || p.x > b.max.x || p.y < b.min.y || p.y > b.max.y) continue;
            if (r.GetComponentInParent<PlayerMaster>()) continue;
            if (++drawn > 8) { lines.Add("  ... more drawn here"); break; }
            lines.Add($"  drawn: {r.sprite.name} on {Path(r.transform)} ({SortingLayer.IDToName(r.sortingLayerID)}/{r.sortingOrder})");
        }
        return lines;
    }

    static string Path(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t && i < 3; i++, t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }

    /// <summary>Insert > Under the mouse: what Shift+P pinned.</summary>
    public static void DrawMenu()
    {
        ImGui.TextWrapped("Point at a spot in the world and press Shift+P: what's there is pinned here (and written to the log). " +
                          "Our collision's verdict, the dungeon rooms' tiles in that cell (the game's layer tag, the tilemap's name, the tile, the room), " +
                          "the colliders and the things drawn there. /walls also shows it live beside the mouse.");
        if (pinned == null) { ImGui.TextDisabled("Nothing pinned yet."); return; }
        ImGui.TextDisabled($"Pinned {pinnedWhen:HH:mm:ss}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(string.Join("\n", pinned));
        ImGui.SameLine();
        if (ImGui.SmallButton("Log it")) NetLog.Info("Under the mouse: " + string.Join(" | ", pinned), toast: true);
        ImGui.Separator();
        foreach (var l in pinned) ImGui.TextUnformatted(l);
    }
}

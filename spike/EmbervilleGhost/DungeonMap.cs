using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Tilemaps;

namespace EmbervilleGhost;

/// <summary>
/// The dungeon minimap. The game's minimap is a second camera (MinimapCam) filming the "Minimap" layer into the
/// panel at the top right (UIManager.panelMinimap: its frame and +/- zoom), behind a story unlock; the full game's
/// dungeon renderer drew each floor on that layer, the demo doesn't. In a generated dungeon we: show a copy of the
/// panel (unlock or not) at the top left under the party strip (the game's own stays as the game has it); draw the floor on the Minimap layer (floor pale,
/// walls dark) as you explore it (about 8 steps around you, fog elsewhere); and mark what you've seen: the stairs
/// (ember), the way home (blue), chests (gold, grey once opened), the floor's boss (red). Teammates' own markers are
/// already on that layer. The ~ key hides / shows it. Back to the top right (and its lock) when you leave.
/// </summary>
internal static class DungeonMap
{
    const float RevealRadius = 8f;
    static bool shown = true;

    static GameObject mapRoot;
    static Tilemap map;
    static Tile tile;
    static readonly HashSet<Vector3Int> revealed = new HashSet<Vector3Int>();
    static readonly List<(GameObject go, SpriteRenderer sr, System.Func<bool> visible, System.Func<Color> colour)> markers = new List<(GameObject, SpriteRenderer, System.Func<bool>, System.Func<Color>)>();
    static readonly HashSet<Enemy> bossMarked = new HashSet<Enemy>();
    static float nextReveal, nextMarkers;
    static int minimapLayer = -1, sortLayer;

    static readonly Color Floor = new Color(0.62f, 0.6f, 0.55f, 1f), Wall = new Color(0.18f, 0.17f, 0.2f, 1f);

    // ---- the floor on the Minimap layer ----

    /// <summary>A new floor is built (and we're in it): its map starts dark.</summary>
    public static void Built()
    {
        revealed.Clear();
        markers.Clear();
        bossMarked.Clear();
        if (mapRoot) Object.Destroy(mapRoot);
        var root = Dungeon.Root;
        if (!root) return;
        minimapLayer = MinimapLayer();
        if (minimapLayer < 0) { NetLog.Warn("Dungeon map: no minimap layer found", toast: false); return; }
        var me = LocalPlayer.Get();
        sortLayer = 0;
        if (me)
            foreach (var sr in me.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr.gameObject.layer == minimapLayer) { sortLayer = sr.sortingLayerID; break; }

        mapRoot = new GameObject("EG_DungeonMap") { layer = minimapLayer };
        mapRoot.transform.SetParent(root.transform, false);
        mapRoot.transform.position = Vector3.zero;
        mapRoot.AddComponent<Grid>().cellSize = Dungeon.cellSize;
        var go = new GameObject("Cells") { layer = minimapLayer };
        go.transform.SetParent(mapRoot.transform, false);
        map = go.AddComponent<Tilemap>();
        var r = go.AddComponent<TilemapRenderer>();
        r.sortingLayerID = sortLayer;
        r.sortingOrder = -50;
        if (!tile)
        {
            var tex = new Texture2D(4, 4) { filterMode = FilterMode.Point };
            var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
            tex.SetPixels(px); tex.Apply();
            tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            tile.colliderType = Tile.ColliderType.None;
            tile.flags = TileFlags.None;
        }

        // markers: the floor's pads, its chests (the boss is added once it's spawned)
        foreach (var (at, down, home) in Delve.FloorPads())
        {
            var c = down ? new Color(1f, 0.55f, 0.15f) : home ? new Color(0.4f, 0.75f, 1f) : Color.white;
            var spot = at;
            Marker(spot, 2.2f, () => Seen(spot), () => c);
        }
        foreach (var chest in root.GetComponentsInChildren<Chest>(true))
        {
            var ch = chest;
            var spot = ch.transform.position;
            Marker(spot, 1.4f, () => ch && Seen(spot), () => ch && ch.alreadyOpened ? new Color(0.45f, 0.45f, 0.45f) : new Color(1f, 0.82f, 0.25f));
        }
        nextReveal = nextMarkers = 0f;
    }

    /// <summary>The layer the minimap camera films (its culling mask; the layer's name isn't there in the demo).</summary>
    static int MinimapLayer()
    {
        int byName = LayerMask.NameToLayer("Minimap");
        if (byName >= 0) return byName;
        FindCamera();
        if (cam)
            for (int i = 0; i < 32; i++) if ((cam.cullingMask & (1 << i)) != 0) return i;
        return 17;   // (the demo's: from its tag manager)
    }

    static void FindCamera()
    {
        if (cam) return;
        foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            if (c && c.gameObject.scene.IsValid() && c.CompareTag("MinimapCam")) { cam = c; break; }
    }

    static bool Seen(Vector3 at)
    {
        var cs = Dungeon.cellSize;
        return revealed.Contains(new Vector3Int(Mathf.FloorToInt(at.x / cs.x), Mathf.FloorToInt(at.y / cs.y), 0));
    }

    static GameObject Marker(Vector3 at, float size, System.Func<bool> visible, System.Func<Color> colour)
    {
        var go = new GameObject("Marker") { layer = minimapLayer };
        go.transform.SetParent(mapRoot.transform, false);
        go.transform.position = at;
        go.transform.localScale = Vector3.one * size;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = tile.sprite;
        sr.sortingLayerID = sortLayer;
        sr.sortingOrder = -40;
        go.SetActive(false);
        markers.Add((go, sr, visible, colour));
        return go;
    }

    static void Reveal()
    {
        var me = LocalPlayer.Get();
        if (!me || !map) return;
        var cs = Dungeon.cellSize;
        var p = me.transform.position;
        int cx = Mathf.FloorToInt(p.x / cs.x), cy = Mathf.FloorToInt(p.y / cs.y), r = Mathf.CeilToInt(RevealRadius / cs.x);
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            {
                if (x * x + y * y > r * r) continue;
                var c = new Vector3Int(cx + x, cy + y, 0);
                if (revealed.Contains(c)) continue;
                bool floor = Dungeon.floorCells.Contains(c), wall = !floor && Dungeon.wallCells.Contains(c);
                if (!floor && !wall) continue;
                revealed.Add(c);
                map.SetTile(c, tile);
                map.SetTileFlags(c, TileFlags.None);
                map.SetColor(c, floor ? Floor : Wall);
            }
    }

    static void UpdateMarkers()
    {
        if (!mapRoot) return;
        foreach (var (go, sr, visible, colour) in markers)
        {
            if (!go) continue;
            bool v = visible();
            if (go.activeSelf != v) go.SetActive(v);
            if (v) sr.color = colour();
        }
        // the floor's boss: a red mark that follows it while it lives (once you've seen where it is)
        foreach (var e in MiniBoss.Alive)
        {
            if (!e || bossMarked.Contains(e)) continue;
            bossMarked.Add(e);
            var boss = e;
            var go = Marker(boss.transform.position, 2.4f, () => boss && !boss.IsDead && Seen(boss.transform.position), () => new Color(1f, 0.2f, 0.2f));
            go.AddComponent<Follow>().Target = boss.transform;
        }
    }

    sealed class Follow : MonoBehaviour
    {
        public Transform Target;
        void LateUpdate() { if (Target) transform.position = Target.position; }
    }

    // ---- the panel: top left, always there in a dungeon, ~ to hide ----

    static RectTransform panel;   // our copy of the game's minimap panel (the game's own stays as it is)
    static bool hijacked;

    public static void Tick()
    {
        var kb = Keyboard.current;
        bool inDungeon = Dungeon.Active;
        if (inDungeon && kb != null && kb.backquoteKey.wasPressedThisFrame && !ImGuiUnity.Typing && !Chat.IsOpen)
        {
            shown = !shown;
            NetLog.Info(shown ? "Dungeon map: shown (~ hides it)" : "Dungeon map: hidden (~ shows it)", toast: true);
        }
        if (inDungeon) Hijack(); else Release();
        if (!inDungeon) return;
        if (Time.unscaledTime >= nextReveal) { nextReveal = Time.unscaledTime + 0.2f; Reveal(); }
        if (Time.unscaledTime >= nextMarkers) { nextMarkers = Time.unscaledTime + 0.5f; UpdateMarkers(); }
    }

    static UIManager UI => Object.FindAnyObjectByType<UIManager>();

    static void Hijack()
    {
        hijacked = true;
        if (!panel)
        {
            var ui = UI;
            if (!ui || !ui.panelMinimap) return;
            // the panel is a full-screen layer with the minimap's frame at its top right: copy just the frame (the part
            // holding the map picture)
            panel = CopyPanel(MapFrame(ui), ui.panelMinimap.transform as RectTransform);
            if (!panel) return;
        }
        bool want = shown && !PlayerStatusUtil.LocalBusy;
        if (panel.gameObject.activeSelf != want) panel.gameObject.SetActive(want);
        if (want) FollowCamera();
        if (want && !reported) { reported = true; Report(); }
    }

    static bool reported;

    /// <summary>
    /// A copy of the game's minimap panel (its frame, and the picture of the minimap camera), pinned top left under
    /// the party strip: copied switched off (its scripts never wake), the game's scripts and buttons stripped, every
    /// part of it on (before the story unlock some are off), the original's size.
    /// </summary>
    /// <summary>The minimap's frame: the game's map holder, else the smallest box around the map picture that has art of its own.</summary>
    static RectTransform MapFrame(UIManager ui)
    {
        if (ui.minimapHolder && ui.minimapHolder.transform is RectTransform h && h.rect.width > 8f && h.rect.width < Screen.width * 0.6f) return h;
        var raw = ui.panelMinimap.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
        if (!raw) return ui.panelMinimap.transform as RectTransform;
        RectTransform best = raw.rectTransform;
        for (var t = raw.transform.parent; t && t != ui.panelMinimap.transform; t = t.parent)
        {
            var rt = t as RectTransform;
            if (!rt || rt.rect.width > Screen.width * 0.6f) break;
            best = rt;
        }
        return best;
    }

    static RectTransform CopyPanel(RectTransform source, RectTransform canvasLayer)
    {
        if (!source) return null;
        var holder = new GameObject("EG_MapHolder");
        holder.SetActive(false);
        holder.transform.SetParent(canvasLayer.parent, false);
        RectTransform copy;
        try
        {
            copy = (RectTransform)Object.Instantiate(source.gameObject, holder.transform).transform;
            copy.name = "EG_DungeonMapPanel";
            foreach (var b in copy.GetComponentsInChildren<UnityEngine.UI.Selectable>(true)) Object.DestroyImmediate(b.gameObject);   // zoom buttons, the toggle: they'd do nothing here
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb && mb.GetType().Assembly == typeof(UIManager).Assembly) Object.DestroyImmediate(mb);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
            foreach (var g in copy.GetComponentsInChildren<CanvasGroup>(true)) { g.alpha = 1f; g.blocksRaycasts = false; }
            foreach (var gr in copy.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) gr.raycastTarget = false;
            // the "Mini Map" tab under the frame: its label, and the little plate it sits on
            foreach (var tmp in copy.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                var tab = tmp.transform.parent && tmp.transform.parent != copy && !tmp.transform.parent.GetComponentInChildren<UnityEngine.UI.RawImage>(true) ? tmp.transform.parent : tmp.transform;
                Object.DestroyImmediate(tab.gameObject);
            }
            copy.SetParent(canvasLayer.parent, false);
        }
        finally { Object.Destroy(holder); }

        var size = source.rect.size;
        if (size.x < 8f || size.y < 8f)
        {
            var raw = source.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
            size = raw && raw.rectTransform.rect.width > 8f ? raw.rectTransform.rect.size + new Vector2(24f, 24f) : new Vector2(220f, 220f);
        }
        copy.anchorMin = copy.anchorMax = new Vector2(0f, 1f);
        copy.pivot = new Vector2(0f, 1f);
        copy.sizeDelta = size;
        copy.anchoredPosition = new Vector2(16f, -96f);
        copy.SetAsLastSibling();
        copy.gameObject.SetActive(true);
        return copy;
    }

    static Camera cam;

    /// <summary>The minimap camera, switched on and over us (in a scene it may be placed by the scene, not follow us).</summary>
    static void FollowCamera()
    {
        FindCamera();
        if (!cam) return;
        if (!cam.gameObject.activeSelf) cam.gameObject.SetActive(true);
        if (!cam.enabled) cam.enabled = true;
        if (!cam.GetComponent<OnUs>()) cam.gameObject.AddComponent<OnUs>();
    }

    /// <summary>On the minimap camera: over us, after whatever the game moves it with (it stays at the well otherwise).</summary>
    [DefaultExecutionOrder(30000)]
    sealed class OnUs : MonoBehaviour
    {
        void LateUpdate()
        {
            if (!hijacked) return;
            var me = LocalPlayer.Get();
            if (!me) return;
            var p = me.transform.position;
            transform.position = new Vector3(p.x, p.y, transform.position.z);
        }

        void OnPreCull()
        {
            if (!hijacked) return;
            var me = LocalPlayer.Get();
            if (me) transform.position = new Vector3(me.transform.position.x, me.transform.position.y, transform.position.z);
        }
    }

    /// <summary>Once per visit, into the log: what the panel and its camera look like (for when it doesn't show).</summary>
    static void Report()
    {
        string path = panel.name;
        for (var t = panel.parent; t; t = t.parent) path = t.name + (t.gameObject.activeSelf ? "" : "[OFF]") + "/" + path;
        var raws = panel.GetComponentsInChildren<UnityEngine.UI.RawImage>(true);
        string rawInfo = string.Join(", ", System.Linq.Enumerable.Select(raws, r => $"{r.name}{(r.isActiveAndEnabled ? "" : "[off]")} tex={(r.texture ? r.texture.name : "none")}"));
        NetLog.Info($"Dungeon map: panel {path} activeInHierarchy={panel.gameObject.activeInHierarchy} size={panel.rect.size}; images: {rawInfo}; " +
                    $"camera {(cam ? $"{cam.name} on={cam.isActiveAndEnabled} at {cam.transform.position} target={(cam.targetTexture ? cam.targetTexture.name : "screen")} mask={cam.cullingMask}" : "NOT FOUND")}; " +
                    $"map cells {revealed.Count}, layer {minimapLayer}, panel now {panel.rect.size}", toast: false);
    }

    static void Release()
    {
        if (!hijacked) return;
        hijacked = false;
        if (panel) Object.Destroy(panel.gameObject);
        panel = null;
        reported = false;
        bossMarked.Clear();
        markers.Clear();
        if (mapRoot) Object.Destroy(mapRoot);
    }
}

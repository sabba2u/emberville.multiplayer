using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>The camera views (Insert > View): the game's own, tilted, chasing behind the player, through their eyes.</summary>
internal enum ViewMode { Flat, Tilted, Chase, FirstPerson }

/// <summary>
/// The camera views (Insert > View; /tilt, /chase, /fps). Tilted: the world seen from behind and above at an angle, in perspective,
/// the floor receding, everything that stands (people, creatures, props, trees, labels) stood up facing the camera like a
/// pop-up book. Only the picture changes: just before the game's camera draws a frame it's turned and the standing things
/// are stood up; straight after, all of it is put back, so the game (Cinemachine, physics, AI, its own maths) never sees
/// it. The aim follows the picture (a ray from the tilted camera through the mouse, onto the ground). The game's
/// pixel-perfect camera (it only works flat) is off while it's on. A test: tiles stay flat (walls too).
/// Chase: the camera low behind the player, turned by the mouse (the cursor held while playing; Alt frees it), the player
/// walking the way the camera faces (W ahead, A/D sideways) and aiming straight ahead. First person: the same through the
/// player's eyes (the mouse looks up and down too), the player unseen. In those two the walls are raised as blocks
/// (Walls3D) and the standing things turn to face the way you look.
/// </summary>
[HarmonyPatch]
internal static class TiltView
{
    static ConfigEntry<bool> on;   // (the old "Tilted" switch: read once into the mode)
    static ConfigEntry<ViewMode> mode;
    static ConfigEntry<float> pitch, fov, chaseDistance, chaseAngle, chaseFov, eyeHeight, fpsFov, turnSpeed;
    static ConfigEntry<string> flatWords;
    static bool hooked;

    // what the frame changed, put back after it
    struct CamState { public Vector3 Pos; public Quaternion Rot; public bool Ortho; public float Fov, Near, Far; }
    static CamState saved;
    static readonly List<(Transform t, Quaternion rot)> stood = new();
    static readonly List<Transform> roots = new();
    static readonly Dictionary<Transform, List<Transform>> flats = new();   // a standing thing's parts that lie on the ground
    static readonly List<(Transform t, Vector3 lp, Quaternion lr)> kept = new();
    static readonly Dictionary<Transform, Transform> riders = new();   // art laid over a bigger thing (vines on a house) -> that thing
    static readonly List<(Transform t, Transform parent, int index, Vector3 lp, Quaternion lr, Vector3 ls)> moved = new();
    static readonly Dictionary<Transform, SortingGroup> addedGroups = new();
    static readonly Dictionary<UnityEngine.Tilemaps.TilemapRenderer, int> fogWas = new();
    static float nextScan;
    static Behaviour pixelPerfect;   // (URP's PixelPerfectCamera: found by name, its namespace moved between URP versions)
    static bool pixelPerfectWas;

    public static ViewMode Mode => mode != null ? mode.Value : ViewMode.Flat;
    public static bool On => Mode != ViewMode.Flat;
    /// <summary>Chase or first person: the camera turned by the mouse, the walls raised.</summary>
    public static bool Turning => Mode == ViewMode.Chase || Mode == ViewMode.FirstPerson;

    static float yaw, lookUp;   // the way the camera faces (degrees anticlockwise from north), and first person's look up/down
    static bool locked;
    /// <summary>The way the camera faces, on the ground.</summary>
    public static Vector2 Heading => new Vector2(-Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("View", "Tilted", false, "A tilted 2.5D view (the world from behind and above, things standing up); only the picture changes");
        pitch = config.Bind("View", "TiltAngle", 50f, "How far the camera leans back from looking straight down, in degrees (20-75)");
        fov = config.Bind("View", "TiltFieldOfView", 40f, "The tilted camera's field of view, in degrees");
        mode = config.Bind("View", "Camera", ViewMode.Flat, "The camera: Flat (the game's own), Tilted, Chase (behind the player), FirstPerson");
        if (on.Value) { mode.Value = ViewMode.Tilted; on.Value = false; }
        chaseDistance = config.Bind("View", "ChaseDistance", 5f, "Chase camera: how far behind the player (2-12)");
        chaseAngle = config.Bind("View", "ChaseAngle", 22f, "Chase camera: how far it looks down, in degrees (5-60)");
        chaseFov = config.Bind("View", "ChaseFieldOfView", 60f, "Chase camera: field of view, in degrees");
        eyeHeight = config.Bind("View", "EyeHeight", 1.1f, "First person: the eyes' height above the ground (0.3-3)");
        fpsFov = config.Bind("View", "FirstPersonFieldOfView", 75f, "First person: field of view, in degrees");
        turnSpeed = config.Bind("View", "MouseTurn", 0.15f, "Chase and first person: degrees turned per pixel of mouse movement");
        flatWords = config.Bind("View", "TiltFlatNames", "stair,step,rug,carpet,path,floor,decal,puddle,shadow,bridge,road",
            "In the tilted view, ground art drawn on the game's Default layer whose name (or a parent's) has one of these words stays lying flat; the rest stands up");
    }

    public static void Toggle(bool value) => SetMode(value ? ViewMode.Tilted : ViewMode.Flat);

    /// <summary>A view by chat (/tilt, /chase, /fps): that view, or the game's own when it's already on.</summary>
    public static void Switch(ViewMode m) => SetMode(Mode == m ? ViewMode.Flat : m);

    public static void SetMode(ViewMode m)
    {
        var was = Mode;
        mode.Value = m;
        if (m == ViewMode.Flat) RestorePixelPerfect();
        if (Turning && (was == ViewMode.Flat || was == ViewMode.Tilted))
        {
            // (starts facing the way the player looks)
            var p = LocalPlayer.Get();
            var d = p ? p.lastLookDir : Vector2.up;
            if (d.sqrMagnitude > 0.01f) yaw = Vector2.SignedAngle(Vector2.up, d);
            lookUp = 0f;
        }
        string how = Turning ? " (the mouse turns; hold Alt for the cursor)" : "";
        NetLog.Info(m == ViewMode.Flat ? "The game's own view" : $"{Name(m)} view on{how}", toast: true);
    }

    static string Name(ViewMode m) => m switch { ViewMode.FirstPerson => "First-person", ViewMode.Chase => "Chase", ViewMode.Tilted => "Tilted", _ => "Flat" };

    public static void Tick()
    {
        if (on == null) return;
        if (!hooked)
        {
            hooked = true;
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }
        if (!Turning) Lock(false);
        Walls3D.Tick(Turning);
        if (!On) { if (addedGroups.Count > 0 || fogWas.Count > 0) Undo(); return; }
        if (Turning) Turn();
        var cam = Camera.main;
        if (cam && !pixelPerfect)
        {
            pixelPerfect = cam.GetComponents<Behaviour>().FirstOrDefault(b => b && b.GetType().Name == "PixelPerfectCamera");
            if (pixelPerfect) { pixelPerfectWas = pixelPerfect.enabled; pixelPerfect.enabled = false; }
        }
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; Scan(); }
    }

    /// <summary>Chase and first person: the cursor held in the middle while playing (free with Alt held, or with a
    /// screen, menu or chat up), and its movement turns the camera.</summary>
    static void Turn()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        bool alt = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
        bool playing = MainInput.Actions.Player.enabled && !UIPanels.HasOpenedPanel && !ImGuiUnity.IsOpen && !Chat.IsOpen;
        Lock(Application.isFocused && playing && !alt && Time.timeScale > 0f);
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (!locked || mouse == null) return;
        var d = mouse.delta.ReadValue();
        float k = Mathf.Clamp(turnSpeed.Value, 0.01f, 2f);
        yaw = Mathf.Repeat(yaw - d.x * k + 180f, 360f) - 180f;
        if (Mode == ViewMode.FirstPerson) lookUp = Mathf.Clamp(lookUp + d.y * k, -60f, 60f);
    }

    static void Lock(bool value)
    {
        if (locked == value) return;
        locked = value;
        Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !value;
    }

    static void RestorePixelPerfect()
    {
        if (pixelPerfect) pixelPerfect.enabled = pixelPerfectWas;
        pixelPerfect = null;
    }

    // ---- what stands up ----

    /// <summary>
    /// The things to stand up: everything drawn as a sprite (people, creatures, trees, houses, crates...), except ground
    /// art: on the game's "Default" layer, a sprite whose name or a parent's has one of TiltFlatNames' words (stairs and
    /// their railings, rugs, paths, shadows...) stays lying flat. Each standing thing turns as a whole about its own feet, so its
    /// parts keep their places (a tree's leaves over its trunk); its ground parts (a staircase's steps under its posts) are
    /// kept lying where they were. The root of a thing: the nearest one up its parents with a body or a game part (a
    /// Rigidbody2D, a creature, the player, a breakable) or an "Art" child; else the sprite itself. World labels too.
    /// </summary>
    static void Scan()
    {
        roots.Clear();
        flats.Clear();
        var seen = new HashSet<Transform>();
        var standing = new HashSet<SpriteRenderer>();
        foreach (var sr in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
        {
            if (!sr || !sr.enabled || !sr.gameObject.activeInHierarchy) continue;
            var root = RootOf(sr.transform);
            if (!root || !Stands(sr, root)) continue;
            standing.Add(sr);
            if (seen.Add(root)) roots.Add(root);
        }
        foreach (var t in Object.FindObjectsByType<TMPro.TextMeshPro>(FindObjectsSortMode.None))
            if (t && t.gameObject.activeInHierarchy && seen.Add(t.transform)) roots.Add(t.transform);
        // (a root inside another root turns with it: only the outermost)
        roots.RemoveAll(r => { for (var p = r.parent; p; p = p.parent) if (seen.Contains(p)) return true; return false; });
        foreach (var r in roots)
        {
            var lying = r.GetComponentsInChildren<SpriteRenderer>(false).Where(sr => sr && !standing.Contains(sr) && sr.transform != r).Select(sr => sr.transform).ToList();
            if (lying.Count > 0) flats[r] = lying;
        }
        FindRiders(standing);
        Group(standing);
        LowerFog();
    }

    /// <summary>
    /// Art laid over a bigger standing thing (vines and holes on a house, made as separate objects) rides on it: while
    /// the frame draws it's put under that thing, so it stands up with it (about the thing's feet, not its own) and sorts
    /// with it. A rider: most of it inside the bigger thing's picture, its bottom above the thing's feet (on the wall, not
    /// on the ground in front), and nothing of its own that moves or breaks.
    /// </summary>
    static void FindRiders(HashSet<SpriteRenderer> standing)
    {
        riders.Clear();
        var box = new Dictionary<Transform, Bounds>();
        foreach (var sr in standing)
        {
            var r = RootOf(sr.transform);
            for (var p = r ? r.parent : null; p; p = p.parent) if (box.ContainsKey(p) || roots.Contains(p)) { r = p; break; }
            if (!r || !roots.Contains(r)) continue;
            if (box.TryGetValue(r, out var b)) { b.Encapsulate(sr.bounds); box[r] = b; } else box[r] = sr.bounds;
        }
        static float Area(Bounds b) => b.size.x * b.size.y;
        var bySize = box.OrderByDescending(kv => Area(kv.Value)).ToList();
        foreach (var kv in bySize)
        {
            var r = kv.Key; var b = kv.Value;
            if (Area(b) <= 0f || GameThing(r) || flats.ContainsKey(r)) continue;
            Transform best = null; float bestArea = float.MaxValue;
            foreach (var hkv in bySize)
            {
                var h = hkv.Key; var hb = hkv.Value;
                float ha = Area(hb);
                if (ha < Area(b) * 2f) break;   // (sorted: the rest are smaller)
                if (h == r || riders.ContainsKey(h) || ha >= bestArea || !hb.Contains(b.center)) continue;
                if (b.min.y < h.position.y + 0.05f) continue;   // (stands on the ground in front)
                var o = Overlap(b, hb);
                if (o < Area(b) * 0.8f) continue;
                best = h; bestArea = ha;
            }
            if (best) riders[r] = best;
        }
    }

    static float Overlap(Bounds a, Bounds b)
    {
        float w = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x), h = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
        return w > 0f && h > 0f ? w * h : 0f;
    }

    static bool GameThing(Transform t) =>
        t.GetComponent<Rigidbody2D>() || t.GetComponent<Enemy>() || t.GetComponent<PlayerMaster>() || t.GetComponent<DamageableObject>();

    /// <summary>
    /// Each standing thing of several pictures (a house of pieces) or on the ground layer sorts as one, by where it stands
    /// (a sorting group on the "Player" layer, while the view is tilted): stood up, its top reaches over what's behind it,
    /// and a tree behind sorts behind all of it. (Things with ground parts left lying keep their own sorting.)
    /// </summary>
    static void Group(HashSet<SpriteRenderer> standing)
    {
        foreach (var t in addedGroups.Keys.Where(t => !t).ToList()) addedGroups.Remove(t);
        int ground = SortingLayer.NameToID("Default");
        var count = new Dictionary<Transform, (int n, bool low)>();
        foreach (var sr in standing)
        {
            var r = RootOf(sr.transform);
            for (var p = r ? r.parent : null; p; p = p.parent) if (roots.Contains(p)) { r = p; break; }
            if (!r) continue;
            count.TryGetValue(r, out var c);
            count[r] = (c.n + 1, c.low || sr.sortingLayerID == ground);
        }
        foreach (var r in roots)
        {
            if (!r || riders.ContainsKey(r) || flats.ContainsKey(r) || addedGroups.ContainsKey(r) || r.GetComponent<SortingGroup>()) continue;
            if (!count.TryGetValue(r, out var c) || (c.n < 2 && !c.low)) continue;
            var g = r.gameObject.AddComponent<SortingGroup>();
            g.sortingLayerName = "Player";
            g.sortingOrder = 0;
            addedGroups[r] = g;
        }
    }

    /// <summary>The dark world-edge fog (drawn over everything) goes under the standing things while tilted: stood up, a
    /// house's roof reaches into it.</summary>
    static void LowerFog()
    {
        int player = SortingLayer.NameToID("Player");
        foreach (var r in fogWas.Keys.Where(r => !r).ToList()) fogWas.Remove(r);
        foreach (var r in Object.FindObjectsByType<UnityEngine.Tilemaps.TilemapRenderer>(FindObjectsSortMode.None))
        {
            if (!r || fogWas.ContainsKey(r) || r.sortingLayerID != player || r.sortingOrder <= 0) continue;
            if (r.name.IndexOf("Fog", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            fogWas[r] = r.sortingOrder;
            r.sortingOrder = -1;
        }
    }

    /// <summary>The view off: our sorting groups gone, the fog back over everything.</summary>
    static void Undo()
    {
        foreach (var g in addedGroups.Values) if (g) Object.Destroy(g);
        addedGroups.Clear();
        foreach (var kv in fogWas) if (kv.Key) kv.Key.sortingOrder = kv.Value;
        fogWas.Clear();
        riders.Clear();
    }

    static string[] words = new string[0];
    static string wordsFrom;

    /// <summary>Stands unless it's ground art: on "Default", with a TiltFlatNames word in its name or a parent's (up to
    /// six levels: a railing under its staircase).</summary>
    static bool Stands(SpriteRenderer sr, Transform root)
    {
        if (sr.sortingLayerID != SortingLayer.NameToID("Default")) return true;
        if (wordsFrom != flatWords.Value)
        {
            wordsFrom = flatWords.Value;
            words = (wordsFrom ?? "").Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
        }
        var t = sr.transform;
        for (int i = 0; t && i < 6; i++, t = t.parent)
            foreach (var w in words)
                if (t.name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    /// <summary>The thing a sprite belongs to: up its parents, one with a body or a game part or an "Art" child; else the
    /// nearest with a sorting group (the game's own "these pictures are one thing": a wall and its vines); else itself.</summary>
    static Transform RootOf(Transform t)
    {
        var at = t;
        Transform grouped = null;
        for (int i = 0; at && i < 8; i++, at = at.parent)
        {
            if (GameThing(at) || at.Find("Art")) return at;
            if (at.GetComponent<Canvas>()) return null;   // (UI)
            if (!grouped && at.GetComponent<SortingGroup>() && !addedGroups.ContainsKey(at)) grouped = at;
        }
        return grouped ? grouped : t;
    }

    // ---- the frame ----

    static float P => Mathf.Clamp(pitch.Value, 20f, 75f);
    static readonly Vector3 Down = new Vector3(0f, 0f, 1f);   // (the ground is z = 0; up from it is -z)

    /// <summary>How far the view looks down from level, in degrees (the tilted view's lean is from straight down).</summary>
    static float Below => Mode switch
    {
        ViewMode.Chase => Mathf.Clamp(chaseAngle.Value, 5f, 60f),
        ViewMode.FirstPerson => -lookUp,
        _ => 90f - P,
    };

    static float Fov => Mathf.Clamp(Mode switch { ViewMode.Chase => chaseFov.Value, ViewMode.FirstPerson => fpsFov.Value, _ => fov.Value }, 15f, 110f);

    /// <summary>The camera for the game camera as it is now. Tilted: looking at the same middle from behind and above,
    /// as much ground in view. Chase: behind the player, looking the camera's way. First person: from the player's eyes.</summary>
    static (Vector3 pos, Quaternion rot) Pose(Camera cam)
    {
        var h = Mode == ViewMode.Tilted ? Vector2.up : Heading;
        float below = Below * Mathf.Deg2Rad;
        var forward = new Vector3(h.x, h.y, 0f) * Mathf.Cos(below) + Down * Mathf.Sin(below);
        var rot = Quaternion.LookRotation(forward, -Down);
        var player = LocalPlayer.Get();
        if (Mode == ViewMode.Tilted || !player)
        {
            var mid = cam.transform.position; mid.z = 0f;
            float half = Mathf.Max(0.5f, cam.orthographicSize);
            float d = half / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            return (mid - forward * d, rot);
        }
        var at = player.transform.position; at.z = 0f;
        if (Mode == ViewMode.FirstPerson) return (at - Down * Mathf.Clamp(eyeHeight.Value, 0.3f, 3f), rot);
        var target = at - Down * 1f;   // (a little above the feet)
        return (target - forward * Mathf.Clamp(chaseDistance.Value, 2f, 12f), rot);
    }

    // the 2D renderer's draw order (the game sorts by height on the screen: y); while the camera is turned it sorts by
    // distance the way the camera faces
    static System.Reflection.FieldInfo sortAxisField;
    static object sortData;
    static Vector3 sortAxisWas;
    static bool sortLooked, sortSet;

    static void SetSortAxis(Vector2 h)
    {
        if (!sortLooked)
        {
            sortLooked = true;
            try
            {
                var asset = GraphicsSettings.currentRenderPipeline ?? QualitySettings.renderPipeline;
                var list = asset ? AccessTools.Field(asset.GetType(), "m_RendererDataList")?.GetValue(asset) as ScriptableRendererData[] : null;
                sortData = list?.FirstOrDefault(d => d && d.GetType().Name == "Renderer2DData");
                if (sortData != null) sortAxisField = AccessTools.Field(sortData.GetType(), "m_TransparencySortAxis");
            }
            catch (System.Exception e) { NetLog.Info("View: no draw-order axis: " + e.Message); }
        }
        if (sortAxisField == null) return;
        if (!sortSet) sortAxisWas = (Vector3)sortAxisField.GetValue(sortData);
        sortAxisField.SetValue(sortData, new Vector3(h.x, h.y, sortAxisWas.z));
        sortSet = true;
    }

    static void RestoreSortAxis()
    {
        if (!sortSet) return;
        sortAxisField.SetValue(sortData, sortAxisWas);
        sortSet = false;
    }

    static readonly List<Renderer> hidden = new();

    static void Begin(ScriptableRenderContext ctx, Camera cam)
    {
        var main = Camera.main;
        if (!On || !main || (cam != main && cam != TiltShift.Camera) || !cam.orthographic) return;
        if (cam != main)
        {
            // (the tilt-shift's blur camera: the game camera's view, tilted the same)
            cam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
            cam.orthographicSize = main.orthographicSize;
        }
        saved = new CamState { Pos = cam.transform.position, Rot = cam.transform.rotation, Ortho = true, Fov = cam.fieldOfView, Near = cam.nearClipPlane, Far = cam.farClipPlane };
        var (pos, rot) = Pose(cam);
        cam.orthographic = false;
        cam.fieldOfView = Fov;
        cam.nearClipPlane = Mode == ViewMode.FirstPerson ? 0.05f : 0.1f; cam.farClipPlane = 2000f;
        cam.transform.SetPositionAndRotation(pos, rot);
        var h = Mode == ViewMode.Tilted ? Vector2.up : Heading;
        if (Turning) SetSortAxis(h);
        // stood up facing the camera: leaned back from the ground by the view's angle (about x), then turned the camera's way
        float upright = Mode == ViewMode.Tilted ? P : 90f - Mathf.Max(0f, Below);
        var stand = Quaternion.AngleAxis(Vector2.SignedAngle(Vector2.up, h), Vector3.forward) * Quaternion.AngleAxis(-upright, Vector3.right);
        hidden.Clear();
        if (Mode == ViewMode.FirstPerson && LocalPlayer.Get() is { } me)
            foreach (var r in me.GetComponentsInChildren<Renderer>(false))
                if (r.enabled) { r.enabled = false; hidden.Add(r); }   // (through their own eyes: not drawn)
        Walls3D.Begin();
        stood.Clear();
        kept.Clear();
        moved.Clear();
        foreach (var kv in riders)
        {
            var r = kv.Key; var host = kv.Value;
            // (rides on its bigger thing for the frame: under it, so it stands up and sorts with it)
            if (!r || !host || !r.parent) continue;
            moved.Add((r, r.parent, r.GetSiblingIndex(), r.localPosition, r.localRotation, r.localScale));
            r.SetParent(host, true);
        }
        foreach (var r in roots)
        {
            if (!r || riders.ContainsKey(r)) continue;
            List<(Transform t, Vector3 wp, Quaternion wr)> lie = null;
            if (flats.TryGetValue(r, out var parts))
                foreach (var f in parts)
                    if (f) { (lie ??= new()).Add((f, f.position, f.rotation)); kept.Add((f, f.localPosition, f.localRotation)); }
            stood.Add((r, r.rotation));
            r.rotation = stand * r.rotation;
            if (lie != null) foreach (var (t, wp, wr) in lie) t.SetPositionAndRotation(wp, wr);   // (its ground parts stay lying)
        }
    }

    static void End(ScriptableRenderContext ctx, Camera cam)
    {
        if ((cam != Camera.main && cam != TiltShift.Camera) || !saved.Ortho) return;
        foreach (var (t, rot) in stood) if (t) t.rotation = rot;
        foreach (var (t, lp, lr) in kept) if (t) { t.localPosition = lp; t.localRotation = lr; }
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            var (t, parent, index, lp, lr, ls) = moved[i];
            if (!t || !parent) continue;
            t.SetParent(parent, false);
            t.SetSiblingIndex(index);
            t.localPosition = lp; t.localRotation = lr; t.localScale = ls;
        }
        foreach (var r in hidden) if (r) r.enabled = true;
        hidden.Clear();
        Walls3D.End();
        RestoreSortAxis();
        stood.Clear();
        kept.Clear();
        moved.Clear();
        cam.orthographic = true;
        cam.fieldOfView = saved.Fov;
        cam.nearClipPlane = saved.Near; cam.farClipPlane = saved.Far;
        cam.transform.SetPositionAndRotation(saved.Pos, saved.Rot);
        saved.Ortho = false;
    }

    // ---- the mouse, on the ground in the turned picture ----

    /// <summary>Where the mouse points on the ground (z = 0), in the turned picture (chase and first person: the middle
    /// of the screen, or a few steps ahead when that's sky). False when the view is the game's own.</summary>
    public static bool MouseWorld(out Vector3 at)
    {
        at = default;
        var cam = Camera.main;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (!On || !cam || mouse == null) return false;
        var (pos, rot) = Pose(cam);
        var mp = locked ? new Vector2(Screen.width / 2f, Screen.height / 2f) : mouse.position.ReadValue();
        float tanY = Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad), tanX = tanY * Screen.width / Mathf.Max(1f, Screen.height);
        var dir = rot * new Vector3((mp.x / Screen.width * 2f - 1f) * tanX, (mp.y / Screen.height * 2f - 1f) * tanY, 1f);
        float k = Mathf.Abs(dir.z) < 1e-5f ? -1f : -pos.z / dir.z;
        if (k <= 0f || k > 60f)
        {
            if (!Turning || !LocalPlayer.Get()) return false;
            var h = Heading;
            at = LocalPlayer.Get().transform.position + new Vector3(h.x, h.y, 0f) * 4f;
        }
        else at = pos + dir * k;
        at.z = 0f;
        return true;
    }

    /// <summary>The player's aim from the turned picture (after the game's own, which used the flat one): chase and first
    /// person aim the way the camera faces.</summary>
    [HarmonyPatch(typeof(PlayerMaster), "GetLookDirection"), HarmonyPostfix]
    static void Aim(PlayerMaster __instance, bool __runOriginal)
    {
        if (!On || !__runOriginal || !__instance.mouseLook || MainInput.CurrentScheme == ControllerScheme.Gamepad) return;
        if (__instance != LocalPlayer.Get()) return;
        Vector2 d;
        if (Turning) d = Heading;
        else if (MouseWorld(out var at)) d = at - __instance.transform.position;
        else return;
        if (d.sqrMagnitude < 0.0001f) return;
        __instance.lookDir = d.normalized;
        __instance.lastLookDir = __instance.lookDir;
        __instance.SetPlayerDirection();
    }

    /// <summary>Chase and first person: the movement keys go the camera's way (W ahead, S back, A/D sideways).</summary>
    [HarmonyPatch(typeof(PlayerMaster), "GetInput"), HarmonyPostfix]
    static void Steer(PlayerMaster __instance)
    {
        if (!Turning || __instance != LocalPlayer.Get()) return;
        var i = __instance.input;
        var h = Heading;
        var right = new Vector2(h.y, -h.x);
        __instance.input = right * i.x + h * i.y;
    }

    // ---- Insert menu ----

    /// <summary>A Reset button beside a slider (shown when it's off its default): back to the default.</summary>
    static void Slider(string label, ConfigEntry<float> entry, float min, float max, string format, string id)
    {
        float v = entry.Value;
        if (ImGui.SliderFloat(label, ref v, min, max, format)) entry.Value = v;
        Reset(entry, id);
    }

    internal static void Reset(ConfigEntry<float> entry, string id)
    {
        float def = (float)entry.DefaultValue;
        if (Mathf.Approximately(entry.Value, def)) return;
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##" + id)) entry.Value = def;
    }

    public static void DrawMenu()
    {
        foreach (ViewMode m in System.Enum.GetValues(typeof(ViewMode)))
        {
            if (m != ViewMode.Flat) ImGui.SameLine();
            if (ImGui.RadioButton(Name(m) + "##view", Mode == m)) SetMode(m);
        }
        ImGui.TextDisabled("Tilted: the world from behind and above, things standing up. Chase: behind the player, the mouse turns");
        ImGui.TextDisabled("(hold Alt for the cursor), W goes the way you face. First person: through the player's eyes, walls raised.");
        ImGui.Separator();
        ImGui.Text("Tilted");
        float a = pitch.Value;
        if (ImGui.SliderFloat("Tilt##tilt", ref a, 20f, 75f, "%.0f°")) pitch.Value = a;
        Reset(pitch, "tilt");
        float f = fov.Value;
        if (ImGui.SliderFloat("Field of view##tilt", ref f, 20f, 70f, "%.0f°")) fov.Value = f;
        Reset(fov, "fov");
        ImGui.Text("Chase");
        Slider("Distance##chase", chaseDistance, 2f, 12f, "%.1f", "chased");
        Slider("Looking down##chase", chaseAngle, 5f, 60f, "%.0f°", "chasea");
        Slider("Field of view##chase", chaseFov, 30f, 100f, "%.0f°", "chasef");
        ImGui.Text("First person");
        Slider("Eye height##fps", eyeHeight, 0.3f, 3f, "%.2f", "eye");
        Slider("Field of view##fps", fpsFov, 40f, 110f, "%.0f°", "fpsf");
        Slider("Mouse turn##view", turnSpeed, 0.02f, 0.6f, "%.2f", "turn");
        Walls3D.DrawMenu();
        ImGui.TextDisabled("A test: the ground's tiles stay flat (walls too); only the picture changes, the game plays as ever.");
        TiltShift.DrawMenu();
    }
}

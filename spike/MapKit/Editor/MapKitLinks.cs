// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/MapKitLinks.cs (Unity 6000.1.17f1).
// In the Scene view it labels every marker ("Door: Vault", "Teleport: Blue"...) and draws what's linked to what:
//   a lever or plate -> its door, a teleporter -> its pair or its "Arrive:" spot (an arrow for one-way), stairs down ->
//   the room they go to (a label). A link that goes nowhere is drawn red, and listed by "Emberville > Check Links".
// Editor only: it never goes into a bundle, and the game doesn't need it.
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class EmbervilleMapKitLinks
{
    static readonly Regex Kind = new Regex(@"^\s*(PlayerStart|Breakable|Door|Lever|Plate|Exit|Teleport|Arrive|Stairs)\s*(:\s*(.*))?$", RegexOptions.IgnoreCase);
    static readonly Color Ok = new Color(0.55f, 0.9f, 1f), Broken = new Color(1f, 0.3f, 0.3f);

    static EmbervilleMapKitLinks()
    {
        SceneView.duringSceneGui += Draw;
        EditorApplication.hierarchyChanged += SyncDoors;
    }

    /// <summary>A door renamed by hand: its tick boxes follow the new name.</summary>
    static void SyncDoors()
    {
        foreach (var d in Object.FindObjectsByType<EmbervilleDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (d && d.gameObject.name != d.Compose() && d.ReadName()) EditorUtility.SetDirty(d);
    }

    // ---- doors: the doorway they fill ----

    /// <summary>The tilemaps whose tiles block (the game's room layers, and our first names for them).</summary>
    static readonly HashSet<string> Solid = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Wall", "WallCover", "Fence", "Collision", "Walls", "WallTops" };

    /// <summary>The wall cells of the open scenes (tiles on the solid layers, and stone_wall placeholders anywhere), and the
    /// cell size.</summary>
    static HashSet<Vector3Int> Walls(out Vector3 cell)
    {
        var walls = new HashSet<Vector3Int>();
        cell = Vector3.one;
        foreach (var tm in Object.FindObjectsByType<UnityEngine.Tilemaps.Tilemap>(FindObjectsSortMode.None))
        {
            if (tm.layoutGrid) cell = tm.layoutGrid.cellSize;
            foreach (var c in tm.cellBounds.allPositionsWithin)
            {
                var t = tm.GetTile(c);
                if (t && (Solid.Contains(tm.name) || t.name == "stone_wall"))
                {
                    var w = tm.CellToWorld(c) + new Vector3(cell.x, cell.y, 0f) * 0.5f;
                    walls.Add(new Vector3Int(Mathf.FloorToInt(w.x / cell.x), Mathf.FloorToInt(w.y / cell.y), 0));
                }
            }
        }
        return walls;
    }

    /// <summary>The opening a door marker is in (the same rule the game's loader uses): walls above and below it -> an
    /// up-down wall, the door seen side-on; walls left and right -> a left-right wall, the door seen from the front.
    /// The cells of the opening, in order.</summary>
    static bool Doorway(HashSet<Vector3Int> walls, Vector3 at, Vector3 cell, out List<Vector3Int> opening, out bool sideOn)
    {
        var c = new Vector3Int(Mathf.FloorToInt(at.x / cell.x), Mathf.FloorToInt(at.y / cell.y), 0);
        int Run(int dx, int dy) { for (int i = 1; i <= 4; i++) if (walls.Contains(c + new Vector3Int(dx * i, dy * i, 0))) return i; return 0; }
        int up = Run(0, 1), down = Run(0, -1), right = Run(1, 0), left = Run(-1, 0);
        bool ud = up > 0 && down > 0, lr = left > 0 && right > 0;
        if (ud && lr) { if (up + down <= left + right) lr = false; else ud = false; }
        opening = new List<Vector3Int>();
        sideOn = ud;
        if (ud) for (int y = c.y - down + 1; y <= c.y + up - 1; y++) opening.Add(new Vector3Int(c.x, y, 0));
        else if (lr) for (int x = c.x - left + 1; x <= c.x + right - 1; x++) opening.Add(new Vector3Int(x, c.y, 0));
        return opening.Count > 0;
    }

    static readonly Color DoorFill = new Color(0.6f, 0.35f, 0.15f, 0.55f), DoorEdge = new Color(1f, 0.85f, 0.2f, 1f),
        Gold = new Color(1f, 0.82f, 0.2f), Grey = new Color(0.75f, 0.75f, 0.75f);

    /// <summary>The door over its doorway: a half in each tile, the split between them, a padlock (shut and gold when
    /// locked, open and grey when not), the key's name; drawn apart when it starts open; red when the doorway isn't 2.</summary>
    static void DrawDoor(M m, HashSet<Vector3Int> walls, Vector3 cell, GUIStyle style)
    {
        var opt = Regex.Match(m.arg, @"\((.*)\)\s*$").Groups[1].Value;
        var lockM = Regex.Match(opt, @"locked\s*:\s*([^,]+)", RegexOptions.IgnoreCase);
        bool locked = lockM.Success, open = Regex.IsMatch(opt, @"(^|,)\s*open\s*(,|$)", RegexOptions.IgnoreCase);
        if (!Doorway(walls, m.at, cell, out var opening, out bool sideOn))
        {
            Handles.color = Broken;
            Handles.DrawWireDisc(m.at, Vector3.forward, 0.45f);
            Handles.Label(m.at + new Vector3(0.35f, -0.1f, 0f), "not in a doorway (no walls either side)", new GUIStyle(style) { normal = { textColor = Broken } });
            return;
        }
        bool good = opening.Count == 2;
        var edge = good ? DoorEdge : Broken;
        var along = sideOn ? Vector3.up : Vector3.right;
        float step = sideOn ? cell.y : cell.x;
        Vector3 Corner(Vector3Int c) => new Vector3(c.x * cell.x, c.y * cell.y, 0f);
        for (int i = 0; i < opening.Count; i++)
        {
            var c = Corner(opening[i]);
            // starts open: each half pulled back towards its own wall end
            var shift = open ? along * (i < (opening.Count - 1) / 2f ? -0.25f : 0.25f) * step : Vector3.zero;
            var r = new[] { c, c + new Vector3(cell.x, 0f, 0f), c + new Vector3(cell.x, cell.y, 0f), c + new Vector3(0f, cell.y, 0f) };
            for (int k = 0; k < 4; k++) r[k] += shift;
            Handles.DrawSolidRectangleWithOutline(r, DoorFill, edge);
        }
        // the split down the middle
        var a = Corner(opening[0]);
        var b = Corner(opening[opening.Count - 1]) + new Vector3(cell.x, cell.y, 0f);
        var mid = (a + b) * 0.5f;
        var across = sideOn ? new Vector3(cell.x * 0.5f, 0f, 0f) : new Vector3(0f, cell.y * 0.5f, 0f);
        Handles.color = edge;
        Handles.DrawAAPolyLine(4f, mid - across, mid + across);
        Padlock(mid, locked);
        var notes = new List<string>();
        if (locked) notes.Add("key: " + lockM.Groups[1].Value.Trim());
        if (open) notes.Add("starts open");
        if (!good) notes.Add($"doorway is {opening.Count} tiles (doors are 2)");
        if (notes.Count > 0)
            Handles.Label(mid + new Vector3(-0.5f, -0.35f, 0f), string.Join(", ", notes), new GUIStyle(style) { normal = { textColor = good ? Color.white : Broken } });
    }

    static void Padlock(Vector3 at, bool locked)
    {
        var col = locked ? Gold : Grey;
        float w = 0.32f, h = 0.24f;
        var body = new[] { at + new Vector3(-w / 2, -h, 0f), at + new Vector3(w / 2, -h, 0f), at + new Vector3(w / 2, 0f, 0f), at + new Vector3(-w / 2, 0f, 0f) };
        Handles.DrawSolidRectangleWithOutline(body, col, Color.black);
        // the shackle: down into the body when locked, lifted and off to one side when not
        Handles.color = col;
        var top = at + new Vector3(locked ? 0f : 0.06f, locked ? 0f : 0.08f, 0f);
        Handles.DrawWireArc(top, Vector3.forward, Vector3.right, 180f, w * 0.32f, 3f);
        Handles.color = Color.black;
        Handles.DrawSolidDisc(at + new Vector3(0f, -h * 0.5f, 0f), Vector3.forward, 0.035f);
    }

    struct M { public string kind, arg, name; public bool oneWay; public Vector3 at; public Transform t; }

    static List<M> Markers()
    {
        var list = new List<M>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.isLoaded) continue;
            foreach (var t in s.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
            {
                var m = Kind.Match(t.name);
                if (!m.Success) continue;
                string arg = m.Groups[3].Value.Trim();
                string name = Regex.Replace(arg, @"^\s*(up|down)?\s*(->|→)?\s*", "", RegexOptions.IgnoreCase);
                name = Regex.Replace(name, @"\s*\(.*\)\s*$", "").Trim();   // (a door's options: open, locked: Key)
                list.Add(new M { kind = m.Groups[1].Value.ToLowerInvariant(), arg = arg, name = name, oneWay = Regex.IsMatch(arg, @"(->|→)"), at = t.position, t = t });
            }
        }
        return list;
    }

    /// <summary>Each link: from, to (null: goes nowhere), why.</summary>
    static IEnumerable<(M from, M? to, string problem)> Links(List<M> all)
    {
        bool Named(M m, string kind, string name) => m.kind == kind && string.Equals(m.name, name, System.StringComparison.OrdinalIgnoreCase);
        foreach (var m in all)
        {
            switch (m.kind)
            {
                case "lever":
                case "plate":
                {
                    var doors = all.Where(d => Named(d, "door", m.name)).ToList();
                    if (doors.Count == 0) yield return (m, null, $"no \"Door: {m.name}\"");
                    else yield return (m, doors[0], null);
                    break;
                }
                case "teleport" when m.oneWay:
                {
                    var a = all.Where(d => Named(d, "arrive", m.name)).ToList();
                    if (a.Count == 0) yield return (m, null, $"no \"Arrive: {m.name}\"");
                    else yield return (m, a[0], null);
                    break;
                }
                case "teleport":
                {
                    var pair = all.Where(d => Named(d, "teleport", m.name) && !d.oneWay).ToList();
                    if (pair.Count != 2) yield return (m, null, $"{pair.Count} teleporters called {m.name} (a pair is two)");
                    else { var other = pair[0].t == m.t ? pair[1] : pair[0]; if (m.t.GetInstanceID() < other.t.GetInstanceID()) yield return (m, other, null); }
                    break;
                }
                case "stairs":
                    if (m.name.Length == 0 && !Regex.IsMatch(m.arg, @"^\s*up\s*$", RegexOptions.IgnoreCase)) yield return (m, null, "goes nowhere (Stairs: up, or Stairs: down -> room)");
                    break;
            }
        }
    }

    static void Draw(SceneView view)
    {
        var all = Markers();
        if (all.Count == 0) return;
        var style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = Color.white } };
        var walls = Walls(out var cell);
        foreach (var m in all.Where(m => m.kind == "door")) DrawDoor(m, walls, cell, style);
        foreach (var m in all)
            Handles.Label(m.at + new Vector3(0.35f, 0.45f, 0f), m.t.name, style);

        foreach (var (from, to, problem) in Links(all))
        {
            if (to is M d)
            {
                Handles.color = Ok;
                Handles.DrawDottedLine(from.at, d.at, 4f);
                if (from.kind != "teleport" || from.oneWay) Arrow(from.at, d.at);
            }
            else
            {
                Handles.color = Broken;
                Handles.DrawWireDisc(from.at, Vector3.forward, 0.45f);
                Handles.Label(from.at + new Vector3(0.35f, -0.1f, 0f), problem, new GUIStyle(style) { normal = { textColor = Broken } });
            }
        }
    }

    static void Arrow(Vector3 a, Vector3 b)
    {
        var dir = (b - a).normalized;
        if (dir == Vector3.zero) return;
        var side = new Vector3(-dir.y, dir.x, 0f);
        var tip = b - dir * 0.35f;
        Handles.DrawLine(tip, tip - dir * 0.3f + side * 0.18f);
        Handles.DrawLine(tip, tip - dir * 0.3f - side * 0.18f);
    }

    [MenuItem("Emberville/Check Links")]
    public static void Check()
    {
        var all = Markers();
        var bad = Links(all).Where(l => l.problem != null).ToList();
        if (!all.Any(m => m.kind == "playerstart")) Debug.LogWarning("Emberville: no PlayerStart (you'll arrive in the middle of the room)");
        var walls = Walls(out var cell);
        foreach (var d in all.Where(m => m.kind == "door"))
        {
            if (!Doorway(walls, d.at, cell, out var opening, out _)) Debug.LogWarning($"Emberville: \"{d.t.name}\" isn't in a doorway (no walls on either side)", d.t);
            else if (opening.Count != 2) Debug.LogWarning($"Emberville: \"{d.t.name}\": its doorway is {opening.Count} tiles; the game's doors are 2", d.t);
        }
        foreach (var (from, _, problem) in bad) Debug.LogWarning($"Emberville: \"{from.t.name}\": {problem}", from.t);
        Debug.Log(bad.Count == 0 ? $"Emberville: {all.Count} markers, every link goes somewhere" : $"Emberville: {bad.Count} broken links (above; click one to select it)");
    }
}

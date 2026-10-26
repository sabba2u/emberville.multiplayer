// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/PropsWindow.cs (with the Core folder).
// Emberville > Props: the game's props, harvested from its scenes and prefabs in your export (Emberville > Settings).
// Harvest once (a list kept in Library/); pick a prop, click in the Scene view to place it (or scatter several with the
// brush). The first time a prop is used it's cut out of its scene as a prefab of its own (Assets/GameArt/Props/...), its
// art copied in. In the game a "live" prop is swapped for the game's own (it breaks, burns, drops loot); a "look only"
// one stays as placed (it looks right and blocks where the game's does).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EmbervilleMapKit.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class EmbervilleProps
{
    const string CatalogFile = "Library/EmbervilleProps.tsv", ClassesFile = "Library/EmbervilleScriptClasses.tsv";
    public const string PropsFolder = EmbervilleGameArt.ArtFolder + "/Props";

    static Dictionary<string, string> classes;
    static List<PropEntry> catalog;

    /// <summary>The game's script classes by GUID (from the export's script stubs' .meta files).</summary>
    public static string ClassOf(string guid)
    {
        if (classes == null)
        {
            classes = new Dictionary<string, string>();
            if (File.Exists(ClassesFile))
                foreach (var l in File.ReadAllLines(ClassesFile)) { var p = l.Split('\t'); if (p.Length == 2) classes[p[0]] = p[1]; }
            else if (EmbervilleGameArt.Ready)
            {
                foreach (var meta in Directory.EnumerateFiles(Path.Combine(EmbervilleGameArt.ExportRoot, "Scripts"), "*.cs.meta", SearchOption.AllDirectories))
                {
                    string head; using (var r = new StreamReader(meta)) head = r.ReadLine() + "\n" + r.ReadLine();
                    var g = ExportIndex.GuidOfMeta(head);
                    if (g != null) classes[g] = Path.GetFileName(meta).Replace(".cs.meta", "");
                }
                File.WriteAllLines(ClassesFile, classes.Select(p => p.Key + "\t" + p.Value));
            }
        }
        return guid != null && classes.TryGetValue(guid, out var c) ? c : null;
    }

    public static List<PropEntry> Catalog()
    {
        if (catalog == null) catalog = File.Exists(CatalogFile) ? PropCatalog.Load(CatalogFile) : new List<PropEntry>();
        return catalog;
    }

    /// <summary>Every prop of the game's scenes, and every prefab that is a prop, listed (one entry per look).</summary>
    public static void Harvest()
    {
        if (!EmbervilleGameArt.Ready) { Debug.LogWarning("Emberville: pick the export in Emberville > Settings first"); return; }
        string root = EmbervilleGameArt.ExportRoot;
        var prefabs = Directory.GetFiles(Path.Combine(root, "GameObject"), "*.prefab");
        var live = new HashSet<string>(prefabs.Select(Path.GetFileNameWithoutExtension).SelectMany(n => new[] { n, UnityYaml.BaseName(n) }));
        var scenes = Directory.GetFiles(Path.Combine(root, "Scenes"), "*.unity", SearchOption.AllDirectories);
        var all = new List<PropEntry>();
        var t = System.DateTime.Now;
        try
        {
            for (int i = 0; i < scenes.Length; i++)
            {
                var name = Path.GetFileNameWithoutExtension(scenes[i]);
                EditorUtility.DisplayProgressBar("Emberville", $"Harvesting props: {name}", (float)i / (scenes.Length + 1));
                var rel = scenes[i].Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                foreach (var e in PropCatalog.FromFile(name, File.ReadAllText(scenes[i]), ClassOf, live)) { e.File = rel; all.Add(e); }
            }
            // the game's own prefabs that are a prop as a whole (their name is what the game spawns)
            var seen = new HashSet<string>();
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (i % 50 == 0) EditorUtility.DisplayProgressBar("Emberville", "Harvesting props: the game's prefabs", (scenes.Length + (float)i / prefabs.Length) / (scenes.Length + 1));
                var text = File.ReadAllText(prefabs[i]);
                if (!text.Contains("m_Name: Art")) continue;   // (quick: no Art part, not a prop)
                var y = UnityYaml.Parse(text);
                var top = y.GameObjects.FirstOrDefault(g => y.ParentOf(g) == 0);
                if (top == 0 || !Props.Find(y, ClassOf).Contains(top)) continue;
                foreach (var e in PropCatalog.FromFile("Prefabs", text, ClassOf, live).Where(e => e.RootId == top))
                {
                    e.Name = Path.GetFileNameWithoutExtension(prefabs[i]);
                    e.Live = !e.SaveTouching;
                    e.File = "GameObject/" + Path.GetFileName(prefabs[i]);
                    if (seen.Add(e.Name)) all.Add(e);
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }
        PropCatalog.Save(all, CatalogFile);
        catalog = all;
        Debug.Log($"Emberville: harvested {all.Count} props in {(System.DateTime.Now - t).TotalSeconds:0}s: " +
                  string.Join(", ", all.GroupBy(e => e.Source).Select(g => $"{g.Key} {g.Count()}")) + "; " +
                  string.Join(", ", all.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}")) + $"; {all.Count(e => e.Live)} live");
    }

    static string Safe(string s) => Regex.Replace(s ?? "", @"[^A-Za-z0-9 _#()\-.]", "_");

    public static string PrefabPath(PropEntry e) => $"{PropsFolder}/{Safe(e.Source)}/{Safe(e.Name)}.prefab";

    /// <summary>The prop as a prefab in the project (cut out of its file the first time, its art copied in).</summary>
    public static GameObject ImportProp(PropEntry e)
    {
        var path = PrefabPath(e);
        if (!File.Exists(path))
        {
            var text = File.ReadAllText(Path.Combine(EmbervilleGameArt.ExportRoot, e.File));
            var cut = Props.Cut(UnityYaml.Parse(text), e.RootId, ClassOf);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int copied = 0;
            // its art copied in, and its references pointed at the art's new GUIDs
            var newOf = new Dictionary<string, string>();
            var idx = EmbervilleGameArt.Index();
            foreach (var guid in ExportIndex.ReferencedGuids(cut))
            {
                var rel = idx.PathOf(guid);
                if (rel == null || rel.StartsWith("Scripts/") || rel.StartsWith("Shader/")) continue;
                copied += EmbervilleGameArt.Import(guid, refresh: false).Count;
                var it = EmbervilleGameArt.Identity(rel);
                if (it != null) newOf[guid] = it.NewGuid;
            }
            File.WriteAllText(path, ArtIdentity.Rewrite(cut, newOf));
            // its own GUID from what it is, so everyone's is the same
            var key = "prop|" + e.Source + "|" + e.Name;
            File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + ArtIdentity.Guid(key) + "\nPrefabImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");
            EmbervilleGameArt.Record(new[] { (ArtIdentity.Guid(key), key) });
            AssetDatabase.Refresh();
            Debug.Log($"Emberville: imported the prop {e.Name} from {e.Source} ({copied} art files copied)");
        }
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    /// <summary>One placed in the open room, at this spot: under "== Props" ("== Doors" for doors).</summary>
    public static GameObject Place(PropEntry e, Vector3 at, bool flip = false)
    {
        var prefab = ImportProp(e);
        if (!prefab) { Debug.LogWarning($"Emberville: couldn't import {e.Name}"); return null; }
        var scene = EditorSceneManager.GetActiveScene();
        var room = EmbervilleRoomLayers.Organize(scene, string.IsNullOrEmpty(scene.name) ? "Room" : scene.name);
        var group = room.transform.Find(e.Kind == "Door" ? "== Doors" : e.Kind == "Light" ? "== Lights" : "== Props");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        go.transform.SetParent(group, false);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        if (flip) { var s = go.transform.localScale; go.transform.localScale = new Vector3(-s.x, s.y, s.z); }
        Undo.RegisterCreatedObjectUndo(go, "Place " + e.Name);
        EditorSceneManager.MarkSceneDirty(scene);
        return go;
    }

    // ---- thumbnails: the prop's first sprite, cut from its texture straight out of the export ----

    static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
    static readonly Dictionary<string, (Texture2D tex, Rect uv)> thumbs = new Dictionary<string, (Texture2D, Rect)>();
    static readonly Regex RectRe = new Regex(@"m_Rect:\s*serializedVersion: \d+\s*x: ([-\d.e]+)\s*y: ([-\d.e]+)\s*width: ([-\d.e]+)\s*height: ([-\d.e]+)");
    static readonly Regex TexRe = new Regex(@"texture: \{fileID: 2800000, guid: ([0-9a-f]+)");

    public static (Texture2D tex, Rect uv) Thumb(PropEntry e)
    {
        if (thumbs.TryGetValue(e.FirstSprite ?? "", out var t)) return t;
        t = (null, default);
        try
        {
            var guid = (e.FirstSprite ?? "").Split(':').LastOrDefault();
            var idx = EmbervilleGameArt.Index();
            var spritePath = idx?.PathOf(guid);
            if (spritePath != null)
            {
                var text = File.ReadAllText(Path.Combine(EmbervilleGameArt.ExportRoot, spritePath));
                var r = RectRe.Match(text); var tx = TexRe.Match(text);
                var texPath = tx.Success ? idx.PathOf(tx.Groups[1].Value) : null;
                if (r.Success && texPath != null)
                {
                    if (!textures.TryGetValue(texPath, out var tex))
                    {
                        tex = new Texture2D(2, 2) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                        tex.LoadImage(File.ReadAllBytes(Path.Combine(EmbervilleGameArt.ExportRoot, texPath)));
                        textures[texPath] = tex;
                    }
                    float F(int i) => float.Parse(r.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
                    t = (tex, new Rect(F(1) / tex.width, F(2) / tex.height, F(3) / tex.width, F(4) / tex.height));
                }
            }
        }
        catch (System.Exception ex) { Debug.LogWarning($"Emberville: no thumbnail for {e.Name}: {ex.Message}"); }
        thumbs[e.FirstSprite ?? ""] = t;
        return t;
    }

    public static void ClearThumbs()
    {
        foreach (var tex in textures.Values) if (tex) Object.DestroyImmediate(tex);
        textures.Clear(); thumbs.Clear();
    }
}

/// <summary>Emberville > Props.</summary>
public class EmbervillePropsWindow : EditorWindow
{
    string search = "";
    int source;
    readonly Dictionary<string, bool> kinds = new Dictionary<string, bool>();
    Vector2 scroll;
    readonly List<PropEntry> picked = new List<PropEntry>();
    bool scatter;
    float radius = 2f, density = 0.6f;
    Vector3 lastScatter = new Vector3(float.NaN, 0, 0);
    readonly List<Vector3> stroke = new List<Vector3>();
    const int Cell = 72, Shown = 400;

    [MenuItem("Emberville/Props")]
    public static void Open() => GetWindow<EmbervillePropsWindow>("Props").minSize = new Vector2(360, 300);

    void OnEnable() => SceneView.duringSceneGui += OnScene;
    void OnDisable() { SceneView.duringSceneGui -= OnScene; EmbervilleProps.ClearThumbs(); }

    void OnGUI()
    {
        if (!EmbervilleGameArt.Ready)
        {
            EditorGUILayout.HelpBox("Pick the export's Assets folder in Emberville > Settings first.", MessageType.Warning);
            if (GUILayout.Button("Open Settings")) EmbervilleSettingsWindow.Open();
            return;
        }
        var all = EmbervilleProps.Catalog();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(all.Count == 0 ? "Harvest the game's props" : "Harvest again", GUILayout.Width(170))) { EmbervilleProps.Harvest(); all = EmbervilleProps.Catalog(); }
        GUILayout.Label(all.Count == 0 ? "nothing harvested yet" : $"{all.Count} props", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
        if (all.Count == 0) return;

        var sources = new[] { "All" }.Concat(all.Select(e => e.Source).Distinct().OrderBy(s => s == "Prefabs" ? "~" : s)).ToArray();
        EditorGUILayout.BeginHorizontal();
        source = EditorGUILayout.Popup(Mathf.Min(source, sources.Length - 1), sources, GUILayout.Width(170));
        search = EditorGUILayout.TextField(search);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        foreach (var k in new[] { "Decoration", "Furniture", "Breakable", "Door", "Light", "Other" })
        {
            if (!kinds.ContainsKey(k)) kinds[k] = true;
            kinds[k] = GUILayout.Toggle(kinds[k], k, EditorStyles.miniButton);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        scatter = GUILayout.Toggle(scatter, "Scatter brush", EditorStyles.miniButton, GUILayout.Width(100));
        using (new EditorGUI.DisabledScope(!scatter))
        {
            radius = EditorGUILayout.Slider(radius, 0.5f, 8f);
            density = EditorGUILayout.Slider(density, 0.1f, 4f);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField(picked.Count == 0 ? "Pick a prop (Ctrl+click: several, the brush mixes them), then click in the Scene view. Esc: put it down."
            : $"Placing: {string.Join(", ", picked.Select(p => p.Name))}" + (scatter ? $"  (brush: radius {radius:0.0}, {density:0.0} per unit²)" : ""), EditorStyles.wordWrappedMiniLabel);

        var list = all.Where(e => source == 0 || e.Source == sources[source])
            .Where(e => !kinds.TryGetValue(e.Kind, out var on) || on)
            .Where(e => search.Length == 0 || e.Name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(e => e.Name).Take(Shown).ToList();
        int cols = Mathf.Max(1, (int)((position.width - 20) / Cell));
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < list.Count; i += cols)
        {
            EditorGUILayout.BeginHorizontal();
            for (int j = i; j < Mathf.Min(i + cols, list.Count); j++) Tile(list[j]);
            EditorGUILayout.EndHorizontal();
        }
        if (list.Count == Shown) EditorGUILayout.HelpBox($"Showing the first {Shown}: search to narrow it down.", MessageType.None);
        EditorGUILayout.EndScrollView();
    }

    void Tile(PropEntry e)
    {
        var r = GUILayoutUtility.GetRect(Cell, Cell + 14, GUILayout.Width(Cell));
        bool on = picked.Contains(e);
        if (on) EditorGUI.DrawRect(r, new Color(0.25f, 0.5f, 1f, 0.35f));
        var pic = new Rect(r.x + 4, r.y + 2, Cell - 8, Cell - 8);
        var (tex, uv) = EmbervilleProps.Thumb(e);
        if (tex)
        {
            // keep the sprite's shape
            float aspect = uv.width * tex.width / Mathf.Max(1f, uv.height * tex.height);
            var fit = aspect >= 1 ? new Rect(pic.x, pic.y + (pic.height - pic.width / aspect) / 2, pic.width, pic.width / aspect)
                                  : new Rect(pic.x + (pic.width - pic.height * aspect) / 2, pic.y, pic.height * aspect, pic.height);
            GUI.DrawTextureWithTexCoords(fit, tex, uv);
        }
        else EditorGUI.DrawRect(pic, new Color(0, 0, 0, 0.15f));
        var badge = new Rect(r.x + 2, r.y + 2, 30, 12);
        EditorGUI.DrawRect(badge, e.Live ? new Color(0.2f, 0.7f, 0.3f, 0.9f) : new Color(0.4f, 0.4f, 0.4f, 0.8f));
        GUI.Label(badge, e.Live ? "live" : "look", EditorStyles.miniBoldLabel);
        GUI.Label(new Rect(r.x, r.y + Cell - 6, Cell, 20), e.Name, EditorStyles.centeredGreyMiniLabel);
        if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
        {
            if (!Event.current.control) picked.Clear();
            if (on && Event.current.control) picked.Remove(e); else if (!on) picked.Add(e);
            Event.current.Use();
            Repaint();
        }
        GUI.tooltip = $"{e.Name} ({e.Kind}, from {e.Source}){(e.Live ? ": live in the game" : ": look only")}";
    }

    void OnScene(SceneView view)
    {
        if (picked.Count == 0) return;
        var ev = Event.current;
        if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) { picked.Clear(); Repaint(); ev.Use(); return; }
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        var ray = HandleUtility.GUIPointToWorldRay(ev.mousePosition);
        var at = ray.origin + ray.direction * (Mathf.Abs(ray.direction.z) > 1e-4f ? -ray.origin.z / ray.direction.z : 0f);
        at.z = 0f;
        if (scatter)
        {
            Handles.color = new Color(0.4f, 0.8f, 1f, 0.8f);
            Handles.DrawWireDisc(at, Vector3.forward, radius);
            view.Repaint();
        }
        if (ev.alt || ev.button != 0) return;
        if (ev.type == EventType.MouseDown)
        {
            stroke.Clear();
            if (!scatter) { EmbervilleProps.Place(picked[Random.Range(0, picked.Count)], at); ev.Use(); return; }
            Scatter(at); lastScatter = at; ev.Use();
        }
        else if (ev.type == EventType.MouseDrag && scatter)
        {
            if (float.IsNaN(lastScatter.x) || (at - lastScatter).magnitude >= radius * 0.5f) { Scatter(at); lastScatter = at; }
            ev.Use();
        }
        else if (ev.type == EventType.MouseUp) { lastScatter = new Vector3(float.NaN, 0, 0); stroke.Clear(); }
    }

    /// <summary>The brush: about density x area props in the circle, at least 0.6 apart, a random mix of the picked ones,
    /// some flipped.</summary>
    void Scatter(Vector3 at)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(density * Mathf.PI * radius * radius * 0.25f));
        for (int i = 0, tries = 0; i < n && tries < n * 8; tries++)
        {
            var p = at + (Vector3)(Random.insideUnitCircle * radius);
            if (stroke.Any(s => (s - p).sqrMagnitude < 0.36f)) continue;
            stroke.Add(p);
            EmbervilleProps.Place(picked[Random.Range(0, picked.Count)], p, Random.value < 0.5f);
            i++;
        }
    }
}

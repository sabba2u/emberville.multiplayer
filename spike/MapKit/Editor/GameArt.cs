// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/GameArt.cs (with the Core folder next to it).
// The game's own art in the project: Emberville > Settings points the kit at your extraction of the game (an AssetRipper
// export's Assets folder); Emberville > Tile Sets imports the tiles you pick from it, with their sprites and textures, into
// Assets/GameArt, and puts each set on a palette. Only what you pick is copied, never the whole export.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMapKit.Core;
using UnityEditor;
using UnityEditor.Tilemaps;
using UnityEngine;
using UnityEngine.Tilemaps;

public static class EmbervilleGameArt
{
    const string RootKey = "Emberville.ExportAssets";
    public const string ArtFolder = "Assets/GameArt";
    const string IndexFile = "Library/EmbervilleExportIndex.tsv", IndexRootFile = "Library/EmbervilleExportIndex.root";
    const string TilesFile = "Library/EmbervilleExportTiles.tsv";

    /// <summary>The export's Assets folder (remembered on this computer).</summary>
    public static string ExportRoot
    {
        get => EditorPrefs.GetString(RootKey, "");
        set { EditorPrefs.SetString(RootKey, value ?? ""); index = null; tiles = null; }
    }

    /// <summary>An AssetRipper export's Assets folder (it has the folders the kit reads).</summary>
    public static bool LooksLikeExport(string path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path) &&
        new[] { "MonoBehaviour", "Sprite", "Texture2D" }.All(d => Directory.Exists(Path.Combine(path, d)));

    public static bool Ready => LooksLikeExport(ExportRoot);

    static ExportIndex index;

    /// <summary>The export's GUIDs -> files: built once (a minute or so: every .meta of the export), kept in Library/.</summary>
    public static ExportIndex Index()
    {
        if (index != null) return index;
        if (!Ready) return null;
        if (File.Exists(IndexFile) && CacheMatches()) return index = ExportIndex.Load(IndexFile);
        try
        {
            EditorUtility.DisplayProgressBar("Emberville", "Indexing the game export (once)...", 0.5f);
            var t = System.Diagnostics.Stopwatch.StartNew();
            index = ExportIndex.Build(ExportRoot);
            index.Save(IndexFile);
            File.WriteAllText(IndexRootFile, ExportIndex.Stamp(ExportRoot));
            if (File.Exists(TilesFile)) File.Delete(TilesFile);   // (made from the old index)
            tiles = null;
            Debug.Log($"Emberville: indexed the game export: {index.Count} assets in {t.Elapsed.TotalSeconds:0}s");
        }
        finally { EditorUtility.ClearProgressBar(); }
        return index;
    }

    /// <summary>The cached index was made from this very extraction (same folder, same files): a new build of the game
    /// extracted over it makes it stale.</summary>
    static bool CacheMatches() => File.Exists(IndexRootFile) && File.ReadAllText(IndexRootFile) == ExportIndex.Stamp(ExportRoot);

    /// <summary>The index and tile list made again from the export (after re-extracting the game).</summary>
    public static void Rebuild()
    {
        foreach (var f in new[] { IndexFile, IndexRootFile, TilesFile }) if (File.Exists(f)) File.Delete(f);
        index = null; tiles = null;
        Index();
        Tiles();
    }

    /// <summary>One of the export's tiles.</summary>
    public struct ExportTile { public string Guid, RelPath, Name, Set; }

    static List<ExportTile> tiles;

    /// <summary>Every tile in the export (MonoBehaviour/*.asset whose script is a tile), kept in Library/.</summary>
    public static List<ExportTile> Tiles()
    {
        if (tiles != null) return tiles;
        if (!Ready) return new List<ExportTile>();
        if (File.Exists(TilesFile) && File.Exists(IndexFile) && CacheMatches())
        {
            tiles = File.ReadAllLines(TilesFile).Select(l => l.Split('\t')).Where(p => p.Length == 4)
                .Select(p => new ExportTile { Guid = p[0], RelPath = p[1], Name = p[2], Set = p[3] }).ToList();
            return tiles;
        }
        Index();
        tiles = new List<ExportTile>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ExportRoot, "MonoBehaviour"), "*.asset"))
        {
            string head;
            using (var r = new StreamReader(file)) { var lines = new List<string>(); for (int i = 0; i < 20 && !r.EndOfStream; i++) lines.Add(r.ReadLine()); head = string.Join("\n", lines); }
            if (!TileAssets.IsTile(head)) continue;
            var meta = file + ".meta";
            var guid = File.Exists(meta) ? ExportIndex.GuidOfMeta(File.ReadAllText(meta)) : null;
            if (guid == null) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            tiles.Add(new ExportTile { Guid = guid, RelPath = "MonoBehaviour/" + Path.GetFileName(file), Name = name, Set = TileAssets.SetName(name) });
        }
        File.WriteAllLines(TilesFile, tiles.Select(t => $"{t.Guid}\t{t.RelPath}\t{t.Name}\t{t.Set}"));
        return tiles;
    }

    // ---- identities: every imported file's GUID, folder and name from what it is (ArtIdentity; the design's Part 3) ----

    static readonly Dictionary<string, ArtItem> identities = new Dictionary<string, ArtItem>();

    /// <summary>What an export file is in the project: its new GUID and place (worked out once, from the export).</summary>
    public static ArtItem Identity(string relPath)
    {
        if (identities.TryGetValue(relPath, out var known)) return known;
        var idx = Index();
        var item = idx == null ? null : ArtIdentity.Identify(new[] { relPath }, idx, ReadExport, PngSizeOf, null).FirstOrDefault();
        identities[relPath] = item;
        return item;
    }

    static string ReadExport(string rel) => File.ReadAllText(Path.Combine(ExportRoot, rel));

    static (int, int) PngSizeOf(string rel)
    {
        var head = new byte[24];
        using (var f = File.OpenRead(Path.Combine(ExportRoot, rel))) f.Read(head, 0, 24);
        return ArtIdentity.PngSize(head);
    }

    /// <summary>Where an export file lands in the project (its fixed place: Texture2D/, Sprite/&lt;texture&gt;/, Tiles/&lt;set&gt;/...).</summary>
    public static string ProjectPath(string relPath) => ArtFolder + "/" + (Identity(relPath)?.Dest ?? relPath);

    // ---- the manifest: which game build, which keys and GUIDs ----

    public const string ManifestFile = ArtFolder + "/manifest.json";

    /// <summary>The game's Steam build (from steamapps/appmanifest_5355880.acf next to the game), or "unknown".</summary>
    public static string GameBuild()
    {
        try
        {
            var steamapps = Directory.GetParent(Directory.GetParent(EmbervilleBuildBundles.GamePath).FullName).FullName;
            var acf = File.ReadAllText(Path.Combine(steamapps, "appmanifest_5355880.acf"));
            var m = System.Text.RegularExpressions.Regex.Match(acf, "\"buildid\"\\s+\"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>The manifest's entries (GUID -> key) and its game build.</summary>
    public static (string build, SortedDictionary<string, string> assets) ReadManifest()
    {
        var assets = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
        string build = "";
        if (!File.Exists(ManifestFile)) return (build, assets);
        foreach (var line in File.ReadAllLines(ManifestFile))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "^\\s*\"([^\"]+)\": \"(.*)\",?$");
            if (!m.Success) continue;
            var v = m.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            if (m.Groups[1].Value == "gameBuild") build = v;
            else if (m.Groups[1].Value != "kit") assets[m.Groups[1].Value] = v;
        }
        return (build, assets);
    }

    /// <summary>These identities added to the manifest (written in a fixed order, so everyone's is the same).</summary>
    public static void Record(IEnumerable<(string guid, string key)> added)
    {
        var (_, assets) = ReadManifest();
        foreach (var (g, k) in added) assets[g] = k;
        string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new System.Text.StringBuilder("{\n  \"kit\": \"3\",\n  \"gameBuild\": " + Q(GameBuild()) + ",\n  \"assets\": {\n");
        int i = 0;
        foreach (var p in assets) sb.Append("    " + Q(p.Key) + ": " + Q(p.Value) + (++i < assets.Count ? "," : "") + "\n");
        sb.Append("  }\n}\n");
        Directory.CreateDirectory(ArtFolder);
        File.WriteAllText(ManifestFile, sb.ToString());
    }

    /// <summary>A .meta of the export's, with its GUID replaced (its import settings kept).</summary>
    public static string MetaWithGuid(string metaText, string guid) =>
        System.Text.RegularExpressions.Regex.Replace(metaText, "(?m)^guid: [0-9a-f]+", "guid: " + guid);

    /// <summary>The asset with this GUID and all it needs, copied in (with their .meta files: same GUIDs, so their
    /// references hold). Files already there are left alone. The tiles' scripts are pointed at the project's Tilemap
    /// Extras. Returns the project paths copied.</summary>
    public static List<string> Import(string guid, bool refresh = true)
    {
        var idx = Index();
        var copied = new List<string>();
        if (idx == null) return copied;
        var closure = idx.Closure(guid, ReadExport);
        var items = closure.Select(Identity).Where(i => i != null).ToList();
        var newOf = items.ToDictionary(i => i.OldGuid, i => i.NewGuid);
        foreach (var it in items)
        {
            var dest = ArtFolder + "/" + it.Dest;
            if (File.Exists(dest)) continue;
            var src = Path.Combine(ExportRoot, it.RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (it.RelPath.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) File.Copy(src, dest);
            else
            {
                // the references to the files it needs: their new GUIDs; a tile's script: the project's Tilemap Extras
                var text = ArtIdentity.Rewrite(File.ReadAllText(src), newOf);
                if (TileAssets.IsTile(text)) text = TileAssets.TranslateScripts(text);
                File.WriteAllText(dest, text);
            }
            File.WriteAllText(dest + ".meta", File.Exists(src + ".meta") ? MetaWithGuid(File.ReadAllText(src + ".meta"), it.NewGuid) : "fileFormatVersion: 2\nguid: " + it.NewGuid + "\n");
            copied.Add(dest);
        }
        if (copied.Count > 0) Record(items.Select(i => (i.NewGuid, i.Key)));
        if (refresh && copied.Count > 0) AssetDatabase.Refresh();
        return copied;
    }

    /// <summary>A set's tiles imported and put on its palette (Assets/GameArt/Palettes/&lt;set&gt;.prefab). Returns the
    /// number of files copied.</summary>
    public static int ImportSet(string set)
    {
        var mine = Tiles().Where(t => t.Set == set).OrderBy(t => t.Name).ToList();
        int n = 0;
        try
        {
            for (int i = 0; i < mine.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Emberville", $"Importing {set}: {mine[i].Name}", (i + 0.5f) / mine.Count);
                n += Import(mine[i].Guid, refresh: false).Count;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Emberville: couldn't import \"{set}\": {e.Message}. If the game was extracted again, use Emberville > Settings > Rebuild index.");
        }
        finally { EditorUtility.ClearProgressBar(); }
        AssetDatabase.Refresh();
        Palette(set, mine.Select(t => AssetDatabase.LoadAssetAtPath<TileBase>(ProjectPath(t.RelPath))).Where(t => t).ToList());
        Debug.Log($"Emberville: imported the tile set \"{set}\" ({mine.Count} tiles, {n} files copied)");
        return n;
    }

    static void Palette(string set, List<TileBase> setTiles)
    {
        if (setTiles.Count == 0) return;
        string folder = ArtFolder + "/Palettes", path = folder + "/" + set + ".prefab";
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var made = GridPaletteUtility.CreateNewPalette(folder, set, GridLayout.CellLayout.Rectangle,
                GridPalette.CellSizing.Automatic, Vector3.one, GridLayout.CellSwizzle.XYZ);
            path = AssetDatabase.GetAssetPath(made);
            var contents = PrefabUtility.LoadPrefabContents(path);
            var map = contents.GetComponentInChildren<Tilemap>();
            for (int i = 0; i < setTiles.Count; i++) map.SetTile(new Vector3Int(i, 0, 0), setTiles[i]);
            PrefabUtility.SaveAsPrefabAsset(contents, path);
            PrefabUtility.UnloadPrefabContents(contents);
        }
        catch (System.Exception e) { Debug.LogWarning($"Emberville: couldn't make the palette for {set} ({e.Message}); its tiles are in {ArtFolder}/MonoBehaviour"); }
    }

    /// <summary>A set taken back out of the project: its tiles, its palette, and the sprites and textures no other imported
    /// set still uses. Asks first when a scene still has its tiles painted (they'd vanish from that room). Returns the
    /// number of files removed.</summary>
    public static int UnloadSet(string set)
    {
        var idx = Index();
        if (idx == null) return 0;
        var all = Tiles();
        System.Func<string, string> read = p => File.Exists(Path.Combine(ExportRoot, p)) ? File.ReadAllText(Path.Combine(ExportRoot, p)) : "";
        var mine = all.Where(t => t.Set == set).ToList();
        var others = all.Where(t => t.Set != set && File.Exists(ProjectPath(t.RelPath)));
        var files = ExportIndex.OnlyFor(mine.SelectMany(t => idx.Closure(t.Guid, read)), others.SelectMany(t => idx.Closure(t.Guid, read)))
            .Select(ProjectPath).Where(File.Exists).ToList();
        var palette = ArtFolder + "/Palettes/" + set + ".prefab";
        if (File.Exists(palette)) files.Add(palette);
        if (files.Count == 0) return 0;

        var guids = mine.Select(t => t.Guid).ToList();
        var rooms = Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories)
            .Where(f => { var text = File.ReadAllText(f); return guids.Any(g => text.Contains(g)); })
            .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        if (rooms.Count > 0 && !EditorUtility.DisplayDialog("Emberville",
                $"\"{set}\" is painted in {string.Join(", ", rooms)}. Unloading it removes those tiles from the room(s) (you can import it again later, but they'd have to be painted again).",
                "Unload anyway", "Keep it"))
            return 0;

        var failed = new List<string>();
        AssetDatabase.DeleteAssets(files.ToArray(), failed);
        Debug.Log($"Emberville: unloaded the tile set \"{set}\" ({files.Count - failed.Count} files removed{(failed.Count > 0 ? ", couldn't remove: " + string.Join(", ", failed) : "")})");
        return files.Count - failed.Count;
    }

    /// <summary>Any of a set's tiles are in the project.</summary>
    public static bool AnyImported(string set) => Tiles().Where(t => t.Set == set).Any(t => File.Exists(ProjectPath(t.RelPath)));

    /// <summary>All of a set's tiles are in the project already.</summary>
    public static bool Imported(string set) => Tiles().Where(t => t.Set == set).All(t => File.Exists(ProjectPath(t.RelPath)));
}

/// <summary>Emberville > Settings: where your extraction of the game is.</summary>
public class EmbervilleSettingsWindow : EditorWindow
{
    [MenuItem("Emberville/Settings")]
    public static void Open() => GetWindow<EmbervilleSettingsWindow>(true, "Emberville Settings").minSize = new Vector2(520, 130);

    void OnGUI()
    {
        EditorGUILayout.LabelField("The game's export (AssetRipper): its Assets folder", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var root = EditorGUILayout.TextField(EmbervilleGameArt.ExportRoot);
        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
        {
            var picked = EditorUtility.OpenFolderPanel("The export's Assets folder", EmbervilleGameArt.ExportRoot, "");
            if (!string.IsNullOrEmpty(picked)) root = picked;
        }
        EditorGUILayout.EndHorizontal();
        if (root != EmbervilleGameArt.ExportRoot) EmbervilleGameArt.ExportRoot = root;
        if (EmbervilleGameArt.Ready) EditorGUILayout.HelpBox("Found the game's tiles, sprites and textures here.", MessageType.Info);
        else EditorGUILayout.HelpBox("Not an AssetRipper export's Assets folder (it should have MonoBehaviour, Sprite and Texture2D folders in it).", MessageType.Error);
        if (EmbervilleGameArt.Ready && GUILayout.Button("Open Tile Sets")) EmbervilleTileSetsWindow.Open();
        if (EmbervilleGameArt.Ready && GUILayout.Button("Rebuild index (after extracting the game again)")) EmbervilleGameArt.Rebuild();
    }
}

/// <summary>Emberville > Tile Sets: the game's tiles by set; import the ones you want to paint with.</summary>
public class EmbervilleTileSetsWindow : EditorWindow
{
    string search = "";
    Vector2 scroll;

    [MenuItem("Emberville/Tile Sets")]
    public static void Open() => GetWindow<EmbervilleTileSetsWindow>("Tile Sets").minSize = new Vector2(420, 300);

    void OnGUI()
    {
        if (!EmbervilleGameArt.Ready)
        {
            EditorGUILayout.HelpBox("Pick the export's Assets folder in Emberville > Settings first.", MessageType.Warning);
            if (GUILayout.Button("Open Settings")) EmbervilleSettingsWindow.Open();
            return;
        }
        search = EditorGUILayout.TextField("Search", search);
        var sets = EmbervilleGameArt.Tiles().GroupBy(t => t.Set)
            .Where(g => search.Length == 0 || g.Key.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(g => g.Key).ToList();
        EditorGUILayout.LabelField($"{sets.Count} sets", EditorStyles.miniLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var g in sets)
        {
            EditorGUILayout.BeginHorizontal();
            bool done = EmbervilleGameArt.Imported(g.Key);
            var first = done ? AssetDatabase.LoadAssetAtPath<TileBase>(EmbervilleGameArt.ProjectPath(g.OrderBy(t => t.Name).First().RelPath)) : null;
            var pic = first ? AssetPreview.GetAssetPreview(first) : null;
            GUILayout.Label(pic ? pic : Texture2D.grayTexture, GUILayout.Width(32), GUILayout.Height(32));
            EditorGUILayout.LabelField($"{g.Key}  ({g.Count()} tile{(g.Count() == 1 ? "" : "s")})", done ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Height(32));
            if (GUILayout.Button(done ? "Re-import" : "Import", GUILayout.Width(80), GUILayout.Height(24))) EmbervilleGameArt.ImportSet(g.Key);
            using (new EditorGUI.DisabledScope(!EmbervilleGameArt.AnyImported(g.Key)))
                if (GUILayout.Button("Unload", GUILayout.Width(64), GUILayout.Height(24))) EmbervilleGameArt.UnloadSet(g.Key);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }
}

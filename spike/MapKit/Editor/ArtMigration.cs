// Emberville Map Kit: copy this file into your Unity project at Assets/Editor/ArtMigration.cs.
// Emberville > Re-file Game Art: art imported before the kit gave everything its own identity (AssetRipper's names,
// folders and GUIDs) moves to its fixed place with its fixed GUID, and every scene, prefab and asset in the project is
// pointed at the new GUIDs. A backup of Assets/GameArt and Assets/Emberville is made first, next to Assets/.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMapKit.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class EmbervilleArtMigration
{
    static readonly string[] TextAssets = { ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", ".overrideController" };

    [MenuItem("Emberville/Re-file Game Art")]
    public static void Run()
    {
        if (!EmbervilleGameArt.Ready) { EditorUtility.DisplayDialog("Emberville", "Pick the export's Assets folder in Emberville > Settings first.", "OK"); return; }
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
            {
                EditorUtility.DisplayDialog("Emberville", "Save the open scene first (File > Save), then run Re-file Game Art again.", "OK");
                return;
            }
        string art = EmbervilleGameArt.ArtFolder;
        if (!Directory.Exists(art)) { Debug.Log("Emberville: no game art imported yet: nothing to re-file"); return; }
        var idx = EmbervilleGameArt.Index();

        // what each file is: its old GUID (its .meta), the export file it came from, its new GUID and place
        var map = new Dictionary<string, string>();                    // old GUID -> new GUID
        var moves = new List<(string from, string to, string guid, string key)>();
        int unknown = 0;
        foreach (var file in Directory.GetFiles(art, "*", SearchOption.AllDirectories).Select(f => f.Replace('\\', '/')))
        {
            if (file.EndsWith(".meta") || file.EndsWith("/manifest.json") || file.Contains("/Palettes/") || !File.Exists(file + ".meta")) continue;
            var old = ExportIndex.GuidOfMeta(File.ReadAllText(file + ".meta"));
            if (old == null) continue;
            string to, guid, key;
            if (file.StartsWith(EmbervilleProps.PropsFolder + "/"))
            {
                // a harvested prop: Props/<source>/<name>.prefab
                var source = Path.GetFileName(Path.GetDirectoryName(file));
                key = "prop|" + source + "|" + Path.GetFileNameWithoutExtension(file);
                guid = ArtIdentity.Guid(key);
                to = file;
            }
            else
            {
                var rel = idx.PathOf(old);
                var it = rel == null ? null : EmbervilleGameArt.Identity(rel);
                if (it == null) { unknown++; Debug.LogWarning($"Emberville: {file} isn't from the export (its GUID {old} isn't in it): left as it is"); continue; }
                to = art + "/" + it.Dest; guid = it.NewGuid; key = it.Key;
            }
            if (old != guid) map[old] = guid;
            moves.Add((file, to, guid, key));
        }
        if (!EditorUtility.DisplayDialog("Emberville", $"Re-file {moves.Count} game art files ({map.Count} get a new GUID) and point every scene and prefab at them? " +
                                                       "A backup of Assets/GameArt and Assets/Emberville is made first.", "Re-file", "Cancel")) return;

        var backup = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GameArtBackup-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Copy(art, Path.Combine(backup, "GameArt"));
        if (Directory.Exists("Assets/Emberville")) Copy("Assets/Emberville", Path.Combine(backup, "Emberville"));

        AssetDatabase.StartAssetEditing();
        int moved = 0, rewritten = 0;
        try
        {
            // the files to their places, their .meta files with their new GUIDs
            foreach (var (from, to, guid, _) in moves)
            {
                var meta = EmbervilleGameArt.MetaWithGuid(File.ReadAllText(from + ".meta"), guid);
                if (from != to)
                {
                    if (File.Exists(to)) { Debug.LogWarning($"Emberville: {to} is there already: kept, {from} dropped"); File.Delete(from); File.Delete(from + ".meta"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Move(from, to);
                    File.Delete(from + ".meta");
                    moved++;
                }
                File.WriteAllText(to + ".meta", meta);
            }
            // every reference in the project pointed at the new GUIDs
            foreach (var f in Directory.GetFiles("Assets", "*", SearchOption.AllDirectories).Where(f => TextAssets.Contains(Path.GetExtension(f))))
            {
                var text = File.ReadAllText(f);
                var now = ArtIdentity.Rewrite(text, map);
                if (now != text) { File.WriteAllText(f, now); rewritten++; }
            }
            // the old folders, now empty
            foreach (var d in Directory.GetDirectories(art, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) { Directory.Delete(d); if (File.Exists(d + ".meta")) File.Delete(d + ".meta"); }
            EmbervilleGameArt.Record(moves.Select(m => (m.guid, m.key)));
        }
        finally { AssetDatabase.StopAssetEditing(); }
        AssetDatabase.Refresh();
        Debug.Log($"Emberville: re-filed the game art: {moves.Count} files ({moved} moved, {map.Count} new GUIDs), {rewritten} scenes/prefabs/assets pointed at them, {unknown} left as they were. Backup: {backup}");
    }

    static void Copy(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }
}

// Emberville Map Kit: the game's export (an AssetRipper "Assets" folder) by GUID. Plain C#, no Unity: tested on its own
// (spike/MapKit.Tests) and used by the editor windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbervilleMapKit.Core
{
    /// <summary>
    /// Every asset of the export by its GUID (from the .meta files next to them), and what an asset needs: the files it
    /// refers to by GUID, and theirs, and so on. Paths are relative to the export's Assets folder, with "/".
    /// </summary>
    public class ExportIndex
    {
        static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-z]+)");
        static readonly Regex MetaGuid = new Regex(@"^guid: ([0-9a-f]+)", RegexOptions.Multiline);
        static readonly HashSet<string> BuiltIn = new HashSet<string> { "0000000000000000e000000000000000", "0000000000000000f000000000000000" };
        static readonly string[] Skipped = { "Scripts/", "Shader/" };   // (stubs and broken shaders: never brought into a project)
        static readonly string[] Followed = { ".asset", ".prefab", ".mat", ".controller", ".anim", ".overrideController" };

        readonly Dictionary<string, string> paths;

        public ExportIndex(IDictionary<string, string> guidToRelPath) => paths = new Dictionary<string, string>(guidToRelPath);

        public int Count => paths.Count;

        Dictionary<string, string> guids;

        /// <summary>The GUID of the asset at this path, or null.</summary>
        public string GuidOf(string relPath)
        {
            if (guids == null) { guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var p in paths) guids[p.Value] = p.Key; }
            return relPath != null && guids.TryGetValue(relPath, out var g) ? g : null;
        }

        /// <summary>The asset with this GUID (not its .meta), or null.</summary>
        public string PathOf(string guid) => guid != null && paths.TryGetValue(guid, out var p) ? p : null;

        /// <summary>The GUID a .meta file gives its asset, or null.</summary>
        public static string GuidOfMeta(string metaText)
        {
            var m = MetaGuid.Match(metaText ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Every GUID the text refers to, each once, in the order first seen (Unity's built-ins left out).</summary>
        public static IReadOnlyList<string> ReferencedGuids(string yamlText)
        {
            var seen = new HashSet<string>();
            var list = new List<string>();
            foreach (Match m in GuidRef.Matches(yamlText ?? ""))
            {
                var g = m.Groups[1].Value;
                if (!BuiltIn.Contains(g) && seen.Add(g)) list.Add(g);
            }
            return list;
        }

        /// <summary>The index of an export's Assets folder: every .meta under it.</summary>
        public static ExportIndex Build(string assetsRoot)
        {
            var map = new Dictionary<string, string>();
            string root = Path.GetFullPath(assetsRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var meta in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
            {
                string guid;
                using (var r = new StreamReader(meta))
                {
                    // (the guid is on the second line of a .meta; read a few in case)
                    var head = "";
                    for (int i = 0; i < 4 && !r.EndOfStream; i++) head += r.ReadLine() + "\n";
                    guid = GuidOfMeta(head);
                }
                if (guid == null) continue;
                var asset = Path.GetFullPath(meta.Substring(0, meta.Length - ".meta".Length));
                map[guid] = asset.Substring(root.Length).Replace('\\', '/');
            }
            return new ExportIndex(map);
        }

        /// <summary>What an export looks like right now (its tiles folder's file count and last change): a cached index
        /// made from another extraction of the game (a new build re-extracted to the same folder) no longer matches.</summary>
        public static string Stamp(string assetsRoot)
        {
            var dir = Path.Combine(assetsRoot, "MonoBehaviour");
            if (!Directory.Exists(dir)) return Path.GetFullPath(assetsRoot);
            var files = Directory.GetFiles(dir);
            long newest = files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
            return Path.GetFullPath(assetsRoot) + "|" + files.Length + "|" + newest;
        }

        /// <summary>The files to delete when the first group goes: those of it no file to keep still needs.</summary>
        public static List<string> OnlyFor(IEnumerable<string> remove, IEnumerable<string> keep)
        {
            var kept = new HashSet<string>(keep);
            return remove.Distinct().Where(p => !kept.Contains(p)).ToList();
        }

        /// <summary>One "guid TAB path" a line.</summary>
        public void Save(string file) => File.WriteAllLines(file, paths.Select(p => p.Key + "\t" + p.Value));

        public static ExportIndex Load(string file)
        {
            var map = new Dictionary<string, string>();
            foreach (var line in File.ReadLines(file))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0) map[line.Substring(0, tab)] = line.Substring(tab + 1);
            }
            return new ExportIndex(map);
        }

        /// <summary>
        /// The asset with this GUID and everything it needs, the asset first: the files it refers to, theirs, and so on
        /// (only out of text assets: a texture's references aren't followed). Scripts and shaders, and GUIDs the export
        /// hasn't got (Unity's packages), are left out.
        /// </summary>
        public IReadOnlyList<string> Closure(string startGuid, Func<string, string> readText)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(startGuid);
            seen.Add(startGuid);
            while (queue.Count > 0)
            {
                var path = PathOf(queue.Dequeue());
                if (path == null || Skipped.Any(s => path.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(path);
                if (!Followed.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var g in ReferencedGuids(readText(path)))
                    if (seen.Add(g)) queue.Enqueue(g);
            }
            return result;
        }
    }
}

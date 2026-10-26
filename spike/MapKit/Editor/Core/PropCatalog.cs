// Emberville Map Kit: the list of the game's props the Props window offers. Plain C#, no Unity (tested in
// spike/MapKit.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EmbervilleMapKit.Core
{
    /// <summary>One prop on offer: where it's from (a scene, or "Prefabs"), what it is, how to find it in its file again,
    /// what it looks like (its first sprite), and whether the game can spawn the real one (Live) or must never (it saves
    /// to the player's world: chests, buildables).</summary>
    public class PropEntry
    {
        public string Name, BaseName, Source, Kind, ArtKey, FirstSprite;
        public string File = "";   // (the export file it's in, relative to the export's Assets folder; set by the harvest)
        public long RootId;
        public bool Live, SaveTouching;
    }

    public static class PropCatalog
    {
        static readonly string[] Saving = { "Chest", "PlacementObjectComponent", "SavedObject" };

        /// <summary>Things that save to the player's world: never swapped for the game's live one.</summary>
        public static bool IsSaveTouching(string baseName, IEnumerable<string> rootClasses) =>
            (baseName ?? "").StartsWith("Place_", StringComparison.Ordinal) || rootClasses.Any(c => Saving.Contains(c));

        /// <summary>A file's props, one entry per look: copies with the same base name and the same sprites are one entry;
        /// the same name with other sprites becomes "Name#2", "Name#3".</summary>
        public static List<PropEntry> FromFile(string source, string yamlText, Func<string, string> classOfScript, ISet<string> livePrefabNames)
        {
            var y = UnityYaml.Parse(yamlText);
            var entries = new List<PropEntry>();
            var looks = new Dictionary<string, Dictionary<string, PropEntry>>();   // base name -> art key -> entry
            foreach (var root in Props.Find(y, classOfScript))
            {
                var baseName = UnityYaml.BaseName(y.NameOf(root));
                var art = Props.ArtKey(y, root);
                if (!looks.TryGetValue(baseName, out var byArt)) looks[baseName] = byArt = new Dictionary<string, PropEntry>();
                if (byArt.ContainsKey(art)) continue;
                var classes = y.ComponentsOf(root).Select(y.ScriptGuidOf).Where(g => g != null).Select(classOfScript).Where(c => c != null).ToList();
                var e = new PropEntry
                {
                    Name = byArt.Count == 0 ? baseName : baseName + "#" + (byArt.Count + 1).ToString(CultureInfo.InvariantCulture),
                    BaseName = baseName, Source = source, Kind = Props.Kind(y, root, classOfScript), ArtKey = art,
                    FirstSprite = Props.Sprites(y, root).FirstOrDefault() ?? "", RootId = root,
                    SaveTouching = IsSaveTouching(baseName, classes),
                };
                e.Live = !e.SaveTouching && livePrefabNames.Contains(baseName);
                byArt[art] = e;
                entries.Add(e);
            }
            return entries;
        }

        public static void Save(List<PropEntry> entries, string file) =>
            System.IO.File.WriteAllLines(file, entries.Select(e => string.Join("\t", e.Name, e.BaseName, e.Source, e.Kind, e.ArtKey, e.FirstSprite,
                e.RootId.ToString(CultureInfo.InvariantCulture), e.Live ? "1" : "0", e.SaveTouching ? "1" : "0", e.File ?? "")));

        public static List<PropEntry> Load(string file) =>
            System.IO.File.ReadAllLines(file).Select(l => l.Split('\t')).Where(p => p.Length >= 9).Select(p => new PropEntry
            {
                Name = p[0], BaseName = p[1], Source = p[2], Kind = p[3], ArtKey = p[4], FirstSprite = p[5],
                RootId = long.Parse(p[6], CultureInfo.InvariantCulture), Live = p[7] == "1", SaveTouching = p[8] == "1", File = p.Length > 9 ? p[9] : "",
            }).ToList();
    }
}

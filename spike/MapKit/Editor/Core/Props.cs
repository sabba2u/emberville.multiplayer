// Emberville Map Kit: the game's props in its scenes and prefabs, and one cut out as a prefab of its own. Plain C#, no
// Unity (tested in spike/MapKit.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EmbervilleMapKit.Core
{
    /// <summary>
    /// A prop, as the game builds them (notes/emberville-tile-layers.md): an object with an "Art" child and a "Colliders"
    /// or "HeightMap" child, or a door (GateDoor), with no prop above it. Its kind comes from the game's components on it.
    /// A cut-out prop is its own objects only, at the origin, without the game's scripts (our project hasn't got them; the
    /// Light2D is kept, pointed at the project's URP), its sprites drawn with URP's Sprite-Lit-Default.
    /// </summary>
    public static class Props
    {
        public const string Light2DExport = "d856b19b20cd26eeff9dcf8732cdefdd", Light2DPackage = "073797afb82c5a1438f328866b10b3f0";
        public const string SpriteLitDefault = "{fileID: 2100000, guid: a97c105638bdf8b4a8650670310a4cd3, type: 2}";

        static readonly Regex SpriteRef = new Regex(@"\n  m_Sprite: \{fileID: (-?\d+), guid: ([0-9a-f]+)");
        static readonly Regex Materials = new Regex(@"(\n  m_Materials:\n)((?:  - \{[^}\n]*\}\n)+)");
        static readonly Regex LocalRef = new Regex(@"\{fileID: (-?\d+)\}");
        static readonly Regex Father = new Regex(@"\n  m_Father: \{fileID: -?\d+\}");
        static readonly Regex LocalPosition = new Regex(@"\n  m_LocalPosition: \{[^}\n]*\}");

        static IEnumerable<string> Classes(UnityYaml y, long go, Func<string, string> classOfScript) =>
            y.ComponentsOf(go).Select(y.ScriptGuidOf).Where(g => g != null).Select(classOfScript).Where(c => c != null);

        static bool LooksLikeProp(UnityYaml y, long go, Func<string, string> classOfScript)
        {
            var kids = y.ChildrenOf(go).Select(y.NameOf).ToList();
            return kids.Contains("Art") && (kids.Contains("Colliders") || kids.Contains("HeightMap")) || Classes(y, go, classOfScript).Contains("GateDoor");
        }

        /// <summary>Every prop's root (props inside a prop are part of it).</summary>
        public static IReadOnlyList<long> Find(UnityYaml y, Func<string, string> classOfScript)
        {
            var props = new HashSet<long>(y.GameObjects.Where(g => LooksLikeProp(y, g, classOfScript)));
            return props.Where(g =>
            {
                for (long p = y.ParentOf(g); p != 0; p = y.ParentOf(p)) if (props.Contains(p)) return false;
                return true;
            }).ToList();
        }

        /// <summary>Door, Breakable, Furniture, Light, Decoration or Other.</summary>
        public static string Kind(UnityYaml y, long root, Func<string, string> classOfScript)
        {
            var on = new HashSet<string>(Classes(y, root, classOfScript));
            if (on.Contains("GateDoor")) return "Door";
            if (on.Contains("DamageableObject")) return on.Contains("ItemDropper") ? "Breakable" : "Furniture";
            var sub = y.Subtree(root);
            bool sprites = sub.SelectMany(y.ComponentsOf).Any(c => y.Doc(c)?.ClassId == 212);
            if (!sprites && sub.SelectMany(g => Classes(y, g, classOfScript)).Contains("Light2D")) return "Light";
            return on.All(c => c == "SimpleEvents") ? "Decoration" : "Other";
        }

        /// <summary>The sprites it draws ("fileID:guid", sorted, ";"): two copies with the same key look the same.</summary>
        public static string ArtKey(UnityYaml y, long root) => string.Join(";", Sprites(y, root).Distinct().OrderBy(s => s, StringComparer.Ordinal));

        /// <summary>The sprites it draws, in order ("fileID:guid").</summary>
        public static IEnumerable<string> Sprites(UnityYaml y, long root)
        {
            foreach (var go in y.Subtree(root))
                foreach (var c in y.ComponentsOf(go))
                {
                    var d = y.Doc(c);
                    if (d == null || d.ClassId != 212) continue;
                    var m = SpriteRef.Match(d.Text);
                    if (m.Success && m.Groups[2].Value.Length > 0) yield return m.Groups[1].Value + ":" + m.Groups[2].Value;
                }
        }

        /// <summary>The prop as a prefab file of its own (see the class summary).</summary>
        public static string Cut(UnityYaml y, long root, Func<string, string> classOfScript)
        {
            var gos = y.Subtree(root);
            var keep = new HashSet<long>();
            var dropped = new HashSet<long>();
            foreach (var go in gos)
            {
                keep.Add(go);
                foreach (var c in y.ComponentsOf(go))
                {
                    var guid = y.ScriptGuidOf(c);
                    if (y.Doc(c)?.ClassId == 114 && guid != Light2DExport) dropped.Add(c);
                    else keep.Add(c);
                }
            }
            long rootTransform = y.TransformOf(root);
            var sb = new StringBuilder(y.Head);
            foreach (var d in y.Docs)
            {
                if (!keep.Contains(d.FileId)) continue;
                var t = d.Text;
                if (d.ClassId == 1)
                    foreach (var c in dropped) t = t.Replace("  - component: {fileID: " + c.ToString(CultureInfo.InvariantCulture) + "}\n", "");
                if (d.FileId == rootTransform)
                {
                    t = Father.Replace(t, "\n  m_Father: {fileID: 0}");
                    t = LocalPosition.Replace(t, "\n  m_LocalPosition: {x: 0, y: 0, z: 0}");
                }
                if (d.ClassId == 114) t = t.Replace("guid: " + Light2DExport, "guid: " + Light2DPackage);
                if (d.ClassId == 212)
                    t = Materials.Replace(t, m => m.Groups[1].Value + Regex.Replace(m.Groups[2].Value, @"  - \{[^}\n]*\}\n", "  - " + SpriteLitDefault + "\n"));
                // (anything pointing outside it: its parent group, the scene's objects)
                t = LocalRef.Replace(t, m =>
                {
                    long id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    return id == 0 || keep.Contains(id) ? m.Value : "{fileID: 0}";
                });
                // (the header line keeps its own id)
                sb.Append("--- !u!").Append(t);
            }
            return sb.ToString();
        }
    }
}

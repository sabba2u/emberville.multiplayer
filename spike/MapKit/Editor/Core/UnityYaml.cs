// Emberville Map Kit: Unity's text files (scenes, prefabs) read as documents. Plain C#, no Unity (tested in
// spike/MapKit.Tests).
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EmbervilleMapKit.Core
{
    /// <summary>One object of a Unity text file: its class (1 GameObject, 4 Transform, 114 MonoBehaviour, 212
    /// SpriteRenderer...), its id in the file, and its text (the header line "1 &amp;100" first).</summary>
    public class YamlDoc
    {
        public int ClassId;
        public long FileId;
        public string Text;
    }

    /// <summary>A scene or prefab file: its objects, and how they hang together (which GameObject a component is on, a
    /// GameObject's parent and children through their Transforms, a MonoBehaviour's script).</summary>
    public class UnityYaml
    {
        static readonly Regex Header = new Regex(@"^(\d+) &(-?\d+)");
        static readonly Regex OwnerRef = new Regex(@"\n  m_GameObject: \{fileID: (-?\d+)\}");
        static readonly Regex FatherRef = new Regex(@"\n  m_Father: \{fileID: (-?\d+)\}");
        static readonly Regex ChildrenList = new Regex(@"\n  m_Children:\n((?:  - \{fileID: -?\d+\}\n)*)");
        static readonly Regex ListRef = new Regex(@"\{fileID: (-?\d+)\}");
        static readonly Regex ComponentList = new Regex(@"\n  m_Component:\n((?:  - component: \{fileID: -?\d+\}\n)*)");
        static readonly Regex NameField = new Regex(@"\n  m_Name: ([^\n]*)");
        static readonly Regex ScriptRef = new Regex(@"\n  m_Script: \{fileID: -?\d+, guid: ([0-9a-f]+)");
        static readonly Regex CopyNumber = new Regex(@"(\s*\(\d+\)|_\d+| Variant)$");

        public string Head = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
        readonly List<YamlDoc> docs = new List<YamlDoc>();
        readonly Dictionary<long, YamlDoc> byId = new Dictionary<long, YamlDoc>();
        readonly Dictionary<long, long> transformOf = new Dictionary<long, long>();   // GameObject -> its Transform
        readonly Dictionary<long, long> ownerOf = new Dictionary<long, long>();       // component -> GameObject

        public IReadOnlyList<YamlDoc> Docs => docs;

        public static UnityYaml Parse(string text)
        {
            var y = new UnityYaml();
            text = text.Replace("\r\n", "\n");
            var parts = text.Split(new[] { "\n--- !u!" }, System.StringSplitOptions.None);
            if (parts.Length > 0 && !parts[0].StartsWith("--- !u!")) y.Head = parts[0] + "\n";
            for (int i = 0; i < parts.Length; i++)
            {
                var p = i == 0 && parts[0].StartsWith("--- !u!") ? parts[0].Substring(7) : parts[i];
                if (i == 0 && !parts[0].StartsWith("--- !u!")) continue;
                var m = Header.Match(p);
                if (!m.Success) continue;
                var d = new YamlDoc { ClassId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), FileId = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), Text = p.EndsWith("\n") ? p : p + "\n" };
                y.docs.Add(d);
                y.byId[d.FileId] = d;
                if (d.ClassId == 1) continue;
                var o = OwnerRef.Match(d.Text);
                if (!o.Success) continue;
                long go = long.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture);
                y.ownerOf[d.FileId] = go;
                if (d.ClassId == 4 || d.ClassId == 224) y.transformOf[go] = d.FileId;
            }
            return y;
        }

        public YamlDoc Doc(long fileId) => byId.TryGetValue(fileId, out var d) ? d : null;

        public IEnumerable<long> GameObjects
        {
            get { foreach (var d in docs) if (d.ClassId == 1) yield return d.FileId; }
        }

        public string NameOf(long goId)
        {
            var d = Doc(goId);
            var m = d == null ? null : NameField.Match(d.Text);
            return m != null && m.Success ? m.Groups[1].Value.Trim() : "";
        }

        public long GameObjectOf(long componentId) => ownerOf.TryGetValue(componentId, out var g) ? g : 0;

        public long TransformOf(long goId) => transformOf.TryGetValue(goId, out var t) ? t : 0;

        public IReadOnlyList<long> ComponentsOf(long goId)
        {
            var list = new List<long>();
            var d = Doc(goId);
            var m = d == null ? null : ComponentList.Match(d.Text);
            if (m == null || !m.Success) return list;
            foreach (Match r in ListRef.Matches(m.Groups[1].Value)) list.Add(long.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture));
            return list;
        }

        /// <summary>The child GameObjects, in the Transform's order.</summary>
        public IReadOnlyList<long> ChildrenOf(long goId)
        {
            var list = new List<long>();
            var t = Doc(TransformOf(goId));
            var m = t == null ? null : ChildrenList.Match(t.Text);
            if (m == null || !m.Success) return list;
            foreach (Match r in ListRef.Matches(m.Groups[1].Value))
            {
                long go = GameObjectOf(long.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture));
                if (go != 0) list.Add(go);
            }
            return list;
        }

        /// <summary>The parent GameObject, or 0 at the top.</summary>
        public long ParentOf(long goId)
        {
            var t = Doc(TransformOf(goId));
            var m = t == null ? null : FatherRef.Match(t.Text);
            return m != null && m.Success ? GameObjectOf(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : 0;
        }

        /// <summary>A MonoBehaviour's script GUID, or null.</summary>
        public string ScriptGuidOf(long componentId)
        {
            var d = Doc(componentId);
            if (d == null || d.ClassId != 114) return null;
            var m = ScriptRef.Match(d.Text);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>A GameObject and everything under it (itself first).</summary>
        public List<long> Subtree(long goId)
        {
            var list = new List<long> { goId };
            for (int i = 0; i < list.Count; i++) list.AddRange(ChildrenOf(list[i]));
            return list;
        }

        /// <summary>A name without its copy number: "GrassDark2_13" -> "GrassDark2", "Light 2D (3)" -> "Light 2D",
        /// "Papers2 Variant_02" -> "Papers2".</summary>
        public static string BaseName(string name)
        {
            string s = (name ?? "").Trim(), before;
            do { before = s; s = CopyNumber.Replace(s, "").Trim(); } while (s != before && s.Length > 0);
            return s.Length > 0 ? s : (name ?? "").Trim();
        }
    }
}

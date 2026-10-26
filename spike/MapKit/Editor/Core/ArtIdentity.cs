// Emberville Map Kit: the identity the kit gives the game's art as it imports it. Plain C#, no Unity (tested in
// spike/MapKit.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EmbervilleMapKit.Core
{
    /// <summary>One export file on its way into the project: where it was, what it is (its key), its new GUID and place.</summary>
    public class ArtItem
    {
        public string OldGuid, RelPath, Key, NewGuid, Dest;
    }

    /// <summary>
    /// Every imported file's GUID, folder and name come from what it is, not from where AssetRipper put it or how it
    /// numbered it, so two mapmakers importing the same game build get the same files (notes in the design, Part 3):
    /// a texture by its name and size, a sprite by its texture, name and rectangle, a tile by its name, kind and default
    /// sprite, anything else by its name and what it refers to. Files land in fixed places under their own names
    /// (Texture2D/, Sprite/&lt;texture&gt;/, Tiles/&lt;set&gt;/, &lt;Type&gt;/); one AssetRipper had to rename (another asset had the
    /// name) gets "__&lt;first 6 of its GUID&gt;".
    /// </summary>
    public static class ArtIdentity
    {
        static readonly Regex NameRe = new Regex(@"\n  m_Name: ([^\n]*)");
        static readonly Regex RectRe = new Regex(@"m_Rect:\s*serializedVersion: \d+\s*x: ([-\d.eE+]+)\s*y: ([-\d.eE+]+)\s*width: ([-\d.eE+]+)\s*height: ([-\d.eE+]+)");
        static readonly Regex TextureRe = new Regex(@"texture: \{fileID: 2800000, guid: ([0-9a-f]+)");
        static readonly Regex DefaultSpriteRe = new Regex(@"m_DefaultSprite: \{fileID: -?\d+, guid: ([0-9a-f]+)");
        static readonly Regex ScriptRe = new Regex(@"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]+)");
        static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-z]+)");

        public static string Guid(string key)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>A PNG's size, from its first 24 bytes (the IHDR chunk); (0, 0) if it isn't one.</summary>
        public static (int w, int h) PngSize(byte[] head)
        {
            if (head == null || head.Length < 24 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return (0, 0);
            int Be(int at) => head[at] << 24 | head[at + 1] << 16 | head[at + 2] << 8 | head[at + 3];
            return (Be(16), Be(20));
        }

        public static string Safe(string s) => Regex.Replace((s ?? "").Trim(), @"[\\/:*?""<>|]", "_");

        public static string Rewrite(string text, IDictionary<string, string> oldToNew) =>
            GuidRef.Replace(text ?? "", m => oldToNew.TryGetValue(m.Groups[1].Value, out var n) ? "guid: " + n : m.Value);

        static string Name(string text) { var m = NameRe.Match(text ?? ""); return m.Success ? m.Groups[1].Value.Trim() : null; }

        static string Top(string rel) { int i = rel.IndexOf('/'); return i < 0 ? "" : rel.Substring(0, i); }

        /// <summary>What these export files are, their new GUIDs and places. Keys are worked out dependencies first (a
        /// sprite's texture, a tile's sprite, whatever a file refers to), the same whichever order the files come in.</summary>
        public static List<ArtItem> Identify(IEnumerable<string> relPaths, ExportIndex idx, Func<string, string> readText,
            Func<string, (int, int)> pngSize, ISet<string> takenDests)
        {
            var keys = new Dictionary<string, string>();   // guid -> key
            var visiting = new HashSet<string>();
            string Text(string rel) { try { return readText(rel); } catch { return ""; } }

            string KeyOf(string guid)
            {
                if (keys.TryGetValue(guid, out var known)) return known;
                var rel = idx.PathOf(guid);
                if (rel == null) return "ext|" + guid;   // (Unity's packages: their GUIDs are already the same everywhere)
                var file = Path.GetFileNameWithoutExtension(rel);
                if (!visiting.Add(guid)) return "cycle|" + Top(rel) + "|" + file;
                string key;
                var top = Top(rel);
                if (rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var (w, h) = pngSize(rel);
                    key = $"tex|{file}|{w}x{h}";
                }
                else
                {
                    var text = Text(rel);
                    var name = Name(text) ?? file;
                    var tex = TextureRe.Match(text);
                    var rect = RectRe.Match(text);
                    if (top == "Sprite" && tex.Success && rect.Success)
                    {
                        string R(int i) => Math.Round(double.Parse(rect.Groups[i].Value, CultureInfo.InvariantCulture), 2).ToString("0.##", CultureInfo.InvariantCulture);
                        key = $"sprite|{KeyOf(tex.Groups[1].Value)}|{name}|{R(1)},{R(2)},{R(3)},{R(4)}";
                    }
                    else if (TileAssets.IsTile(text))
                    {
                        var ds = DefaultSpriteRe.Match(text);
                        key = $"tile|{name}|{TileClass(text)}|{(ds.Success ? KeyOf(ds.Groups[1].Value) : "")}";
                    }
                    else
                    {
                        var refs = ExportIndex.ReferencedGuids(text).Where(g => g != guid).Select(KeyOf).OrderBy(k => k, StringComparer.Ordinal);
                        key = $"{top}|{name}|{string.Join(";", refs)}";
                    }
                }
                visiting.Remove(guid);
                keys[guid] = key;
                return key;
            }

            var items = new List<ArtItem>();
            foreach (var rel in relPaths.Distinct())
            {
                var guid = idx.GuidOf(rel);
                if (guid == null) continue;
                items.Add(new ArtItem { OldGuid = guid, RelPath = rel, Key = KeyOf(guid) });
            }
            var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // dest -> guid (this run)
            foreach (var it in items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                it.NewGuid = Guid(it.Key);
                var text = it.RelPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "" : Text(it.RelPath);
                var file = Path.GetFileNameWithoutExtension(it.RelPath);
                var name = Name(text) ?? file;
                var ext = Path.GetExtension(it.RelPath);
                var top = Top(it.RelPath);
                string folder;
                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) folder = "Texture2D";
                else if (top == "Sprite" && TextureRe.Match(text) is var t && t.Success && idx.PathOf(t.Groups[1].Value) is string texRel)
                    folder = "Sprite/" + Safe(Path.GetFileNameWithoutExtension(texRel));
                else if (TileAssets.IsTile(text)) folder = "Tiles/" + Safe(TileAssets.SetName(name));
                else folder = top.Length > 0 ? top : "Other";
                // AssetRipper renamed it (another asset had its name): our own mark instead, from what it is
                bool renamed = !string.Equals(Safe(name), file, StringComparison.Ordinal) && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
                string dest = $"{folder}/{Safe(name)}{(renamed ? "__" + it.NewGuid.Substring(0, 6) : "")}{ext}";
                // (two of these files still landing on one name: the later key gets the mark too. What's already on disk
                // never changes a place: places come from the export alone, so every import, in any order, agrees)
                if (taken.TryGetValue(dest, out var g) && g != it.NewGuid)
                    dest = $"{folder}/{Safe(name)}__{it.NewGuid.Substring(0, 6)}{ext}";
                taken[dest] = it.NewGuid;
                it.Dest = dest;
            }
            return items;
        }

        static string TileClass(string text)
        {
            if (text.Contains("fileID: 13312, guid: 0000000000000000e000000000000000")) return "Tile";
            var m = ScriptRe.Match(text);
            return m.Success && TileAssets.ScriptClass(m.Groups[1].Value) is string c ? c : "Tile";
        }
    }
}

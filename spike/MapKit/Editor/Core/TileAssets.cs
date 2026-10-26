// Emberville Map Kit: the game's tile assets, as files. Plain C#, no Unity (tested in spike/MapKit.Tests).
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbervilleMapKit.Core
{
    /// <summary>Which of the export's assets are tiles, which set each belongs to, and how to make them load in our
    /// project: the export's stand-ins for Unity's tile scripts (RuleTile...) have other GUIDs than the real package's.</summary>
    public static class TileAssets
    {
        /// <summary>The export's GUID for each Tilemap Extras tile script -> the package's (com.unity.2d.tilemap.extras 4.3.1).</summary>
        public static readonly IReadOnlyDictionary<string, string> ScriptGuids = new Dictionary<string, string>
        {
            ["0fb507873f962509567a8ace1719bac8"] = "9d1514134bc4fbd41bb739b1b9a49231",   // RuleTile
            ["2a4a1641abb0351b75232b4ddd396a7c"] = "13b75c95f34a00d4e8c04f76b73312e6",   // AnimatedTile
            ["dc1824ac1449a36de6ef9c978d6c8fe6"] = "b08ceb78e3b2495b8d808a5860e66794",   // AutoTile
            ["0b582850e874c88d4e4f480f3ac3c58b"] = "4c779848d9dd029409ca676b49e10a18",   // RuleOverrideTile
            ["14409170b2d08a9f4a7d72ad1618e3ec"] = "9ff2caf20a1df406d9ab8f2f5a57f734",   // AdvancedRuleOverrideTile
            ["96170a718f2b3a675d04b1ee1b1e2e3f"] = "5e6f3fbb560bd6041a202ea918fa4f23",   // IsometricRuleTile
            ["8c15ccfc9323dc8bc2e832095e45769e"] = "55f747e8af38fbe4d8819f37be6fd5aa",   // HexagonalRuleTile
        };

        static readonly Dictionary<string, string> Classes = new Dictionary<string, string>
        {
            ["0fb507873f962509567a8ace1719bac8"] = "RuleTile", ["2a4a1641abb0351b75232b4ddd396a7c"] = "AnimatedTile",
            ["dc1824ac1449a36de6ef9c978d6c8fe6"] = "AutoTile", ["0b582850e874c88d4e4f480f3ac3c58b"] = "RuleOverrideTile",
            ["14409170b2d08a9f4a7d72ad1618e3ec"] = "AdvancedRuleOverrideTile", ["96170a718f2b3a675d04b1ee1b1e2e3f"] = "IsometricRuleTile",
            ["8c15ccfc9323dc8bc2e832095e45769e"] = "HexagonalRuleTile",
        };

        /// <summary>A tile script's class name, from the export's GUID or the package's; null if it isn't one.</summary>
        public static string ScriptClass(string guid)
        {
            if (guid == null) return null;
            if (Classes.TryGetValue(guid, out var c)) return c;
            foreach (var p in ScriptGuids) if (p.Value == guid) return Classes[p.Key];
            return null;
        }

        const string BuiltInTile = "fileID: 13312, guid: 0000000000000000e000000000000000";
        static readonly Regex Script = new Regex(@"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]+)");
        static readonly Regex Suffix = new Regex(@"(_rules)?(_\d+)?$");

        /// <summary>A tile: Unity's own Tile, or one of the Tilemap Extras tiles.</summary>
        public static bool IsTile(string yamlText)
        {
            if (yamlText == null) return false;
            if (yamlText.Contains("m_Script: {" + BuiltInTile)) return true;
            var m = Script.Match(yamlText);
            return m.Success && ScriptGuids.ContainsKey(m.Groups[1].Value);
        }

        /// <summary>The tile scripts pointed at the project's package; everything else as it was.</summary>
        public static string TranslateScripts(string yamlText) =>
            ScriptGuids.Aggregate(yamlText ?? "", (t, p) => t.Replace("guid: " + p.Key, "guid: " + p.Value));

        /// <summary>The set a tile belongs to: its name without "_rules", "_rules_1", "_0"... ("Walls and Top_Zone1_rules_1"
        /// is in "Walls and Top_Zone1").</summary>
        public static string SetName(string assetName) => Suffix.Replace(assetName ?? "", "").Trim();
    }
}

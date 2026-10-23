using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbervilleGhost.Core;

/// <summary>
/// A custom room's props (CustomRoom), placed from the Map Kit's Props window: the game's object a prop is, by its name
/// without its copy number ("Vessel1_Breakable_01" -> "Vessel1_Breakable"; the same rule as the kit's harvest), and
/// whether it saves to the player's world (chests, buildables): those are never swapped for the game's live one.
/// Plain C#, tested on its own.
/// </summary>
public static class PropRules
{
    static readonly Regex CopyNumber = new(@"(\s*\(\d+\)|\s*\(Clone\)|_\d+| Variant)$");
    static readonly string[] Saving = { "Chest", "PlacementObjectComponent", "SavedObject" };

    public static string BaseName(string name)
    {
        string s = (name ?? "").Trim(), before;
        do { before = s; s = CopyNumber.Replace(s, "").Trim(); } while (s != before && s.Length > 0);
        return s.Length > 0 ? s : (name ?? "").Trim();
    }

    /// <summary>For a prop the game can't spawn (it only exists in the game's scenes) that could be hit in the game (it had
    /// a hit box): the game's own breakable it borrows its behaviour from. Breakables with loot ("..._Breakable": vases,
    /// boxes, bookshelves) the barrel's, furniture (tables, benches, chairs) the breakable table's; null for the rest.</summary>
    public static string BorrowFrom(string baseName, bool hadHitBox)
    {
        if (!hadHitBox) return null;
        return (baseName ?? "").IndexOf("Breakable", StringComparison.OrdinalIgnoreCase) >= 0 ? "BarrelEmpty_Breakable" : "Table_Vert_Roots_Breakable";
    }

    public static bool SaveTouching(string baseName, IEnumerable<string> componentClasses) =>
        (baseName ?? "").StartsWith("Place_", StringComparison.Ordinal) || componentClasses.Any(c => Saving.Contains(c));
}

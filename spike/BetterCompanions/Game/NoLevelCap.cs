using System.Collections.Generic;
using HarmonyLib;

namespace BetterCompanions;

/// <summary>
/// No cap on the character level (yours and the companion's: PlayerClassType.None, the base class). The game stops at
/// its XP table's last level (and GameData.MaxLevels["PlayerClass"]); past it, each level costs what the last one did.
/// Combat classes, masteries and jobs keep their caps.
/// </summary>
[HarmonyPatch]
internal static class NoLevelCap
{
    /// <summary>Total XP at the start of a level, on a table (past its end: its last cost, again and again).</summary>
    public static int XpAt(List<int> table, int level)
    {
        long total = 0;
        for (int i = 0; i < level - 1; i++) total += Cost(table, i);
        return (int)System.Math.Min(total, int.MaxValue);
    }

    static int Cost(List<int> table, int i) =>
        table == null || table.Count == 0 ? 1 : System.Math.Max(1, i < table.Count ? table[i] : table[table.Count - 1]);

    [HarmonyPatch(typeof(PlayerData), "GetMaxLevel"), HarmonyPostfix]
    static void Uncapped(object key, ref int __result)
    {
        if (key is PlayerClassType t && t == PlayerClassType.None) __result = int.MaxValue;
    }

    [HarmonyPatch(typeof(PlayerData), "GetLevelTableBased"), HarmonyPrefix]
    static bool PastTheTable(int totalPoints, int maxLevel, ref int remaining, List<int> table, ref int nextLevel, ref int __result)
    {
        if (table != GameData.PlayerXPTable || table.Count == 0) return true;
        remaining = totalPoints;
        int level = 0;
        while (level < maxLevel && remaining >= Cost(table, level))
        {
            remaining -= Cost(table, level);
            level++;
        }
        nextLevel = Cost(table, level);
        __result = level;
        return false;
    }
}

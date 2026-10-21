using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// /respec (and Ellie's "I want to RESPEC myself"): every point you've put into Strength, Dexterity and the rest back to
/// spend again. Each primary stat goes back to the class's starting value (the game works out the free points from your
/// level and what's in them: CharacterStats.GetAvailablePoints), the secondary stats follow (the class's conversion
/// table, as when a point is spent), and it's saved as the stat screen's Apply saves it. Not CharacterStats.ResetStats:
/// that builds new stats, without what your gear and effects add to them.
/// </summary>
internal static class Respec
{
    public static void Run()
    {
        var me = LocalPlayer.Get();
        var stats = me ? me.characterStats : null;
        if (!stats || !GameData.HasPlayerData || stats.baseClass == null) { NetLog.Info("Can't respec right now", toast: true); return; }
        int before = stats.GetAvailablePoints();
        foreach (var pair in stats.primStatDict)
        {
            var start = stats.baseClass.primaryStats.FirstOrDefault(s => s.stat.Equals(pair.Key));
            if (start != null) pair.Value.BaseValue = start.baseValue;
        }
        stats.baseClass.conversionTable.Calculate(stats.primStatDict, stats.secStatDict);
        stats.UpdateBaseValues(stats.primStatDict, stats.secStatDict);   // (the same values: tells the HUD and stat screen)
        GameData.playerData.SavePlayerStats(stats.primStatDict, stats.secStatDict);
        // less health / Kymia than before: not more than the new most
        if (me.CurrentHealthPoints > me.MaxHealthPoints) me.HealPercentNoBarNoFullNotification(1f, HealType.None);
        if (me.CurrentKymiaPoints > me.MaxKymiaPoints) me.ConsumeKymia(me.CurrentKymiaPoints - me.MaxKymiaPoints, false);
        int now = stats.GetAvailablePoints();
        NetLog.Info(now > before ? $"Respec: {now} points to spend again (open your character screen)" : "Respec: you had no points spent", toast: true);
    }
}

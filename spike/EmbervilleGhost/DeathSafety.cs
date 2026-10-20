using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The game keeps every creature it ever registered on a list (EnemiesManager) and walks it after a death
/// (ResetAll) and on every change of area (GameManager.CurrentArea saves them all). A creature destroyed since (our
/// arena's and dungeons' when you clear or leave them) throws there, and the rest never runs: after a death, or
/// halfway through a teleport, the screen stays black. Ours are taken off the list when we remove them (Forget), and
/// anything destroyed is dropped before either walk, in case.
/// </summary>
internal static class DeathSafety
{
    /// <summary>Take these creatures off the game's lists (call before destroying them).</summary>
    public static void Forget(GameObject root)
    {
        if (!root) return;
        foreach (var e in root.GetComponentsInChildren<Enemy>(true))
        {
            EnemiesManager.AllEnemies.Remove(e);
            EnemiesManager.AliveEnemies.Remove(e);
        }
    }

    static int Purge()
    {
        int gone = EnemiesManager.AllEnemies.RemoveWhere(e => !e);
        EnemiesManager.AliveEnemies.RemoveWhere(e => !e);
        return gone;
    }

    [HarmonyPatch(typeof(EnemiesManager), nameof(EnemiesManager.ResetAll))]
    static class OnDeathReset
    {
        static void Prefix()
        {
            int gone = Purge();
            if (gone > 0) NetLog.Info($"Death reset: {gone} creatures that no longer exist skipped", toast: false);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.CurrentArea), MethodType.Setter)]
    static class OnAreaChange
    {
        static void Prefix() => Purge();
    }
}

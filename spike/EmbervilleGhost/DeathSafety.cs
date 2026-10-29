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
    // ---- creatures kept through your death ----
    // After a death the game puts every creature back as it last saved it (Enemy.ResetToLastState), and one it never
    // saved is switched off for good. It saves them on entering an area: ours, and a dungeon's that come as you near,
    // come after that, so a death took them all (the floor then "cleared"). So: a living creature with nothing saved
    // is saved where it stands (a death puts it back there, alive); and in our places (dungeons, the arena) and for
    // our own creatures anywhere, one you've killed is saved dead (a death doesn't undo your clearing). The game's own
    // areas keep theirs: their creatures come back after a death as they always have.

    static float nextKeep;

    /// <summary>Once a second (Plugin.Update).</summary>
    public static void Keep()
    {
        if (Time.unscaledTime < nextKeep) return;
        nextKeep = Time.unscaledTime + 1f;
        bool ours = Dungeon.Active || Arena.Active;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e) continue;
            var saved = e.GetSavedState();
            if (!e.IsDead && saved == null && e.gameObject.activeInHierarchy) e.SaveState();
            else if (e.IsDead && saved != null && !saved.IsDead && (ours || e.GetComponent<Monster>())) e.SaveState();
        }
    }

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

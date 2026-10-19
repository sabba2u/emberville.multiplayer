using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EmbervilleGhost;

/// <summary>
/// Dying in co-op resets only you, not the shared world.
///
/// Emberville respawns Dark Souls style: after the death screen GameManager resets the player, then calls
/// EnemiesManager.ResetAll() (every creature back to its saved state: killed ones alive again, back at their spots)
/// and ResetToLastState() on every IResettable (breakables, rocks, projectiles...), back to how the area was when
/// entered. On one copy only, that rewinds that player's world while everyone else's goes on (a creature alive on one
/// screen, dead on the other). So while connected, the world part of the reset is skipped: from ResetAll to the end
/// of that frame, every ResetToLastState in the game (except the player's own, which already ran) does nothing.
/// </summary>
[HarmonyPatch]
internal static class CoopDeath
{
    public static Plugin Plugin;
    static bool blocking;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    [HarmonyPatch(typeof(EnemiesManager), nameof(EnemiesManager.ResetAll)), HarmonyPrefix]
    static bool BeforeWorldReset()
    {
        if (!Connected) return true;
        blocking = true;   // the resettables loop runs right after this, in the same frame
        NetLog.Info("You respawned; the world stays as it is for everyone (co-op)");
        return false;
    }

    /// <summary>Every ResetToLastState declared in the game (IResettable implementations).</summary>
    [HarmonyPatch]
    static class Resettables
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(IResettable).Assembly))
            {
                if (!typeof(IResettable).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract) continue;
                var m = AccessTools.DeclaredMethod(type, nameof(IResettable.ResetToLastState));
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        static bool Prefix() => !blocking;
    }

    /// <summary>Every frame (Plugin.Update): the death reset is over.</summary>
    public static void Tick() => blocking = false;
}

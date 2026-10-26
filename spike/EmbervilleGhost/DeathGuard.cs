using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Dying stays dying. The game's stun (a Stun effect on a hit, a trap) doesn't check you're dead: one landing in the
/// three seconds of the death animation takes you out of it, the "you died" screen never comes, and you're left
/// standing at 0 health with no controls. So: no stuns on the dead, and if you're ever at 0 health and not dead
/// (whatever did it), you die again properly.
/// </summary>
[HarmonyPatch]
internal static class DeathGuard
{
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Stun)), HarmonyPrefix]
    static bool NoStunWhenDead(PlayerMaster __instance) => !__instance.IsDead;

    static readonly System.Reflection.MethodInfo die = AccessTools.Method(typeof(PlayerMaster), "Die");
    static float zeroSince = -1f;

    /// <summary>Every frame: at 0 health but not dead for a second, die (again).</summary>
    public static void Tick()
    {
        var me = LocalPlayer.Get();
        if (!me || me.IsDead || me.CurrentHealthPoints > 0) { zeroSince = -1f; return; }
        if (zeroSince < 0f) { zeroSince = Time.unscaledTime; return; }
        if (Time.unscaledTime - zeroSince < 1f) return;
        zeroSince = -1f;
        NetLog.Warn("Your death got interrupted; dying properly", toast: false);
        try { die?.Invoke(me, null); }
        catch (System.Exception e) { NetLog.Warn($"DeathGuard: {e.Message}"); }
    }
}

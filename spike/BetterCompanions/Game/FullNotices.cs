using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// "Kymia Full" and "Health Full": once in a while, not a stack. Some skills' effects (a buff from your companion's,
/// or your own) refill Kymia every frame while they last; each refill on a full bar showed the game's "Kymia Full", so
/// they piled up on top of each other. Now each character shows it at most every 1.5 s; the refills are as they were.
/// </summary>
[HarmonyPatch]
internal static class FullNotices
{
    const float Every = 1.5f;
    static readonly Dictionary<int, float> kymiaAt = new(), healthAt = new();

    static bool Shown(Dictionary<int, float> at, PlayerMaster p)
    {
        int id = p.GetInstanceID();
        if (at.TryGetValue(id, out float t) && Time.time - t < Every) return false;
        at[id] = Time.time;
        return true;
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreKymia)), HarmonyPrefix]
    static bool Kymia(PlayerMaster __instance, float value, bool showFeedback, ref bool __result)
    {
        // a kill reward (the companion's class: kymia back on a kill) can point at a player that never woke: the copy of
        // you companions are made from, which its graph still names. The companion that made the kill gets it instead
        // (a companion swapped out since: nobody does)
        if (!__instance || !__instance.myTransform)
        {
            var now = RealPlayer.Current;
            __result = now && now != __instance && now.myTransform && now.RestoreKymia(value, showFeedback);
            return false;
        }
        bool full = value != 0f && Mathf.Min(value, __instance.currentMaxKymia - __instance.currentKymia) <= 0f;
        if (!full || Shown(kymiaAt, __instance)) return true;
        __result = false;   // (as the game returns for a full bar, without its notice)
        return false;
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RestoreHealth)), HarmonyPrefix]
    static void Health(PlayerMaster __instance, int value, ref bool displayHealthFullNotification)
    {
        if (!displayHealthFullNotification) return;
        bool full = Mathf.Min(value, __instance.MaxHealthPoints - __instance.currentHealth) <= 0;
        if (full && !Shown(healthAt, __instance)) displayHealthFullNotification = false;
    }
}

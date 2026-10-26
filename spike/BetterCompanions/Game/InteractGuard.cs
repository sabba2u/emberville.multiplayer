using HarmonyLib;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// F next to a downed companion (ours, or a teammate's: Hooks.DownedNear) is for reviving it: the game's own F
/// (picking up, opening, talking: InteractablesHandler) stands down meanwhile, so it doesn't grab the loot it fell on.
/// </summary>
[HarmonyPatch(typeof(InteractablesHandler), "OnInteract")]
internal static class InteractGuard
{
    const float Reach = 1.5f;

    static bool Prefix(InteractablesHandler __instance) => !ReviveHere(__instance.transform.position);

    public static bool ReviveHere(Vector3 at)
    {
        var b = Companion.Body;
        if (b != null && !b.Lost && b.Unit && b.Unit.Revive.Down && Vector2.Distance(at, b.Position) <= Reach) return true;
        return RealBrain.DownNear(at, Reach) || Hooks.DownedNear(at, Reach);
    }

    /// <summary>Something of the game's is in reach for F (an item, a chest, someone to talk to).</summary>
    public static bool GameHasTarget(PlayerMaster me)
    {
        var h = me ? me.GetComponentInChildren<InteractablesHandler>() : null;
        return h && h.currentTarget != null;
    }
}

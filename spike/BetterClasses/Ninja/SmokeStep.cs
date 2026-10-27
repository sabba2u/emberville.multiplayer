using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace BetterClasses;

/// <summary>
/// A Ninja's dodge is a vanish: a puff of smoke where you were, and you're somewhere else, one and a half times a dodge's distance
/// the way you were going (or facing). Walls, doors, fences: it goes through them, as long as there's floor to land
/// on (the creatures' walkable map); none that far, the furthest floor short of it. Unseen for a moment, then the
/// game's teleport flash and another puff where you come out. Invincible throughout, as the game's dodge is.
/// (A place with no walkable map: the game's own dash, half as far again.)
/// </summary>
[HarmonyPatch]
internal static class SmokeStep
{
    const float Farther = 1.5f, Unseen = 0.12f, Floor = 0.35f, Step = 0.25f;

    static PlayerMaster hidden;
    static bool jumped;   // (it teleported: the game's dash doesn't move it on)

    [HarmonyPatch(typeof(PlayerDodge), nameof(PlayerDodge.EnterState)), HarmonyPostfix]
    static void Vanish(PlayerDodge __instance)
    {
        var p = __instance.owner;
        if (!Ninja.Is(p)) return;
        Fx.Puff(p.transform.position, p);
        p.ShowPlayer(false);
        hidden = p;
        jumped = false;
        float far = __instance.duration * p.currentDodge.dodgeSpeed * Farther;   // (the game's dodge distance, its bonuses in)
        var from = p.transform.position;
        if (Landing(from, (Vector3)__instance.dir.normalized, far, out var to))
        {
            p.rb.position = to;
            p.transform.position = to;
            __instance.duration = Unseen;
            jumped = true;
        }
        else __instance.duration *= Farther;   // (no walkable map here: the game's dash, half as long again)
    }

    /// <summary>The furthest floor along the way, up to that far (none: false).</summary>
    static bool Landing(Vector3 from, Vector3 dir, float far, out Vector3 to)
    {
        to = from;
        if (!NavMesh.SamplePosition(new Vector3(from.x, from.y, 0f), out _, 2f, NavMesh.AllAreas)) return false;   // (this place has none)
        for (float d = far; d >= Step * 2f; d -= Step)
        {
            var want = from + dir * d;
            if (NavMesh.SamplePosition(new Vector3(want.x, want.y, 0f), out var hit, Floor, NavMesh.AllAreas))
            {
                to = new Vector3(hit.position.x, hit.position.y, from.z);
                return true;
            }
        }
        return false;
    }

    /// <summary>A vanish that teleported: no dash on top, just the moment unseen.</summary>
    [HarmonyPatch(typeof(PlayerDodge), nameof(PlayerDodge.FixedUpdateState)), HarmonyPrefix]
    static bool NoDash(PlayerDodge __instance)
    {
        if (!jumped || hidden != __instance.owner) return true;
        if ((float)__instance.timeSinceStarted >= __instance.duration) __instance.owner.ChangeState(__instance.owner.stateIdle);
        return false;
    }

    [HarmonyPatch(typeof(PlayerDodge), nameof(PlayerDodge.ExitState)), HarmonyPostfix]
    static void Appear(PlayerDodge __instance)
    {
        var p = __instance.owner;
        if (!p || hidden != p) return;
        hidden = null;
        jumped = false;
        p.ShowPlayer(true);
        Fx.Puff(p.transform.position, p);
        try { p.PlayExtraVFX("Teleport"); } catch { }
    }
}

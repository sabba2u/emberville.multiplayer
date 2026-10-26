using System;

namespace BetterCompanions.Core;

public enum Move { Idle, Follow, Teleport, GoTo, Attack }

/// <summary>Everything the brain looks at in one frame. Positions are world (x, y); times are seconds.</summary>
public record struct Situation(
    (float x, float y) me, (float x, float y) owner, (float x, float y)? target, (float x, float y)? sentTo,
    bool downed, Style style, float stuckFor, float sinceDash, float sinceSwap, float cutOffFor = 0f);

/// <summary>
/// The companion's decisions, without Unity: what to do next (follow, fight, go where it was sent, catch up), which
/// of its two favourite weapons to hold, when to dash, and whether an enemy would rather fight it than the player.
/// </summary>
public static class Brain
{
    public const float SwapCooldown = 1.5f, DashCooldown = 2.5f, DashDistance = 3f;
    const float RangedFrom = 3.5f, MeleeWithin = 2f, Crowd = 3;

    public static (Move move, (float x, float y) where) Decide(Situation s)
    {
        if (s.downed) return (Move.Idle, s.me);
        if (Dist(s.me, s.owner) > Rules.TeleportFar || s.stuckFor >= Rules.StuckSeconds || s.cutOffFor >= Rules.CutOffSeconds) return (Move.Teleport, s.owner);
        if (s.sentTo is { } to) return (Move.GoTo, to);   // (a ping breaks off whatever it's doing)
        if (s.target is { } t && Dist(s.owner, t) <= Rules.Leash)
            return Dist(s.me, t) <= Rules.AttackRange(s.style) ? (Move.Attack, t) : (Move.GoTo, t);
        return Dist(s.me, s.owner) <= Rules.FollowGap ? (Move.Idle, s.me) : (Move.Follow, s.owner);
    }

    /// <summary>
    /// Bow (or staff) or sword? With only one kind, that one. Otherwise ranged beyond 3.5, melee within 2, and in
    /// between whatever it holds now (so it doesn't flicker); crowded with the bow out, it keeps the bow and dashes out.
    /// </summary>
    public static bool WantRanged(float targetDist, int enemiesWithin2, bool hasMelee, bool hasRanged, bool rangedNow)
    {
        if (!hasRanged) return false;
        if (!hasMelee) return true;
        if (rangedNow && enemiesWithin2 >= Crowd) return true;
        if (targetDist > RangedFrom) return true;
        if (targetDist <= MeleeWithin) return false;
        return rangedNow;
    }

    /// <summary>
    /// Where to dash, if anywhere: up close with a sword, onto a target 3-6 away; with the bow, straight away from a
    /// target within 1.5 or out of a crowd. Never sooner than DashCooldown after the last one.
    /// </summary>
    public static (float x, float y)? DashTo(Situation s, (float x, float y) target, int enemiesWithin2, bool ranged)
    {
        if (s.downed || s.sinceDash < DashCooldown) return null;
        float d = Dist(s.me, target);
        if (d < 0.001f) return null;
        float dx = (target.x - s.me.x) / d, dy = (target.y - s.me.y) / d;
        if (!ranged && d >= 3f && d <= 6f) return (s.me.x + dx * DashDistance, s.me.y + dy * DashDistance);
        if (ranged && (d <= 1.5f || enemiesWithin2 >= Crowd)) return (s.me.x - dx * DashDistance, s.me.y - dy * DashDistance);
        return null;
    }

    /// <summary>
    /// Would this enemy rather fight the companion than the player? The nearer one, ties going to the companion if it
    /// hit this enemy last; a downed companion never.
    /// </summary>
    public static bool EnemyPrefersCompanion((float x, float y) enemy, (float x, float y) player, (float x, float y) companion,
        bool companionDowned, bool companionHitItLast)
    {
        if (companionDowned) return false;
        float toPlayer = Dist(enemy, player), toCompanion = Dist(enemy, companion);
        if (Math.Abs(toPlayer - toCompanion) < 0.25f) return companionHitItLast;
        return toCompanion < toPlayer;
    }

    public static float Dist((float x, float y) a, (float x, float y) b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }
}

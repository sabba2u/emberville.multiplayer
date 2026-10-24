using System.Collections.Generic;
using BetterCompanion.Core;
using UnityEngine;

namespace BetterCompanion;

/// <summary>
/// Enemies fight the companion for real. A creature's AI chases whatever its behaviour graph's "Player" is (the
/// game sets it to the player when the creature wakes, Enemy.Awake), and its attacks hurt any damageable thing they
/// touch. So twice a second each creature near the companion is pointed at whichever of the two it would rather
/// fight (Brain.EnemyPrefersCompanion: the nearer, ties to the companion if it hit that creature last); a downed or
/// dismissed companion hands them all back to the player. In co-op, only creatures whose AI runs on this copy.
/// </summary>
internal static class Aggro
{
    const float Every = 0.5f, Near = 14f;
    static float next;
    static readonly HashSet<Enemy> onCompanion = new();
    public static Enemy LastHitByCompanion;

    public static void Tick(CompanionBody body, PlayerMaster me)
    {
        if (Time.time < next) return;
        next = Time.time + Every;
        if (body == null || body.Lost || !me) { ReleaseAll(); return; }
        bool down = body.Unit.Revive.Down;
        var c = body.Position;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || Hooks.Ignore(e) || Hooks.RemoteRun(e) || !e.BehaviorAgent) continue;
            bool near = Vector2.Distance(e.transform.position, c) <= Near;
            bool want = near && Brain.EnemyPrefersCompanion(Flat(e.transform.position), Flat(me.transform.position), Flat(c), down, e == LastHitByCompanion);
            if (want == onCompanion.Contains(e)) continue;
            Point(e, want ? body.Root : me.gameObject);
            if (want) onCompanion.Add(e); else onCompanion.Remove(e);
        }
        onCompanion.RemoveWhere(e => !e || e.IsDead);
    }

    /// <summary>Every creature back on the player (downed, dismissed, scene change, unload).</summary>
    public static void ReleaseAll()
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        foreach (var e in onCompanion)
            if (e && me) Point(e, me.gameObject);
        onCompanion.Clear();
    }

    static void Point(Enemy e, GameObject at)
    {
        // (only creatures whose graph keeps its target as a GameObject: some keep a Transform, and those we leave be)
        try { if (e.BehaviorAgent.GetVariable("Player", out Unity.Behavior.BlackboardVariable<GameObject> v)) v.Value = at; }
        catch (System.Exception ex) { Log.Warn($"Companion: retargeting {e.name}: {ex.Message}"); }
    }

    static (float x, float y) Flat(Vector3 v) => (v.x, v.y);
}

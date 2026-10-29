using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Better Streaming's raids, made here (the spawning lives in this mod):
///  - a raid: creatures that fit the area (the kinds alive around you now; none: the arena's roster) in a ring 5-7
///    units out, their health scaled by the cheer's size;
///  - a chicken raid: hostile chickens from the screen's edges, a few at a time in waves 3 s apart, walked at you and
///    pecking as the chicken level's do (their own brain off).
/// Every one is Twitch's (Better Streaming's free death checks), and the companion fights them.
/// </summary>
internal static class StreamRaids
{
    sealed class Hen { public Enemy E; public float NextPeck; }

    static readonly HashSet<Enemy> twitch = new();
    static readonly Dictionary<Enemy, float> shieldedSince = new();
    static readonly List<Hen> hens = new();
    static readonly List<(float at, Vector3 around, float tough)> waves = new();
    const float PeckRange = 1.1f, PeckEvery = 1.1f;
    const int PeckBase = 4;

    public static bool IsTwitch(Object o) => o is Enemy e && e && twitch.Contains(e);

    // ---- the mystery chest ----

    static readonly Dictionary<Chest, (bool trap, string viewer)> chests = new();
    static int chestCount;

    /// <summary>Hooks.SpawnChest: (around, a trap?, viewer) → placed? Loot: as a good descent floor's chest. A trap: empty,
    /// and opening it brings a raid.</summary>
    public static bool Chest(Vector3 around, bool trap, string viewer)
    {
        if (!Near(around, out var at, 2f, 3f)) return false;
        var c = DungeonLoot.MakeChest(at, $"EG_TW_{System.DateTime.Now.Ticks}_{chestCount++}", Random.Range(1, int.MaxValue), 6, trap);
        if (!c) return false;
        chests[c] = (trap, viewer);
        Poof(at);
        NetLog.Info($"Twitch mystery chest from {viewer}{(trap ? " (a trap)" : "")}", toast: false);
        return true;
    }

    static void WatchChests()
    {
        foreach (var c in chests.Keys.ToList())
        {
            if (!c) { chests.Remove(c); continue; }
            if (!c.isOpened && !c.alreadyOpened) continue;
            var (trap, viewer) = chests[c];
            chests.Remove(c);
            if (!trap) continue;
            NetLog.Warn($"It was a trap! {viewer}'s chest springs a raid", toast: true);
            Raid(c.transform.position, 4, 1.2f, viewer);
        }
    }

    /// <summary>Hooks.SpawnRaid: (around, how many, toughness, viewer) → any came?</summary>
    public static bool Raid(Vector3 around, int count, float tough, string viewer)
    {
        var kinds = AreaKinds(around);
        int made = 0;
        for (int i = 0; i < count; i++)
        {
            float a = (i + Random.value * 0.5f) / count * Mathf.PI * 2f, r = Random.Range(5f, 7f);
            if (!Walkable(around + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f), around, out var at) && !Near(around, out at)) continue;
            var e = MiniBoss.SpawnPlain(kinds[Random.Range(0, kinds.Count)], at);
            if (!e) continue;
            Poof(at);
            twitch.Add(e);
            if (tough > 1f && e.initHealth > 0f) { e.initHealth *= tough; e.currentHealth = e.initHealth; }
            made++;
        }
        NetLog.Info($"Twitch raid from {viewer}: {made} creatures ({string.Join(", ", kinds)})", toast: false);
        return made > 0;
    }

    /// <summary>The creature kinds that fit the area (alive within 40 units, not shielded, not bosses, not fauna or NPCs);
    /// none: the arena's roster.</summary>
    public static List<string> AreaKinds(Vector3 around)
    {
        var kinds = EnemiesManager.AllEnemies
            .Where(e => e && !e.IsDead && e.gameObject.activeInHierarchy && !twitch.Contains(e) && !MiniBoss.IsBoss(e) && e.CanTakeDamage &&
                        e.enemyType != EnemyType.None && !e.name.StartsWith("NPC_") &&
                        ((Vector2)(e.transform.position - around)).magnitude < 40f)
            .Select(e => MiniBoss.BaseName(e.name)).Distinct().ToList();
        if (kinds.Count == 0) kinds = MiniBoss.Prefabs().Take(6).Select(e => MiniBoss.BaseName(e.name)).ToList();
        return kinds;
    }

    /// <summary>Hooks.ChickenRaid: (around, waves, toughness, viewer) → started?</summary>
    public static bool Chickens(Vector3 around, int count, float tough, string viewer)
    {
        if (!Arena.Chicken()) return false;
        for (int i = 0; i < count; i++) waves.Add((Time.time + i * 3f, around, tough));
        NetLog.Info($"Twitch chicken raid from {viewer}: {count} waves", toast: false);
        return true;
    }

    /// <summary>Every frame: waves due, the chickens at you, and the dead forgotten.</summary>
    public static void Tick()
    {
        if (chests.Count > 0) WatchChests();
        if (twitch.Count == 0 && waves.Count == 0) return;
        twitch.RemoveWhere(e => !e || e.IsDead);
        // (a story creature copied into a raid can keep its scripted shield, e.g. the Acolyte Sorcerer: never for long)
        foreach (var e in twitch)
        {
            if (e.CanTakeDamage) { shieldedSince.Remove(e); continue; }
            if (!shieldedSince.TryGetValue(e, out float since)) shieldedSince[e] = Time.time;
            else if (Time.time - since > 3f) { e.CanTakeDamage = true; shieldedSince.Remove(e); NetLog.Info($"Twitch raid: {e.name}'s shield taken off", toast: false); }
        }
        hens.RemoveAll(h => !h.E || h.E.IsDead);
        var me = LocalPlayer.Get();
        if (!me) return;
        // (a cutscene, dialogue, a menu, dead: no new waves, no pecks; the waves wait their turn)
        if (PlayerStatusUtil.LocalBusy || me.IsDead)
        {
            for (int i = 0; i < waves.Count; i++) waves[i] = (waves[i].at + Time.deltaTime, waves[i].around, waves[i].tough);
            return;
        }
        for (int i = waves.Count - 1; i >= 0; i--)
        {
            if (Time.time < waves[i].at) continue;
            Wave(me.transform.position, waves[i].tough);
            waves.RemoveAt(i);
        }
        var at = me.transform.position;
        foreach (var h in hens)
        {
            var e = h.E;
            var mover = e.navMeshAgent;
            if (mover)
            {
                mover.GetPosition = () => at;
                try { mover.Move(); } catch { }
            }
            if (((Vector2)(e.transform.position - at)).sqrMagnitude < PeckRange * PeckRange && Time.time >= h.NextPeck)
            {
                h.NextPeck = Time.time + PeckEvery;
                var a = e.animator;
                if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) { a.SetInteger("AttackType", 0); a.SetTrigger("Attack"); }
                try { CombatSystem.Resolve(new DamageData(e, me, PeckBase, DamageHitType.Impact, KnockBack.Low)); }
                catch (System.Exception x) { NetLog.Warn($"Twitch chicken peck: {x.Message}", toast: false); }
            }
        }
    }

    /// <summary>The teleport flash there (the player's own "Teleport" effect, as the companion arrives with it): a copy,
    /// moved to the spot, gone after 3 s.</summary>
    internal static void Poof(Vector3 at)
    {
        var me = LocalPlayer.Get();
        var fx = me ? me.extraFxAnimator : null;
        if (!fx) return;
        try
        {
            var offset = fx.transform.position - me.transform.position;
            var clone = Object.Instantiate(fx.gameObject, at + offset, fx.transform.rotation);
            clone.transform.localScale = fx.transform.lossyScale;
            clone.SetActive(true);
            var a = clone.GetComponent<Animator>();
            if (a) { a.fireEvents = false; a.SetTrigger("Teleport"); }
            Object.Destroy(clone, 3f);
        }
        catch (System.Exception e) { NetLog.Warn($"Twitch raid: the teleport flash: {e.Message}", toast: false); }
    }

    /// <summary>The walkable ground nearest that spot (within 4 units), from which there's a way to you: no spawning in
    /// a wall, water or a cliff, or on a patch cut off from you.</summary>
    static bool Walkable(Vector3 want, Vector3 me, out Vector3 at)
    {
        at = want;
        // (no walkable map where you stand: this place has none to go by, so the spot as it is)
        if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(me.x, me.y, 0f), out var mine, 2f, UnityEngine.AI.NavMesh.AllAreas)) return true;
        if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(want.x, want.y, 0f), out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas)) return false;
        var path = new UnityEngine.AI.NavMeshPath();
        if (!UnityEngine.AI.NavMesh.CalculatePath(mine.position, hit.position, UnityEngine.AI.NavMesh.AllAreas, path) || path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete) return false;
        at = new Vector3(hit.position.x, hit.position.y, me.z);
        return true;
    }

    /// <summary>Somewhere walkable that far from you (4-6 units: when the wanted spots all failed).</summary>
    static bool Near(Vector3 me, out Vector3 at, float min = 4f, float max = 6f)
    {
        for (int tries = 0; tries < 8; tries++)
            if (Walkable(me + (Vector3)(Random.insideUnitCircle.normalized * Random.Range(min, max)), me, out at)) return true;
        at = me;
        return false;
    }

    /// <summary>A few chickens from just off the screen's edges.</summary>
    static void Wave(Vector3 me, float tough)
    {
        var cam = Camera.main;
        int n = Random.Range(3, 6);
        for (int i = 0; i < n; i++)
        {
            Vector3 at = Vector3.zero;
            bool found = false;
            for (int tries = 0; tries < 6 && !found; tries++)
            {
                Vector3 want;
                if (cam)
                {
                    var edge = Random.Range(0, 4) switch { 0 => new Vector3(-0.05f, Random.value, 0f), 1 => new Vector3(1.05f, Random.value, 0f), 2 => new Vector3(Random.value, -0.05f, 0f), _ => new Vector3(Random.value, 1.05f, 0f) };
                    edge.z = Mathf.Abs(cam.transform.position.z - me.z);
                    want = cam.ViewportToWorldPoint(edge);
                    want.z = me.z;
                }
                else want = me + (Vector3)(Random.insideUnitCircle.normalized * 8f);
                found = Walkable(want, me, out at);
            }
            if (!found && !Near(me, out at)) continue;
            var e = MiniBoss.SpawnPlain("Mob_Chicken", at, null, Arena.Chicken());
            if (!e) continue;
            Poof(at);
            if (e.BehaviorAgent) e.BehaviorAgent.enabled = false;
            if (e.initHealth > 0f) { e.initHealth *= tough; e.currentHealth = e.initHealth; }
            twitch.Add(e);
            hens.Add(new Hen { E = e, NextPeck = Time.time + 0.8f });
        }
    }
}

using System.Collections.Generic;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The arena's "There is no chicken level": survive as long as you can.
/// - Every 5 s a chicken, or a flock of them, comes at you from a random side of the arena; each wave a little more
///   health and a little harder pecks.
/// - Every minute some big chickens (bigger, much tougher, peck harder; not mini-bosses).
/// - Every 3 minutes a mini-boss chicken (the boss bar, its name), its health growing each time.
/// They never leave you alone: the game's chickens don't fight, so their own brain is switched off and ours walks
/// them at you (the game's own mover, on the arena's navigation mesh) and pecks when close (the game's own hit, the
/// chicken as the one hitting). The timer shows while it runs; when you fall, your time and your best (kept in the
/// config) come up.
/// </summary>
internal static partial class Arena
{
    static ConfigEntry<float> bestChickens;

    public static void BindChickens(ConfigFile config) =>
        bestChickens = config.Bind("Arena", "ChickenBest", 0f, "There is no chicken level: your best survival time (seconds)");

    const float WaveEvery = 5f, BigEvery = 60f, BossEvery = 180f, MaxChickens = 60;
    const float PeckRange = 1.1f, PeckEvery = 1.1f, PeckBase = 4f;

    sealed class Hen
    {
        public Enemy E;
        public float Health = 1f, Damage = 1f;   // x its own health; x the pecks' base
        public bool Scaled;
        public float NextPeck;
    }

    static readonly List<Hen> hens = new();
    static bool chickensOn;
    static float chickenStart, nextWave, nextBig, nextBoss, endedAt = -100f, lastTime;
    static int chickenWave, bigRound, bossRound;
    static bool trapsSpikes, trapsArrows;

    /// <summary>One of the chicken level's chickens (for the companion: a foe, though fauna).</summary>
    public static bool IsHen(Enemy e) => chickensOn && e && hens.Exists(h => h.E == e);

    static float Survived => chickensOn ? Time.time - chickenStart : lastTime;

    static void StartChickens()
    {
        if (rushWave > 0) EndRush("stopped");
        ClearSpawned();
        hens.Clear();
        if (!Chicken()) { NetLog.Warn("No chickens loaded in this game yet (look for one in the world first)", toast: true); return; }
        chickensOn = true;
        chickenStart = Time.time;
        nextWave = Time.time + 2f;
        nextBig = Time.time + BigEvery;
        nextBoss = Time.time + BossEvery;
        chickenWave = bigRound = bossRound = 0;
        trapsSpikes = trapsArrows = false;
        ArenaTraps.Switch(false, false);
        folded = true;
        NetLog.Warn("THERE IS NO CHICKEN LEVEL. Survive!", toast: true);
    }

    static void EndChickens(string why)
    {
        if (!chickensOn) return;
        chickensOn = false;
        lastTime = Time.time - chickenStart;
        endedAt = Time.unscaledTime;
        bool best = lastTime > bestChickens.Value;
        if (best) bestChickens.Value = lastTime;
        NetLog.Info($"There is no chicken level {why}: you lasted {Clock(lastTime)}" + (best ? " - a new best!" : $" (best {Clock(bestChickens.Value)})"), toast: true);
        ArenaTraps.On = false;
        // what's left goes (their own brain back on threw: a chicken's graph doesn't start in the middle of things):
        // out of sight now, gone a moment later (one still being knocked back finishes that first, or it throws)
        foreach (var h in hens)
        {
            if (!h.E) continue;
            var go = h.E.gameObject;
            spawned.Remove(go);
            DeathSafety.Forget(go);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in go.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
            Object.Destroy(go, 2f);
        }
        hens.Clear();
    }

    static string Clock(float s) => $"{(int)(s / 60f)}:{(int)(s % 60f):00}";

    /// <summary>Every frame (Arena.Tick): the waves, and every chicken at you.</summary>
    static void Chickens()
    {
        if (!chickensOn) return;
        if (!Active) { EndChickens("over (you left)"); return; }
        var me = LocalPlayer.Get();
        if (!me) return;
        if (me.IsDead) { EndChickens("over"); return; }
        hens.RemoveAll(h => !h.E || h.E.IsDead);
        // the traps: spikes after a minute, the arrows after three
        float t = Time.time - chickenStart;
        bool spikes = t >= 60f, arrows = t >= 180f;
        if (spikes != trapsSpikes || arrows != trapsArrows) { trapsSpikes = spikes; trapsArrows = arrows; ArenaTraps.Switch(spikes, arrows); }

        if (Time.time >= nextWave)
        {
            nextWave += WaveEvery;
            chickenWave++;
            int flock = Random.value < 0.4f ? 1 : Random.Range(2, 3 + chickenWave / 4);
            float hp = 1f + 0.12f * chickenWave, dmg = 1f + 0.05f * chickenWave;
            var from = Side(me.transform.position);
            for (int i = 0; i < flock && hens.Count < MaxChickens; i++) Plain(from + (Vector3)(Random.insideUnitCircle * 1.2f), 1f, hp, dmg);
        }
        if (Time.time >= nextBig)
        {
            nextBig += BigEvery;
            bigRound++;
            float hp = (4f + 1.5f * bigRound) * (1f + 0.12f * chickenWave), dmg = (1.8f + 0.2f * bigRound) * (1f + 0.05f * chickenWave);
            for (int i = 0; i < 1 + bigRound && hens.Count < MaxChickens; i++) Plain(Side(me.transform.position), 1.9f, hp, dmg);
            NetLog.Warn(bigRound == 1 ? "Big chickens!" : $"Big chickens! ({1 + bigRound})", toast: true);
        }
        if (Time.time >= nextBoss)
        {
            nextBoss += BossEvery;
            bossRound++;
            var look = new MiniBoss.Look
            {
                Name = bossRound == 1 ? "The Colonel" : $"The Colonel, round {bossRound}",
                Base = "Mob_Chicken",
                Tint = new Color(1f, 0.35f, 0.25f),
                Scale = 3f + 0.2f * bossRound,
                Health = 30f * (1f + 0.6f * (bossRound - 1)),
                Damage = 1.5f + 0.3f * bossRound,
                LootFloor = 1 + bossRound * 2,
            };
            var boss = MiniBoss.Spawn(look, Side(me.transform.position), root.transform, Chicken());
            if (boss) { spawned.Add(boss.gameObject); Take(boss, 1f, 1.5f, scale: false); }   // (its health and damage: MiniBoss's look)
            NetLog.Warn($"{look.Name} is here!", toast: true);
        }

        var at = me.transform.position;
        foreach (var h in hens)
        {
            var e = h.E;
            if (!h.Scaled && e.initHealth > 0f)
            {
                e.initHealth *= h.Health;
                e.currentHealth = e.initHealth;
                h.Scaled = true;
            }
            // at you, always (the game's own mover: speed, the navigation mesh, which way it faces)
            var mover = e.navMeshAgent;
            if (mover)
            {
                mover.GetPosition = () => at;
                try { mover.Move(); } catch { }
            }
            if (((UnityEngine.Vector2)(e.transform.position - at)).sqrMagnitude < PeckRange * PeckRange * Mathf.Max(1f, e.transform.localScale.x) && Time.time >= h.NextPeck)
            {
                h.NextPeck = Time.time + PeckEvery;
                var a = e.animator;
                if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) { a.SetInteger("AttackType", 0); a.SetTrigger("Attack"); }
                try { CombatSystem.Resolve(new DamageData(e, me, Mathf.RoundToInt(PeckBase * h.Damage), DamageHitType.Impact, KnockBack.Low)); }
                catch (System.Exception x) { NetLog.Warn($"Chicken peck: {x.Message}", toast: false); }
            }
        }
    }

    /// <summary>A plain chicken (size: 1 = its own), a share of its own health, pecks a share of the base.</summary>
    static void Plain(Vector3 at, float size, float health, float damage)
    {
        var e = MiniBoss.SpawnPlain("Mob_Chicken", at, root.transform, Chicken());
        if (!e) return;
        if (size != 1f) e.transform.localScale = Vector3.one * size;
        spawned.Add(e.gameObject);
        Take(e, health, damage, scale: true);
    }

    static Enemy chicken;

    /// <summary>The game's chicken: its prefab (the boss maker's list leaves chickens out, and the scene's are named just
    /// "Chicken"), else one standing in the scene. None in memory: the biomes are loaded (they carry it) and looked again.</summary>
    static Enemy Chicken()
    {
        if (chicken) return chicken;
        chicken = FindChicken();
        if (!chicken) { Dungeon.AllSettings(); chicken = FindChicken(); }
        if (chicken) NetLog.Info($"Chickens: from {chicken.name}{(chicken.gameObject.scene.IsValid() ? " (one in the scene)" : "")}", toast: false);
        return chicken;
    }

    static Enemy FindChicken()
    {
        Enemy prefab = null, alive = null;
        foreach (var e in Resources.FindObjectsOfTypeAll<Enemy>())
        {
            if (!e || hens.Exists(h => h.E == e)) continue;
            string n = e.name.Replace("(Clone)", "").Trim();
            if (n.StartsWith("Mob_")) n = n.Substring(4);
            int cut = n.IndexOfAny(new[] { '_', ' ', '(' });
            if (cut > 0) n = n.Substring(0, cut);
            if (!n.Equals("Chicken", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!e.gameObject.scene.IsValid()) { prefab = e; break; }
            if (!e.IsDead && e.gameObject.activeInHierarchy) alive ??= e;
        }
        return prefab ? prefab : alive;
    }

    /// <summary>Ours to walk: its own brain off (it would wander, or run away).</summary>
    static void Take(Enemy e, float health, float damage, bool scale)
    {
        if (e.BehaviorAgent) e.BehaviorAgent.enabled = false;
        hens.Add(new Hen { E = e, Health = health, Damage = damage, Scaled = !scale, NextPeck = Time.time + 0.8f });
    }

    /// <summary>A spot near one of the arena's sides, away from you: they come from all around.</summary>
    static Vector3 Side(Vector3 me)
    {
        for (int tries = 0; tries < 8; tries++)
        {
            var at = Random.Range(0, 4) switch
            {
                0 => new Vector3(Origin.x + 2.5f, Origin.y + Random.Range(2.5f, H - 2.5f), 0f),
                1 => new Vector3(Origin.x + W - 2.5f, Origin.y + Random.Range(2.5f, H - 2.5f), 0f),
                2 => new Vector3(Origin.x + Random.Range(2.5f, W - 2.5f), Origin.y + 2.5f, 0f),
                _ => new Vector3(Origin.x + Random.Range(2.5f, W - 2.5f), Origin.y + H - 2.5f, 0f),
            };
            if (((UnityEngine.Vector2)(at - me)).magnitude > 6f) return at;
        }
        return new Vector3(Origin.x + W / 2f, Origin.y + H - 2.5f, 0f);
    }

    /// <summary>The peck plays the chicken's attack animation, whose end calls Enemy.AttackEnd: written for creatures
    /// that attack (its attack hitbox), which a chicken hasn't got, so it threw. (Also after a
    /// chicken died, when it's no longer one of ours: so any creature without an attack hitbox.) Just the parts it has.</summary>
    /// (A class of its own: Harmony only finds patches in a class marked as one.)
    [HarmonyLib.HarmonyPatch(typeof(Enemy), nameof(Enemy.AttackEnd))]
    static class PeckEnd
    {
        static bool Prefix(Enemy __instance)
        {
            if (__instance.attackHitboxRenderer) return true;   // (a creature that attacks: the game's)
            __instance.isAttacking = false;
            __instance.currentAttack = null;
            return false;
        }
    }

    // ---- on screen ----

    /// <summary>The timer while it runs (top middle); a few seconds after the end, your time and your best.</summary>
    public static void DrawChickens()
    {
        bool showEnd = !chickensOn && Time.unscaledTime - endedAt < 8f;
        if (!chickensOn && !showEnd) return;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x / 2f, vp.WorkPos.y + 70f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.7f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!ImGui.Begin("##chickens", flags)) { ImGui.End(); return; }
        ImGui.SetWindowFontScale(1.6f);
        var gold = new Vector4(1f, 0.8f, 0.35f, 1f);
        if (chickensOn)
        {
            ImGui.TextColored(gold, $"THERE IS NO CHICKEN LEVEL   {Clock(Survived)}");
            ImGui.SetWindowFontScale(1f);
            ImGui.TextDisabled($"wave {chickenWave}   chickens {hens.Count}   best {Clock(bestChickens.Value)}");
        }
        else
        {
            ImGui.TextColored(gold, $"You lasted {Clock(lastTime)}");
            ImGui.SetWindowFontScale(1.2f);
            ImGui.Text(lastTime >= bestChickens.Value - 0.01f ? "A new best!" : $"Best: {Clock(bestChickens.Value)}");
        }
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
    }
}

using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// Fire palm, held like the Mage's beam: while the button's held, fire pours from your hands the way you face (a cone
/// of flame that turns as you aim, you free to move), every foe in it hit a few times a second and set burning (the
/// game's own burning), and a burning foe runs from you until it's out (bosses stand their ground), setting
/// the creatures it bumps into alight too, who panic in turn. Kymia drains each
/// second it's going (the game's drain); let go or run dry and it's out.
/// </summary>
internal static class FirePalm
{
    public const int KymiaPerSecond = 12;
    const float Reach = 4f, HalfAngle = 30f, BurnFor = 4f, HitEvery = 0.25f;

    static PlayerMaster who;
    static ParticleSystem flames;
    static float nextHit;

    /// <summary>The fire on (true: it's going, until Stop).</summary>
    public static bool Start(PlayerMaster p)
    {
        Stop();
        who = p;
        flames = Flames(p);
        nextHit = Time.time;
        Ninja.FireSkill.OnFinishCallback += (System.Action)Stop;   // (however the skill ends, the fire's out)
        return true;
    }

    /// <summary>The fire out (its last flames finish flying).</summary>
    public static void Stop()
    {
        if (flames)
        {
            flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Object.Destroy(flames.gameObject, 1f);
        }
        flames = null;
        who = null;
    }

    /// <summary>Every frame: the flames at your hands, turned your way; the foes in them hit.</summary>
    public static void Tick()
    {
        Panic();
        if (!flames) { if (who) Stop(); return; }
        var p = who;
        if (!p || p.IsDead || p.IsStunned || p.currentState == p.stateStatic || p.currentState == p.stateTrapped || Time.timeScale == 0f)
        {
            Ninja.EndFire();   // (dead, stunned, held, trapped, paused: out)
            return;
        }
        var dir = Aim(p);
        var from = Hand(p);
        flames.transform.position = from + dir * 0.3f;
        flames.transform.rotation = Quaternion.LookRotation(dir, Vector3.back);
        if (Time.time < nextHit) return;
        nextHit = Time.time + HitEvery;
        int amount = Mathf.Max(1, Mathf.RoundToInt(8f + p.characterStats.GetSecondaryStatValue(SecondaryStat.DexterityDamage) * 0.8f));
        foreach (var e in Hook.Foes(mobsToo: true).ToList())
        {
            var to = (Vector2)(e.transform.position + Vector3.up * 0.4f - from);
            if (to.magnitude > Reach || Vector2.Angle(dir, to) > HalfAngle) continue;
            CombatSystem.Resolve(new DamageData(p, e, amount, DamageHitType.Magic, KnockBack.None));
            if (!e || e.IsDead) continue;
            Ignite(e, amount);
        }
    }

    // ---- on fire, they run, and set others alight ----

    static readonly Dictionary<Enemy, (float until, int dps)> burning = new();
    static float nextRun;
    const float RunEvery = 0.4f, RunSpeed = 5f, Spread = 1.2f, SpreadChance = 0.5f, Weaker = 0.75f;

    /// <summary>Set burning (the game's burning) and panicking (bosses burn but stand their ground).</summary>
    static void Ignite(Enemy e, int dps)
    {
        e.EffectsHandler?.HandleEffect(new DamageEffect { Effect = EffectType.Burn, Chance = 100f, DamagePerSec = dps, Duration = BurnFor });
        burning[e] = (Time.time + BurnFor, dps);
    }

    /// <summary>Every frame (even with the fire out): a burning foe runs from you, this way and that, until it's out,
    /// and may set the creatures it runs into alight (a little weaker each time it passes on), who panic too.</summary>
    public static void Panic()
    {
        if (burning.Count == 0 || Time.time < nextRun) return;
        nextRun = Time.time + RunEvery;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        var others = Hook.Foes(mobsToo: true).ToList();
        foreach (var e in burning.Keys.ToList())
        {
            var (until, dps) = burning[e];
            if (!e || e.IsDead || Time.time >= until || !me) { burning.Remove(e); continue; }
            // the fire spreads to whoever's close
            int weaker = Mathf.RoundToInt(dps * Weaker);
            if (weaker >= 1)
                foreach (var o in others)
                    if (o != e && !burning.ContainsKey(o) && ((Vector2)(o.transform.position - e.transform.position)).sqrMagnitude < Spread * Spread && Random.value < SpreadChance)
                        Ignite(o, weaker);
            if (Hook.Boss(e)) continue;
            var away = (Vector2)(e.transform.position - me.transform.position);
            if (away.sqrMagnitude < 0.01f) away = Random.insideUnitCircle;
            var run = (Vector2)(Quaternion.Euler(0f, 0f, Random.Range(-45f, 45f)) * away.normalized);   // (panicking: not quite straight)
            var info = new Enemy.KnockBackInfo { Use = true, Curve = new AnimationCurve(new Keyframe(0f, RunSpeed), new Keyframe(RunEvery, RunSpeed)) };
            e.Knockback(run, info).Forget();
        }
    }

    static Vector3 Aim(PlayerMaster p) => (Vector3)(p.lastLookDir.sqrMagnitude > 0.01f ? p.lastLookDir.normalized : Vector2.right);

    static Vector3 Hand(PlayerMaster p) => p.CenterOfMass ? p.CenterOfMass.position : p.transform.position + Vector3.up * 0.5f;

    /// <summary>The flames: a cone of hot little squares, yellow at the hand, red, then smoke; going until stopped.</summary>
    static ParticleSystem Flames(PlayerMaster p)
    {
        var go = new GameObject("NinjaFirePalm");
        var dir = Aim(p);
        go.transform.position = Hand(p) + dir * 0.3f;
        go.transform.rotation = Quaternion.LookRotation(dir, Vector3.back);   // (a cone fires along its forward)
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 1f;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 8f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.3f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.35f), new Color(1f, 0.45f, 0.1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 400;
        var emission = ps.emission;
        emission.rateOverTime = 200f;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = HalfAngle * 0.8f;
        shape.radius = 0.05f;
        var colour = ps.colorOverLifetime;
        colour.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.95f, 0.5f), 0f), new GradientColorKey(new Color(1f, 0.35f, 0.05f), 0.45f), new GradientColorKey(new Color(0.3f, 0.25f, 0.25f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.5f), new GradientAlphaKey(0f, 1f) });
        colour.color = g;
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 1.6f)));
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = Fx.Material;
        Fx.Above(r, p, 25);
        ps.Play();
        return ps;
    }
}

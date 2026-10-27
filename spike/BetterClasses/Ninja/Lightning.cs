using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// Chain lightning (as Better Streaming's Twitch one): a bolt from your hands to the nearest creature you can see, then
/// on from each to the nearest one not hit yet, until every creature on the screen has been hit (no limit), a hop every
/// few hundredths of a second. Each one hit takes damage, is stunned for 5 seconds and sparks (the game's shock look).
/// </summary>
internal static class Lightning
{
    const float HopEvery = 0.07f, StunFor = 5f, SparkFor = 0.5f;

    static readonly List<(Vector3 from, Enemy to, float at, int amount, PlayerMaster by)> hops = new();
    static readonly List<(Enemy e, float off)> sparking = new();

    public static void Cast(PlayerMaster p)
    {
        int amount = Mathf.Max(1, Mathf.RoundToInt(12f + p.characterStats.GetSecondaryStatValue(SecondaryStat.DexterityDamage) * 1.2f));
        var cam = Camera.main;
        var left = Hook.Foes(mobsToo: true).Where(e => OnScreen(cam, e)).ToList();
        Vector3 from = Hand(p);
        float at = Time.time;
        while (left.Count > 0)
        {
            var near = left.OrderBy(e => ((Vector2)(Middle(e) - from)).sqrMagnitude).First();
            left.Remove(near);
            hops.Add((from, near, at, amount, p));
            from = Middle(near);
            at += HopEvery;
        }
        if (hops.Count == 0) Bolt(Hand(p), Hand(p) + (Vector3)(p.lastLookDir.sqrMagnitude > 0.01f ? p.lastLookDir.normalized : Vector2.right) * 3f);   // (nothing to hit: a crackle into the air)
    }

    static bool OnScreen(Camera cam, Enemy e)
    {
        if (!cam) return true;
        var v = cam.WorldToViewportPoint(e.transform.position);
        return v.z >= 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
    }

    /// <summary>Every frame: the hops that are due, and the sparks that are done.</summary>
    public static void Tick()
    {
        for (int i = hops.Count - 1; i >= 0; i--)
        {
            var h = hops[i];
            if (Time.time < h.at) continue;
            hops.RemoveAt(i);
            if (!h.to || h.to.IsDead) continue;
            Bolt(h.from, Middle(h.to));
            if (h.by) CombatSystem.Resolve(new DamageData(h.by, h.to, h.amount, DamageHitType.Magic, KnockBack.None));
            if (!h.to || h.to.IsDead) continue;
            h.to.Stun(StunFor);
            if (!h.to.appliedEffects.Contains(EffectType.Shock)) { h.to.SetEffect(EffectType.Shock, true); sparking.Add((h.to, Time.time + SparkFor)); }
        }
        for (int i = sparking.Count - 1; i >= 0; i--)
            if (Time.time >= sparking[i].off) { if (sparking[i].e) sparking[i].e.SetEffect(EffectType.Shock, false); sparking.RemoveAt(i); }
    }

    static Vector3 Hand(PlayerMaster p) => p.CenterOfMass ? p.CenterOfMass.position : p.transform.position + Vector3.up * 0.5f;

    static Vector3 Middle(Enemy e)
    {
        if (e.CenterOfMass) return e.CenterOfMass.position;
        var col = e.GetComponentInChildren<Collider2D>();
        return col ? (Vector3)col.bounds.center : e.transform.position + Vector3.up * 0.5f;
    }

    // ---- the bolt: two jagged lines (a blue glow, a white core), re-jagged once, gone in 0.2 s ----

    static readonly Color Glow = new(0.45f, 0.75f, 1f), Core = new(0.9f, 0.97f, 1f);

    static void Bolt(Vector3 a, Vector3 b)
    {
        a.z = b.z = 0f;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        var go = new GameObject("NinjaBolt");
        Line(go.transform, Glow, 0.16f, a, b, me, 50);
        Line(go.transform, Core, 0.05f, a, b, me, 51);
        go.AddComponent<Flicker>().Ends = (a, b);
        Object.Destroy(go, 0.2f);
    }

    static void Line(Transform parent, Color colour, float width, Vector3 a, Vector3 b, Component near, int above)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = Fx.Material;
        lr.startColor = lr.endColor = colour;
        lr.startWidth = lr.endWidth = width;
        lr.useWorldSpace = true;
        lr.numCapVertices = 2;
        Fx.Above(lr, near, above);
        Jag(lr, a, b);
    }

    /// <summary>Straight from a to b, each middle point pushed sideways at random.</summary>
    static void Jag(LineRenderer lr, Vector3 a, Vector3 b)
    {
        const int Points = 9;
        lr.positionCount = Points;
        var side = Vector3.Cross(b - a, Vector3.forward).normalized;
        float spread = Mathf.Min(0.6f, (b - a).magnitude * 0.08f);
        for (int i = 0; i < Points; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)(Points - 1));
            if (i > 0 && i < Points - 1) p += side * Random.Range(-spread, spread);
            lr.SetPosition(i, p);
        }
    }

    sealed class Flicker : MonoBehaviour
    {
        public (Vector3 a, Vector3 b) Ends;
        float at;
        bool done;
        void Start() => at = Time.time + 0.09f;
        void Update()
        {
            if (done || Time.time < at) return;
            done = true;
            foreach (var lr in GetComponentsInChildren<LineRenderer>()) Jag(lr, Ends.a, Ends.b);
        }
    }
}

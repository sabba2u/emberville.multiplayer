using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// The hunter's hook (the Acolyte Tracker's, ours to throw): a hook on a chain flies the way you face. The first foe it
/// reaches takes a hit and is dragged to you, then stunned; nothing caught, the chain reels back. Bosses don't budge
/// (they're hit and stunned where they stand).
/// </summary>
internal static class Hook
{
    const float Range = 7f, Speed = 24f, Reel = 30f, Catch = 0.7f, StunFor = 1.2f;

    sealed class Flying
    {
        public PlayerMaster from;
        public Vector3 dir, tip;
        public float gone;          // (how far out)
        public bool back;
        public Enemy caught;
        public float dragUntil;
        public LineRenderer chain;
        public SpriteRenderer head;
    }

    static readonly List<Flying> hooks = new();
    static Sprite hookSprite;

    /// <summary>Your hook thrown.</summary>
    public static void Throw(PlayerMaster p)
    {
        var dir = (Vector3)(p.lastLookDir.sqrMagnitude > 0.01f ? p.lastLookDir.normalized : Vector2.right);
        var h = new Flying { from = p, dir = dir, tip = Hand(p) };
        var go = new GameObject("NinjaHook");
        h.chain = go.AddComponent<LineRenderer>();
        h.chain.material = Fx.Material;
        h.chain.startColor = h.chain.endColor = new Color(0.55f, 0.55f, 0.6f);
        h.chain.startWidth = h.chain.endWidth = 0.06f;
        h.chain.positionCount = 2;
        h.chain.useWorldSpace = true;
        Fx.Above(h.chain, p, 25);
        if (!hookSprite) hookSprite = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s && s.name.StartsWith("Hookshot projectile"));
        var head = new GameObject("Head");
        head.transform.SetParent(go.transform, false);
        h.head = head.AddComponent<SpriteRenderer>();
        h.head.sprite = hookSprite;
        Fx.Above(h.head, p, 26);
        hooks.Add(h);
    }

    static Vector3 Hand(PlayerMaster p) => p.CenterOfMass ? p.CenterOfMass.position : p.transform.position + Vector3.up * 0.5f;

    /// <summary>Every frame: the hooks out flying, dragging, reeling in.</summary>
    public static void Tick()
    {
        for (int i = hooks.Count - 1; i >= 0; i--)
        {
            var h = hooks[i];
            if (!h.from || !h.chain || !Step(h))
            {
                if (h.chain) Object.Destroy(h.chain.gameObject);
                hooks.RemoveAt(i);
            }
        }
    }

    /// <summary>One frame of a hook; false when it's done.</summary>
    static bool Step(Flying h)
    {
        var hand = Hand(h.from);
        float dt = Time.deltaTime;
        if (h.caught)
        {
            // dragging: the hook stays in the foe until it arrives
            if (!h.caught || h.caught.IsDead || Time.time >= h.dragUntil)
            {
                if (h.caught && !h.caught.IsDead) h.caught.Stun(StunFor);
                h.caught = null;
                h.back = true;
            }
            else h.tip = Middle(h.caught);
        }
        else if (!h.back)
        {
            float step = Speed * dt;
            h.tip += h.dir * step;
            h.gone += step;
            var foe = Foes().Where(e => ((Vector2)(Middle(e) - h.tip)).sqrMagnitude < Catch * Catch)
                .OrderBy(e => ((Vector2)(Middle(e) - h.tip)).sqrMagnitude).FirstOrDefault();
            if (foe) Hit(h, foe, hand);
            else if (h.gone >= Range) h.back = true;
        }
        else
        {
            var toHand = hand - h.tip;
            float step = Reel * dt;
            if (toHand.magnitude <= step) return false;
            h.tip += toHand.normalized * step;
        }
        Draw(h, hand);
        return true;
    }

    static void Hit(Flying h, Enemy foe, Vector3 hand)
    {
        var p = h.from;
        int amount = Mathf.Max(1, Mathf.RoundToInt(6f + p.characterStats.GetSecondaryStatValue(SecondaryStat.DexterityDamage)));
        CombatSystem.Resolve(new DamageData(p, foe, amount, DamageHitType.Pierce, KnockBack.None));
        if (!foe || foe.IsDead) { h.back = true; return; }
        h.caught = foe;
        h.tip = Middle(foe);
        var toMe = hand - foe.transform.position;
        float dist = ((Vector2)toMe).magnitude - 1.1f;   // (to arm's length)
        if (dist <= 0.2f || Boss(foe)) { h.dragUntil = Time.time; return; }
        float secs = dist / 16f;
        var info = new Enemy.KnockBackInfo { Use = true, Curve = new AnimationCurve(new Keyframe(0f, 16f), new Keyframe(secs, 16f)) };
        h.dragUntil = Time.time + secs + 0.05f;
        foe.Knockback(((Vector2)toMe).normalized, info).Forget();
    }

    static void Draw(Flying h, Vector3 hand)
    {
        h.chain.SetPosition(0, hand);
        h.chain.SetPosition(1, h.tip);
        if (h.head)
        {
            h.head.transform.position = h.tip;
            var d = h.caught ? h.tip - hand : (h.back ? hand - h.tip : h.dir);
            h.head.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }
    }

    static Vector3 Middle(Enemy e) => e.transform.position + Vector3.up * 0.4f;

    internal static bool Boss(Enemy e) => e.name.StartsWith("Boss_") || e.name.Contains("Matriarch");

    /// <summary>The creatures you fight: not the townsfolk, not anything on your side (pets, summons); the animals and
    /// other untyped creatures only when <paramref name="mobsToo"/>.</summary>
    internal static IEnumerable<Enemy> Foes(bool mobsToo = false) => EnemiesManager.AllEnemies.Where(e =>
        e && !e.IsDead && e.gameObject.activeInHierarchy && e.initHealth > 0f && !e.name.StartsWith("NPC_") && !e.name.Contains("_Pet_")
        && (mobsToo || e.enemyType != EnemyType.None) && !(Hooks.IsFriendly?.Invoke(e) ?? false));
}

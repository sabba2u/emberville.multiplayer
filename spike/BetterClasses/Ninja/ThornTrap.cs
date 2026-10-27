using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// Thorn trap: set at your feet, a seed in the ground (up to three set at once, each armed for half a minute). The first
/// foe to step on one: sparkly steel spikes shoot up around it, it's hurt and held fast for
/// a moment, then the spikes break up. The spikes are our own drawing: steel, with glints twinkling on the points.
/// </summary>
internal static class ThornTrap
{
    const int Most = 3;
    const float Armed = 30f, Trigger = 0.7f, HoldFor = 2f, Fps = 14f;

    sealed class Trap
    {
        public GameObject go;
        public SpriteRenderer look;
        public float until, sprungAt = -1f;
        public PlayerMaster by;
    }

    static readonly List<Trap> traps = new();
    static Sprite[] frames;   // (the spikes shooting up, then breaking up)
    static int burst;         // (how many of the frames are the bursting)

    public static void Set(PlayerMaster p)
    {
        if (traps.Count >= Most) { Remove(traps[0]); traps.RemoveAt(0); }   // (the oldest goes)
        Load();
        var go = new GameObject("NinjaThornTrap");
        go.transform.position = p.transform.position;
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = frames[0];
        r.color = new Color(1f, 1f, 1f, 0.55f);   // (a seed in the ground: easy to miss)
        r.transform.localScale = Vector3.one * 0.6f;
        Fx.Above(r, p, -5);
        traps.Add(new Trap { go = go, look = r, until = Time.time + Armed, by = p });
        Fx.Puff(go.transform.position, p, 1f);
    }

    /// <summary>Every frame: a foe on a seed springs it; sprung ones play out; old ones wither.</summary>
    public static void Tick()
    {
        if (traps.Count == 0) return;
        List<Enemy> foes = null;
        for (int i = traps.Count - 1; i >= 0; i--)
        {
            var t = traps[i];
            if (!t.go) { traps.RemoveAt(i); continue; }
            if (t.sprungAt < 0f)
            {
                if (Time.time >= t.until) { Remove(t); traps.RemoveAt(i); continue; }
                foes ??= Hook.Foes(mobsToo: true).ToList();
                var at = t.go.transform.position;
                var foe = foes.FirstOrDefault(e => e && ((Vector2)(e.transform.position - at)).sqrMagnitude < Trigger * Trigger);
                if (foe) Spring(t, foe);
                continue;
            }
            int f = (int)((Time.time - t.sprungAt) * Fps);
            // burst up, hold on the last bursting frame while it's held, then crumble
            float holdEnds = burst / Fps + HoldFor;
            if (Time.time - t.sprungAt >= holdEnds) f = burst + (int)((Time.time - t.sprungAt - holdEnds) * Fps);
            else f = Mathf.Min(f, burst - 1);
            if (f >= frames.Length) { Remove(t); traps.RemoveAt(i); continue; }
            t.look.sprite = frames[f];
        }
    }

    static void Spring(Trap t, Enemy foe)
    {
        t.sprungAt = Time.time;
        t.go.transform.position = foe.transform.position;
        t.look.color = Color.white;
        t.look.transform.localScale = Vector3.one;
        Fx.Above(t.look, foe, 5);
        var p = t.by;
        int amount = p ? Mathf.Max(1, Mathf.RoundToInt(10f + p.characterStats.GetSecondaryStatValue(SecondaryStat.DexterityDamage))) : 10;
        if (p) CombatSystem.Resolve(new DamageData(p, foe, amount, DamageHitType.Pierce, KnockBack.None));
        if (foe && !foe.IsDead) foe.Stun(HoldFor);
    }

    static void Remove(Trap t) { if (t.go) Object.Destroy(t.go); }

    // ---- the spikes: our own, steel with glints ----

    static void Load()
    {
        if (frames != null && frames.Length > 0 && frames[0]) return;
        frames = Drawn();
        burst = 6;
    }

    /// <summary>Sparkly steel spikes (our own): six frames shooting up out of the ground, glints twinkling on the
    /// points, then four breaking up into falling sparkles.</summary>
    static Sprite[] Drawn()
    {
        const int W = 24, H = 24;
        var steel = new Color(0.55f, 0.6f, 0.7f);
        var shade = new Color(0.28f, 0.3f, 0.4f);
        var shine = new Color(0.9f, 0.95f, 1f);
        var spark = new Color(1f, 1f, 0.85f);
        int[] xs = { 3, 7, 11, 15, 19 };
        int[] tall = { 9, 14, 18, 13, 10 };
        var list = new List<Sprite>();
        var rng = new System.Random(7);
        for (int f = 0; f < 10; f++)
        {
            var px = new Color[W * H];
            void Put(int x, int y, Color c) { if (x >= 0 && x < W && y >= 0 && y < H) px[y * W + x] = c; }
            float grow = f < 6 ? (f + 1) / 6f : 1f;
            for (int k = 0; k < xs.Length; k++)
            {
                int h = Mathf.Max(1, Mathf.RoundToInt(tall[k] * grow));
                for (int y = 0; y < h; y++)
                {
                    if (f >= 6 && rng.Next(10 - f) == 0) continue;   // (breaking up: gaps)
                    int halfWidth = y < h / 3 ? 1 : 0;               // (wide at the base, a needle at the top)
                    for (int dx = -halfWidth; dx <= halfWidth; dx++) Put(xs[k] + dx, y, dx < 0 ? shade : steel);
                    if (y > 0 && y < h - 1) Put(xs[k] + halfWidth, y, shine);   // (the lit edge)
                }
                // a glint on the point: a little cross, twinkling (on some frames, not others)
                if (f >= 2 && f < 8 && (f + k) % 2 == 0)
                {
                    int ty = Mathf.Min(H - 2, h);
                    Put(xs[k], ty, spark); Put(xs[k] - 1, ty, spark); Put(xs[k] + 1, ty, spark); Put(xs[k], ty + 1, spark); Put(xs[k], ty - 1, spark);
                }
            }
            if (f >= 6)   // (crumbling: sparkles drifting down and out)
                for (int n = 0; n < 6; n++) Put(rng.Next(W), rng.Next(2, H - 2 - (f - 6) * 3), spark);
            var tex = new Texture2D(W, H) { filterMode = FilterMode.Point, hideFlags = HideFlags.DontUnloadUnusedAsset };
            tex.SetPixels(px);
            tex.Apply();
            list.Add(Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.1f), 16f));
        }
        return list.ToArray();
    }
}

using System.Linq;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// NPCs pacing around their spot: pick a random point within "wander" of home that they can walk to in a straight line
/// (nothing that would stop the player: walls, water, closed doors), walk there, wait "pause" seconds, again. Never
/// outside the circle; stuck → back home. They stop while a player is close (so they don't walk off mid-greeting) and
/// while talking. In co-op the host picks the points and sends them (Hooks.SendWander); guests walk to them.
/// </summary>
internal static class Wander
{
    const float Speed = 1.6f, NearPlayer = 2.2f, Radius = 0.3f;

    internal sealed class Plan
    {
        public Vector3? Target;
        public float WaitUntil, CheckAt;
        public Vector3 CheckFrom;
    }

    public static void Tick(Npc n)
    {
        var body = n.Body;
        var plan = n.Walk;
        if (!body.Root) return;
        var pos = body.Root.transform.position;
        var me = LocalPlayer.Get();

        if (Talk.IsOpen && Talk.Speaking == n || (me && (me.transform.position - pos).sqrMagnitude < NearPlayer * NearPlayer) || Hooks.OthersNear(pos, NearPlayer))
        {
            if (plan.Target != null && !Hooks.IsClient()) { plan.Target = null; Hooks.SendWander(n.Def.Id, pos); }
            body.Stop();
            if (me) body.Face(me.transform.position);
            plan.WaitUntil = Time.time + Random.Range(n.Def.PauseMin, n.Def.PauseMax);
            return;
        }
        if (n.Def.Wander <= 0f) return;

        if (plan.Target == null)
        {
            if (Hooks.IsClient() || Time.time < plan.WaitUntil) return;
            plan.Target = Pick(n, pos, me);
            if (plan.Target == null) { plan.WaitUntil = Time.time + Random.Range(n.Def.PauseMin, n.Def.PauseMax); return; }
            plan.CheckAt = Time.time + 3f;
            plan.CheckFrom = pos;
            Hooks.SendWander(n.Def.Id, plan.Target.Value);
        }

        var to = plan.Target.Value - pos;
        to.z = 0f;
        float dist = to.magnitude;
        if (dist < 0.08f)
        {
            body.Stop();
            plan.Target = null;
            plan.WaitUntil = Time.time + Random.Range(n.Def.PauseMin, n.Def.PauseMax);
            return;
        }
        float step = Mathf.Min(Speed * Time.deltaTime, dist);
        if (!Hooks.IsClient() && Blocked(pos, pos + to / dist * step, me)) { plan.Target = n.Home; return; }
        body.Walk(to, step / Mathf.Max(Time.deltaTime, 0.0001f));
        if (Time.time > plan.CheckAt)
        {
            if ((pos - plan.CheckFrom).sqrMagnitude < 0.05f * 0.05f && !Hooks.IsClient()) plan.Target = n.Home;   // stuck: home
            plan.CheckAt = Time.time + 3f;
            plan.CheckFrom = pos;
        }
    }

    /// <summary>A guest: the host sent where this NPC walks to next.</summary>
    public static void Remote(string id, Vector3 to)
    {
        var n = Npcs.Find(id);
        if (n == null) return;
        if ((to - n.Body.Root.transform.position).sqrMagnitude < 0.01f) { n.Walk.Target = null; n.Body.Stop(); return; }
        n.Walk.Target = to;
    }

    static Vector3? Pick(Npc n, Vector3 pos, PlayerMaster me)
    {
        if (Random.value < 0.3f && (pos - n.Home).sqrMagnitude > 0.25f && !Blocked(pos, n.Home, me)) return n.Home;
        for (int i = 0; i < 12; i++)
        {
            var p = n.Home + (Vector3)(Random.insideUnitCircle * n.Def.Wander);
            if ((p - pos).sqrMagnitude < 0.5f * 0.5f) continue;
            if (!Blocked(pos, p, me)) return p;
        }
        return null;
    }

    /// <summary>Would something stop the player walking from a to b (their body's collision layers)?</summary>
    static bool Blocked(Vector3 a, Vector3 b, PlayerMaster me)
    {
        int mask = ~0;
        if (me && me.rb)
        {
            var col = me.rb.GetComponents<Collider2D>().FirstOrDefault(c => c && !c.isTrigger);
            mask = Physics2D.GetLayerCollisionMask(col ? col.gameObject.layer : me.gameObject.layer);
        }
        var d = (Vector2)(b - a);
        float len = d.magnitude;
        var hits = len < 0.001f ? Physics2D.OverlapCircleAll(b, Radius, mask).Select(c => c) : Physics2D.CircleCastAll(a, Radius, d / len, len, mask).Select(h => h.collider);
        foreach (var c in hits.Concat(Physics2D.OverlapCircleAll(b, Radius, mask)))
        {
            if (!c || c.isTrigger) continue;
            if (me && me.rb && c.attachedRigidbody == me.rb) continue;
            if (c.GetComponentInParent<Enemy>() || c.GetComponentInParent<PlayerMaster>()) continue;
            return true;
        }
        return false;
    }
}

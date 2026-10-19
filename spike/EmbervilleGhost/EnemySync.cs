using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Creatures (Emberville's Enemy: monsters AND fauna like chickens, which are Enemy with type None), run by whoever
/// runs the area (Plugin's area owner: the longest-present player there).
///
/// IDs: SceneIds (path in the scene). Randomly spawned dungeon enemies don't match yet (todo).
///
/// Owner: streams each active creature (position, facing, health, animators) ~10x/sec to the others in the area.
/// Puppets: the game's AI is off (behaviour graph blocked, navmesh off, physics kinematic) and creatures follow the
/// owner's stream. The Enemy state machine itself keeps running: its hit/die states are timers, not AI, and give us
/// the death animation, corpse fade and hit flash. Handing an area over just turns the AI back on, from where the
/// creatures are, with the health the puppets already mirrored.
/// Hits: a puppet's hit on a creature becomes a request (EnemyHit); the owner applies it with the game's own
/// ApplyDamage and broadcasts the result (EnemyDamaged); everyone replays it through ApplyDamage behind the
/// applyingHost flag (Duckov / MegabonkTogether pattern), with the hitter's own player as aggressor so they get the XP.
/// </summary>
[HarmonyPatch]
internal static class EnemySync
{
    public static Plugin Plugin;

    const float SendInterval = 0.1f;
    const float MissingHideSeconds = 3f;   // puppet: a creature the owner hasn't mentioned for this long is gone there

    static float sendTimer;
    static bool applyingHost;      // replaying the owner's result: let ApplyDamage through
    static int hitFrom = -1;       // owner: which player's request is being applied (-1 = none)
    static int replayHitter = -1;  // puppet: who dealt the hit we're replaying

    /// <summary>Insert menu: everyone in the area gets the XP of a kill, not just the killer (default on).</summary>
    public static BepInEx.Configuration.ConfigEntry<bool> SharedXp;

    static readonly Dictionary<Enemy, Animator[]> animators = new Dictionary<Enemy, Animator[]>();

    class Remote
    {
        public readonly SnapshotBuffer Snapshots = new SnapshotBuffer { Delay = 0.15f };
        public bool Flip, Dead;
        public float Health;
        public AnimSync.Frame[] Anim;
        public float LastHeard;
    }
    static readonly Dictionary<uint, Remote> remote = new Dictionary<uint, Remote>();
    static readonly Dictionary<Enemy, RigidbodyType2D> frozen = new Dictionary<Enemy, RigidbodyType2D>();   // puppets
    static float puppetSince;

    /// <summary>Someone else runs our area, so its creatures are the real ones.</summary>
    public static bool PuppetMode => Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && !Plugin.IOwnHere;
    public static int Count => frozen.Count;

    static uint IdOf(Enemy e) => SceneIds.Of(e);
    static Enemy Find(uint id) => SceneIds.Find<Enemy>(id);

    static Animator[] AnimatorsOf(Enemy e)
    {
        if (!animators.TryGetValue(e, out var a)) animators[e] = a = AnimSync.Collect(e.gameObject);
        return a;
    }

    public static void Clear()
    {
        Release();
        animators.Clear(); remote.Clear();
        hitFrom = -1; applyingHost = false;
    }

    public static void Tick()
    {
        if (PuppetMode) PuppetTick();
        else { Release(); OwnerTick(); }
    }

    // ---- owner ----

    static void OwnerTick()
    {
        sendTimer += Time.unscaledDeltaTime;
        if (sendTimer < SendInterval || !Plugin.AnyoneElseHere) return;
        sendTimer = 0f;

        var w = new NetDataWriter();
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.isActiveAndEnabled) continue;
            w.Reset();
            w.Put((byte)MsgType.EnemyState);
            w.Put(IdOf(e));
            w.Put(Time.unscaledTimeAsDouble);
            var p = e.transform.position;
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(e.IsFlipped);
            w.Put(e.IsDead);
            w.Put(e.CurrentHealth);
            AnimSync.Write(w, AnimatorsOf(e));
            Plugin.SendToPlace(w, DeliveryMethod.Unreliable);
        }
    }

    /// <summary>Owner: another player hit one of our creatures; apply it for real.</summary>
    public static void OnHit(NetDataReader r, int fromId)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var result = (DamageResult)r.GetByte();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        var e = Find(id);
        if (!e || !e.isActiveAndEnabled || e.IsDead) return;

        var d = new DamageData(RemoteHitter.For(Plugin, fromId), e, amount, hitType, knock, result: result, noStagger: noStagger);
        ApplyFor(fromId, () => e.ApplyDamage(d));
    }

    // ---- puppet ----

    public static void OnState(NetDataReader r)
    {
        uint id = r.GetUInt();
        double t = r.GetDouble();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        bool flip = r.GetBool();
        bool dead = r.GetBool();
        float health = r.GetFloat();
        var anim = AnimSync.Read(r);

        if (!remote.TryGetValue(id, out var s)) remote[id] = s = new Remote();
        s.Snapshots.Add(t, pos, 0f, Time.unscaledTimeAsDouble);
        s.Flip = flip; s.Dead = dead; s.Health = health; s.Anim = anim;
        s.LastHeard = Time.unscaledTime;
    }

    static void PuppetTick()
    {
        if (frozen.Count == 0) puppetSince = Time.unscaledTime;

        double now = Time.unscaledTimeAsDouble;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.isActiveAndEnabled) continue;
            Freeze(e);
            uint id = IdOf(e);
            if (!remote.TryGetValue(id, out var s) || Time.unscaledTime - s.LastHeard > MissingHideSeconds)
            {
                // The owner hasn't got this one (killed before we came, or never spawned there): hide it.
                if (Time.unscaledTime - puppetSince > MissingHideSeconds && !e.IsDead) e.gameObject.SetActive(false);
                continue;
            }
            if (s.Dead && !e.IsDead) { e.gameObject.SetActive(false); continue; }   // died out of our sight
            if (e.IsDead) continue;   // our own death replay plays out (animation, corpse, fade)
            if (!s.Snapshots.Sample(now, out var pos, out _, out _)) continue;
            e.transform.position = pos;
            e.IsFlipped = s.Flip;
            AnimSync.Apply(AnimatorsOf(e), s.Anim);
        }
    }

    /// <summary>AI and physics off: the owner moves this one.</summary>
    static void Freeze(Enemy e)
    {
        if (frozen.ContainsKey(e)) return;
        var rb = e.GetComponent<Rigidbody2D>();
        frozen[e] = rb ? rb.bodyType : RigidbodyType2D.Dynamic;
        if (rb) { rb.linearVelocity = Vector2.zero; rb.bodyType = RigidbodyType2D.Kinematic; }
        e.behaviorGraphAgent.enabled = false;
        e.navMeshAgent?.Disable();
        e.CancelAttack();
    }

    /// <summary>Back to the game's own AI (we run the area now, or we're not connected).</summary>
    static void Release()
    {
        if (frozen.Count == 0) return;
        foreach (var pair in frozen)
        {
            var e = pair.Key;
            if (!e) continue;
            var rb = e.GetComponent<Rigidbody2D>();
            if (rb) rb.bodyType = pair.Value;
            if (e.isActiveAndEnabled && !e.IsDead) e.RestartBehavior();
        }
        frozen.Clear();
        remote.Clear();
        NetLog.Info("Creatures: back to our own AI");
    }

    /// <summary>Puppet: the owner's result of a hit; replay it through the game's code.</summary>
    public static void OnDamaged(NetDataReader r)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var result = (DamageResult)r.GetByte();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        float healthBefore = r.GetFloat();
        int hitter = r.GetInt();
        var e = Find(id);
        if (!e || !e.isActiveAndEnabled || e.IsDead || e.Group == null) return;

        // Our own hit: our player is the aggressor (the game gives us the XP). Someone else's: their stand-in (knockback only).
        var d = new DamageData(RemoteHitter.For(Plugin, hitter), e, amount, hitType, knock, result: result, noStagger: noStagger);
        e.currentHealth = healthBefore;
        applyingHost = true;
        replayHitter = hitter;
        try { e.ApplyDamage(d); }
        finally { applyingHost = false; replayHitter = -1; }
    }

    /// <summary>
    /// Shared XP: a teammate killed this creature where we are, so we get the kill's XP too, the way the game gives it
    /// to a killer (Enemy.ApplyDamage: expRange -> PlayerMaster.GainXP -> class, weapon and armour masteries).
    /// The killer's own copy already got theirs from the game. Kills by nobody (hazards...) give nobody XP, like the game.
    /// </summary>
    static void ShareKillXp(Enemy e, int killer)
    {
        if (SharedXp == null || !SharedXp.Value || killer < 0 || killer == Plugin.MyId) return;
        var me = LocalPlayer.Get();
        if (!me || me.IsDead) return;
        int xp = e.expRange.Random();
        if (xp <= 0) return;
        xp = me.GainXP(xp);
        Bus<UIDropXPEvent>.Invoke(new UIDropXPEvent(e.transform.position, e.transform.position - me.transform.position, xp));
    }

    /// <summary>Owner: which player dealt this hit (the one whose request we're applying, else us if it was our player, -1 = not a player).</summary>
    public static int HitterId(DamageData d)
    {
        if (hitFrom >= 0) return hitFrom;
        var me = LocalPlayer.Get();
        return me && ReferenceEquals(d.Aggressor, me) ? Plugin.MyId : -1;
    }

    /// <summary>Owner: apply another player's hit with them as the hitter.</summary>
    public static void ApplyFor(int playerId, System.Action apply)
    {
        hitFrom = playerId;
        try { apply(); }
        finally { hitFrom = -1; }
    }

    // ---- patches ----

    /// <summary>Puppet: never let the AI graph start (states call RestartGraph on their own).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.RestartGraph)), HarmonyPrefix]
    static bool NoPuppetAi() => !PuppetMode;

    struct Before { public float Health; public bool Dead; }

    /// <summary>
    /// Puppet: hits become requests (only our own player's; the owner runs every other source itself).
    /// Owner: remember health, so the postfix can broadcast what happened.
    /// </summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPrefix]
    static bool BeforeDamage(Enemy __instance, DamageData damageData, out Before __state)
    {
        __state = new Before { Health = __instance.CurrentHealth, Dead = __instance.IsDead };
        if (!PuppetMode || applyingHost) return true;
        if (__instance.IsDead) return false;

        var me = LocalPlayer.Get();
        if (me && ReferenceEquals(damageData.Aggressor, me))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EnemyHit);
            w.Put(IdOf(__instance));
            w.Put(damageData.Amount);
            w.Put((byte)damageData.Result);
            w.Put((byte)damageData.HitType);
            w.Put((byte)damageData.KnockBackForce);
            w.Put(damageData.NoStagger);
            Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        }
        return false;
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPostfix]
    static void AfterDamage(Enemy __instance, DamageData damageData, Before __state)
    {
        if (Plugin == null || !Plugin.Connected || __state.Dead) return;
        if (__instance.IsDead) ShareKillXp(__instance, PuppetMode ? replayHitter : HitterId(damageData));
        if (PuppetMode || !Plugin.AnyoneElseHere) return;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.EnemyDamaged);
        w.Put(IdOf(__instance));
        w.Put(damageData.Amount);
        w.Put((byte)damageData.Result);
        w.Put((byte)damageData.HitType);
        w.Put((byte)damageData.KnockBackForce);
        w.Put(damageData.NoStagger);
        w.Put(__state.Health);
        w.Put(HitterId(damageData));
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }
}

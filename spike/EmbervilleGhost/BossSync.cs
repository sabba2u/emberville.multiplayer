using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Boss extras that aren't creatures, for the Spider Matriarch (spider queen):
///
/// Eggs (SpiderEgg: IDamageable, pooled, not an Enemy). Her AI lays them (SpawnEggAction -> SpiderMatriarch.SpawnEgg ->
/// SetAlive) and later hatches some (DestroyDamageableAction -> Destroy(index > 0) -> "Hatch" -> SpawnSpider), only on
/// the copy that runs her. That copy owns its eggs: it tells the area (EggSpawn), the others lay the same egg there.
/// Hits on someone else's egg are requests to its owner (EggHit), the owner applies them and everyone replays the
/// result (EggDamaged), like breakables. Hatching (EggDestroy) is replayed too, but only the owner's egg makes real
/// spiderlings (EnemySync.ExpectSpawn: they get ids from us and we run them; the others see them as synced creatures).
///
/// Trapped (webbed): she reacts to HER copy's local player only (SpiderMatriarch.HandleEffectApplied: spiders nearby
/// rush to where the player is stuck, faster). A player trapped on a copy that doesn't run her tells the area
/// (PlayerTrapped); her runner does the same around that player.
/// </summary>
[HarmonyPatch]
internal static class BossSync
{
    public static Plugin Plugin;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    // ---- eggs ----

    static readonly Dictionary<SpiderEgg, uint> eggIds = new Dictionary<SpiderEgg, uint>();
    static readonly Dictionary<uint, SpiderEgg> eggsById = new Dictionary<uint, SpiderEgg>();
    static readonly Dictionary<SpiderEgg, int> eggOwner = new Dictionary<SpiderEgg, int>();
    static uint nextEgg;
    static int mirroring;   // laying / hitting / hatching an egg because its owner did

    public static void Clear()
    {
        eggIds.Clear(); eggsById.Clear(); eggOwner.Clear();
        mirroring = 0;
    }

    static void Map(SpiderEgg egg, uint id, int owner)
    {
        if (eggIds.TryGetValue(egg, out uint old)) eggsById.Remove(old);   // pooled egg laid again
        eggIds[egg] = id;
        eggsById[id] = egg;
        eggOwner[egg] = owner;
    }

    /// <summary>Is this egg ours to run? (Not connected, or laid here by a boss we run.)</summary>
    static bool Ours(SpiderEgg egg) => !Connected || !eggOwner.TryGetValue(egg, out int p) || p == Plugin.MyId;

    static SpiderEgg EggFor(uint id) => eggsById.TryGetValue(id, out var egg) && egg ? egg : null;

    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.SetAlive)), HarmonyPostfix]
    static void AfterLaid(SpiderEgg __instance)
    {
        if (!Connected || mirroring > 0) return;
        uint id = ((uint)(Plugin.MyId & 0xFF) << 24) | (++nextEgg & 0xFFFFFF);
        Map(__instance, id, Plugin.MyId);
        if (!Plugin.AnyoneElseHere) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.EggSpawn);
        w.Put(id);
        var p = __instance.transform.position;
        w.Put(p.x); w.Put(p.y);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnEggSpawn(NetDataReader r, int from)
    {
        uint id = r.GetUInt();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        if (EggFor(id) is SpiderEgg have && have.isActiveAndEnabled) return;
        var prefab = EggPrefab();
        if (!prefab) { NetLog.Warn("Can't show a spider egg: no egg prefab here"); return; }
        PoolManager.CreatePool(prefab, "SpiderEggs", 2);
        var go = PoolManager.Get(prefab);
        if (!go || !go.TryGetComponent<SpiderEgg>(out var egg)) return;
        go.transform.position = pos;
        Map(egg, id, from);
        mirroring++;
        try { egg.SetAlive(); }
        finally { mirroring--; }
    }

    static GameObject EggPrefab()
    {
        foreach (var m in Object.FindObjectsByType<SpiderMatriarch>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (m.eggsPrefab) return m.eggsPrefab;
        foreach (var c in Object.FindObjectsByType<MatriarchBossCinematic>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (c.eggsPrefab) return c.eggsPrefab;
        return null;
    }

    /// <summary>Someone else's egg: our player's hits become requests to its owner, nothing else touches it here.</summary>
    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.ApplyDamage)), HarmonyPrefix]
    static bool BeforeEggHit(SpiderEgg __instance, DamageData damageData, out float __state)
    {
        __state = __instance.health;
        if (mirroring > 0 || Ours(__instance)) return true;
        var me = LocalPlayer.Get();
        if (me && ReferenceEquals(damageData.Aggressor, me) && eggIds.TryGetValue(__instance, out uint id))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EggHit);
            w.Put(id);
            w.Put(damageData.Amount);
            w.Put((byte)damageData.HitType);
            w.Put((byte)damageData.KnockBackForce);
            Plugin.SendTo(eggOwner[__instance], w);
        }
        return false;
    }

    /// <summary>Owner: what a hit did to our egg, for everyone to replay.</summary>
    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.ApplyDamage)), HarmonyPostfix]
    static void AfterEggHit(SpiderEgg __instance, DamageData damageData, float __state)
    {
        if (!Connected || mirroring > 0 || !Ours(__instance) || __instance.health == __state || !Plugin.AnyoneElseHere) return;
        if (!eggIds.TryGetValue(__instance, out uint id)) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.EggDamaged);
        w.Put(id);
        w.Put(damageData.Amount);
        w.Put((byte)damageData.HitType);
        w.Put((byte)damageData.KnockBackForce);
        w.Put(__state);
        w.Put(EnemySync.HitterId(damageData));
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnEggHit(NetDataReader r, int from)
    {
        var egg = EggFor(r.GetUInt());
        int amount = r.GetInt();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        if (!egg || !egg.isActiveAndEnabled || egg.IsDead || !Ours(egg)) return;
        var hitter = RemoteHitter.For(Plugin, from) ?? RemoteHitter.At(egg.transform.position);
        var d = new DamageData(hitter, egg, amount, hitType, knock);
        EnemySync.ApplyFor(from, () => egg.ApplyDamage(d));
    }

    public static void OnEggDamaged(NetDataReader r)
    {
        var egg = EggFor(r.GetUInt());
        int amount = r.GetInt();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        float healthBefore = r.GetFloat();
        int hitter = r.GetInt();
        if (!egg || !egg.isActiveAndEnabled || egg.IsDead || Ours(egg)) return;
        var me = LocalPlayer.Get();
        IDamageable aggressor = hitter == Plugin.MyId && me ? me
            : RemoteHitter.For(Plugin, hitter) ?? RemoteHitter.At(egg.transform.position);
        egg.health = healthBefore;
        mirroring++;
        try { egg.ApplyDamage(new DamageData(aggressor, egg, amount, hitType, knock)); }
        finally { mirroring--; }
    }

    /// <summary>Owner: the boss's AI hatched / removed an egg; everyone does the same.</summary>
    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.Destroy), new[] { typeof(int) }), HarmonyPrefix]
    static bool BeforeEggGone(SpiderEgg __instance, int index)
    {
        if (!Connected || mirroring > 0) return true;
        if (!Ours(__instance)) return false;   // its owner decides
        if (Plugin.AnyoneElseHere && eggIds.TryGetValue(__instance, out uint id))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.EggDestroy);
            w.Put(id);
            w.Put(index);
            Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        }
        return true;
    }

    public static void OnEggDestroy(NetDataReader r)
    {
        var egg = EggFor(r.GetUInt());
        int index = r.GetInt();
        if (!egg || !egg.isActiveAndEnabled || egg.IsDead || Ours(egg)) return;   // already broken by a replayed hit
        mirroring++;
        try { egg.Destroy(index); }
        finally { mirroring--; }
    }

    /// <summary>Only the egg's owner makes the real spiderlings; they reach everyone else as synced creatures.</summary>
    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.SpawnSpider)), HarmonyPrefix]
    static bool BeforeHatch(SpiderEgg __instance)
    {
        if (!Connected) return true;
        if (!Ours(__instance)) return false;
        if (__instance.spiderPrefab) EnemySync.ExpectSpawn(__instance.spiderPrefab.name);
        return true;
    }

    // ---- trapped ----

    static int lastTrapFrame = -1;

    [HarmonyPatch(typeof(SpiderMatriarch), nameof(SpiderMatriarch.HandleEffectApplied)), HarmonyPrefix]
    static bool BeforeTrapped(SpiderMatriarch __instance, Effect effect) => TrapHere(__instance, effect, true);

    [HarmonyPatch(typeof(SpiderMatriarch), nameof(SpiderMatriarch.HandleEffectRemoved)), HarmonyPrefix]
    static bool BeforeFreed(SpiderMatriarch __instance, Effect effect) => TrapHere(__instance, effect, false);

    /// <summary>Our player got (un)trapped. Her runner reacts, so: we run her, the game's own code; else tell the area.</summary>
    static bool TrapHere(SpiderMatriarch m, Effect effect, bool on)
    {
        if (!Connected || effect == null || effect.Type != EffectType.Trapped) return true;
        if (!m.enemy || EnemySync.Runs(m.enemy)) return true;
        if (lastTrapFrame != Time.frameCount && Plugin.AnyoneElseHere)   // one message, however many copies of her listen
        {
            lastTrapFrame = Time.frameCount;
            var w = new NetDataWriter();
            w.Put((byte)MsgType.PlayerTrapped);
            w.Put(on);
            var me = LocalPlayer.Get();
            var p = me ? me.transform.position : Vector3.zero;
            w.Put(p.x); w.Put(p.y);
            Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
            React(on, p, Plugin.MyId);   // and here: the spiders we run, her if we own the area
        }
        return false;
    }

    /// <summary>
    /// Everyone in the area: another player is (no longer) stuck in a web; react like the game does for its own player.
    /// She goes for whoever she webbed, as in single player: the area owner hands her to them (her AI only chases the
    /// player of the copy that runs her). The spiders nearby rush there: each copy moves the ones it runs.
    /// </summary>
    public static void OnPlayerTrapped(NetDataReader r, int from)
    {
        bool on = r.GetBool();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        var avatar = Plugin.AvatarOf(from);
        if (avatar) pos = avatar.position;
        React(on, pos, from);
    }

    static void React(bool on, Vector3 pos, int from)
    {
        foreach (var m in Object.FindObjectsByType<SpiderMatriarch>(FindObjectsSortMode.None))
        {
            if (!m.enemy || m.enemy.IsDead) continue;
            if (on) EnemySync.HandTo(m.enemy, from);
            NetLog.Info($"{Plugin.NameOf(from)} is {(on ? "trapped" : "free again")}: the spiders react");
            m.playerTrappedEvent.SendEventMessage(on);
            if (on) RushTo(m, pos);
            else foreach (var e in EnemiesManager.AliveEnemies) e.EffectsHandler.RemoveEffect(EffectType.MoveSpeedBuff);
        }
    }

    /// <summary>SpiderMatriarch.HandleEffectApplied, around a position instead of our own player.</summary>
    static void RushTo(SpiderMatriarch m, Vector3 pos)
    {
        foreach (var group in EnemiesManager.AllGroups.Where(x => Vector3.Distance(x.Area.center, pos) < 30f && x.EnemyTag == m.tagToBuff))
        {
            group.LastPlayerSeenPosition = pos;
            group.TimeToExpirePlayerLastSeen = 15f;
            group.IsForcedToPosition = true;
            foreach (var e in group.Enemies)
            {
                if (e.IsDead || !EnemySync.Runs(e)) continue;   // the others' spiders: their runners move them
                if (!e.TryGetComponent<GraphTrigger>(out var trigger)) trigger = e.gameObject.AddComponent<GraphTrigger>();
                trigger.Trigger("ForceNewPosition");
                e.CurrentMoveSpeed = e.MoveSpeeds.Get("Run");
                e.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.MoveSpeedBuff, DamagePerSec = 1 });
            }
        }
    }
}

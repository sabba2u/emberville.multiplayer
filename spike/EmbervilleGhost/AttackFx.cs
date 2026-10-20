using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Attacks that aren't projectiles (FxSync has those), from creatures and from players: attack objects (slams,
/// explosions, ground spikes, beams, magic circles, damage / heal areas: Attack.attackSpawner, IAttackObject) and
/// creatures' charge-up effects (Attack.buildupVFX).
///
/// Only the attacker's own copy runs the attack (a creature's runner, a player's own game), so only there does
/// Enemy.PerformAttackHit / StartBuildup / PlayerMaster.PerformAttackHit take objects from the game's pool. We catch
/// those (PoolManager.Get while inside one of them) and tell the area: which prefab, where, turned how, aimed where,
/// triggered or not. The others take the same prefab from their pool, owned by their copy of the attacker (our copy
/// of the creature, or the player's stand-in), and trigger it the same way: same visuals and sounds. Its hits do
/// nothing there (PlayerHits blocks hits by creatures this copy doesn't run and by stand-ins): the real ones already
/// travel from the attacker's copy. Held attacks (beams) are released on the copies when the caster lets go.
/// Projectile spawners are skipped: their projectiles come through FxSync one by one.
/// </summary>
[HarmonyPatch]
internal static class AttackFx
{
    public static Plugin Plugin;

    enum Kind : byte { Hit, Buildup, Cancel, Release }

    static MonoBehaviour capturing;   // inside this attacker's PerformAttackHit / StartBuildup (an Enemy we run, or our player)
    static bool endsBuildup;
    static readonly List<(GameObject prefab, GameObject go)> spawned = new List<(GameObject, GameObject)>();
    static readonly Dictionary<Enemy, GameObject> fakeBuildup = new Dictionary<Enemy, GameObject>();
    static readonly Dictionary<GameObject, uint> ourHolds = new Dictionary<GameObject, uint>();     // our held attacks out there
    static readonly Dictionary<ulong, IHoldAttack> theirHolds = new Dictionary<ulong, IHoldAttack>(); // (player, fx id) -> copy
    static uint nextFx;
    static Dictionary<string, GameObject> prefabs;

    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && Plugin.AnyoneElseHere;

    static bool Sending(Enemy e) => Connected && !EnemySync.LocalOnly(e) && EnemySync.Runs(e) && EnemySync.NetId(e) != 0;

    public static void Clear()
    {
        capturing = null;
        spawned.Clear();
        fakeBuildup.Clear();
        ourHolds.Clear();
        theirHolds.Clear();
        prefabs = null;
    }

    // ---- the attacker's copy ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PerformAttackHit)), HarmonyPrefix]
    static void BeforeHit(Enemy __instance) { if (Sending(__instance)) Begin(__instance, __instance.attackBuildVFX); }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PerformAttackHit)), HarmonyFinalizer]
    static void AfterHit(Enemy __instance) => End(__instance, Kind.Hit);

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.StartBuildup)), HarmonyPrefix]
    static void BeforeBuildup(Enemy __instance) { if (Sending(__instance)) Begin(__instance, false); }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.StartBuildup)), HarmonyFinalizer]
    static void AfterBuildup(Enemy __instance) => End(__instance, Kind.Buildup);

    /// <summary>An attack called off mid charge-up: its charge-up effect goes away there, so here too.</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.CancelAttack)), HarmonyPrefix]
    static void BeforeCancel(Enemy __instance)
    {
        if (capturing || !__instance.isAttacking || !__instance.attackBuildVFX || !Sending(__instance)) return;
        spawned.Clear();
        Send(__instance, Kind.Cancel, true);
    }

    /// <summary>Our player's skills and spells (magic circles, ground effects, beams...).</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttackHit)), HarmonyPrefix]
    static void BeforePlayerHit(PlayerMaster __instance)
    {
        if (Connected && ReferenceEquals(__instance, LocalPlayer.Get())) Begin(__instance, false);
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttackHit)), HarmonyFinalizer]
    static void AfterPlayerHit(PlayerMaster __instance) => End(__instance, Kind.Hit);

    /// <summary>We let go of a held attack (beam): let go of its copies too.</summary>
    [HarmonyPatch(typeof(BeamAttack), nameof(BeamAttack.Release)), HarmonyPostfix]
    static void AfterRelease(BeamAttack __instance)
    {
        if (!ourHolds.TryGetValue(__instance.gameObject, out uint fx)) return;
        ourHolds.Remove(__instance.gameObject);
        if (!Connected) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.AttackFx);
        w.Put(true);
        w.Put((uint)Plugin.MyId);
        w.Put((byte)Kind.Release);
        w.Put(false);
        w.Put(0f); w.Put(0f);
        w.Put((byte)0);
        w.Put(fx);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    static void Begin(MonoBehaviour attacker, bool hadBuildup)
    {
        if (capturing) return;
        capturing = attacker;
        endsBuildup = hadBuildup;
        spawned.Clear();
    }

    static void End(MonoBehaviour attacker, Kind kind)
    {
        if (capturing != attacker) return;
        capturing = null;
        try { Send(attacker, kind, endsBuildup); }
        finally { spawned.Clear(); }
    }

    [HarmonyPatch(typeof(PoolManager), nameof(PoolManager.Get)), HarmonyPostfix]
    static void OnPoolGet(GameObject prefab, GameObject __result)
    {
        if (capturing && prefab && __result) spawned.Add((prefab, __result));
    }

    static bool Mirrorable(GameObject go) =>
        !go.GetComponent<Projectile>() && !go.GetComponent<ProjectileSpawner>() && !go.GetComponent<ProjectileSpawnerCircularSpread>();

    static void Send(MonoBehaviour attacker, Kind kind, bool endBuildup)
    {
        int count = 0;
        foreach (var s in spawned) if (s.go && Mirrorable(s.go)) count++;
        if (count == 0 && !endBuildup) return;

        var player = attacker as PlayerMaster;
        var attack = player ? player.currentAttack : ((Enemy)attacker).currentAttack;
        Vector2 dir = player ? player.lookDir : Vector2.zero;
        bool trigger = kind == Kind.Hit && attack && !attack.preventTrigger;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.AttackFx);
        w.Put(player);
        w.Put(player ? (uint)Plugin.MyId : EnemySync.NetId((Enemy)attacker));
        w.Put((byte)kind);
        w.Put(endBuildup);
        w.Put(dir.x); w.Put(dir.y);
        w.Put((byte)count);
        foreach (var (prefab, go) in spawned)
        {
            if (!go || !Mirrorable(go)) continue;
            uint fx = 0;
            if (player && go.GetComponent<IHoldAttack>() != null) ourHolds[go] = fx = ++nextFx;
            w.Put(prefab.name);
            var t = go.transform;
            w.Put(t.position.x); w.Put(t.position.y);
            w.Put(t.eulerAngles.z);
            w.Put(t.localScale.x);
            w.Put(trigger && go.GetComponent<IAttackObject>() != null);
            w.Put(fx);
        }
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    // ---- everyone else ----

    public static void OnAttackFx(NetDataReader r)
    {
        bool fromPlayer = r.GetBool();
        uint id = r.GetUInt();
        var kind = (Kind)r.GetByte();
        bool endBuildup = r.GetBool();
        var dir = new Vector2(r.GetFloat(), r.GetFloat());
        int count = r.GetByte();

        if (kind == Kind.Release)
        {
            uint fx = r.GetUInt();
            if (theirHolds.TryGetValue(((ulong)id << 32) | fx, out var hold))
            {
                theirHolds.Remove(((ulong)id << 32) | fx);
                if (hold != null && hold.GameObject) hold.Release();
            }
            return;
        }

        // Who it belongs to here: our copy of the creature, or the player's stand-in.
        Enemy e = fromPlayer ? null : EnemySync.FindById(id);
        IDamageable owner = fromPlayer ? RemoteHitter.For(Plugin, (int)id) : e;
        if (e && endBuildup && fakeBuildup.TryGetValue(e, out var old))
        {
            if (old) old.SetActive(false);
            fakeBuildup.Remove(e);
        }
        bool ok = fromPlayer ? owner != null && (int)id != Plugin.MyId : e && e.isActiveAndEnabled && !EnemySync.Runs(e);
        for (int i = 0; i < count; i++)
        {
            string name = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            float angle = r.GetFloat();
            float scaleX = r.GetFloat();
            bool trigger = r.GetBool();
            uint fx = r.GetUInt();
            if (!ok) continue;
            var prefab = FindPrefab(name);
            if (!prefab) { NetLog.Warn($"Can't show attack '{name}': no such prefab here"); continue; }
            var go = Spawn(owner, e ? e.currentAttack : null, prefab, pos, angle, scaleX, dir, trigger);
            if (!go) continue;
            if (kind == Kind.Buildup && e)
            {
                if (fakeBuildup.TryGetValue(e, out var prev) && prev && prev != go) prev.SetActive(false);
                fakeBuildup[e] = go;
            }
            if (fx != 0 && go.GetComponent<IHoldAttack>() is IHoldAttack hold) theirHolds[((ulong)id << 32) | fx] = hold;
        }
    }

    /// <summary>The game's own order (PerformAttackHit): from the pool, owner, on, placed, aimed, damage data, trigger.</summary>
    static GameObject Spawn(IDamageable owner, Attack attack, GameObject prefab, Vector3 pos, float angle, float scaleX, Vector2 dir, bool trigger)
    {
        PoolManager.CreatePool(prefab, "EmbervilleGhost_Fx", 2);
        var go = PoolManager.Get(prefab);
        if (!go) return null;
        var ao = go.GetComponent<IAttackObject>();
        if (ao != null) ao.Owner = owner;
        go.SetActive(true);
        var t = go.transform;
        t.position = pos;
        t.eulerAngles = new Vector3(t.eulerAngles.x, t.eulerAngles.y, angle);
        var scale = t.localScale;
        if (scaleX != 0f) { scale.x = scaleX; t.localScale = scale; }
        if (ao is IDirectionAttackSpawner aimed && dir != Vector2.zero) aimed.SetDirection(dir);
        // The copy of the attacker as the aggressor: PlayerHits refuses its hits (the attacker's copy does the real ones).
        if (ao is IDamageDataAttack dd) dd.DamageData = new DamageData { Aggressor = owner, Attack = attack };
        if (trigger) ao.Trigger();
        return go;
    }

    /// <summary>Attack objects and charge-up effects of every Attack asset loaded, by prefab name.</summary>
    static GameObject FindPrefab(string name)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (prefabs == null || attempt == 1)
            {
                prefabs = new Dictionary<string, GameObject>();
                foreach (var a in Resources.FindObjectsOfTypeAll<Attack>())
                {
                    if (a.attackSpawner) prefabs[a.attackSpawner.name] = a.attackSpawner;
                    if (a.buildupVFX) prefabs[a.buildupVFX.name] = a.buildupVFX;
                }
            }
            if (prefabs.TryGetValue(name, out var found) && found) return found;
        }
        return null;
    }
}

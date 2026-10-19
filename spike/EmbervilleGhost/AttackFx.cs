using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Creature attacks that aren't projectiles (FxSync has those): attack objects (slams, explosions, ground spikes,
/// beams, damage/heal areas: Attack.attackSpawner, IAttackObject) and charge-up effects (Attack.buildupVFX).
///
/// Only the creature's runner runs its AI, so only there does Enemy.PerformAttackHit / StartBuildup take objects from
/// the game's pool. We catch those (PoolManager.Get while inside one of them) and tell the area: which prefab, where,
/// turned how, triggered or not. The others take the same prefab from their pool, owned by their copy of the creature,
/// and trigger it the same way: same visuals and sounds. Its hits do nothing there (PlayerHits blocks any hit by a
/// creature this copy doesn't run): the real ones already travel as PlayerHit from the runner.
/// Projectile spawners are skipped: their projectiles come through FxSync one by one.
/// </summary>
[HarmonyPatch]
internal static class AttackFx
{
    public static Plugin Plugin;

    enum Kind : byte { Hit, Buildup, Cancel }

    static Enemy capturing;   // runner: inside this creature's PerformAttackHit / StartBuildup
    static bool endsBuildup;
    static readonly List<(GameObject prefab, GameObject go)> spawned = new List<(GameObject, GameObject)>();
    static readonly Dictionary<Enemy, GameObject> fakeBuildup = new Dictionary<Enemy, GameObject>();
    static Dictionary<string, GameObject> prefabs;

    static bool Sending(Enemy e) =>
        Plugin != null && Plugin.Connected && Plugin.MyId >= 0 && Plugin.AnyoneElseHere
        && !EnemySync.LocalOnly(e) && EnemySync.Runs(e) && EnemySync.NetId(e) != 0;

    public static void Clear()
    {
        capturing = null;
        spawned.Clear();
        fakeBuildup.Clear();
        prefabs = null;
    }

    // ---- runner ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PerformAttackHit)), HarmonyPrefix]
    static void BeforeHit(Enemy __instance) => Begin(__instance, __instance.attackBuildVFX);

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PerformAttackHit)), HarmonyFinalizer]
    static void AfterHit(Enemy __instance) => End(__instance, Kind.Hit);

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.StartBuildup)), HarmonyPrefix]
    static void BeforeBuildup(Enemy __instance) => Begin(__instance, false);

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

    static void Begin(Enemy e, bool hadBuildup)
    {
        if (capturing || !Sending(e)) return;
        capturing = e;
        endsBuildup = hadBuildup;
        spawned.Clear();
    }

    static void End(Enemy e, Kind kind)
    {
        if (capturing != e) return;
        capturing = null;
        try { Send(e, kind, endsBuildup); }
        finally { spawned.Clear(); }
    }

    [HarmonyPatch(typeof(PoolManager), nameof(PoolManager.Get)), HarmonyPostfix]
    static void OnPoolGet(GameObject prefab, GameObject __result)
    {
        if (capturing && prefab && __result) spawned.Add((prefab, __result));
    }

    static bool Mirrorable(GameObject go) =>
        !go.GetComponent<Projectile>() && !go.GetComponent<ProjectileSpawner>() && !go.GetComponent<ProjectileSpawnerCircularSpread>();

    static void Send(Enemy e, Kind kind, bool endBuildup)
    {
        int count = 0;
        foreach (var s in spawned) if (s.go && Mirrorable(s.go)) count++;
        if (count == 0 && !endBuildup) return;

        var attack = e.currentAttack;
        bool trigger = kind == Kind.Hit && attack && !attack.preventTrigger;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.AttackFx);
        w.Put(EnemySync.NetId(e));
        w.Put((byte)kind);
        w.Put(endBuildup);
        w.Put((byte)count);
        foreach (var (prefab, go) in spawned)
        {
            if (!go || !Mirrorable(go)) continue;
            w.Put(prefab.name);
            var t = go.transform;
            w.Put(t.position.x); w.Put(t.position.y);
            w.Put(t.eulerAngles.z);
            w.Put(t.localScale.x);
            w.Put(trigger && go.GetComponent<IAttackObject>() != null);
        }
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    // ---- everyone else ----

    public static void OnAttackFx(NetDataReader r)
    {
        var e = EnemySync.FindById(r.GetUInt());
        var kind = (Kind)r.GetByte();
        bool endBuildup = r.GetBool();
        int count = r.GetByte();
        if (e && endBuildup && fakeBuildup.TryGetValue(e, out var old))
        {
            if (old) old.SetActive(false);
            fakeBuildup.Remove(e);
        }
        for (int i = 0; i < count; i++)
        {
            string name = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            float angle = r.GetFloat();
            float scaleX = r.GetFloat();
            bool trigger = r.GetBool();
            if (!e || !e.isActiveAndEnabled || EnemySync.Runs(e)) continue;
            var prefab = FindPrefab(name);
            if (!prefab) { NetLog.Warn($"Can't show attack '{name}': no such prefab here"); continue; }
            Spawn(e, prefab, kind, pos, angle, scaleX, trigger);
        }
    }

    /// <summary>The game's own order (Enemy.PerformAttackHit): from the pool, owner, on, placed, damage data, trigger.</summary>
    static void Spawn(Enemy e, GameObject prefab, Kind kind, Vector3 pos, float angle, float scaleX, bool trigger)
    {
        PoolManager.CreatePool(prefab, "EmbervilleGhost_Fx", 2);
        var go = PoolManager.Get(prefab);
        if (!go) return;
        var ao = go.GetComponent<IAttackObject>();
        if (ao != null) ao.Owner = e;
        go.SetActive(true);
        var t = go.transform;
        t.position = pos;
        t.eulerAngles = new Vector3(t.eulerAngles.x, t.eulerAngles.y, angle);
        var scale = t.localScale;
        if (scaleX != 0f) { scale.x = scaleX; t.localScale = scale; }
        // Our copy of the creature as the aggressor: PlayerHits refuses its hits (the runner's are the real ones).
        if (ao is IDamageDataAttack dd) dd.DamageData = new DamageData { Aggressor = e, Attack = e.currentAttack };
        if (trigger) ao.Trigger();
        if (kind == Kind.Buildup)
        {
            if (fakeBuildup.TryGetValue(e, out var old) && old && old != go) old.SetActive(false);
            fakeBuildup[e] = go;
        }
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

using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleGhost;

/// <summary>
/// Host-owned breakables: boxes, crates, pots, beds... everything that is a DamageableObject (DamageableType.Props;
/// HP or a hit count, its prefab's onDestroyEvent plays the break and drops the loot).
/// Same pattern as EnemySync, run by the area's owner: matching ids from the scene path (SceneIds); a puppet's hit is
/// a request (PropHit), the owner applies it with the game's own ApplyDamage and broadcasts the result (PropDamaged),
/// everyone replays it through ApplyDamage (applyingHost). Loot from a break only drops on the owner (DropSync
/// blocks droppers under a DamageableObject on puppets) and reaches everyone as synced drops.
/// A player arriving later gets the list of what's already broken here.
/// </summary>
[HarmonyPatch]
internal static class PropSync
{
    public static Plugin Plugin;

    static bool applyingHost;
    // Everyone keeps this (broken by us, replayed, or told on arrival), so whoever runs the area next can tell newcomers.
    static readonly HashSet<uint> broken = new HashSet<uint>();
    static string brokenScene = "";

    static void MarkBroken(uint id)
    {
        string scene = LocalPlayer.Scene;
        if (scene != brokenScene) { broken.Clear(); brokenScene = scene; }
        broken.Add(id);
    }

    static bool Puppet => EnemySync.PuppetMode;

    public static void Clear()
    {
        broken.Clear();
        brokenScene = "";
        applyingHost = false;
    }

    // ---- host ----

    /// <summary>Host: a client hit a breakable.</summary>
    public static void OnHit(NetDataReader r, int fromId)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        var prop = SceneIds.Find<DamageableObject>(id);
        if (!prop || !prop.isActiveAndEnabled || prop.health <= 0f) return;
        var d = new DamageData(RemoteHitter.For(Plugin, fromId), prop, amount, hitType, knock);
        EnemySync.ApplyFor(fromId, () => prop.ApplyDamage(d));
    }

    /// <summary>Owner: what's already broken in this scene, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (broken.Count == 0 || brokenScene != LocalPlayer.Scene) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PropBroken);
        w.Put(broken.Count);
        foreach (uint id in broken) w.Put(id);
        Plugin.SendTo(playerId, w);
    }

    // ---- client ----

    public static void OnDamaged(NetDataReader r)
    {
        uint id = r.GetUInt();
        int amount = r.GetInt();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        float healthBefore = r.GetFloat();
        int hitter = r.GetInt();
        if (!Puppet) return;
        var prop = SceneIds.Find<DamageableObject>(id);
        if (!prop || !prop.isActiveAndEnabled || prop.health <= 0f) return;

        prop.health = healthBefore;
        var d = new DamageData(RemoteHitter.For(Plugin, hitter), prop, amount, hitType, knock);
        applyingHost = true;
        try { prop.ApplyDamage(d); }
        finally { applyingHost = false; }
    }

    /// <summary>Client: these were broken before we arrived; break them quietly (their loot already lies here as synced drops).</summary>
    public static void OnBroken(NetDataReader r)
    {
        int n = r.GetInt();
        int done = 0;
        for (int i = 0; i < n; i++)
        {
            uint id = r.GetUInt();
            MarkBroken(id);
            var prop = SceneIds.Find<DamageableObject>(id);
            if (!prop || prop.health <= 0f) continue;
            applyingHost = true;
            try { prop.Destroy(); done++; }
            finally { applyingHost = false; }
        }
        if (done > 0) NetLog.Info($"{done} things were already broken here");
    }

    // ---- patches ----

    struct Before { public float Health; }

    /// <summary>Client: our player's hits become requests; every other source is the host's business.</summary>
    [HarmonyPatch(typeof(DamageableObject), nameof(DamageableObject.ApplyDamage)), HarmonyPrefix]
    static bool BeforeDamage(DamageableObject __instance, DamageData damageData, out Before __state)
    {
        __state = new Before { Health = __instance.health };
        if (!Puppet || applyingHost) return true;
        if (__instance.health <= 0f || __instance.ignorePlayer || !__instance.receiveDamage) return false;

        var me = LocalPlayer.Get();
        if (me && ReferenceEquals(damageData.Aggressor, me))
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.PropHit);
            w.Put(SceneIds.Of(__instance));
            w.Put(damageData.Amount);
            w.Put((byte)damageData.HitType);
            w.Put((byte)damageData.KnockBackForce);
            Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
        }
        return false;
    }

    [HarmonyPatch(typeof(DamageableObject), nameof(DamageableObject.ApplyDamage)), HarmonyPostfix]
    static void AfterDamage(DamageableObject __instance, DamageData damageData, Before __state)
    {
        if (Plugin == null || !Plugin.Connected || __state.Health <= 0f || __instance.health == __state.Health) return;
        uint id = SceneIds.Of(__instance);
        if (__instance.health <= 0f) MarkBroken(id);
        if (Puppet || !Plugin.AnyoneElseHere) return;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.PropDamaged);
        w.Put(id);
        w.Put(damageData.Amount);
        w.Put((byte)damageData.HitType);
        w.Put((byte)damageData.KnockBackForce);
        w.Put(__state.Health);
        w.Put(EnemySync.HitterId(damageData));
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }
}

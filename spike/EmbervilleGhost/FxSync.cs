using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Projectiles everyone can see (todo #5, attack visuals), Duckov's "fake bullets" idea.
///
/// A projectile only exists on the copy that fired it: the area owner for creature shots, the player for their own.
/// When one appears (Projectile.OnEnable: the game sets position, Direction, Range and damage data just before
/// SetActive(true)), that copy tells everyone in the area which prefab, where, which way, how far. The others spawn the
/// same prefab through the game's pool with preventDamage on: it flies the same path, stops on walls / players and
/// plays its hit effect, but never hurts anyone. The real damage already travels its own way (PlayerHits, EnemySync).
/// The pool is shared with the game, so a harmless copy gets its real preventDamage back when the game reuses it.
/// </summary>
[HarmonyPatch]
internal static class FxSync
{
    public static Plugin Plugin;

    static bool spawningFake;
    static readonly Dictionary<Projectile, bool> faked = new Dictionary<Projectile, bool>();   // fake -> its own preventDamage
    static Dictionary<string, GameObject> prefabs;

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnEnable)), HarmonyPostfix]
    static void OnProjectile(Projectile __instance)
    {
        if (spawningFake) return;
        if (faked.TryGetValue(__instance, out bool real)) { __instance.preventDamage = real; faked.Remove(__instance); }   // the game reuses it for real
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0 || !Plugin.AnyoneElseHere) return;

        var shooter = __instance.damageData.Aggressor ?? __instance.Owner;
        bool ours = shooter is Enemy en ? EnemySync.Runs(en) : ReferenceEquals(shooter, LocalPlayer.Get());
        if (!ours) return;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.ProjectileFx);
        w.Put(Clean(__instance.gameObject.name));
        var p = __instance.transform.position;
        w.Put(p.x); w.Put(p.y);
        var d = __instance.Direction;
        w.Put(d.x); w.Put(d.y);
        w.Put(__instance.Range);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    public static void OnProjectileFx(NetDataReader r)
    {
        string name = r.GetString();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        var dir = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        float range = r.GetFloat();
        var prefab = FindPrefab(name);
        if (!prefab) return;

        PoolManager.CreatePool(prefab, "EmbervilleGhost_Fx", 4);
        var go = PoolManager.Get(prefab);
        if (!go || !go.TryGetComponent<Projectile>(out var proj)) return;
        if (!faked.ContainsKey(proj)) faked[proj] = proj.preventDamage;
        proj.preventDamage = true;
        proj.TargetToFollow = null;
        proj.Owner = null;
        proj.damageData = default;
        proj.transform.position = pos;
        proj.Direction = dir;
        proj.Range = range;
        spawningFake = true;
        try { go.SetActive(true); }
        finally { spawningFake = false; }
    }

    public static void Clear()
    {
        faked.Clear();
        prefabs = null;
    }

    /// <summary>"Spit_Projectile(Clone)" -> "Spit_Projectile".</summary>
    static string Clean(string n) => n.Replace("(Clone)", "").Trim();

    /// <summary>The projectile prefab with that name among the game's loaded assets (not scene objects).</summary>
    static GameObject FindPrefab(string name)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (prefabs == null || attempt == 1)
            {
                prefabs = new Dictionary<string, GameObject>();
                foreach (var p in Resources.FindObjectsOfTypeAll<Projectile>())
                    if (p && !p.gameObject.scene.IsValid() && !prefabs.ContainsKey(p.gameObject.name)) prefabs[p.gameObject.name] = p.gameObject;
            }
            if (prefabs.TryGetValue(name, out var found) && found) return found;
        }
        return null;
    }
}

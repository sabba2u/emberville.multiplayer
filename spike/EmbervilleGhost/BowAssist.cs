using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Bow lock-on: an arrow (bow or crossbow) we shoot curves onto the closest living creature roughly where we aimed
/// (within LockAngle of the shot, LockRange away). It's the game's own homing (Projectile.TargetToFollow, which some
/// creatures' shots use), turned gentler, so near misses hit but a shot the other way still goes where it's aimed.
/// /bowlock turns it on and off (remembered).
/// </summary>
internal static class BowAssist
{
    static ConfigEntry<bool> on;
    const float LockRange = 14f, LockAngle = 60f, TurnSpeed = 7f;
    static readonly HashSet<Projectile> ours = new HashSet<Projectile>();

    public static void Bind(ConfigFile config) =>
        on = config.Bind("Combat", "BowLockOn", true, "Arrows from your bow / crossbow curve onto the closest creature near where you aim");

    public static void Toggle()
    {
        on.Value = !on.Value;
        NetLog.Info(on.Value ? "Bow lock-on: on (arrows curve onto the creature nearest your aim)" : "Bow lock-on: off", toast: true);
    }

    static bool HoldingBow(PlayerMaster me) =>
        me && me.currentWeapon != null && (me.currentWeapon.weaponCategory == WeaponCategory.Bows || me.currentWeapon.weaponCategory == WeaponCategory.Crossbows);

    /// <summary>A shot just fired (it's set up and switched on in the same frame): ours, from a bow? Pick its target.</summary>
    [HarmonyPatch(typeof(Projectile), "Update")]
    static class Lock
    {
        static void Prefix(Projectile __instance)
        {
            if (!on.Value || __instance.TargetToFollow || ours.Contains(__instance)) return;
            if ((float)__instance.lived > 0.15f) return;   // only fresh shots
            var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
            var me = gm ? gm.player : null;
            if (!me || !HoldingBow(me)) return;
            var who = __instance.damageData.Aggressor;
            bool mine = who is PlayerMaster p ? p == me
                : who == null && ((Vector2)(__instance.transform.position - me.transform.position)).sqrMagnitude < 2.5f * 2.5f;   // no attacker set: one leaving our bow
            if (!mine) return;
            ours.Add(__instance);
            var target = Closest(__instance.transform.position, __instance.Direction);
            if (!target) return;
            __instance.TargetToFollow = target;
            __instance.turnSpeed = TurnSpeed;
        }
    }

    /// <summary>Pooled: a shot coming back for someone else mustn't keep our target.</summary>
    [HarmonyPatch(typeof(Projectile), "DisableProjectile")]
    static class Release
    {
        static void Postfix(Projectile __instance)
        {
            if (!ours.Remove(__instance)) return;
            __instance.TargetToFollow = null;
            __instance.turnSpeed = 20f;   // the game's default
        }
    }

    static Transform Closest(Vector3 from, Vector3 dir)
    {
        Transform best = null;
        float bestD = LockRange * LockRange;
        var d2 = ((Vector2)dir).normalized;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || e.IsDead || !e.gameObject.activeInHierarchy) continue;
            var at = e.CenterOfMass ? e.CenterOfMass.position : e.transform.position;
            Vector2 to = at - from;
            float d = to.sqrMagnitude;
            if (d > bestD || d < 0.01f) continue;
            if (Vector2.Angle(d2, to) > LockAngle) continue;
            bestD = d;
            best = e.CenterOfMass ? e.CenterOfMass : e.transform;
        }
        return best;
    }
}

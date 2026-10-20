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
    static ConfigEntry<bool> on, trueAim;
    static ConfigEntry<float> angle, range;
    const float TurnSpeed = 9f;
    static readonly HashSet<Projectile> ours = new HashSet<Projectile>();

    public static void Bind(ConfigFile config)
    {
        // a new key (it was "BowLockOn", on): lock-on now starts off for everyone, until /bowlock or the Insert menu
        on = config.Bind("Combat", "BowLock", false, "Arrows from your bow / crossbow curve onto the closest creature near where you aim (/bowlock, or the Insert menu)");
        angle = config.Bind("Combat", "BowLockAngle", 20f, "How far off your aim a creature can be and still be locked on to (degrees either side; /bowlock 15)");
        range = config.Bind("Combat", "BowLockRange", 14f, "How far away a creature can be and still be locked on to");
        trueAim = config.Bind("Combat", "BowTrueAim", true, "Arrows leave in exactly the direction you aim (the game may snap a bow's arrow to up / right / down / left)");
    }

    /// <summary>The Insert menu: lock-on on / off, its cone and range; true aim.</summary>
    public static void DrawMenu()
    {
        ImGuiNET.ImGui.Separator();
        bool v = on.Value;
        if (ImGuiNET.ImGui.Checkbox("Bow lock-on: arrows curve onto the creature nearest your aim", ref v)) on.Value = v;
        if (on.Value)
        {
            float a = angle.Value, r = range.Value;
            ImGuiNET.ImGui.SetNextItemWidth(160);
            if (ImGuiNET.ImGui.SliderFloat("Cone (degrees either side)##bowlock", ref a, 2f, 90f, "%.0f")) angle.Value = a;
            ImGuiNET.ImGui.SetNextItemWidth(160);
            if (ImGuiNET.ImGui.SliderFloat("Range##bowlock", ref r, 4f, 25f, "%.0f")) range.Value = r;
        }
        bool t = trueAim.Value;
        if (ImGuiNET.ImGui.Checkbox("Bows aim true (any direction, not just up / right / down / left)", ref t)) trueAim.Value = t;
    }

    /// <summary>/bowlock 15: the lock-on cone, degrees either side of the aim.</summary>
    public static void SetAngle(float degrees)
    {
        angle.Value = Mathf.Clamp(degrees, 2f, 90f);
        on.Value = true;
        NetLog.Info($"Bow lock-on: {angle.Value:0}° either side of your aim ({angle.Value * 2f:0}° wide), up to {range.Value:0} steps", toast: true);
    }

    public static void Toggle()
    {
        on.Value = !on.Value;
        NetLog.Info(on.Value ? $"Bow lock-on: on ({angle.Value:0}° either side of your aim; /bowlock 15 to change)" : "Bow lock-on: off", toast: true);
    }

    static bool HoldingBow(PlayerMaster me) =>
        me && me.currentWeapon != null && (me.currentWeapon.weaponCategory == WeaponCategory.Bows || me.currentWeapon.weaponCategory == WeaponCategory.Crossbows);

    /// <summary>A shot just fired (it's set up and switched on in the same frame): ours, from a bow? Pick its target.</summary>
    [HarmonyPatch(typeof(Projectile), "Update")]
    static class Lock
    {
        static void Prefix(Projectile __instance)
        {
            if (!(on.Value || trueAim.Value) || __instance.TargetToFollow || ours.Contains(__instance) || trapShots.Contains(__instance)) return;
            if ((float)__instance.lived > 0.15f) return;   // only fresh shots
            var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
            var me = gm ? gm.player : null;
            if (!me || !HoldingBow(me)) return;
            var who = __instance.damageData.Aggressor;
            bool mine = who is PlayerMaster p ? p == me
                : who == null && ((Vector2)(__instance.transform.position - me.transform.position)).sqrMagnitude < 2.5f * 2.5f;   // no attacker set: one leaving our bow
            if (!mine) return;
            ours.Add(__instance);
            // true aim: along the exact aim, not the game's four-way snap (only bow arrows we shot, on their first frame)
            if (trueAim.Value && me.lastLookDir.sqrMagnitude > 0.01f)
                __instance.Direction = (Vector3)me.lastLookDir.normalized;
            if (!on.Value) return;
            var target = Closest(__instance.transform.position, __instance.Direction);
            if (!target) return;
            __instance.TargetToFollow = target;
            __instance.turnSpeed = TurnSpeed;
        }
    }

    /// <summary>
    /// Homing only bends a shot that's still on its way: once the target is off to the side or behind (it flew past,
    /// or the creature dodged), or the shot has already turned as far as a near miss needs, it flies straight on. The
    /// game's homing would keep steering and bring it back round in a loop.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "Update")]
    static class LetGo
    {
        const float GiveUpAngle = 70f, MaxTurn = 50f;

        static void Postfix(Projectile __instance)
        {
            var target = __instance.TargetToFollow;
            if (!target || !ours.Contains(__instance)) return;
            Vector2 dir = __instance.Direction, to = target.position - __instance.transform.position;
            if (!fired.TryGetValue(__instance, out var start)) fired[__instance] = start = dir;
            if (to.sqrMagnitude < 0.04f || Vector2.Angle(dir, to) > GiveUpAngle || Vector2.Angle(start, dir) > MaxTurn)
                __instance.TargetToFollow = null;
        }
    }

    static readonly Dictionary<Projectile, Vector2> fired = new Dictionary<Projectile, Vector2>();

    // ---- trap arrows (wall shooters: no attacker on them either) are never ours ----

    static readonly HashSet<Projectile> trapShots = new HashSet<Projectile>();
    static bool trapFiring;

    [HarmonyPatch(typeof(ShooterTrap), "ShootFromPoint")]
    static class TrapFiring
    {
        static void Prefix() => trapFiring = true;
        static void Postfix() => trapFiring = false;
    }

    /// <summary>A shot switched on while a trap is firing is the trap's.</summary>
    [HarmonyPatch(typeof(Projectile), "OnEnable")]
    static class MarkTrapShot
    {
        static void Postfix(Projectile __instance) { if (trapFiring) trapShots.Add(__instance); }
    }

    /// <summary>Pooled: a shot coming back for someone else mustn't keep our target.</summary>
    [HarmonyPatch(typeof(Projectile), "DisableProjectile")]
    static class Release
    {
        static void Postfix(Projectile __instance)
        {
            trapShots.Remove(__instance);
            fired.Remove(__instance);
            if (!ours.Remove(__instance)) return;
            __instance.TargetToFollow = null;
            __instance.turnSpeed = 20f;   // the game's default
        }
    }

    static Transform Closest(Vector3 from, Vector3 dir)
    {
        Transform best = null;
        float bestD = range.Value * range.Value;
        var d2 = ((Vector2)dir).normalized;
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || e.IsDead || !e.gameObject.activeInHierarchy) continue;
            var at = e.CenterOfMass ? e.CenterOfMass.position : e.transform.position;
            Vector2 to = at - from;
            float d = to.sqrMagnitude;
            if (d > bestD || d < 0.01f) continue;
            if (Vector2.Angle(d2, to) > angle.Value) continue;
            bestD = d;
            best = e.CenterOfMass ? e.CenterOfMass : e.transform;
        }
        return best;
    }
}

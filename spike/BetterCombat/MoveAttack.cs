using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;

namespace BetterCombat;

/// <summary>
/// Moving while attacking. The game stops you for every attack (Attack.stopPlayer: speed to zero, facing locked, a
/// small lunge) and its attack state has no movement at all. With this on, our attacks don't stop us and the attack
/// state steers with WASD / the stick through the game's own Move (walls, stairs, speed buffs): melee and magic at full
/// speed (or AttackMoveSpeed); bows and crossbows (and holding a charged shot, aiming) at BowAimSpeed. Only our own character.
/// </summary>
public static class MoveAttack
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> bowSpeed, attackSpeed;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Combat", "MoveAttack", false, "Keep moving while you attack (melee and magic at full speed, bows slower while aiming). /moveattack, or the Insert menu");
        bowSpeed = config.Bind("Combat", "BowAimSpeed", 0.6f, "Speed while shooting / aiming a bow or crossbow (1 = full speed)");
        attackSpeed = config.Bind("Combat", "AttackMoveSpeed", 1f, "Speed while swinging a melee weapon or casting magic (1 = full speed, 0 = you stop, the game's own way)");
    }

    /// <summary>/moveattack: on / off; /moveattack 70 sets the bow speed (and turns it on).</summary>
    public static void Command(string arg)
    {
        if (float.TryParse(arg.Trim().TrimEnd('%'), out float pct))
        {
            bowSpeed.Value = Mathf.Clamp(pct, 20f, 100f) / 100f;
            on.Value = true;
        }
        else on.Value = !on.Value;
        Log.Info(on.Value ? $"Move while attacking: on (bows at {bowSpeed.Value * 100f:0}% while aiming; /moveattack again to turn it off)" : "Move while attacking: off", toast: true);
    }

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Move while attacking");
        bool v = on.Value;
        if (ImGui.Checkbox("Keep moving while you attack (/moveattack)", ref v)) on.Value = v;
        if (!on.Value) ImGui.BeginDisabled();
        float s = bowSpeed.Value * 100f;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Bow speed##moveattack", ref s, 20f, 100f, "%.0f%%")) bowSpeed.Value = s / 100f;
        ImGui.SameLine(); ImGui.TextDisabled("your speed while aiming a bow or crossbow");
        float a = attackSpeed.Value * 100f;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Attack speed##moveattack", ref a, 0f, 100f, a <= 0f ? "0%% (stop, like the game)" : "%.0f%%")) attackSpeed.Value = a / 100f;
        ImGui.SameLine(); ImGui.TextDisabled("your speed while swinging a melee weapon or casting magic (100%% = no slowdown)");
        if (!on.Value) ImGui.EndDisabled();
    }

    static bool Mine(PlayerMaster p) => on.Value && p && Singleton<GameManager>.HasMain && p == Singleton<GameManager>.main.player;

    static bool Bow(PlayerMaster p) =>
        p.currentWeapon != null && (p.currentWeapon.weaponCategory == WeaponCategory.Bows || p.currentWeapon.weaponCategory == WeaponCategory.Crossbows);

    /// <summary>Attack speed 0 (melee and magic): the game's own stop, lunge and locked facing.</summary>
    static bool Stops(PlayerMaster p) => !Bow(p) && attackSpeed.Value <= 0f;

    /// <summary>Our attack doesn't stop us (nor lock the facing, nor lunge): its stopPlayer is off just for this start.</summary>
    [HarmonyPatch(typeof(PlayerAttack), nameof(PlayerAttack.EnterState))]
    static class NoStop
    {
        static void Prefix(PlayerAttack __instance, out bool __state)
        {
            __state = false;
            var p = __instance.owner;
            if (!Mine(p) || p.currentAttack == null || !p.currentAttack.stopPlayer || Stops(p)) return;
            __state = true;
            p.currentAttack.stopPlayer = false;
        }

        static void Postfix(PlayerAttack __instance, bool __state)
        {
            if (__state && __instance.owner.currentAttack != null) __instance.owner.currentAttack.stopPlayer = true;
        }
    }

    /// <summary>The attack state steers (it has no movement of its own).</summary>
    [HarmonyPatch(typeof(PlayerMaster), "FixedUpdate")]
    static class Steer
    {
        static void Postfix(PlayerMaster __instance)
        {
            if (!Mine(__instance) || __instance.currentState != __instance.stateAttack || Stops(__instance)) return;
            float speed = __instance.characterStats.CurrentMoveSpeed * (Bow(__instance) ? bowSpeed.Value : attackSpeed.Value);
            __instance.Move(speed);
        }
    }

    /// <summary>Holding a charged bow shot (aiming): slower, wherever the game moves us.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Move))]
    static class AimSlow
    {
        static void Prefix(PlayerMaster __instance, ref float maxSpeed)
        {
            if (Mine(__instance) && __instance.holdActive && Bow(__instance) && __instance.currentState != __instance.stateAttack)
                maxSpeed *= bowSpeed.Value;
        }
    }
}

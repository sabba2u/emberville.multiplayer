using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;

namespace BetterCombat;

/// <summary>
/// Staggers you can tune, and no stun lock. Every enemy hit (even one for 0 damage) staggers you: PlayerDamage freezes
/// you for 0.3 s (no attacks, spells or dodge), and the game's only limit on how often is PlayerMaster.hitStunCooldown,
/// from its data, so a pack can stagger you again the moment you recover. Here: [Combat] StaggerLength sets how long a
/// stagger lasts (0 = never staggered), and with StaggerGuard on, after a stagger ends you can't be staggered again for
/// StaggerGap seconds (you still take the damage): a window to cast, dodge or swing.
/// </summary>
public static class StaggerGuard
{
    const float GameLength = 0.3f;   // (PlayerDamage's own)

    static ConfigEntry<bool> on;
    static ConfigEntry<float> gap, length;
    static bool logged;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Combat", "StaggerGuard", true, "No stun lock: after a stagger ends, you can't be staggered again for StaggerGap seconds (you still take the damage)");
        gap = config.Bind("Combat", "StaggerGap", 0.5f, "Seconds after a stagger ends before another hit can stagger you (0.1 to 3)");
        length = config.Bind("Combat", "StaggerLength", GameLength, "How long a hit staggers you (seconds; 0.3 = the game's, 0 = never staggered)");
    }

    static float Length => on.Value ? Mathf.Clamp(length.Value, 0f, 1f) : GameLength;

    public static void DrawMenu()
    {
        bool v = on.Value;
        if (ImGui.Checkbox("Tune staggers, and no stun lock", ref v)) on.Value = v;
        if (!on.Value) ImGui.BeginDisabled();
        float l = length.Value;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Stagger length##stagger", ref l, 0f, 1f, l <= 0f ? "never staggered" : "%.2f s")) length.Value = l;
        ImGui.SameLine(); ImGui.TextDisabled("how long a hit freezes you (the game: 0.30 s)");
        if (length.Value > 0f)
        {
            float g = gap.Value;
            ImGui.SetNextItemWidth(280);
            if (ImGui.SliderFloat("Gap between staggers##stagger", ref g, 0.1f, 3f, "%.1f s")) gap.Value = g;
            ImGui.SameLine(); ImGui.TextDisabled("after a stagger, time you can't be staggered: fight back (you still take the damage)");
        }
        if (!on.Value) ImGui.EndDisabled();
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.TryHitStun))]
    static class Guard
    {
        static bool Prefix(PlayerMaster __instance)
        {
            if (!Mine(__instance)) return true;
            if (!logged)
            {
                logged = true;
                Log.Info($"Stagger: the game's own gap between staggers is {__instance.hitStunCooldown:0.00} s (a stagger lasts {GameLength} s)");
            }
            if (!on.Value || __instance.currentState == __instance.stateStatic) return true;
            if (Length <= 0f) return false;
            return (float)__instance.timeSinceLastHitStun > Length + gap.Value;
        }
    }

    /// <summary>The stagger's length (PlayerDamage leaves after its cooldown field).</summary>
    [HarmonyPatch(typeof(PlayerDamage), nameof(PlayerDamage.EnterState))]
    static class Length_
    {
        static void Postfix(PlayerDamage __instance)
        {
            if (Mine(__instance.owner)) __instance.cooldown = Length;
        }
    }

    static bool Mine(PlayerMaster p) => Singleton<GameManager>.HasMain && p == Singleton<GameManager>.main.player;
}

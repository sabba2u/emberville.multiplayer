using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BetterCombat;

/// <summary>
/// A dodge press that's remembered. The game's dodge already cuts a swing short, but a press it can't do right then
/// (the dodge cooling down, being hit or stunned, a spell's animation, mid-dodge) is dropped. With this on, it's kept
/// for [Combat] SpellBuffer seconds and the dodge goes the moment it can (in the direction you hold then).
/// </summary>
public static class DodgeKey
{
    static ConfigEntry<bool> on;
    static float bufferedUntil = -1f;
    static readonly System.Reflection.FieldInfo onDodge = AccessTools.Field(typeof(PlayerMaster), "OnDodge");

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Combat", "DodgeBuffer", true, "Remember a dodge press you couldn't do yet (cooling down, being hit, mid-spell) and dodge as soon as you can (for SpellBuffer seconds)");
    }

    public static bool On { get => on.Value; set => on.Value = value; }

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    static bool CanDodge(PlayerMaster p) => Time.timeScale != 0f && p.canDodge && !p.onDodgeCooldown && p.currentState != p.stateDodge && !p.IsDead;

    public static void Tick()
    {
        if (bufferedUntil < 0f) return;
        var p = Me;
        if (!on.Value || !p || Time.unscaledTime > bufferedUntil) { bufferedUntil = -1f; return; }
        if (!CanDodge(p)) return;
        bufferedUntil = -1f;
        // (the game's PerformDodge, without its input context)
        p.currentDodge = p.equippedDodge;
        (onDodge?.GetValue(p) as Action)?.Invoke();
        if (p.currentState == p.stateAttack) p.OnAttackEnd?.InvokeAndClear();
        p.ChangeState(p.stateDodge);
    }

    /// <summary>A press the game turns down: remembered.</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformDodge))]
    static class Remember
    {
        static void Prefix(PlayerMaster __instance)
        {
            if (!on.Value || __instance != Me || SpellKeys.Buffer <= 0f) return;
            bufferedUntil = CanDodge(__instance) ? -1f : Time.unscaledTime + SpellKeys.Buffer;
        }
    }
}

using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterCombat;

/// <summary>
/// An attack out of a roll. The game ignores an attack click while you dodge (PerformAttack's dodge branch only logs
/// "Dodge combined attack"), so a click before you land is lost. With this on, it's kept for [Combat] SpellBuffer
/// seconds and the attack goes the moment the roll ends.
/// </summary>
public static class AttackKey
{
    static ConfigEntry<bool> on;
    static float bufferedUntil = -1f;
    static InputAction.CallbackContext click;
    static bool replaying;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Combat", "AttackBuffer", true, "An attack clicked during a dodge roll goes the moment you land (clicked within SpellBuffer seconds of landing)");
    }

    public static bool On { get => on.Value; set => on.Value = value; }

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    public static void Tick()
    {
        if (bufferedUntil < 0f) return;
        var p = Me;
        if (!on.Value || !p || p.IsDead || Time.unscaledTime > bufferedUntil) { bufferedUntil = -1f; return; }
        if (p.currentState == p.stateDodge || Time.timeScale == 0f) return;
        bufferedUntil = -1f;
        replaying = true;
        try { p.PerformAttack(click); }
        catch (System.Exception e) { Log.Warn($"Attack out of a roll: {e.Message}"); }
        finally { replaying = false; }
    }

    /// <summary>A click while rolling: remembered (the game would drop it).</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttack))]
    static class Remember
    {
        static void Prefix(PlayerMaster __instance, InputAction.CallbackContext context)
        {
            if (replaying || !on.Value || __instance != Me || SpellKeys.Buffer <= 0f || __instance.currentState != __instance.stateDodge) return;
            click = context;
            bufferedUntil = Time.unscaledTime + SpellKeys.Buffer;
        }
    }
}

using BetterStreaming.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterStreaming;

/// <summary>
/// An effect that's a switch: on for its time (the timer says so), with optional things to do as it starts and ends.
/// The patches and ticks that do the work ask Controls.On(id).
/// </summary>
internal sealed class Switch : IEffect
{
    readonly System.Action<PlayerMaster> start, end;
    public string Id { get; }

    public Switch(string id, System.Action<PlayerMaster> start = null, System.Action<PlayerMaster> end = null) { Id = id; this.start = start; this.end = end; }

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size)
    {
        if (!Controls.Running.Contains(Id)) { Controls.Running.Add(Id); start?.Invoke(Effects.Player); }
        return true;
    }

    public void Stop()
    {
        if (!Controls.Running.Remove(Id)) return;
        var me = Effects.Player;
        if (me) end?.Invoke(me);
    }
}

/// <summary>
/// The control effects, on the game's own input (only your character: a companion is a player too):
///  - backwards: the move input turned round;
///  - can't stop walking: no input means the way you last went, so you never stand still;
///  - swapped buttons: the attack button dodges, the dodge button attacks;
///  - random dodge: a dodge on its own every 2-4 s;
///  - invisible: your character hidden.
/// </summary>
[HarmonyPatch]
internal static class Controls
{
    public static readonly System.Collections.Generic.HashSet<string> Running = new();
    public static bool On(string id) => Running.Contains(id);

    static Vector2 lastWay = Vector2.down;
    static float nextDodge;

    public static void Register()
    {
        foreach (var fx in new IEffect[]
        {
            new Switch("backwards"), new Switch("autowalk"), new Switch("swapped", StopCharging, StopCharging), new Switch("randomdodge"),
            new Switch("invisible", me => me.ShowPlayer(false), me => { if (!me.IsDead) me.ShowPlayer(true); }),
        })
            Streamer.Effects[fx.Id] = fx;
    }

    static bool Mine(PlayerMaster p) => Singleton<GameManager>.HasMain && ReferenceEquals(Singleton<GameManager>.main.player, p);

    [HarmonyPatch(typeof(PlayerMaster), "GetInput"), HarmonyPostfix]
    static void Bend(PlayerMaster __instance)
    {
        if (Running.Count == 0 || !Mine(__instance)) return;
        if (On("backwards")) __instance.input = -__instance.input;
        if (On("autowalk"))
        {
            if (__instance.input.sqrMagnitude < 0.04f) __instance.input = lastWay;
            else lastWay = __instance.input.normalized;
        }
        Filters.Drift(__instance);   // (drunk: the steps wander)
    }

    // ---- swapped buttons ----

    static bool swallowed;   // (an attack press that became a dodge: its release is ours too)

    /// <summary>As the swap starts and ends: no charge or hold left running from the other way round.</summary>
    static void StopCharging(PlayerMaster p)
    {
        if (!p) return;
        try { p.primAttackHold.Cancel(); p.secAttackHold.Cancel(); p.CancelCharged(); } catch (System.Exception e) { Log.Warn($"Streaming: swapped buttons: {e.Message}"); }
        swallowed = false;
    }

    [HarmonyPatch(typeof(PlayerMaster), "StartedPrimAction"), HarmonyPrefix]
    static bool AttackDodges(PlayerMaster __instance)
    {
        if (!Mine(__instance) || !On("swapped")) return true;
        swallowed = true;
        Dodge(__instance);
        return false;
    }

    [HarmonyPatch(typeof(PlayerMaster), "FinishedPrimAction"), HarmonyPrefix]
    static bool AttackReleased(PlayerMaster __instance)
    {
        if (!Mine(__instance) || !swallowed) return true;
        swallowed = false;
        return false;
    }

    [HarmonyPatch(typeof(PlayerMaster), "PerformDodge"), HarmonyPrefix]
    static bool DodgeAttacks(PlayerMaster __instance)
    {
        if (!On("swapped") || !Mine(__instance)) return true;
        Attack(__instance);
        return false;
    }

    /// <summary>
    /// The primary attack, as the game's PerformAttack does it for the attack button (a button's context names which
    /// attack, and one can't be kept past its own press: so its steps here, without one): not while holding, paused,
    /// mid-combo-move or unable; the combo buffer; the attack's Kymia; then attacking.
    /// </summary>
    static void Attack(PlayerMaster p)
    {
        if (p.currentWeapon == null || Time.timeScale == 0f || !p.canAttack || p.primAttackHold.IsHolding || p.secAttackHold.IsHolding) return;
        if (p.currentState == p.stateDodge || p.performingComboMove) return;
        p.nextAttack = AttackType.Primary;
        p.bufferCountdownToken?.Cancel();
        p.OnAttackEnd += (System.Action)p.BufferCountdown;
        if (p.currentState == p.stateAttack) return;
        if (!p.UpdateWeaponAttack()) { p.ClearComboBuffer(); return; }
        int cost = Mathf.RoundToInt(p.currentWeapon.CurrentAttack.kymiaCost * p.AttackKymiaConsumptionMultiplier.Value);
        if (cost > 0 && !p.CheckAvailableKymia(cost)) { p.ClearComboBuffer(); return; }
        p.ConsumeKymia(cost, cost > 0);
        p.currentAttack = p.currentWeapon.CurrentAttack;
        p.ChangeState(p.stateAttack);
    }

    static readonly System.Reflection.FieldInfo onDodge = AccessTools.Field(typeof(PlayerMaster), "OnDodge");

    /// <summary>A dodge the way the game's button does it (when it may: not on cooldown, not mid-dodge, not paused), its
    /// OnDodge too (that's what starts the dodge's cooldown).</summary>
    static void Dodge(PlayerMaster p)
    {
        if (Time.timeScale == 0f || !p.canDodge || p.onDodgeCooldown || p.currentState == p.stateDodge || p.IsDead) return;
        p.currentDodge = p.equippedDodge;
        (onDodge?.GetValue(p) as System.Action)?.Invoke();
        if (p.currentState == p.stateAttack) p.OnAttackEnd?.InvokeAndClear();
        p.ChangeState(p.stateDodge);
    }

    /// <summary>Every frame (Plugin.Update): the random dodges.</summary>
    public static void Tick()
    {
        if (!On("randomdodge") || Time.time < nextDodge) return;
        nextDodge = Time.time + Random.Range(2f, 4f);
        var me = Effects.Player;
        if (me && Streamer.CanFight()) Dodge(me);
    }
}

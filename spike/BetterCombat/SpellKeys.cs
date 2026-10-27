using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterCombat;

/// <summary>
/// Spell keys (1-4) that answer every press. The game runs each key through a HoldingModule with a 1 s minimum hold:
/// once you've pressed, the key ignores every press for a whole second (even after you let go), and a press while
/// you're busy (a swing, between combo hits, hit, dodging) is simply dropped. With this on, every press casts on the
/// spot, and one that can't (busy, or the last moments of the cooldown) is remembered for [Combat] SpellBuffer seconds
/// and cast the moment it can. Held powers (canHold) still charge at least the game's 1 s before they're let go.
/// </summary>
public static class SpellKeys
{
    const float MinHold = 1f;   // (the game's: a held power charges at least this long)

    static ConfigEntry<bool> on;
    static ConfigEntry<float> buffer;
    static ConfigEntry<bool> cancel;
    static PlayerMaster landed;   // (whose weapon swing has hit: a spell may cut the rest of it short)
    static readonly int ClearAttack = Animator.StringToHash("ClearAttack");

    sealed class Key
    {
        public SkillHandler Handler;
        public int Index;
        public bool Down;
        public float BufferedUntil = -1f, CastAt = -1f, ReleaseAt = -1f;
    }

    static readonly List<Key> keys = new();

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Combat", "SpellKeys", true, "Spell keys answer every press (the game ignores presses for 1 s after each one, and drops them while you swing). /spellkeys, or the Insert menu");
        cancel = config.Bind("Combat", "SpellCancel", true, "A spell cuts the rest of a weapon swing short, once the swing's hit has landed (like Diablo)");
        buffer = config.Bind("Combat", "SpellBuffer", 0.4f, "How long a spell press is remembered while you're busy or the spell is nearly off cooldown (seconds; 0 = not at all)");
    }

    public static float Buffer => buffer.Value;

    public static void Command(string arg)
    {
        if (float.TryParse(arg.Trim().TrimEnd('s'), out float sec))
        {
            buffer.Value = Mathf.Clamp(sec, 0f, 1f);
            on.Value = true;
        }
        else on.Value = !on.Value;
        Log.Info(on.Value ? $"Spell keys: every press counts (remembered for {buffer.Value:0.0} s while you're busy; /spellkeys again to turn it off)" : "Spell keys: the game's own (1 s between presses)", toast: true);
    }

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Spell keys");
        bool v = on.Value;
        if (ImGui.Checkbox("Spell keys answer every press, like Diablo (/spellkeys)", ref v)) on.Value = v;
        if (!on.Value) ImGui.BeginDisabled();
        float s = buffer.Value;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Remember a press##spellkeys", ref s, 0f, 1f, s <= 0f ? "not at all" : "%.1f s")) buffer.Value = s;
        ImGui.SameLine(); ImGui.TextDisabled("pressed mid-swing or just before the cooldown ends: cast as soon as it can");
        if (!on.Value) ImGui.EndDisabled();
        bool x = cancel.Value;
        if (ImGui.Checkbox("A spell cuts the end of a swing short, once its hit has landed", ref x)) cancel.Value = x;
        bool d = DodgeKey.On;
        if (ImGui.Checkbox("Remember a dodge press too (cooling down, being hit, mid-spell: dodge as soon as you can)", ref d)) DodgeKey.On = d;
        bool a = AttackKey.On;
        if (ImGui.Checkbox("Attack out of a roll: a click late in a dodge attacks the moment you land", ref a)) AttackKey.On = a;
        StaggerGuard.DrawMenu();
    }

    public static void Tick()
    {
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            var k = keys[i];
            if (!k.Handler) { keys.RemoveAt(i); continue; }
            if (k.BufferedUntil >= 0f)
            {
                if (!on.Value || Time.unscaledTime > k.BufferedUntil) k.BufferedUntil = -1f;
                else if (TryCast(k)) k.BufferedUntil = -1f;
            }
            if (k.ReleaseAt >= 0f && Time.time >= k.ReleaseAt)
            {
                k.ReleaseAt = -1f;
                k.CastAt = -1f;
                k.Handler.ReleaseSkill(k.Index);
            }
        }
    }

    static SkillContainer Slot(Key k)
    {
        var h = k.Handler;
        if (k.Index >= h.mainSlots.Count) return null;
        var slot = h.mainSlots[k.Index];
        return h.skillSlots.Find(x => x.slot == slot);
    }

    /// <summary>Casts it if it can be cast now; false if it can't yet (busy, cooling down), so a buffered press waits.</summary>
    static bool TryCast(Key k)
    {
        var c = Slot(k);
        if (c == null || c.skill == null) return true;
        var h = k.Handler;
        bool toggleOff = c.skill.activationType == Skill.ActivationType.Toggle && c.isActive;
        if (!toggleOff && !h.player.CanPerformSkills && CanCut(h.player) && !c.isActive && !c.InCooldown) Cut(h.player);
        if (!toggleOff && (!SkillHandler.CanUseSkill || !h.player.CanPerformSkills || Time.timeScale == 0f || c.isActive || c.InCooldown)) return false;
        h.CallSkill(k.Index);
        bool cast = toggleOff || c.isActive || c.InCooldown || h.player.currentSkill == c.skill;
        if (cast && c.skill.canHold)
        {
            k.CastAt = Time.time;
            if (!k.Down) k.ReleaseAt = k.CastAt + MinHold;
        }
        return true;   // (tried: out of kymia isn't worth waiting for)
    }

    /// <summary>Our weapon swing (not a power's attack) whose hit has already landed: what's left is just the follow-through.</summary>
    static bool CanCut(PlayerMaster p) =>
        on.Value && cancel.Value && p == landed && p.currentState == p.stateAttack && p.currentSkill == null && SkillHandler.CanUseSkill && Time.timeScale != 0f;

    /// <summary>Ends the swing the way the game's dodge does (its end callbacks, the animation cleared), to idle.</summary>
    static void Cut(PlayerMaster p)
    {
        landed = null;
        p.OnAttackEnd?.InvokeAndClear();
        p.animator.SetTrigger(ClearAttack);
        p.ChangeState(p.stateIdle);
    }

    [HarmonyPatch(typeof(PlayerAttack), nameof(PlayerAttack.EnterState))]
    static class NewSwing
    {
        static void Prefix(PlayerAttack __instance) { if (landed == __instance.owner) landed = null; }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttackHit))]
    static class Hit
    {
        static void Postfix(PlayerMaster __instance)
        {
            if (Singleton<GameManager>.HasMain && __instance == Singleton<GameManager>.main.player && __instance.currentState == __instance.stateAttack) landed = __instance;
        }
    }

    static void Press(Key k)
    {
        k.Down = true;
        k.BufferedUntil = -1f;
        if (TryCast(k)) return;
        var c = Slot(k);
        if (c != null && !c.isActive && buffer.Value > 0f && c.cooldownLeft <= buffer.Value)
            k.BufferedUntil = Time.unscaledTime + buffer.Value;
    }

    static void Release(Key k)
    {
        k.Down = false;
        if (k.CastAt >= 0f) k.ReleaseAt = Mathf.Max(Time.time, k.CastAt + MinHold);
    }

    /// <summary>Our own press and release for each spell key, in place of the game's HoldingModule (which is still
    /// there, wired the game's way, for when this is switched off).</summary>
    [HarmonyPatch(typeof(SkillHandler), nameof(SkillHandler.CreateActionsForSkill))]
    static class Wire
    {
        static bool Prefix(SkillHandler __instance, int index, ref IDisposable __result)
        {
            var h = __instance;
            InputAction action = index switch
            {
                0 => MainInput.Actions.Player.Select1,
                1 => MainInput.Actions.Player.Select2,
                2 => MainInput.Actions.Player.Select3,
                3 => MainInput.Actions.Player.Select4,
                _ => null,
            };
            if (action == null) return true;
            var key = new Key { Handler = h, Index = index };
            var module = new HoldingModule(0f, MinHold);
            Action<InputAction.CallbackContext> single = _ => { h.CallSkill(index); h.ReleaseSkill(index); };
            Action<InputAction.CallbackContext> holdStart = _ => h.CallSkill(index);
            Action<InputAction.CallbackContext, float> holdRelease = (_, _) => h.ReleaseSkill(index);
            Action<InputAction.CallbackContext> performed = c => { if (on.Value) Press(key); else module.StartPressing(c); };
            Action<InputAction.CallbackContext> canceled = c => { if (on.Value) Release(key); else module.StopPressing(c); };
            Events(module, single, holdStart, holdRelease, add: true);
            action.performed += performed;
            action.canceled += canceled;
            h.skillHoldingModules.Add(module);
            keys.Add(key);
            __result = new Unwire(() =>
            {
                action.performed -= performed;
                action.canceled -= canceled;
                Events(module, single, holdStart, holdRelease, add: false);
                keys.Remove(key);
            });
            return false;
        }
    }

    /// <summary>The module's events (by reflection: the publicized build sees each event twice, event and field).</summary>
    static void Events(HoldingModule m, Delegate single, Delegate holdStart, Delegate holdRelease, bool add)
    {
        foreach (var (name, d) in new[] { ("OnSinglePress", single), ("OnHoldStart", holdStart), ("OnHoldRelease", holdRelease) })
        {
            var e = typeof(HoldingModule).GetEvent(name);
            if (add) e.AddEventHandler(m, d); else e.RemoveEventHandler(m, d);
        }
    }

    sealed class Unwire : IDisposable
    {
        Action undo;
        public Unwire(Action undo) { this.undo = undo; }
        public void Dispose() { undo?.Invoke(); undo = null; }
    }
}

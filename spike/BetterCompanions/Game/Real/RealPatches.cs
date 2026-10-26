using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// Where the game assumes it has one player, made to hold with two (RealPlayer). These land together: with only
/// some, the second player steals your keys, or its first death resets your game.
/// - Its HUD events (your health, Kymia, weapon and dodge icons, buff icons, the red hit flash, and "the player died"
///   which resets the level) are swallowed while its own code runs (Scope): the HUD events don't say who sent them.
/// - Its death: no game over, no XP penalty, your controls stay on.
/// - Its skills don't take over your skill bar or keys 1-4, and don't clear the skills' shared pool when it goes.
/// - It and you can't hurt each other; your shots pass through it and its through you.
/// - It doesn't save itself into your save, nor take XP into it.
/// - Mouse and keys don't steer or aim it.
/// </summary>
[HarmonyPatch]
internal static class RealPatches
{
    static int scope;

    /// <summary>While its code runs: its HUD events are dropped.</summary>
    public static IDisposable Scope() { scope++; return new Exit(); }
    sealed class Exit : IDisposable { bool done; public void Dispose() { if (!done) { done = true; scope--; } } }

    static bool C(PlayerMaster p) => RealPlayer.Is(p);

    // ---- waking up ----

    [HarmonyPatch(typeof(PlayerMaster), "Awake"), HarmonyPostfix]
    static void Awake(PlayerMaster __instance)
    {
        if (__instance.GetComponent<RealCompanionMark>()) RealPlayer.Woke(__instance);
        else RealPlayer.TakeTemplate(__instance);
    }

    [HarmonyPatch(typeof(PlayerMaster), "Start"), HarmonyPostfix]
    static void Started(PlayerMaster __instance)
    {
        if (!C(__instance)) return;
        // its save handlers would write its health into YOUR save (and load yours into it)
        GameData.Unregister(__instance.LoadPlayerState);
        GameData.Unregister(__instance.SavePlayerState);
        RealPlayer.Started(__instance);
    }

    [HarmonyPatch(typeof(PlayerMaster), "OnDestroy"), HarmonyPostfix]
    static void Destroyed(PlayerMaster __instance) => RealPlayer.Gone(__instance);

    [HarmonyPatch(typeof(PlayerMaster), "ClearCreatedPools"), HarmonyPrefix]
    static bool KeepPools(PlayerMaster __instance) => !C(__instance);   // (the arrows' pools are yours too)

    // ---- its code runs in scope: its HUD events go nowhere ----

    [HarmonyPatch]
    static class InScope
    {
        static readonly string[] Names =
        {
            "Start", "Update", "LateUpdate", "FixedUpdate", "ApplyDamage", "Die", "ConsumeKymia", "RestoreHealth", "RestoreKymia",
            "ResetToLastState", "ResetToFullHealth", "UpdatePlayerHealthBar", "UpdatePlayerKymiaBar", "EquipSecondarySkill",
            "PerformAttackHit", "PerformSkill", "AttackEnd", "ShowPlayer", "RecalculateHealthAndKymia", "GainXP",
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = typeof(PlayerMaster);
            foreach (var n in Names)
            {
                var m = AccessTools.GetDeclaredMethods(t).FirstOrDefault(x => x.Name == n && !x.IsAbstract);
                if (m != null) yield return m; else Log.Warn($"Companion (real): no PlayerMaster.{n} to watch");
            }
            // the weapon in hand (an explicit IEquipable method)
            var map = t.GetInterfaceMap(typeof(IEquipable));
            foreach (var m in map.TargetMethods) if (m.DeclaringType == t && m.Name.EndsWith("EquipPrimaryWeapon")) yield return m;
        }

        static void Prefix(PlayerMaster __instance, out bool __state)
        {
            __state = C(__instance);
            if (__state) scope++;
        }

        static void Finalizer(bool __state) { if (__state) scope--; }
    }

    [HarmonyPatch(typeof(Bus<UIPlayerEvent>), nameof(Bus<UIPlayerEvent>.Invoke)), HarmonyPrefix]
    static bool HudBars(ref bool __result) => Let(ref __result);
    [HarmonyPatch(typeof(Bus<UISlotEvent>), nameof(Bus<UISlotEvent>.Invoke)), HarmonyPrefix]
    static bool HudSlots(ref bool __result) => Let(ref __result);
    [HarmonyPatch(typeof(Bus<PlayerEvent>), nameof(Bus<PlayerEvent>.Invoke)), HarmonyPrefix]
    static bool PlayerEvents(ref bool __result) => Let(ref __result);
    [HarmonyPatch(typeof(Bus<UIBuffEvent>), nameof(Bus<UIBuffEvent>.Invoke)), HarmonyPrefix]
    static bool BuffIcons(ref bool __result) => Let(ref __result);

    static bool Let(ref bool result)
    {
        if (scope <= 0) return true;
        result = false;
        return false;
    }

    // ---- death: down, not game over ----

    [HarmonyPatch(typeof(PlayerMaster), "Die"), HarmonyPrefix]
    static bool Die(PlayerMaster __instance)
    {
        if (!C(__instance)) return true;
        __instance.goreDeath = false;
        __instance.effectsHandler?.StopAllEffects();
        __instance.ChangeState(__instance.stateDead);
        RealPlayer.WentDown(__instance);
        return false;   // (no XP penalty on your save, no "the player has died")
    }

    [HarmonyPatch(typeof(PlayerDead), nameof(PlayerDead.EnterState)), HarmonyPrefix]
    static bool Dead(PlayerDead __instance)
    {
        var p = __instance.owner;
        if (!C(p)) return true;
        // the game's death, without switching off YOUR controls and without the game over at the end
        p.StopVelocity();
        if (p.bodyCollider) p.bodyCollider.enabled = false;
        p.CanMove = false;
        p.CanRotate = false;
        p.CanDodge = false;
        p.CanPerfomAttacks = false;
        p.CanPerformSkills = false;
        p.animator.SetTrigger("Damage");
        Utils.TimeoutDeltaTime(0.375f, () => { if (p && p.currentState == p.stateDead) p.animator.SetTrigger("Dead"); });
        return false;
    }

    // ---- skills: not your skill bar, not your keys, not your pools ----

    [HarmonyPatch(typeof(SkillHandler), "OnEnable"), HarmonyPrefix]
    static bool SkillsOn(SkillHandler __instance)
    {
        // (no keys 1-4; its dodge cooldown is RealBrain's, which dodges through DodgeCooldown)
        return !C(__instance.player);
    }

    [HarmonyPatch(typeof(SkillHandler), "Start"), HarmonyPrefix]
    static bool SkillsStart(SkillHandler __instance)
    {
        if (!C(__instance.player)) return true;
        __instance.mainSlots.Clear();
        __instance.mainStacks.Clear();
        __instance.skillSlots.Clear();   // (the copy came with your HUD's)
        __instance.equipped.Clear();
        __instance.dodgeSlot = null;
        return false;
    }

    [HarmonyPatch(typeof(SkillHandler), "OnDestroy"), HarmonyPrefix]
    static bool SkillsGone(SkillHandler __instance) => !C(__instance.player);   // (it would clear your skills' pool)

    public static void DodgeCooldown(PlayerMaster p)
    {
        p.onDodgeCooldown = true;
        float wait = 0.6f;
        try { if (p.currentDodge) wait = Mathf.Max(0.1f, p.currentDodge.Cooldown()); } catch { }
        Utils.TimeoutDeltaTime(wait, () => { if (p) p.onDodgeCooldown = false; });
    }

    // ---- no friendly fire ----

    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out }), HarmonyPrefix]
    static bool NoFriendlyFire(DamageData damageData, ref DamageData attackDamage, ref DamageData __result)
    {
        // (you and it don't hurt each other, and it doesn't hurt itself: its own spells' blasts had killed it)
        if (Owner(damageData.Aggressor) is { } a && Owner(damageData.Target) is { } t && (a != t ? C(a) || C(t) : C(a)))
        {
            attackDamage = default;
            __result = default;
            return false;
        }
        return true;
    }

    /// <summary>The player a hit is from or to: itself, or the one its hurtbox (DamageableCollider) belongs to.</summary>
    static PlayerMaster Owner(IDamageable d) => d is PlayerMaster p ? p : d is Component c && c ? c.GetComponentInParent<PlayerMaster>() : null;

    [HarmonyPatch(typeof(Projectile), "OnTriggerEnter2D"), HarmonyPrefix]
    static bool PassThrough(Projectile __instance, Collider2D other)
    {
        var rb = other ? other.attachedRigidbody : null;
        var hit = rb ? rb.GetComponent<PlayerMaster>() : other ? other.GetComponentInParent<PlayerMaster>() : null;
        return !(hit && Owner(__instance.damageData.Aggressor) is { } a && (a != hit ? C(a) || C(hit) : C(a)));
    }

    // ---- not your save ----

    [HarmonyPatch(typeof(PlayerMaster), "GainXP"), HarmonyPrefix]
    static bool NoXpForYou(PlayerMaster __instance, int value, ref int __result)
    {
        if (!C(__instance)) return true;
        __result = RealPlayer.GainXP(__instance, value);   // (into the companion's save, not yours)
        return false;
    }

    // ---- "find the player" finds you ----

    /// <summary>
    /// The game finds "the player" with FindAnyObjectByType&lt;PlayerMaster&gt; in a dozen places (dialogue, HUD labels,
    /// room and wave events, enemy graphs): never the companion. Patched by hand, each on its own: if Unity's method
    /// can't be patched, the rest of the mod still loads.
    /// </summary>
    public static void PatchFinds(Harmony h)
    {
        var fix = new HarmonyMethod(typeof(RealPatches), nameof(NotTheCompanion));
        var fixAll = new HarmonyMethod(typeof(RealPatches), nameof(NotTheCompanionAll));
        foreach (var m in typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.IsGenericMethodDefinition || m.GetParameters().Length == 0 || m.GetParameters()[0].ParameterType != typeof(Type)) continue;
            bool one = m.Name == "FindAnyObjectByType" || m.Name == "FindFirstObjectByType" || m.Name == "FindObjectOfType";
            bool many = m.Name == "FindObjectsByType" || m.Name == "FindObjectsOfType";
            if (!one && !many) continue;
            try { h.Patch(m, postfix: one ? fix : fixAll); }
            catch (Exception e) { Log.Warn($"Companion (real): couldn't watch {m.Name}: {e.Message}"); }
        }
    }

    static void NotTheCompanion(ref UnityEngine.Object __result)
    {
        if (__result is PlayerMaster p && C(p) && Singleton<GameManager>.HasMain) __result = Singleton<GameManager>.main.player;
    }

    static void NotTheCompanionAll(ref UnityEngine.Object[] __result)
    {
        if (__result == null || __result.Length == 0 || !(__result[0] is PlayerMaster)) return;
        if (__result.Any(o => o is PlayerMaster p && C(p)))
        {
            var kept = __result.Where(o => !(o is PlayerMaster p && C(p))).ToArray();
            var arr = (UnityEngine.Object[])Array.CreateInstance(__result.GetType().GetElementType(), kept.Length);
            Array.Copy(kept, arr, kept.Length);
            __result = arr;
        }
    }

    // ---- not your mouse and keys ----

    [HarmonyPatch(typeof(PlayerMaster), "GetInput"), HarmonyPrefix]
    static bool Steer(PlayerMaster __instance)
    {
        if (!C(__instance)) return true;
        if (__instance.currentState != __instance.stateDodge) __instance.input = RealBrain.Stick;   // (a dodge keeps its way)
        return false;
    }

    [HarmonyPatch(typeof(PlayerMaster), "GetLookDirection"), HarmonyPrefix]
    static bool Aim(PlayerMaster __instance)
    {
        if (!C(__instance)) return true;
        var aim = RealBrain.Aim;
        if (aim.sqrMagnitude > 0.0001f)
        {
            __instance.lookDir = aim.normalized;
            __instance.lastLookDir = __instance.lookDir;
        }
        __instance.SetPlayerDirection();
        return false;
    }

    /// <summary>
    /// A skill's script waiting out its running time asks its "caster" for the skill's level; for the companion's skills
    /// that caster comes through empty, and the script threw. Empty: the companion's own level for it (as the game would).
    /// </summary>
    [HarmonyPatch(typeof(WaitForSkillDurationAction), "OnStart"), HarmonyPrefix]
    static bool DurationWithoutCaster(WaitForSkillDurationAction __instance, ref Unity.Behavior.Node.Status __result)
    {
        if (__instance.Caster == null || __instance.Caster.Value) return true;   // (a caster: the game's own way)
        var skill = __instance.Skill?.Value;
        var h = RealPlayer.Current ? RealPlayer.Current.SkillHandler : null;
        __instance.timeSince = 0f;
        if (!skill) __instance.duration = 0f;
        else if (h) __instance.duration = skill.Duration.Evaluate(Mathf.Max(1, h.GetSkillLevel(skill)));
        else __instance.duration = skill.Duration;
        __result = Unity.Behavior.Node.Status.Running;
        return false;
    }
}

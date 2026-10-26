using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The real companion's skills: the ones its own save has (its skill points, and what's on its slots 1-4), used when
/// it makes sense: an attack skill on a creature in reach, a dash to close in or to catch up with you, a buff when a fight
/// starts or it has ground to make up, a toggle on through a fight and off a few seconds after. Never one it can't
/// afford, one at a time, a moment apart.
///
/// Cast the way the game's skill bar does (SkillHandler.CallSkill: the skill's stat buffs and effects on start, off on
/// finish, its cooldown and running time, then PlayerMaster.PerformSkill), without the bar: no slots, no buff icons.
/// Each skill is its own copy: the game keeps a skill's start / finish callbacks on the skill itself, which you share.
/// </summary>
internal static class RealSkills
{
    sealed class Slot
    {
        public Skill Skill;
        public SkillContainer Box;   // (the game's: its cooldown and whether it's running)
        public float RunsUntil = -1f;   // (a skill with a running time: when it ends)
        public float Cooldown;
        public float CooldownTotal = 1f;   // (what the cooldown started at: for its card's sweep)
        public int Index;   // (its power slot, 0-3)
        public float CastAt = -100f;
    }

    /// <summary>For its card: power slot i (0-3): its skill's icon, cooldown left and total, running now, when cast.</summary>
    public static bool Power(int i, out Sprite icon, out float left, out float total, out bool running, out float castAt)
    {
        var s = slots.Find(x => x.Index == i);
        icon = s?.Skill ? s.Skill.uiIcon : null;
        left = s != null ? Mathf.Max(0f, s.Cooldown) : 0f;
        total = s != null ? Mathf.Max(0.01f, s.CooldownTotal) : 1f;
        running = s != null && s.Box.isActive;
        castAt = s != null ? s.CastAt : -100f;
        return s != null;
    }

    static readonly List<Slot> slots = new();
    static PlayerMaster loadedFor;
    static float nextCast, lastFight = -100f;
    const float Gap = 1.2f, ToggleOffAfter = 4f, CatchUp = 6f;

    /// <summary>Its skills, from its save (when it arrives).</summary>
    public static void Load(PlayerMaster p)
    {
        slots.Clear();
        loadedFor = p;
        var data = CompanionSave.Data;
        var h = p ? p.SkillHandler : null;
        if (!h || data == null) return;
        var nodes = Resources.FindObjectsOfTypeAll<PlayerCombatClass>()
            .Where(c => c && c.ClassSkillTree != null).SelectMany(c => c.ClassSkillTree.skillNodes).Where(n => n && n.skill).ToList();
        var mine = new Dictionary<string, Skill>();   // (by the game's skill ID: its slots name them so)
        foreach (var info in data.nodeInfos ?? new List<SkillNodeInfo>())
        {
            if (info == null || info.CurrentPoints <= 0) continue;
            var node = nodes.FirstOrDefault(n => n.uniqueID == info.Id);
            if (!node) continue;
            var original = node.skill;
            h.availableSkills[original] = info.CurrentPoints;   // (an improved skill reads its base's level from here)
            if (original.activationType == Skill.ActivationType.Passive)
            {
                try { h.availableSkills.Remove(original); h.AddSkill(Object.Instantiate(original), info.CurrentPoints); }
                catch (System.Exception e) { Log.Warn($"Companion skills: {original.name} (passive): {e.Message}"); }
                continue;
            }
            var copy = Object.Instantiate(original);
            copy.name = original.name;
            h.availableSkills[copy] = info.CurrentPoints;
            mine[original.ID] = copy;
        }
        foreach (var e in (data.equippedSkills ?? new List<IntNamed>()).OrderBy(e => e.Value))
            if (e.Key != null && mine.TryGetValue(e.Key, out var skill) && slots.Count < 4)
                slots.Add(new Slot { Skill = skill, Box = new SkillContainer { skill = skill }, Index = e.Value });
        Log.Info(slots.Count == 0 ? "Companion skills: none on its slots" :
            "Companion skills: " + string.Join(", ", slots.Select(s => $"{NameOf(s.Skill)} ({s.Skill.activationType}, {s.Skill.skillType})")));
    }

    static string NameOf(Skill s)
    {
        try { var n = s.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return s.name;
    }

    /// <summary>Each frame (RealBrain): running times and cooldowns, and a skill if one makes sense now.</summary>
    /// <param name="target">What it's fighting (null: nothing).</param>
    /// <param name="reach">How far its weapon reaches.</param>
    /// <param name="fromYou">How far it is from you (or the spot it holds).</param>
    public static void Tick(PlayerMaster p, Enemy target, float reach, float fromYou)
    {
        if (!p || p != loadedFor || slots.Count == 0) return;
        var h = p.SkillHandler;
        float dt = Time.deltaTime;
        foreach (var s in slots)
        {
            if (s.Cooldown > 0f) { s.Cooldown -= dt; s.Box.cooldownLeft = Mathf.Max(0f, s.Cooldown); }
            if (s.RunsUntil > 0f && Time.time >= s.RunsUntil) { s.RunsUntil = -1f; Finish(h, s); }
        }
        if (target) lastFight = Time.time;

        // toggles: off a while after the fight
        foreach (var s in slots)
            if (s.Skill.activationType == Skill.ActivationType.Toggle && s.Box.isActive && Time.time - lastFight > ToggleOffAfter)
            { s.Skill.OnFinishCallback?.InvokeAndClear(); StartCooldown(h, s); }

        if (Time.time < nextCast || p.currentState == p.stateAttack || p.currentState == p.stateDodge || !p.CanPerformSkills || !SkillHandler.CanUseSkill) return;
        float d = target ? Vector2.Distance(target.transform.position, p.transform.position) : float.MaxValue;
        foreach (var s in slots)
        {
            if (s.Box.isActive || s.Cooldown > 0f || !Affords(p, h, s.Skill)) continue;
            if (!Wanted(s.Skill, target, d, reach, fromYou)) continue;
            if (Cast(p, h, s)) { nextCast = Time.time + Gap; s.CastAt = Time.time; Log.Info($"Companion skills: {NameOf(s.Skill)}"); return; }
        }
    }

    /// <summary>Does this skill make sense now?</summary>
    static bool Wanted(Skill sk, Enemy target, float d, float reach, float fromYou)
    {
        bool buff = sk.StatModifiers?.Count > 0 || sk.MainStatModifiers?.Count > 0;
        switch (sk.skillType)
        {
            case Skill.PerformType.Attack:
            case Skill.PerformType.HoldAttack:
                return target && d <= reach + 0.5f;   // (an attack: on what's in reach)
            case Skill.PerformType.Dodge:
                return target ? d > reach + 1.5f && d < 7f : fromYou > CatchUp;   // (a dash: to close in, or to catch up)
            default:
                if (sk.activationType == Skill.ActivationType.Toggle) return target;   // (on through a fight)
                if (buff) return target && d < 6f || !target && fromYou > CatchUp;   // (a buff: the fight starting, or ground to make up)
                return target && d < Mathf.Max(reach + 2f, 4f);   // (anything else: in the fight)
        }
    }

    /// <summary>You asked (its card's slot clicked): power slot i now, if it can. Empty: done; else why not.</summary>
    public static string UseNow(PlayerMaster p, int i)
    {
        var s = slots.Find(x => x.Index == i);
        if (s == null) return "That slot is empty (right-click it to choose a power).";
        if (!p || p != loadedFor) return "Your companion isn't here.";
        var h = p.SkillHandler;
        if (s.Box.isActive) return $"{NameOf(s.Skill)} is already running.";
        if (s.Cooldown > 0f) return $"{NameOf(s.Skill)}: {s.Cooldown:0.0}s to wait.";
        if (p.currentState == p.stateAttack || p.currentState == p.stateDodge || !p.CanPerformSkills || !SkillHandler.CanUseSkill) return "It can't use a power right now.";
        if (!Affords(p, h, s.Skill)) return $"Not enough Kymia for {NameOf(s.Skill)}.";
        if (!Cast(p, h, s)) return $"{NameOf(s.Skill)} didn't work.";
        s.CastAt = Time.time;
        nextCast = Time.time + Gap;
        Log.Info($"Companion skills: {NameOf(s.Skill)} (you asked)");
        return "";
    }

    static bool Affords(PlayerMaster p, SkillHandler h, Skill sk)
    {
        if (sk.kymiaConsumption != Skill.KymiaConsumption.Immediate && sk.kymiaConsumption != Skill.KymiaConsumption.PerSecond
            && sk.kymiaConsumption != Skill.KymiaConsumption.OnAttackHit) return true;
        int cost;
        try { cost = sk.kymiaCost.Evaluate(h.GetSkillLevels(sk)); } catch { return false; }
        return cost <= 0 || p.CurrentKymiaPoints >= cost;   // (quietly: the game's own check says "No Kymia")
    }

    /// <summary>The game's skill bar press (SkillHandler.CallSkill), without the bar.</summary>
    static bool Cast(PlayerMaster p, SkillHandler h, Slot s)
    {
        var sk = s.Skill;
        var box = s.Box;
        sk.OnStartCallback = new OneShotAction(() => { h.AddSkillModifiers(sk); h.StartReceiver(sk); });
        sk.OnFinishCallback = new OneShotAction(() => { h.RemoveSkillModifiers(sk); h.FinishReceiver(sk); });
        sk.OnAttackHitCallback = new OneShotAction();
        float runs = 0f;
        try { runs = sk.Duration.Evaluate(h.GetSkillLevel(sk)); } catch { }
        switch (sk.activationType)
        {
            case Skill.ActivationType.Toggle:
                sk.OnStartCallback += () => box.isActive = true;
                sk.OnFinishCallback += () => { box.isActive = false; if (sk.behaviorGraph) h.RemoveAgentSkill(sk); };
                break;
            case Skill.ActivationType.AttackHitCooldown:
                box.isActive = true;
                sk.OnAttackHitCallback += () => { sk.OnFinishCallback?.InvokeAndClear(); box.isActive = false; if (sk.behaviorGraph) h.RemoveAgentSkill(sk); StartCooldown(h, s); };
                break;
            case Skill.ActivationType.CooldownAfterEnd:
                box.isActive = true;
                sk.OnFinishCallback += () => { box.isActive = false; if (sk.behaviorGraph) h.RemoveAgentSkill(sk); StartCooldown(h, s); };
                break;
            case Skill.ActivationType.Instant:
                sk.OnStartCallback += () => StartCooldown(h, s);
                break;
            case Skill.ActivationType.DurationInstaCooldown:
                sk.OnStartCallback += () => { box.isActive = true; s.RunsUntil = Time.time + runs; StartCooldown(h, s); };
                break;
            case Skill.ActivationType.Duration:
                sk.OnStartCallback += () => { box.isActive = true; s.RunsUntil = Time.time + runs; };
                break;
        }
        try { p.PerformSkill(sk); }
        catch (System.Exception e) { Log.Warn($"Companion skills: {NameOf(sk)}: {e.Message}"); box.isActive = false; return false; }
        if (sk.skillType == Skill.PerformType.HoldAttack)   // (held like a button: let go a moment later)
            Utils.TimeoutDeltaTime(0.7f, () => { if (p) p.ReleaseSkill(sk); });
        return true;
    }

    /// <summary>A skill with a running time, ended: its buffs off; then its cooldown (unless it already started).</summary>
    static void Finish(SkillHandler h, Slot s)
    {
        s.Skill.OnFinishCallback?.InvokeAndClear();
        s.Box.isActive = false;
        if (s.Skill.behaviorGraph) h.RemoveAgentSkill(s.Skill);
        if (s.Skill.activationType == Skill.ActivationType.Duration) StartCooldown(h, s);
    }

    static void StartCooldown(SkillHandler h, Slot s)
    {
        try { s.Cooldown = s.Skill.Cooldown.Evaluate(h.GetSkillLevels(s.Skill)); } catch { s.Cooldown = 5f; }
        s.CooldownTotal = Mathf.Max(0.01f, s.Cooldown);
        s.Box.cooldownLeft = s.Cooldown;
    }

    /// <summary>It's gone (or another scene): whatever's running, ended.</summary>
    public static void Stop()
    {
        var h = loadedFor ? loadedFor.SkillHandler : null;
        foreach (var s in slots)
            if (s.Box.isActive) { try { s.Skill.OnFinishCallback?.InvokeAndClear(); } catch { } s.Box.isActive = false; }
        slots.Clear();
        loadedFor = null;
    }
}

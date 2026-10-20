using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Team buffs (Session.ShareBuffs, the host's rule): the game is single player, so a skill's self-buff (Swift Foot's
/// speed, shields, stat boosts: Attack.buffs, applied by PlayerMaster.ResolveAttackBuffs as effects on the caster)
/// only ever lands on the caster. Here the caster's copy tells the area which attack and at what skill level; each
/// teammate within range makes the same effects the game's way (EffectCreator.CreateEffect) and adds them to their own
/// player, so their speed / stats really change. A buff without a duration (a toggled skill) ends on the teammate when
/// it ends on the caster (PlayerEffect "End" with the same effect name).
/// </summary>
[HarmonyPatch]
internal static class TeamBuffs
{
    public static Plugin Plugin;
    public const float Range = 8f;

    // caster -> the effects they gave us (the instances: one of ours with the same name is ours to keep)
    static readonly Dictionary<int, List<Effect>> fromCaster = new Dictionary<int, List<Effect>>();

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ResolveAttackBuffs)), HarmonyPostfix]
    static void AfterBuffs(PlayerMaster __instance, Attack attack)
    {
        if (!Session.ShareBuffs || attack == null || attack.buffs == null || attack.buffs.Count == 0) return;
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0 || !Plugin.AnyoneElseHere || !ReferenceEquals(__instance, LocalPlayer.Get())) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.TeamBuff);
        w.Put(attack.name);
        w.PutArray(__instance.CurrentAttackLevel() ?? new int[0]);
        var p = __instance.transform.position;
        w.Put(p.x); w.Put(p.y);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnBuff(NetDataReader r, int from)
    {
        string name = r.GetString();
        int[] level = r.GetIntArray();
        var at = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        var me = LocalPlayer.Get();
        if (!Session.ShareBuffs || !me || me.IsDead) return;
        var caster = Plugin.AvatarOf(from);
        if (caster) at = caster.position;
        if (Vector2.Distance(me.transform.position, at) > Range) return;
        var attack = PlayerHits.FindAttack(null, name);
        if (!attack || attack.buffs == null) return;

        if (!fromCaster.TryGetValue(from, out var given)) fromCaster[from] = given = new List<Effect>();
        foreach (var creator in attack.buffs)
        {
            var effect = creator.CreateEffect(new EffectCreationContext { Caster = me, Level = level.Length > 0 ? level : null });
            if (effect == null) continue;
            me.EffectsHandler.AddEffect(effect);
            // Only if it's really this instance on us now (an effect we already had just got refreshed: that one is ours).
            if (me.EffectsHandler.effects.Contains(effect)) given.Add(effect);
        }
        NetLog.Info($"{Plugin.NameOf(from)} shared a buff with you ({name})");
    }

    // ---- skills that change stats while they're on (toggles like Swift Foot, timed skills) ----
    //
    // SkillHandler.AddSkillModifiers puts the skill's stat modifiers (Skill.StatModifiers / MainStatModifiers, scaled
    // by the skill's level) on the caster while it's on; RemoveSkillModifiers takes them off. Teammates in range get
    // the same modifiers on their own stats, and lose them when the caster's come off.

    static readonly Dictionary<(int, string), (List<StatModifier<SecondaryStat>> sec, List<StatModifier<PrimaryStat>> prim)> skillMods =
        new Dictionary<(int, string), (List<StatModifier<SecondaryStat>>, List<StatModifier<PrimaryStat>>)>();
    static Dictionary<string, Skill> skills;

    [HarmonyPatch(typeof(SkillHandler), nameof(SkillHandler.AddSkillModifiers)), HarmonyPostfix]
    static void AfterSkillOn(SkillHandler __instance, Skill skill) => SendSkill(__instance, skill, true);

    [HarmonyPatch(typeof(SkillHandler), nameof(SkillHandler.RemoveSkillModifiers)), HarmonyPostfix]
    static void AfterSkillOff(SkillHandler __instance, Skill skill) => SendSkill(__instance, skill, false);

    static void SendSkill(SkillHandler handler, Skill skill, bool on)
    {
        if (!skill || (skill.StatModifiers.Count == 0 && skill.MainStatModifiers.Count == 0)) return;
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0 || !ReferenceEquals(handler.player, LocalPlayer.Get())) return;
        if (on && (!Session.ShareBuffs || !Plugin.AnyoneElseHere)) return;
        // On: to the players here. Off: to everyone, wherever they went since (else they'd keep it).
        var w = new NetDataWriter();
        w.Put((byte)MsgType.TeamSkill);
        w.Put(skill.name);
        w.Put(on);
        w.PutArray(handler.GetSkillLevels(skill) ?? new int[0]);
        var p = handler.player.transform.position;
        w.Put(p.x); w.Put(p.y);
        if (on) Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        else Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnSkill(NetDataReader r, int from)
    {
        string name = r.GetString();
        bool on = r.GetBool();
        int[] level = r.GetIntArray();
        var at = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        var me = LocalPlayer.Get();
        if (!me) return;
        var key = (from, name);
        if (skillMods.TryGetValue(key, out var had))   // off (or on again): take off what they gave us before
        {
            skillMods.Remove(key);
            foreach (var m in had.sec) me.characterStats.RemoveModifier(m);
            foreach (var m in had.prim) me.characterStats.RemoveModifier(m);
        }
        if (!on || !Session.ShareBuffs || me.IsDead) return;
        var caster = Plugin.AvatarOf(from);
        if (caster) at = caster.position;
        if (Vector2.Distance(me.transform.position, at) > Range) return;
        var skill = FindSkill(name);
        if (!skill) return;
        var sec = new List<StatModifier<SecondaryStat>>();
        var prim = new List<StatModifier<PrimaryStat>>();
        foreach (var lm in skill.StatModifiers) { var m = lm.Create(level); me.characterStats.AddModifier(m); sec.Add(m); }
        foreach (var lm in skill.MainStatModifiers) { var m = lm.Create(level); me.characterStats.AddModifier(m); prim.Add(m); }
        skillMods[key] = (sec, prim);
        NetLog.Info($"{Plugin.NameOf(from)} shared {name} with you", toast: true);
    }

    /// <summary>A player left: take off what they gave us.</summary>
    public static void Forget(int from)
    {
        var me = LocalPlayer.Get();
        var gone = new List<(int, string)>();
        foreach (var key in skillMods.Keys) if (key.Item1 == from) gone.Add(key);
        foreach (var key in gone)
        {
            var had = skillMods[key];
            skillMods.Remove(key);
            if (!me) continue;
            foreach (var m in had.sec) me.characterStats.RemoveModifier(m);
            foreach (var m in had.prim) me.characterStats.RemoveModifier(m);
        }
        if (fromCaster.TryGetValue(from, out var given))
        {
            fromCaster.Remove(from);
            if (me) foreach (var e in given) if (e != null && !e.IsRemoved) me.EffectsHandler.RemoveEffect(e);
        }
    }

    /// <summary>Disconnected: take off everything teammates gave us.</summary>
    public static void ForgetAll()
    {
        var ids = new HashSet<int>(fromCaster.Keys);
        foreach (var key in skillMods.Keys) ids.Add(key.Item1);
        foreach (int id in ids) Forget(id);
    }

    static Skill FindSkill(string name)
    {
        if (skills == null || !skills.ContainsKey(name))
        {
            skills = new Dictionary<string, Skill>();
            foreach (var s in Resources.FindObjectsOfTypeAll<Skill>()) skills[s.name] = s;
        }
        return skills.TryGetValue(name, out var found) ? found : null;
    }

    /// <summary>The caster's buff ended: so does the copy they gave us (that instance, never one of our own).</summary>
    public static void CasterLost(int from, string effectName)
    {
        if (string.IsNullOrEmpty(effectName) || !fromCaster.TryGetValue(from, out var given)) return;
        var e = given.Find(x => x != null && x.Name == effectName);
        if (e == null) return;
        given.Remove(e);
        var me = LocalPlayer.Get();
        if (me && !e.IsRemoved) me.EffectsHandler.RemoveEffect(e);
    }
}

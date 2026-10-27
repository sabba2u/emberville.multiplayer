using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// A hireling for a while (Better Streaming: a viewer sends you one). Your companion steps out (its save set aside in
/// memory; the file is never written while the hireling is out) and a ready-made adventurer teleports in, named after
/// the viewer, at your level + 5, its whole class tree learned and its first four powers on its slots (a class with a
/// tree: the demo's Archer has none). It waits for the game's item list (its clothes and weapon come from it). When its time
/// is up, or it goes down, it teleports out and yours comes back as it was. Another character loaded: yours back first.
/// </summary>
internal static class Hireling
{
    static PlayerData keptData;
    static CompanionMeta keptMeta;
    static float until;

    public static bool Active { get; private set; }
    public static float Left => Active ? Mathf.Max(0f, until - Time.time) : 0f;

    /// <summary>One for that long (on already: longer). Empty: done; else why not.</summary>
    public static string Hire(string viewer, float seconds)
    {
        if (Active) { until += seconds; return ""; }
        if (CompanionSave.PlayingSlot == 0) return "load a character first";
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me || me.characterStats == null || !me.characterStats.baseClass) return "no player";
        if (Hooks.Catalogue?.Invoke() == null) return "the item list is still loading (its clothes and weapon)";   // (it waits and tries again)
        // a class that has powers to give (the demo's Archer has no skill tree)
        var withPowers = Enumerable.Range(0, CompanionSave.Presets.Length)
            .Where(k => Tree(CompanionSave.Presets[k].cls).Any(n => n.skill && n.skill.activationType != Skill.ActivationType.Passive)).ToList();
        if (withPowers.Count == 0) return "no class's skill tree is loaded";
        if (CompanionSave.Dirty) { CompanionSave.Dirty = false; CompanionSave.Write(); }   // (yours as it is now, before it steps out)
        int i = withPowers[Random.Range(0, withPowers.Count)];
        var data = CompanionSave.MakePreset(i, out _, out _);
        var cls = CompanionSave.Presets[i].cls;
        data.playerName = string.IsNullOrWhiteSpace(viewer) ? "Hireling" : viewer.Trim();
        // its level: yours + 5 (base and class alike, so its class has the points for its whole tree)
        object baseKey = me.characterStats.baseClass.playerClassType;
        int level = Mathf.Max(1, GameData.playerData.GetCurrentLevel(baseKey)) + 5;
        SetLevel(data, baseKey, level);
        SetLevel(data, cls, level);
        // its whole class tree, and the first four powers it can cast on its slots
        int slot = 0;
        foreach (var node in Tree(cls))
        {
            data.nodeInfos.Add(new SkillNodeInfo { Id = node.uniqueID, CurrentPoints = Mathf.Max(1, node.maxPoints) });
            if (slot < 4 && node.skill && node.skill.activationType != Skill.ActivationType.Passive && !data.equippedSkills.Any(e => e.Key == node.skill.ID))
                data.equippedSkills.Add(new IntNamed { Key = node.skill.ID, Value = slot++ });
        }
        // yours out (the teleport), the hireling in
        Poof();
        keptData = CompanionSave.Data;
        keptMeta = CompanionSave.Meta;
        Active = true;
        until = Time.time + seconds;
        CompanionSave.Data = data;
        CompanionSave.Meta = new CompanionMeta { real = true };
        Companion.Reload();
        Log.Info($"Hireling: {data.playerName}, a level {level} {cls} for {seconds:0} s ({slot} powers on its slots)");
        return "";
    }

    /// <summary>Every frame (Plugin.Update): its time up, or down: out it goes.</summary>
    public static void Tick()
    {
        if (!Active) return;
        var p = RealPlayer.Current;
        if (Time.time >= until || p && RealPlayer.Down(p)) PutBack();
    }

    /// <summary>The hireling out (the teleport), yours back as it was.</summary>
    public static void PutBack()
    {
        if (!Active) return;
        Poof();
        Active = false;
        string name = CompanionSave.Data?.playerName;
        CompanionSave.Data = keptData;
        CompanionSave.Meta = keptMeta ?? new CompanionMeta();
        keptData = null; keptMeta = null;
        Companion.Reload();
        if (!string.IsNullOrEmpty(name)) Hooks.Say($"{name} heads off." + (CompanionSave.Has && !CompanionSave.Meta.dismissed ? $" {CompanionSave.Data.playerName} is back." : ""));
    }

    /// <summary>The teleport where the companion out now stands.</summary>
    static void Poof()
    {
        var p = RealPlayer.Current;
        if (p) { try { p.PlayExtraVFX("Teleport"); } catch { } return; }
        Companion.Body?.Fx("Teleport");
    }

    static void SetLevel(PlayerData data, object key, int level)
    {
        var table = data.GetTable(key);
        data.GetPlayerExperience(key);   // (first: SetXP on a key it hasn't got goes wrong)
        long total = 0;
        for (int i = 0; i < level - 1; i++) total += table == null || table.Count == 0 ? 20 * (i + 1) : System.Math.Max(1, i < table.Count ? table[i] : table[table.Count - 1]);
        data.SetXP(key, (int)System.Math.Min(total, int.MaxValue));
    }

    /// <summary>The class's own tree nodes (the game's copy, not a companion's copy of it).</summary>
    static IEnumerable<SkillNode> Tree(PlayerClassType c) =>
        Resources.FindObjectsOfTypeAll<PlayerCombatClass>()
            .Where(k => k && k.playerClassType == c && k.ClassSkillTree != null)
            .OrderBy(k => k.name.Contains("(BC companion)") ? 1 : 0)
            .Select(k => k.ClassSkillTree.skillNodes).FirstOrDefault()?.Where(n => n) ?? Enumerable.Empty<SkillNode>();
}

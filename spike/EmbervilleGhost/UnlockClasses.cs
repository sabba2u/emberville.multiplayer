using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Testing commands for combat classes:
/// /classes: every class in the game, which you've learned, which you have on.
/// /unlockclasses [name]: every class learned (or just that one), as the class screen's Learn button does it (its ID into
/// GameData.playerData.LearnedClasses), and the class screen itself unlocked (BoolID.UnlockedClasses, as the game's
/// developer panel does).
/// /lockclasses [name]: every class forgotten (or just that one), but the one you have on.
/// Both of the save's class lists: KnownClasses (the class screen shows its card, not "???") and LearnedClasses.
/// Your save keeps it.
/// </summary>
internal static class UnlockClasses
{
    /// <summary>Every combat class the game has loaded: its class screens' lists, and any other copy in memory.</summary>
    public static List<PlayerCombatClass> All()
    {
        var found = new List<PlayerCombatClass>(Resources.FindObjectsOfTypeAll<PlayerCombatClass>());
        foreach (var u in Resources.FindObjectsOfTypeAll<UIClassUpgrade>()) if (u.allCombatClasses != null) found.AddRange(u.allCombatClasses);
        foreach (var s in Resources.FindObjectsOfTypeAll<ClassSelectionUI>()) if (s.combatClasses != null) found.AddRange(s.combatClasses);
        return found.Where(c => c && c.playerClassType != PlayerClassType.None)
            .GroupBy(c => c.playerClassType).Select(g => g.First()).OrderBy(c => c.playerClassType).ToList();
    }

    static PlayerCombatClass Equipped()
    {
        var me = LocalPlayer.Get();
        return me && me.characterStats ? me.characterStats.equippedClass : null;
    }

    static string Id(PlayerCombatClass c) => c.playerClassType.GetEmbervilleID();

    /// <summary>The classes a command means: all of them, or the one whose name starts with what's typed (null: none such, said so).</summary>
    static List<PlayerCombatClass> Pick(string name)
    {
        var all = All();
        name = (name ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0) return all;
        var these = all.Where(c => c.playerClassType.ToString().ToLowerInvariant().StartsWith(name)).ToList();
        if (these.Count == 0) { Chat.System($"No class called \"{name}\": {string.Join(", ", all.Select(c => c.playerClassType))}."); return null; }
        return these;
    }

    /// <summary>/classes mage: what it is, from the game's own data: its description, what it adds to your stats, its skills.</summary>
    public static void Detail(string name)
    {
        var these = Pick(name);
        if (these == null) return;
        foreach (var c in these)
        {
            Chat.System($"--- {Text(c.Name, c.playerClassType.ToString())} ---");
            var about = Text(c.Description, "");
            if (about.Length > 0) Chat.System(about);
            var mods = (c.primaryStats ?? new List<StatModifier<PrimaryStat>>()).Select(m => Mod(m.stat.ToString(), m.modifier))
                .Concat((c.secondaryStats ?? new List<StatModifier<SecondaryStat>>()).Select(m => Mod(m.stat.ToString(), m.modifier)))
                .Where(m => m != null).ToList();
            Chat.System("While on: " + (mods.Count > 0 ? string.Join(", ", mods) : "no stat changes"));
            var nodes = c.ClassSkillTree?.skillNodes?.Where(n => n && n.skill).OrderBy(n => n.requiredLevel).ThenBy(n => n.row).ToList();
            if (nodes == null || nodes.Count == 0) { Chat.System("Skills: none (not finished in the game?)"); continue; }
            Chat.System($"Skills ({nodes.Count}): " + string.Join(", ", nodes.Select(n =>
                $"{Text(n.skill.Name, n.skill.name)} (lv {n.requiredLevel}, {n.skill.activationType}{(n.maxPoints > 1 ? $", {n.maxPoints} ranks" : "")})")));
        }
    }

    static string Mod(string stat, Modifier m)
    {
        if (m == null || !m.HasValidValue) return null;
        return m.type == ModifierType.Multiplier ? $"{stat} x{m.Value:0.##}" : $"{stat} {(m.Value >= 0 ? "+" : "")}{m.Value:0.##}";
    }

    /// <summary>A translated text in your language, without its formatting tags (the chat draws them as they are).</summary>
    static string Text(UnityEngine.Localization.LocalizedString s, string fallback)
    {
        try
        {
            var t = s == null || s.IsEmpty ? null : s.GetLocalizedString();
            if (!string.IsNullOrEmpty(t)) return System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", "").Replace("\n", " ").Trim();
        }
        catch { }
        return fallback;
    }

    public static void List()
    {
        if (!GameData.HasPlayerData) { NetLog.Info("Load a world first", toast: true); return; }
        var learned = GameData.playerData.LearnedClasses;
        var on = Equipped();
        foreach (var c in All())
        {
            int skills = c.ClassSkillTree?.skillNodes?.Count ?? 0;
            string state = c == on || on && on.playerClassType == c.playerClassType ? "equipped" : learned.Contains(Id(c)) ? "learned" : "not learned";
            Chat.System($"{c.playerClassType}: {state} ({skills} skills)");
        }
        if (!GameData.gameBools.IsTrue(BoolID.UnlockedClasses)) Chat.System("The class screen is still locked (/unlockclasses unlocks it).");
    }

    public static void Run(string name)
    {
        if (!GameData.HasPlayerData || !GameData.HasWorldData) { NetLog.Info("Load a world first", toast: true); return; }
        var these = Pick(name);
        if (these == null) return;
        var learned = GameData.playerData.LearnedClasses;
        var known = GameData.playerData.KnownClasses;   // (the class screen's cards: "???" until known)
        var added = new List<string>();
        foreach (var c in these)
        {
            if (!known.Contains(Id(c))) known.Add(Id(c));
            if (!learned.Contains(Id(c))) { learned.Add(Id(c)); added.Add(c.playerClassType.ToString()); }
        }
        GameData.gameBools[BoolID.UnlockedClasses] = true;
        NetLog.Info(added.Count > 0 ? $"Classes learned: {string.Join(", ", added)} (the class screen is unlocked)" :
            $"You already know {string.Join(", ", these.Select(c => c.playerClassType))}", toast: true);
    }

    /// <summary>/classscreen, /charscreen: that screen unlocked (its HUD button too) and opened; "off": locked
    /// again. (The game's own unlock flags: what its story sets.) No class is learned or forgotten.</summary>
    public static void Screen(string arg) => Unlock(arg, BoolID.UnlockedClasses, PanelName.ClassTree, "class screen");
    public static void CharacterScreen(string arg) => Unlock(arg, BoolID.UnlockedCharacterPanel, PanelName.Character, "character screen");

    static void Unlock(string arg, BoolID flag, PanelName panel, string name)
    {
        if (!GameData.HasPlayerData || !GameData.HasWorldData) { NetLog.Info("Load a world first", toast: true); return; }
        bool off = arg.Trim().ToLowerInvariant() == "off";
        GameData.gameBools[flag] = !off;
        if (off) { NetLog.Info($"The {name} is locked again", toast: true); return; }
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        if (panels) panels.TogglePanel(panel);
        NetLog.Info($"The {name} is unlocked" + (panels ? "" : " (open it from the HUD)"), toast: true);
    }

    static readonly (BoolID flag, string name)[] Screens =
    {
        (BoolID.UnlockedInventoryPanel, "inventory"), (BoolID.UnlockedQuestsPanel, "quests"), (BoolID.UnlockedMasteriesPanel, "masteries"),
        (BoolID.UnlockedClasses, "classes"), (BoolID.UnlockedCharacterPanel, "character"), (BoolID.CanShowMiniMap, "minimap"),
    };

    /// <summary>/unlockscreens: every screen the story unlocks bit by bit, unlocked (off: all locked again).</summary>
    public static void AllScreens(string arg)
    {
        if (!GameData.HasPlayerData || !GameData.HasWorldData) { NetLog.Info("Load a world first", toast: true); return; }
        bool off = arg.Trim().ToLowerInvariant() == "off";
        foreach (var (flag, _) in Screens) GameData.gameBools[flag] = !off;
        NetLog.Info($"{(off ? "Locked" : "Unlocked")}: {string.Join(", ", Screens.Select(x => x.name))}", toast: true);
    }

    public static void Lock(string name)
    {
        if (!GameData.HasPlayerData) { NetLog.Info("Load a world first", toast: true); return; }
        var these = Pick(name);
        if (these == null) return;
        var learned = GameData.playerData.LearnedClasses;
        var on = Equipped();
        var removed = new List<string>();
        bool kept = false;
        foreach (var c in these)
        {
            if (on && on.playerClassType == c.playerClassType) { kept = true; continue; }   // (the one you have on stays)
            bool was = learned.Remove(Id(c));
            if (GameData.playerData.KnownClasses.Remove(Id(c)) || was) removed.Add(c.playerClassType.ToString());
        }
        NetLog.Info((removed.Count > 0 ? $"Classes forgotten: {string.Join(", ", removed)}" : "Nothing to forget") +
            (kept ? $" ({on.playerClassType} stays: you have it on)" : ""), toast: true);
    }
}

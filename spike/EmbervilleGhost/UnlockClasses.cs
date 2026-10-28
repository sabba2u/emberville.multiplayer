using BepInEx.Configuration;
using HarmonyLib;
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
/// /unlockclasses also opens the full class tree ([Classes] FullClassTree): the demo caps class levels at 15 (its
/// MaxLevels table) though its XP table goes to 40, and blocks the later nodes ("coming soon"). XP earned past 15 was
/// kept all along, so those levels come back at once. /lockclasses (all of them) puts the cap back.
/// By default ([Classes] in the config) every world loads with all of this on: every class learned, the class screen
/// unlocked, levels to 50, every class at 50 (AllClassesAtTop); and each class has BonusPoints (50) more class points to spend than its level gives.
/// </summary>
[HarmonyPatch]
internal static class UnlockClasses
{
    static ConfigEntry<bool> fullTree, allLearned, allTop;
    static ConfigEntry<int> bonusPoints;
    static readonly HashSet<SkillNode> unblocked = new HashSet<SkillNode>();   // (the game's "coming soon" nodes we opened)

    public static void Bind(ConfigFile config)
    {
        fullTree = config.Bind("Classes", "FullClassTree", true, "Class levels past the demo's 15 (to 50) and its \"coming soon\" skills open; /lockclasses clears it, /unlockclasses sets it");
        allLearned = config.Bind("Classes", "AllClassesLearned", true, "Every class learned (and the class and character screens unlocked) when a world loads; /lockclasses clears it, /unlockclasses sets it");
        allTop = config.Bind("Classes", "AllClassesAtTop", true, "Every class at its highest level (50) when a world loads, yours and your companion's (only ever raised: the class screen's - and Reset still lower it)");
        bonusPoints = config.Bind("Classes", "BonusPoints", 50, "Class points to spend in each class on top of what its level gives (yours and your companion's)");
    }

    /// <summary>The extra class points each class has (the class screen and the Respec rows count them).</summary>
    public static int Bonus => bonusPoints != null ? System.Math.Max(0, bonusPoints.Value) : 0;

    [HarmonyPatch(typeof(UIClassUpgrade), nameof(UIClassUpgrade.GetAvailablePoints)), HarmonyPostfix]
    static void BonusOnScreen(ref int __result) => __result += Bonus;

    static PlayerData doneFor, companionDone;
    static string doneIn;
    static float companionAt;

    /// <summary>Each frame: a world just loaded (a save of its own): every class learned, if that's on; every class at
    /// its top level, if that's on (and the companion's, once it's out).</summary>
    static float retryAt;
    static bool failSaid;

    public static void Tick()
    {
        if (Time.unscaledTime < retryAt) return;
        // (not before the game's level caps are loaded: the levels can't be worked out without them)
        if (GameData.MaxLevels == null || !GameData.MaxLevels.ContainsKey("CombatClass")) return;
        try { Defaults(); }
        catch (System.Exception e)
        {
            retryAt = Time.unscaledTime + 5f;   // (again in a moment)
            if (!failSaid) { failSaid = true; NetLog.Warn($"Classes: the defaults failed (trying again): {e}", toast: false); }
        }
    }

    static void Defaults()
    {
        if (allTop != null && allTop.Value && Time.unscaledTime >= companionAt)
        {
            companionAt = Time.unscaledTime + 2f;
            var t = Integrations.CompanionRespec();
            if (t != null && t.Data != companionDone && All().Count > 0)
            {
                companionDone = t.Data;
                int up = All().Count(c => Respec.ToTop(t.Data, c.playerClassType));
                if (up > 0) { t.Save?.Invoke(); NetLog.Info($"Classes: {up} of {t.Who}'s classes to level {ClassTop}", toast: false); }
            }
        }
        if (allLearned == null || !allLearned.Value || !GameData.HasPlayerData || !GameData.HasWorldData) return;
        var data = GameData.playerData;
        string scene = LocalPlayer.Scene;
        if (data == doneFor && scene == doneIn) return;   // (again on each new area: the save object can be the title screen's)
        var all = All();
        if (all.Count == 0) return;   // (the classes not loaded yet: next frame)
        int added = 0;
        foreach (var c in all)
        {
            if (!data.KnownClasses.Contains(Id(c))) data.KnownClasses.Add(Id(c));
            if (!data.LearnedClasses.Contains(Id(c))) { data.LearnedClasses.Add(Id(c)); added++; }
        }
        GameData.gameBools[BoolID.UnlockedClasses] = true;
        GameData.gameBools[BoolID.UnlockedCharacterPanel] = true;   // (the character screen with it)
        if (fullTree.Value) FullTree(true);
        int raised = allTop.Value ? all.Count(c => Respec.ToTop(data, c.playerClassType)) : 0;
        doneFor = data;   // (only now: a failure above tries again)
        doneIn = scene;
        NetLog.Info($"Classes: {raised} raised to the top, all {all.Count} learned ({added} new), levels to {ClassTop}, {Bonus} bonus points each", toast: false);
    }

    static void FullTree(bool on)
    {
        fullTree.Value = on;
        // every class's level again, from the XP it has (the cap only changes how it's counted)
        foreach (var c in All())
        {
            var xp = GameData.playerData.GetPlayerExperience(c.playerClassType);
            if (xp != null) GameData.playerData.SetXP(c.playerClassType, xp.TotalXP);
        }
        foreach (var u in Resources.FindObjectsOfTypeAll<UIClassUpgrade>())
            if (u.activeCharStats) u.GatherStats(u.activeCharStats);   // (an open class screen redrawn)
    }

    /// <summary>The highest class level with the full tree on (the game's XP table stops at 40: past it, each level
    /// costs what its last did).</summary>
    public const int ClassTop = 50;

    [HarmonyPatch(typeof(PlayerData), "GetMaxLevel"), HarmonyPostfix]
    static void ClassCap(object key, ref int __result)
    {
        if (fullTree != null && fullTree.Value && key is PlayerClassType t && t != PlayerClassType.None)
            __result = System.Math.Max(__result, ClassTop - 1);
    }

    /// <summary>A class's level from its XP: past the end of the game's table, its last step again and again.</summary>
    [HarmonyPatch(typeof(PlayerData), "GetLevelTableBased"), HarmonyPrefix]
    static bool PastTheTable(int totalPoints, int maxLevel, ref int remaining, List<int> table, ref int nextLevel, ref int __result)
    {
        if (fullTree == null || !fullTree.Value || table != GameData.ClassXPTable || table == null || table.Count == 0 || maxLevel <= table.Count) return true;
        int Cost(int i) => System.Math.Max(1, i < table.Count ? table[i] : table[table.Count - 1]);
        remaining = totalPoints;
        int level = 0;
        while (level < maxLevel && remaining >= Cost(level))
        {
            remaining -= Cost(level);
            level++;
        }
        nextLevel = Cost(level);
        __result = level;
        return false;
    }

    [HarmonyPatch(typeof(UISkillTree), nameof(UISkillTree.DrawTree), typeof(SkillTree), typeof(System.Action<SkillNode, UISkillNode>), typeof(System.Action<SkillNode, UISkillNode>)), HarmonyPrefix]
    static void ComingSoon(SkillTree tree)
    {
        if (tree?.skillNodes == null || fullTree == null) return;
        foreach (var n in tree.skillNodes)
        {
            if (!n) continue;
            if (fullTree.Value && n.IsBlocked) { n.IsBlocked = false; unblocked.Add(n); }
            else if (!fullTree.Value && unblocked.Remove(n)) n.IsBlocked = true;
        }
    }

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
        if (string.IsNullOrWhiteSpace(name)) allLearned.Value = true;
        FullTree(true);
        NetLog.Info((added.Count > 0 ? $"Classes learned: {string.Join(", ", added)} (the class screen is unlocked)" :
            $"You already know {string.Join(", ", these.Select(c => c.playerClassType))}") +
            $"; class tree open to level {ClassTop}", toast: true);
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
        bool capped = string.IsNullOrWhiteSpace(name) && fullTree.Value;
        if (string.IsNullOrWhiteSpace(name)) allLearned.Value = false;   // (and not learned again at the next load)
        if (capped) FullTree(false);
        NetLog.Info((removed.Count > 0 ? $"Classes forgotten: {string.Join(", ", removed)}" : "Nothing to forget") +
            (kept ? $" ({on.playerClassType} stays: you have it on)" : "") +
            (capped ? "; class levels capped at the demo's 15 again" : ""), toast: true);
    }
}

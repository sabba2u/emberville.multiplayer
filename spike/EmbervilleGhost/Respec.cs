using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Insert > Respec (your character) and the Respec part of Insert > Companions (your companion): its levels, by hand.
/// - XP as a kill gives it: the next level's worth, to its level, its class, the weapon in hand and the armour worn
///   (as the + test key).
/// - One row per level it has: the character level, each class, each weapon and armour mastery, each gathering and
///   crafting job (Farming, Alchemy...). − a level down,
///   + a level up (that one alone), Reset back to level 1. "Show all" lists the ones it has no XP in yet too.
/// - Reset attributes: the attribute points back to spend (the class's starting values).
/// - Reset class points: the class's skill tree empty, its points back to spend (the skills it gave gone).
/// Going down a level takes back what the lower level can't pay for: the attributes (character level), the tree (a class).
/// /respec (and Ellie's "I want to RESPEC myself"): your attributes reset (Run).
/// The attributes: each primary stat back to the class's starting value (the game works out the free points from the
/// level and what's in them: CharacterStats.GetAvailablePoints), the secondary stats follow (the class's conversion
/// table, as when a point is spent), saved as the stat screen's Apply saves it. Not CharacterStats.ResetStats: that
/// builds new stats, without what gear and effects add to them.
/// </summary>
internal static class Respec
{
    /// <summary>Whose levels: the body (its stats, its skills), its save, and how to save it / reload its skills.</summary>
    internal sealed class Target
    {
        public string Id, Who;
        public bool You;
        public PlayerMaster P;
        public PlayerData Data;
        public Action Save, SkillsChanged;
        public Action<int> KillXp;
    }

    static readonly Dictionary<string, bool> showAll = new();

    /// <summary>/respec: your attributes back to spend.</summary>
    public static void Run()
    {
        var me = LocalPlayer.Get();
        var stats = me ? me.characterStats : null;
        if (!stats || !GameData.HasPlayerData || stats.baseClass == null) { NetLog.Info("Can't respec right now", toast: true); return; }
        int before = stats.GetAvailablePoints();
        Attributes(me, GameData.playerData);
        int now = stats.GetAvailablePoints();
        NetLog.Info(now > before ? $"Respec: {now} points to spend again (open your character screen)" : "Respec: you had no points spent", toast: true);
    }

    /// <summary>Your character (Insert > Respec).</summary>
    public static void DrawYou()
    {
        var p = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        var data = GameData.HasPlayerData ? GameData.playerData : null;
        if (!p || data == null) { ImGui.TextDisabled("Load a character first."); return; }
        // the unlocks (the same as /unlockscreens, /unlockclasses, /unlockmasteries)
        ImGui.SeparatorText("Unlock");
        if (ImGui.Button("Unlock all screens")) UnlockClasses.AllScreens("");
        ImGui.SameLine();
        if (ImGui.Button("Unlock all classes")) UnlockClasses.Run("");
        ImGui.SameLine();
        if (ImGui.Button("Unlock all masteries")) Arsenal.UnlockMasteries();
        ImGui.SameLine();
        if (ImGui.Button("Unlock everything")) { UnlockClasses.AllScreens(""); UnlockClasses.Run(""); Arsenal.UnlockMasteries(); }
        ImGui.TextDisabled("Screens: inventory, quests, masteries, classes, character, minimap. Locking again: /unlockscreens off, /lockclasses, /lockmasteries.");
        ImGui.SeparatorText("Levels");
        Draw(new Target
        {
            Id = "you", Who = "You", You = true, P = p, Data = data,
            Save = () => { },   // (the game saves yours itself)
            SkillsChanged = () => YourSkillsChanged(p),
            KillXp = need => p.GainXP(need, multiply: false),   // (the game's own: no difficulty scaling here)
        });
    }

    public static void Draw(Target t)
    {
        var p = t.P;
        var data = t.Data;
        var stats = p.characterStats;
        if (!stats || !stats.baseClass) { ImGui.TextDisabled("(not loaded yet)"); return; }
        object baseKey = stats.baseClass.playerClassType;
        var cls = stats.equippedClass;

        // XP as a kill
        var bxp = data.GetPlayerExperience(baseKey);
        int need = Mathf.Max(1, bxp.NextLevel - bxp.CurrentPoints);
        if (ImGui.Button($"Give a level's XP ({need})##kill{t.Id}"))
        {
            t.KillXp(need);
            stats.baseClassXP = data.GetPlayerExperience(baseKey);
            p.PlayExtraVFX("LevelUp");
            Done(t, $"{t.Who} {Be(t)} level {stats.baseClassXP.CurrentLevel}" +
                    (cls ? $"; {ClassName(cls.playerClassType)} level {data.GetPlayerExperience(cls.playerClassType).CurrentLevel}" : ""));
        }
        ImGui.SameLine();
        ImGui.TextDisabled("as a kill gives it: level, class, weapon in hand, armour worn");

        // the points to spend
        ImGui.Text($"Attribute points to spend: {stats.GetAvailablePoints()}");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Reset attributes##attr{t.Id}")) { Attributes(t); Done(t, $"{Whose(t)} attributes reset: {stats.GetAvailablePoints()} points to spend."); }
        if (cls)
        {
            ImGui.Text($"{ClassName(cls.playerClassType)} class points to spend: {ClassPoints(data, cls.playerClassType)}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Reset class points##tree{t.Id}")) { Tree(t, cls.playerClassType); Done(t, $"{Whose(t)} {ClassName(cls.playerClassType)} tree reset: {ClassPoints(data, cls.playerClassType)} points to spend."); }
        }
        else ImGui.TextDisabled("No class on: no class points.");

        // the levels
        showAll.TryGetValue(t.Id, out bool all);
        if (ImGui.Checkbox($"Show all (the ones with no XP yet too)##all{t.Id}", ref all)) showAll[t.Id] = all;
        if (ImGui.BeginTable($"levels{t.Id}", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            Row(t, "Character level", baseKey, isBase: true);
            Header("Classes");
            foreach (PlayerClassType c in Enum.GetValues(typeof(PlayerClassType)))
                if (c != PlayerClassType.None && (all || Has(data, c) || cls && cls.playerClassType == c)) Row(t, ClassName(c) + (cls && cls.playerClassType == c ? " (on)" : ""), c);
            Header("Weapon masteries");
            foreach (WeaponCategory w in Enum.GetValues(typeof(WeaponCategory)))
                if (w != WeaponCategory.None && w != WeaponCategory.Shields && (all || Has(data, w))) Row(t, Spaced(w.ToString()), w);
            Header("Armour masteries");
            foreach (GearWeight g in Enum.GetValues(typeof(GearWeight)))
                if (g != GearWeight.None && (all || Has(data, g))) Row(t, g.ToString(), g);
            Header("Gathering & crafting");
            foreach (LifeSkillJob j in Enum.GetValues(typeof(LifeSkillJob)))
                if (j != LifeSkillJob.None && (all || Has(data, j))) Row(t, Spaced(j.ToString()), j);
            ImGui.EndTable();
        }
    }

    static void Header(string text)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(text);
    }

    /// <summary>One level: its name, its level and XP, − + Reset.</summary>
    static void Row(Target t, string name, object key, bool isBase = false)
    {
        var data = t.Data;
        string id = key.GetEmbervilleID();
        int level = Has(data, key) ? data.GetCurrentLevel(key) : 1;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(name);
        ImGui.TableNextColumn();
        if (Has(data, key))
        {
            var xp = data.GetPlayerExperience(key);
            ImGui.Text($"level {xp.CurrentLevel}   ({xp.CurrentPoints}/{xp.NextLevel} XP)");
        }
        else ImGui.TextDisabled("level 1   (no XP)");
        ImGui.TableNextColumn();
        if (level <= 1) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"-##d{t.Id}{id}")) SetLevel(t, key, level - 1, isBase, name);
        if (level <= 1) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton($"+##u{t.Id}{id}")) SetLevel(t, key, level + 1, isBase, name);
        ImGui.SameLine();
        if (level <= 1) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Reset##r{t.Id}{id}")) SetLevel(t, key, 1, isBase, name);
        if (level <= 1) ImGui.EndDisabled();
    }

    static void SetLevel(Target t, object key, int to, bool isBase, string name)
    {
        var data = t.Data;
        var stats = t.P.characterStats;
        var table = data.GetTable(key);
        data.GetPlayerExperience(key);   // (first: AddXP / SetXP on a key it hasn't got go wrong)
        data.SetXP(key, XpAt(table, Mathf.Max(1, to)));
        int now = data.GetCurrentLevel(key);
        string more = "";
        if (isBase)
        {
            stats.baseClassXP = data.GetPlayerExperience(key);
            if (stats.GetAvailablePoints() < 0) { Attributes(t); more = " (attributes reset)"; }   // (more spent than the level gives)
            if (now > 1 && to > 1) t.P.PlayExtraVFX("LevelUp");
        }
        else if (key is PlayerClassType c && ClassPoints(data, c) < 0) { Tree(t, c); more = " (class points reset)"; }
        else if ((key is WeaponCategory || key is GearWeight) && t.You) Masteries(t.P);   // (what its levels unlock: as they are now)
        Done(t, $"{Whose(t)} {name}: level {now}{(to > now ? " (its highest)" : "")}{more}.");
    }

    /// <summary>The total XP that puts it at the start of a level (past the table: its last step again).</summary>
    static int XpAt(List<int> table, int level)
    {
        long total = 0;
        for (int i = 0; i < level - 1; i++) total += table == null || table.Count == 0 ? 20 * (i + 1) : Math.Max(1, i < table.Count ? table[i] : table[table.Count - 1]);
        return (int)Math.Min(total, int.MaxValue);
    }

    static bool Has(PlayerData data, object key)
    {
        string id = key.GetEmbervilleID();
        return !string.IsNullOrEmpty(id) && data.playerXPDictionary.ContainsKey(id);
    }

    /// <summary>The attributes back to the class's starting values, the rest of the stats from them, into the save.</summary>
    static void Attributes(Target t) => Attributes(t.P, t.Data);

    static void Attributes(PlayerMaster p, PlayerData data)
    {
        var stats = p.characterStats;
        foreach (var pair in stats.primStatDict)
        {
            var start = stats.baseClass.primaryStats.FirstOrDefault(s => s.stat.Equals(pair.Key));
            if (start != null) pair.Value.BaseValue = start.baseValue;
        }
        stats.baseClass.conversionTable.Calculate(stats.primStatDict, stats.secStatDict);
        stats.UpdateBaseValues(stats.primStatDict, stats.secStatDict);   // (the same values: tells the HUD and stat screen)
        data.SavePlayerStats(stats.primStatDict, stats.secStatDict);
        // less health / Kymia than before: not more than the new most
        if (p.CurrentHealthPoints > p.MaxHealthPoints) p.HealPercentNoBarNoFullNotification(1f, HealType.None);
        if (p.CurrentKymiaPoints > p.MaxKymiaPoints) p.ConsumeKymia(p.CurrentKymiaPoints - p.MaxKymiaPoints, false);
    }

    // ---- a class's skill tree ----

    /// <summary>A class's tree nodes (the game's own copy of the class: the companion's copies share their IDs).</summary>
    static List<SkillNode> Nodes(PlayerClassType c) =>
        Resources.FindObjectsOfTypeAll<PlayerCombatClass>()
            .Where(k => k && k.playerClassType == c && k.ClassSkillTree != null)
            .OrderBy(k => k.name.Contains("(BC companion)") ? 1 : 0)
            .Select(k => k.ClassSkillTree.skillNodes).FirstOrDefault()?.Where(n => n).ToList() ?? new List<SkillNode>();

    /// <summary>A class's node IDs, found once (the menu draws every frame: searching the game's objects each time made it crawl).</summary>
    static readonly Dictionary<PlayerClassType, HashSet<string>> nodeIds = new();

    static readonly Dictionary<PlayerClassType, float> nodeIdsAt = new();

    static HashSet<string> NodeIds(PlayerClassType c)
    {
        // (none found: the class isn't loaded yet; looked for again after a while, not every frame)
        if (nodeIds.TryGetValue(c, out var ids) && (ids.Count > 0 || Time.unscaledTime < nodeIdsAt[c])) return ids;
        nodeIdsAt[c] = Time.unscaledTime + 5f;
        return nodeIds[c] = new HashSet<string>(Nodes(c).Select(n => n.uniqueID));
    }

    /// <summary>Its class points to spend in that class, as the class screen counts them.</summary>
    static int ClassPoints(PlayerData data, PlayerClassType c)
    {
        int level = Has(data, c) ? data.GetCurrentLevel(c) : 1;
        var ids = NodeIds(c);
        int spent = data.nodeInfos.Where(i => i != null && ids.Contains(i.Id)).Sum(i => i.CurrentPoints);
        return level / 5 * 2 + 2 - spent;
    }

    /// <summary>The tree empty: each node's points to 0 (in its save), the skills they gave gone.</summary>
    static void Tree(Target t, PlayerClassType c)
    {
        var ids = NodeIds(c);
        foreach (var info in t.Data.nodeInfos.Where(i => i != null && ids.Contains(i.Id)))
        {
            info.CurrentPoints = 0;
            info.CommitedPoints = 0;
        }
        t.Data.equippedSkills?.RemoveAll(e => e.Key != null && !StillLearned(t.Data, e.Key));
        t.SkillsChanged?.Invoke();
    }

    static bool StillLearned(PlayerData data, string skillId)
    {
        foreach (var k in Resources.FindObjectsOfTypeAll<PlayerCombatClass>())
            if (k && k.ClassSkillTree != null)
                foreach (var n in k.ClassSkillTree.skillNodes)
                    if (n && n.skill && n.skill.ID == skillId && data.nodeInfos.Any(i => i != null && i.Id == n.uniqueID && i.CurrentPoints > 0)) return true;
        return false;
    }

    /// <summary>Yours: the skills of nodes now empty off your bar (the game's own removal), the class screen again.</summary>
    static void YourSkillsChanged(PlayerMaster p)
    {
        var h = p.SkillHandler;
        if (!h) return;
        var data = GameData.playerData;
        foreach (var k in Resources.FindObjectsOfTypeAll<PlayerCombatClass>().Where(k => k && k.ClassSkillTree != null && !k.name.Contains("(BC companion)")))
            foreach (var n in k.ClassSkillTree.skillNodes)
            {
                if (!n || !n.skill) continue;
                var info = data.nodeInfos.Find(i => i != null && i.Id == n.uniqueID);
                if (info != null && info.CurrentPoints > 0) continue;
                try { h.RemoveSkill(n.skill, update: false); } catch (Exception e) { NetLog.Warn($"Respec: {n.skill.name}: {e.Message}", toast: false); }
            }
        try { h.SetupSkillStackers(); } catch (Exception e) { NetLog.Warn($"Respec: the skill bar: {e.Message}", toast: false); }
    }

    /// <summary>Your weapon and armour masteries' unlocks (combos, passives, attack upgrades) again from their levels, as
    /// the game sets them up when a save loads (MasteriesManager.LoadData, without what it adds once: the damage
    /// modifiers). A level down takes off what that level gave.</summary>
    static void Masteries(PlayerMaster p)
    {
        var m = UnityEngine.Object.FindAnyObjectByType<MasteriesManager>();
        if (!m || m.masteriesBlocksPack == null) return;
        try
        {
            var data = GameData.playerData;
            p.ResetCombos();
            foreach (var e in m.AddedEffects) p.EffectsHandler.RemoveEffect(e);
            m.AddedEffects.Clear();
            p.attacksLevels.Clear();
            foreach (var w in m.weaponsOrder)
            {
                var tree = m.masteriesBlocksPack.weaponTrees.Find(x => x.key.Equals(w));
                if (tree == null) continue;
                var xp = data.GetPlayerExperience(w);
                for (int i = 0; i < tree.nodes.Count; i++) m.SetupLearnedCombos(xp, tree, i);
            }
            foreach (var g in m.armorsOrder)
            {
                var tree = m.masteriesBlocksPack.armorTrees.Find(x => x.key.Equals(g));
                if (tree == null) continue;
                var xp = data.GetPlayerExperience(g);
                for (int i = 0; i < tree.nodes.Count; i++) m.SetupLearnedCombos(xp, tree, i);
            }
        }
        catch (Exception e) { NetLog.Warn($"Respec: the masteries' unlocks: {e.Message}", toast: false); }
    }

    // ---- after each change ----

    static void Done(Target t, string said)
    {
        t.Save?.Invoke();
        // the screens showing it, again
        foreach (var c in Resources.FindObjectsOfTypeAll<UIClassUpgrade>())
            if (c && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy) c.GatherStats(t.P.characterStats);
        foreach (var h in Resources.FindObjectsOfTypeAll<UIStatsHandler>())
            if (h && h.gameObject.scene.IsValid() && h.gameObject.activeInHierarchy) h.GatherStats(t.P.characterStats);
        Chat.System(said);
        NetLog.Info($"Respec ({t.Id}): {said}", toast: false);
    }

    static string Be(Target t) => t.You ? "are" : "is";
    static string Whose(Target t) => t.You ? "Your" : t.Who + "'s";

    static readonly Dictionary<PlayerClassType, string> classNames = new();
    static readonly Dictionary<PlayerClassType, float> classNamesAt = new();

    /// <summary>A class's name, looked up once.</summary>
    static string ClassName(PlayerClassType c)
    {
        if (classNames.TryGetValue(c, out var known) && (known != null || Time.unscaledTime < classNamesAt[c])) return known ?? Spaced(c.ToString());
        classNamesAt[c] = Time.unscaledTime + 5f;
        string name = null;
        var k = Resources.FindObjectsOfTypeAll<PlayerCombatClass>().FirstOrDefault(x => x && x.playerClassType == c);
        try { name = k ? k.Name.GetLocalizedString() : null; } catch { }
        if (string.IsNullOrEmpty(name)) { classNames[c] = null; return Spaced(c.ToString()); }   // (not loaded yet: asked again in a while)
        return classNames[c] = name;
    }

    static string Spaced(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
}

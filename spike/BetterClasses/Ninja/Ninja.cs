using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// The Ninja: a class of our own in the game's empty Archer slot (so it's saved, levelled and shown like the others;
/// without this mod a save simply has no class there). Built at run time from the Wanderer: its card and stats, with
/// more speed, and a skill tree of our own (the hunter's hook, fire palm, chain lightning, thorn trap). As a Ninja your bare hands throw (Throws)
/// and your dodge is a vanish in smoke (SmokeStep). It shows on the class screen like any class: learn it there, or /ninja.
/// </summary>
[HarmonyPatch]
internal static class Ninja
{
    public const PlayerClassType Slot = PlayerClassType.Archer;
    public static string Id => Slot.GetEmbervilleID();
    static PlayerCombatClass cls;
    internal static Skill HookSkill, FireSkill, LightningSkill, TrapSkill;

    /// <summary>The class (built the first time the game's own classes are there to build it from).</summary>
    public static PlayerCombatClass Class => cls ? cls : (cls = Build());

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    /// <summary>That player is you, a Ninja now.</summary>
    public static bool Is(PlayerMaster p) => p && cls && p == Me && p.characterStats && p.characterStats.equippedClass == cls;

    /// <summary>/ninja: the class learnt and on.</summary>
    public static string Become()
    {
        var me = Me;
        if (!me || GameData.playerData == null) return "Load a character first.";
        var c = Class;
        if (!c) return "The game's classes aren't loaded yet: open the class screen once, then try again.";
        GameData.playerData.KnownClasses.Add(Id);
        GameData.playerData.LearnedClasses.Add(Id);
        me.characterStats.EquipClass(c);
        int learnt = LearnAll(me);
        return $"You're a Ninja: your hands throw, your dodge vanishes in smoke, and {learnt} skills are learnt (put them on your skill bar from the class screen).";
    }

    /// <summary>(For testing) every Ninja skill learnt, as if the points were spent: in your skill list to put on the bar.</summary>
    static int LearnAll(PlayerMaster me)
    {
        int n = 0;
        foreach (var node in cls.ClassSkillTree.skillNodes)
        {
            if (!node || !node.skill) continue;
            if (node.CurrentPoints < node.maxPoints)
            {
                node.CommitedPoints = node.maxPoints;
                node.CurrentPoints = node.maxPoints;
                me.SkillHandler?.AddSkill(node.skill, node.CurrentPoints);
            }
            n++;
        }
        return n;
    }

    /// <summary>Every frame: as a Ninja, your bare hands throw.</summary>
    public static void Tick()
    {
        var me = Me;
        if (me && cls) Throws.Keep(me, Is(me));
        FirePalm.Tick();
        Lightning.Tick();
        ThornTrap.Tick();
    }

    // ---- the class ----

    static PlayerCombatClass Build()
    {
        var like = Resources.FindObjectsOfTypeAll<PlayerCombatClass>()
            .Where(c => c && c.playerClassType == PlayerClassType.Wanderer && c.ClassSkillTree != null && c.ClassSkillTree.skillNodes.Count > 0)
            .OrderBy(c => c.name.Contains("(") ? 1 : 0)   // (the game's own, not a companion's copy)
            .FirstOrDefault();
        if (!like) return null;
        var c = Object.Instantiate(like);
        c.name = "Class_Ninja";
        c.hideFlags = HideFlags.DontUnloadUnusedAsset;
        c.playerClassType = Slot;
        c.Name = Text.Of("Ninja");
        c.Description = Text.Of("Fast and hard to pin down. Throws from range with bare hands, vanishes in smoke when dodging, and drags foes in with the hunter's hook.");
        c.videoDescription = null;
        c.primaryStats = new List<StatModifier<PrimaryStat>>(like.primaryStats);
        c.secondaryStats = new List<StatModifier<SecondaryStat>>(like.secondaryStats)
        {
            new StatModifier<SecondaryStat> { stat = SecondaryStat.MoveSpeed, modifier = new Modifier(15f, ModifierType.Multiplier) },   // (+15%)
        };
        c.OnKnownEvents = new CustomEventList { events = new List<CustomEvent>() };   // (not the Wanderer's story events)
        c.OnLearnEvents = new CustomEventList { events = new List<CustomEvent>() };
        c.OnEquipEvents = new CustomEventList { events = new List<CustomEvent>() };
        c.skillTree = Tree(like.ClassSkillTree);
        Log.Info($"Better Classes: the Ninja is ready ({c.skillTree.skillNodes.Count} skills)");
        return c;
    }

    static SkillTree Tree(SkillTree like)
    {
        var node = like.skillNodes.First(n => n && n.skill && n.skill.activationType != Skill.ActivationType.Passive);
        HookSkill = MakeSkill("ninja_hook", "Hunter's Hook", "Throw a hook on a chain. A foe it catches is dragged to you and stunned.",
            Icon("Skill_Knight_ChargeAttack", node.skill.uiIcon), cooldown: 4f, kymia: 10, like: node.skill);
        FireSkill = MakeSkill("ninja_firepalm", "Fire Palm", "Hold: fire pours from your palms, burning everything in front of you, until you let go or your Kymia runs out.",
            Icon("Skill_Mage_MagicShot", node.skill.uiIcon), cooldown: 1.5f, kymia: FirePalm.KymiaPerSecond, like: node.skill);
        // held like the Mage's beam: Kymia drains each second while held, the cooldown starts when it ends
        FireSkill.activationType = Skill.ActivationType.CooldownAfterEnd;
        FireSkill.kymiaConsumption = Skill.KymiaConsumption.PerSecond;
        FireSkill.canHold = true;
        LightningSkill = MakeSkill("ninja_chainlightning", "Chain Lightning", "Lightning leaps from your hands to the nearest foe, then from foe to foe: each one hit is hurt and stunned for a moment.",
            Icon("Skill_Mage_MagicBeam", node.skill.uiIcon), cooldown: 5f, kymia: 20, like: node.skill);
        TrapSkill = MakeSkill("ninja_thorntrap", "Spike Trap", "Set a spike trap at your feet (three at most, each armed for half a minute). The first foe to step on it is caught in a burst of steel spikes: hurt and held fast.",
            Icon("Skill_Knight_WarStomp", node.skill.uiIcon), cooldown: 3f, kymia: 15, like: node.skill);
        var tree = ScriptableObject.CreateInstance<SkillTree>();
        tree.name = "Tree_Ninja";
        tree.hideFlags = HideFlags.DontUnloadUnusedAsset;
        tree.skillNodes.Add(Node("ninja_hook", HookSkill, row: 0, level: 1, node));
        tree.skillNodes.Add(Node("ninja_firepalm", FireSkill, row: 1, level: 1, node));
        tree.skillNodes.Add(Node("ninja_chainlightning", LightningSkill, row: 2, level: 1, node));
        tree.skillNodes.Add(Node("ninja_thorntrap", TrapSkill, row: 3, level: 1, node));
        return tree;
    }

    static SkillNode Node(string id, Skill skill, int row, int level, SkillNode like)
    {
        var n = Object.Instantiate(like);
        n.name = id;
        n.hideFlags = HideFlags.DontUnloadUnusedAsset;
        n.uniqueID = id;
        n.skill = skill;
        n.icon = skill.uiIcon;
        n.row = row;
        n.requiredLevel = level;
        n.maxPoints = 1;
        n.parent = null;
        n.IsBlocked = false;
        return n;
    }

    /// <summary>A skill of ours: on the skill bar, fired whenever (Kymia and a cooldown); what it does is ours (Cast).</summary>
    static Skill MakeSkill(string id, string name, string description, Sprite icon, float cooldown, int kymia, Skill like)
    {
        var s = Object.Instantiate(like);
        s.name = id;
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        s.id = id;
        s.uiIcon = icon;
        s.Name = Text.Of(name);
        s.Description = Text.Of(description);
        s.LevelParam = Text.Of("");
        s.improvedFromSkill = null;
        s.displayLevelParamText = false;
        s.activationType = Skill.ActivationType.Instant;
        s.kymiaConsumption = Skill.KymiaConsumption.Immediate;
        s.kymiaCost = kymia;
        s.skillType = Skill.PerformType.Other;
        s.canHold = false;
        s.attack = null;
        s.behaviorGraph = null;
        s.Duration = 0f;
        s.toggleUIOnUse = false;
        s.Cooldown = cooldown;
        s.StatModifiers = new List<LevelStatModifier<SecondaryStat>>();
        s.MainStatModifiers = new List<LevelStatModifier<PrimaryStat>>();
        s.effects = new NamedEffectCreator[0];
        s.showBuffUI = false;
        s.makePlayerInvincible = false;
        s.sfxSkillStart = null;
        s.sfxSkillEnd = null;
        s.animation = "";
        s.extraFxAnimation = "";
        return s;
    }

    static Sprite Icon(string skill, Sprite fallback) =>
        Resources.FindObjectsOfTypeAll<Skill>().FirstOrDefault(s => s && s.name == skill && s.uiIcon)?.uiIcon ?? fallback;

    // ---- on the class screens ----

    /// <summary>The class picker: the Ninja in the Archer's place, and known (so it can be learnt there).</summary>
    [HarmonyPatch(typeof(ClassSelectionUI), "LoadData"), HarmonyPrefix]
    static void IntoPicker(ClassSelectionUI __instance)
    {
        if (!Add(__instance.combatClasses)) return;
        try { GameData.playerData?.KnownClasses.Add(Id); } catch (System.Exception e) { Log.Warn($"Better Classes: {e.Message}"); }
    }

    /// <summary>The class tree screen (which also puts your saved class back on, and your learnt skills).</summary>
    [HarmonyPatch(typeof(UIClassUpgrade), "LoadData"), HarmonyPrefix]
    static void IntoTree(UIClassUpgrade __instance) => Add(__instance.allCombatClasses);

    static bool Add(List<PlayerCombatClass> list)
    {
        if (list == null) return false;
        var c = Class;
        if (!c) return false;
        if (!list.Contains(c)) { list.RemoveAll(x => !x || x.playerClassType == Slot); list.Add(c); }
        return true;
    }

    /// <summary>(For testing) 20 more skill points while the Ninja is your class.</summary>
    public const int ExtraPoints = 20;

    [HarmonyPatch(typeof(UIClassUpgrade), nameof(UIClassUpgrade.GetAvailablePoints)), HarmonyPostfix]
    static void MorePoints(UIClassUpgrade __instance, ref int __result)
    {
        if (cls && __instance.activeCharStats && __instance.activeCharStats.equippedClass == cls) __result += ExtraPoints;
    }

    // ---- its skills ----

    /// <summary>One of ours went off (it got past the Kymia and the cooldown): the hook thrown (done at once), or the
    /// fire started (done when it's let go or the Kymia runs out: Release).</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformSkill)), HarmonyPostfix]
    static void Cast(PlayerMaster __instance, Skill skill)
    {
        if (!skill || __instance.currentSkill != skill || (skill != HookSkill && skill != FireSkill && skill != LightningSkill && skill != TrapSkill)) return;
        bool held = false;
        try
        {
            if (__instance != Me) return;   // (a companion with the Ninja's skills: nothing yet)
            if (skill == HookSkill) { if (Hooks.ThrowHook == null || !Hooks.ThrowHook(__instance)) Hook.Throw(__instance); }
            else if (skill == LightningSkill) Lightning.Cast(__instance);
            else if (skill == TrapSkill) ThornTrap.Set(__instance);
            else held = FirePalm.Start(__instance);
        }
        catch (System.Exception e) { Log.Warn($"Better Classes: {skill.name}: {e}"); }
        finally { if (!held) skill.OnFinishCallback?.InvokeAndClear(); }
    }

    /// <summary>The fire's button let go, or out of Kymia (the game's drain lets go for you): the fire out, the skill
    /// done (its Kymia drain stops, its cooldown starts).</summary>
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ReleaseSkill)), HarmonyPrefix]
    static bool Release(PlayerMaster __instance, Skill skill)
    {
        if (!skill || skill != FireSkill) return true;
        FirePalm.Stop();
        skill.OnFinishCallback?.InvokeAndClear();
        return false;
    }

    /// <summary>The fire going while you can't (dead, knocked down, stunned, a screen): put out.</summary>
    internal static void EndFire()
    {
        if (FireSkill) FireSkill.OnFinishCallback?.InvokeAndClear();
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Each timed effect in the game's own buff list on the left (its name, an icon, the seconds ticking down), as the
/// game's skills show theirs (Bus UIBuffEvent): added when it lands, its time put right when it's bought again, gone
/// when it ends. The icon is a game skill's that fits (regeneration: Life Recovery; haste: Swift Foot...). The game's
/// own statuses (poison, burn, shock, bleed) show there already: those are left to it.
/// </summary>
internal static class BuffIcons
{
    static readonly HashSet<string> GameShows = new() { "poison", "fire", "shock", "bleed" };

    static readonly Dictionary<string, string> SkillFor = new()
    {
        ["regen"] = "Skill_Wanderer_LifeRecovery", ["haste"] = "Skill_Wanderer_SwiftFoot", ["shield"] = "Skill_Acolyte_Sage_Shield",
        ["kymia"] = "Skill_Mage_ManaOverflow", ["berserk"] = "Skill_Wanderer_FocusedAttack", ["secondwind"] = "Skill_Wanderer_SecondWind",
        ["gold"] = "Skill_Wanderer_LuckyCharm", ["goldenpet"] = "Skill_Wanderer_LuckyCharm", ["battlechickens"] = "Skill_Knight_MoraleBoost",
        ["guardians"] = "Skill_Mage_Synergy", ["chainlightning"] = "Skill_Mage_MagicBeam",
        ["hireling"] = "Skill_Mage_Warcaster", ["freeze"] = "Skill_Knight_Stunned", ["storm"] = "Skill_AcolyteSorcerer_GroupSkill",
        ["fists"] = "Skill_Knight_ChargeAttack", ["roulette"] = "Skill_Knight_ChargeAttack",
        ["tunnel"] = "Skill_Mage_DeathWish", ["lightsout"] = "Skill_Mage_DeathWish",
    };
    const string Curse = "Skill_Mage_Curse";   // (the rest: the camera, the controls, the filters)

    static readonly Dictionary<string, Sprite> icons = new();

    static Sprite Icon(string id)
    {
        if (icons.TryGetValue(id, out var s) && s) return s;
        string want = SkillFor.TryGetValue(id, out var n) ? n : Curse;
        var skill = Resources.FindObjectsOfTypeAll<Skill>().FirstOrDefault(k => k && k.name == want && k.uiIcon)
                    ?? Resources.FindObjectsOfTypeAll<Skill>().FirstOrDefault(k => k && k.name == Curse && k.uiIcon);
        s = skill ? skill.uiIcon : null;
        if (s) icons[id] = s;
        return s;
    }

    static object Owner(string id) => "BS_buff_" + id;

    /// <summary>On, or its time put right (seconds left).</summary>
    public static void Show(string id, string name, double seconds)
    {
        if (GameShows.Contains(id) || seconds <= 0) return;
        try
        {
            Bus<UIBuffEvent>.Invoke(new UIBuffEvent { EventType = UIBuffEvent.Event_Type.Add, Owner = Owner(id), Icon = Icon(id), BuffName = name, Duration = Mathf.CeilToInt((float)seconds) });
        }
        catch (System.Exception e) { Log.Warn($"Streaming: the buff icon for {id}: {e.Message}"); }
    }

    public static void Hide(string id)
    {
        if (GameShows.Contains(id)) return;
        try { Bus<UIBuffEvent>.Invoke(new UIBuffEvent { EventType = UIBuffEvent.Event_Type.Remove, Owner = Owner(id) }); }
        catch (System.Exception e) { Log.Warn($"Streaming: the buff icon for {id}: {e.Message}"); }
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace BetterNPCsAndQuests;

/// <summary>
/// What happens in the world, for quests (kill objectives) and rules: kill &lt;creature&gt;, enter &lt;scene&gt;,
/// pickup &lt;item&gt;, and from the multiplayer mod's dungeon: boss_kill &lt;floor&gt;, descent_floor &lt;n&gt;.
/// On our own or as host they go to the Book; a guest sends them to the host.
/// </summary>
[HarmonyPatch]   // (without it PatchAll skips the patches in this class)
public static class Events
{
    static bool hooked;

    /// <summary>Something happened (for the multiplayer mod: boss_kill, descent_floor).</summary>
    public static void Raise(string ev, string arg)
    {
        if (!State.Loaded || !Wanted(ev)) return;
        if (Hooks.IsClient()) Hooks.Ask($"event {ev} {arg}");
        else State.Book.Raise(ev, arg);
    }

    /// <summary>Only what a rule or a kill objective listens for (a guest doesn't send every pickup).</summary>
    static bool Wanted(string ev) =>
        State.Lib.Rules.Any(r => r.Event == ev) || ev == "kill" && State.Lib.Quests.Any(q => q.Steps.Any(s => s.Objectives.Any(o => o.Kind == "kill")));

    internal static void Hook()
    {
        if (hooked) return;
        hooked = true;
        SceneManager.sceneLoaded += (scene, _) => { if (GameData.HasWorldData) Raise("enter", scene.name); };
    }

    // ---- kills: ours (the local player's blow), once per creature ----

    static readonly HashSet<int> counted = new();

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPostfix]
    static void AfterHit(Enemy __instance, DamageData damageData)
    {
        if (!__instance) return;
        int id = __instance.GetInstanceID();
        if (!__instance.IsDead) { counted.Remove(id); return; }
        if (__instance.name.StartsWith("NPC_") || !ReferenceEquals(damageData.Aggressor, LocalPlayer.Get()) || !counted.Add(id)) return;
        Raise("kill", KeyOf(__instance.name));
    }

    /// <summary>"Mob_Glowbat_02(Clone)" / "Chicken_01 (3)" → "Mob_Glowbat" / "Chicken" (as the Bestiary names them).</summary>
    internal static string KeyOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("(Clone)", "").Trim();
        int sp = name.LastIndexOf(" (");
        if (sp > 0 && name.EndsWith(")")) name = name.Substring(0, sp);
        int u = name.LastIndexOf('_');
        if (u > 0 && int.TryParse(name.Substring(u + 1), out _)) name = name.Substring(0, u);
        return name;
    }

    // ---- pickups (not our own takes / rewards) ----

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem)), HarmonyPrefix]
    static void BeforeAdd(ItemObject itemObj, out int __state) => __state = itemObj != null ? itemObj.amount : 0;

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem)), HarmonyPostfix]
    static void AfterAdd(ItemObject itemObj, int __result, int __state)
    {
        if (Items.Ours || itemObj == null || !itemObj.data || __state - __result <= 0) return;
        Raise("pickup", itemObj.data.addressKey);
    }
}

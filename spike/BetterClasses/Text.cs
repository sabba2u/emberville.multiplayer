using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.Localization;

namespace BetterClasses;

/// <summary>
/// Our own words in the game's text slots. The game's names and descriptions are LocalizedStrings looked up in its
/// tables; ours aren't in any table, so each of ours is a fresh LocalizedString that answers with our text.
/// </summary>
[HarmonyPatch]
internal static class Text
{
    static readonly ConditionalWeakTable<LocalizedString, string> ours = new();   // (by the instance itself)

    /// <summary>A text slot that reads <paramref name="text"/>.</summary>
    public static LocalizedString Of(string text)
    {
        var s = new LocalizedString();
        ours.Add(s, text);
        return s;
    }

    [HarmonyPatch(typeof(LocalizedString), nameof(LocalizedString.GetLocalizedString), new System.Type[0]), HarmonyPrefix]
    static bool Ours(LocalizedString __instance, ref string __result)
    {
        if (__instance == null || !ours.TryGetValue(__instance, out var text)) return true;
        __result = text;
        return false;
    }
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BetterCompanions;

/// <summary>
/// The game's day/night light tint (WorldLight2D) throws if a light hears the time of day before its Start found its
/// Light2D, or has none (a light on a second player, or on one of our stripped copies). Such a light is set up late,
/// or skipped, instead of throwing every time the hour changes; each one is named in the log once.
/// </summary>
[HarmonyPatch(typeof(WorldLight2D), "UpdateLightColor")]
internal static class LightGuard
{
    static readonly HashSet<int> told = new();

    static bool Prefix(WorldLight2D __instance)
    {
        if (__instance.mainLight) return true;
        var light = __instance ? __instance.GetComponent<Light2D>() : null;
        if (light)
        {
            __instance.mainLight = light;
            __instance.baseColor = light.color;
            return true;
        }
        if (__instance && told.Add(__instance.GetInstanceID()))
            Log.Info($"A day/night light with no light to tint, skipped: {Path(__instance.transform)}");
        return false;
    }

    static string Path(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}

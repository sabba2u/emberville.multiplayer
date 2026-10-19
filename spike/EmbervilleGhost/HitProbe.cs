using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Recon tool: logs every hit on anything damageable (and every gather interaction), with the class, the object's
/// path in the scene and whether it died. Tells us what a chicken / crate / tree really is in the game's code before
/// we make the host own it. Toggle in the Insert menu.
/// </summary>
[HarmonyPatch]
internal static class HitProbe
{
    public static bool Enabled = true;

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(Enemy), typeof(DamageableObject), typeof(SpiderEgg), typeof(TestDummy), typeof(DamageableCollider) })
        {
            var m = AccessTools.Method(type, nameof(IDamageable.ApplyDamage), new[] { typeof(DamageData) });
            if (m != null) yield return m;
        }
        yield return AccessTools.Method(typeof(GatheringObject), nameof(GatheringObject.OnInteract));
    }

    static void Postfix(object __instance, object[] __args)
    {
        if (!Enabled) return;
        try
        {
            var mb = __instance as MonoBehaviour;
            string what = __instance.GetType().Name;
            string detail = "";
            if (__args.Length > 0 && __args[0] is DamageData d)
                detail = $" dmg {d.Amount} from {(d.Aggressor as MonoBehaviour ? ((MonoBehaviour)d.Aggressor).name : d.Aggressor?.GetType().Name ?? "?")}";
            if (__instance is IDamageable dmg) detail += dmg.IsDead ? " -> DEAD" : "";
            if (__instance is Enemy e) detail += $" type {e.enemyType}";
            NetLog.Info($"[hit] {what} '{(mb ? Path(mb.transform) : "?")}'{detail}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"HitProbe: {e.Message}");
        }
    }

    public static string Path(Transform t)
    {
        string p = t.name;
        for (var x = t.parent; x; x = x.parent) p = x.name + "/" + p;
        return t.gameObject.scene.name + ":" + p;
    }
}

using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The town's broken buildings (RepairableBuilding). In the demo they have no save ids, so the game would store all of
/// them under "" (one repaired = all repaired after a reload, or none): each gets its own id from its name.
/// /repair fixes the nearest broken one with the usual materials (the same as walking onto it and pressing F).
/// </summary>
internal static class Repairs
{
    static void EnsureId(RepairableBuilding b)
    {
        if (b && string.IsNullOrEmpty(b.saveID)) b.saveID = "EG_" + b.name;
    }

    [HarmonyPatch(typeof(RepairableBuilding), nameof(RepairableBuilding.OnLoad))]
    static class IdOnLoad { static void Prefix(RepairableBuilding __instance) => EnsureId(__instance); }

    [HarmonyPatch(typeof(RepairableBuilding), "OnSave")]
    static class IdOnSave { static void Prefix(RepairableBuilding __instance) => EnsureId(__instance); }

    [HarmonyPatch(typeof(RepairableBuilding), "Start")]
    static class IdOnStart { static void Prefix(RepairableBuilding __instance) => EnsureId(__instance); }

    public static void RepairNearest()
    {
        var me = LocalPlayer.Get();
        if (!me) { Chat.System("Load into a world first."); return; }
        var b = Object.FindObjectsByType<RepairableBuilding>(FindObjectsSortMode.None)
            .Where(x => !x.isRepaired)
            .OrderBy(x => Vector2.Distance(me.transform.position, x.transform.position))
            .FirstOrDefault();
        if (!b) { Chat.System("No broken buildings in this area."); return; }
        float d = Vector2.Distance(me.transform.position, b.transform.position);
        if (d > 15f) { Chat.System($"Nearest broken building: {Nice(b)}, {d:0} away. Walk closer, then /repair."); return; }
        b.Initialize();
        if (!b.CheckResources())
        {
            var missing = b.GetItemCosts.Where(r => r?.itemData && Singleton<Inventory>.main.GetTotalAmount(r.itemData, b.sharedChest) < r.Amount)
                .Select(r => $"{r.Amount} {GameUi.ItemName(r.itemData)} (have {Singleton<Inventory>.main.GetTotalAmount(r.itemData, b.sharedChest)})");
            Chat.System($"{Nice(b)} needs: {string.Join(", ", missing)}. /resources gives you plenty.");
            return;
        }
        b.hasEnough = true;
        b.Repair();
        Chat.System($"Repaired {Nice(b)}.");
    }

    static string Nice(RepairableBuilding b) => b.name.Replace("Repairable_", "");
}

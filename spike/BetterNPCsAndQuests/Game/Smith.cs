using HarmonyLib;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// (trigger: "command sharpen") in a conversation: the weapon in the talking player's hand gets one better: its damage
/// up a tenth (at least 1) and "+1" (then +2, +3...) on its name. Kept on the item itself (its damage, and a text
/// attribute the game saves with it and never reads), so it stays sharp without the mod too, only the "+N" goes.
/// </summary>
[HarmonyPatch]
internal static class Smith
{
    const ItemRandomValues.Attr Sharpened = (ItemRandomValues.Attr)87;   // (85 / 86: Better Chests' favourite / junk)

    public static int Level(ItemObject item)
    {
        var a = item?.randomValues?.strAttributes?.Find(x => x != null && x.Key == Sharpened);
        return a != null && int.TryParse(a.Value, out int n) ? n : 0;
    }

    /// <summary>$holding_weapon: a weapon in the talking player's hands.</summary>
    public static bool Holding()
    {
        var me = LocalPlayer.Get();
        return me && me.currentWeaponItem?.data is CollectableWeapon;
    }

    public static void Sharpen()
    {
        var me = LocalPlayer.Get();
        var item = me ? me.currentWeaponItem : null;
        if (item?.randomValues == null || !(item.data is CollectableWeapon)) { Hooks.Say("Nothing in your hands to sharpen"); return; }
        var rv = item.randomValues;
        var d = rv.damageRange;
        d.x += Mathf.Max(1, Mathf.RoundToInt(d.x * 0.1f));
        d.y += Mathf.Max(1, Mathf.RoundToInt(d.y * 0.1f));
        rv.damageRange = d;
        int level = Level(item) + 1;
        rv.strAttributes.RemoveAll(x => x != null && x.Key == Sharpened);
        rv.strAttributes.Add(new ItemRandomValues.StrItemAttribute(Sharpened, level.ToString()));
        if (Singleton<Inventory>.HasMain) Singleton<Inventory>.main.SetInventoryChanged();
        Hooks.Say($"{rv.CurrentName}: damage now {d.x}-{d.y}");
        Log.Info($"NPCs and quests: sharpened {item.data.name} to +{level} ({d.x}-{d.y})");
    }

    // last, after the mods that give their items a name of their own
    [HarmonyPatch(typeof(ItemRandomValues), nameof(ItemRandomValues.CurrentName), MethodType.Getter), HarmonyPostfix, HarmonyPriority(Priority.Last)]
    static void PlusOnName(ItemRandomValues __instance, ref string __result)
    {
        var a = __instance.strAttributes?.Find(x => x != null && x.Key == Sharpened);
        if (a != null && !string.IsNullOrEmpty(a.Value) && a.Value != "0") __result += " +" + a.Value;
    }
}

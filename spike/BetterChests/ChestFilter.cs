using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BetterChests;

/// <summary>
/// A chest set to take only some kinds of things (V: Takes, by the game's own item categories: weapons, armour...)
/// refuses the rest, on every way in: dragging from the bag (Inventory.ChangeSection by slot), moving an item across
/// (ChangeSection by item), dragging worn gear in (TryToUnequip), and the chest screen's "send all / send commons" and
/// X's quick deposit (ChangeAllSection: the refused ones sit that sweep out, so the rest still go in; a refusal in the
/// middle would stop the game's sweep). What's in it already stays; it can come out, not go back in.
/// The chest in question: the open one (Chest.OpenChest .. CloseChest), or the one X is depositing into.
/// </summary>
[HarmonyPatch]
internal static class ChestFilter
{
    static Chest open;        // the chest whose screen is open
    static string openId => open && open.isOpened ? open.ID : null;   // (in a session the first OpenChest is only a request,
                                                                        // and may be refused: then it never opened)
    internal static string QuickId;   // X's chest, while it deposits

    static string CurrentId => QuickId ?? openId;

    /// <summary>The chest whose screen is open (null = none).</summary>
    internal static string OpenChestId => openId;

    /// <summary>What the open chest takes (0 = anything, or no chest open).</summary>
    internal static int OpenTakes => openId == null ? 0 : ChestLooks.TakesOf(openId);

    // before it runs: OpenChest opens the inventory screen itself (its ChestEvent), and the bag's slots are filled then
    [HarmonyPatch(typeof(Chest), nameof(Chest.OpenChest)), HarmonyPrefix, HarmonyPriority(Priority.First)]
    static void Opened(Chest __instance) => open = __instance;

    [HarmonyPatch(typeof(Chest), nameof(Chest.CloseChest)), HarmonyPostfix]
    static void Closed() { open = null; RefusedSlots.Clear(); ChestSearch.Reset(); }

    static bool Allowed(ItemObject item, out int takes)
    {
        takes = ChestLooks.TakesOf(CurrentId);
        if (takes == 0 || item?.data == null) return true;
        return ((int)item.data.itemCategory & takes) != 0;
    }

    static float lastNote;

    static void Refuse(ItemObject item, int takes)
    {
        if (Time.unscaledTime - lastNote < 0.5f) return;   // one line, not one per item
        lastNote = Time.unscaledTime;
        QuickStack.Note(typeof(ChestFilter), item?.data ? item.data.itemIconUI : null, "This chest only takes " + ChestLooks.TakesText(takes), 1);
    }

    static bool Check(ItemObject item)
    {
        if (Allowed(item, out int takes)) return true;
        Refuse(item, takes);
        return false;
    }

    // dragging a bag (or belt) slot onto the chest
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ChangeSection), new[] { typeof(int), typeof(int), typeof(InventorySection), typeof(InventorySection) })]
    [HarmonyPrefix]
    static bool BySlot(Inventory __instance, int fromIndex, InventorySection fromSection, InventorySection toSection, ref bool __result)
    {
        if (toSection != InventorySection.Chest || fromSection == InventorySection.Chest) return true;
        var item = __instance.allItems.Find(x => x.section == fromSection && x.slotIndex == fromIndex);
        if (item == null || Check(item.itemObject)) return true;
        __result = false;
        return false;
    }

    // one item across
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ChangeSection), new[] { typeof(InventoryItem), typeof(InventorySection), typeof(InventorySection) })]
    [HarmonyPrefix]
    static bool ByItem(InventoryItem item, InventorySection fromSection, InventorySection toSection, ref bool __result)
    {
        if (toSection != InventorySection.Chest || fromSection == InventorySection.Chest || item == null || Check(item.itemObject)) return true;
        __result = false;
        return false;
    }

    // worn gear dragged into the chest
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.TryToUnequip)), HarmonyPrefix]
    static bool Unequip(Inventory __instance, GearSlot fromGearSlot, InventorySection toSection, ref bool __result)
    {
        if (toSection != InventorySection.Chest) return true;
        var worn = __instance.equippedItems.Find(x =>
        {
            var d = x.itemObject.data;
            GearSlot? s = d is CollectableWeapon w ? w.weapon.gearSlot : d is CollectableGear g ? g.gear.gearSlot : (GearSlot?)null;
            return s == fromGearSlot;
        });
        if (worn == null || Check(worn.itemObject)) return true;
        __result = false;
        return false;
    }

    // "send all / send commons" and X: the refused ones sit the sweep out
    const InventorySection Aside = (InventorySection)(1 << 20);

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyPrefix]
    static void BeforeSweep(Inventory __instance, InventorySection fromSection, InventorySection toSection, out List<InventoryItem> __state)
    {
        __state = null;
        if (toSection != InventorySection.Chest) return;
        foreach (var it in __instance.allItems)
        {
            if (it.section != fromSection || Allowed(it.itemObject, out _)) continue;
            (__state ??= new List<InventoryItem>()).Add(it);
            it.section = Aside;
        }
    }

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyFinalizer]
    static void AfterSweep(Inventory __instance, InventorySection fromSection, List<InventoryItem> __state)
    {
        if (__state == null) return;
        foreach (var it in __state) it.section = fromSection;
        Allowed(__state[0].itemObject, out int takes);
        Refuse(__state[0].itemObject, takes);
        __instance.SetInventoryChanged();   // the screen, with them back in the bag
    }
}

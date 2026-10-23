using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace BetterChests;

/// <summary>
/// Ctrl + click while a chest is open (the stash too): an item in your bag (or on your belt) goes into the chest, an
/// item in the chest comes into your bag, the whole stack. The game's own move (Inventory.ChangeSection): it stacks
/// onto what's there, keeps to the chest's room, and keeps to what the chest takes (V); what doesn't fit stays.
/// </summary>
[HarmonyPatch]
internal static class CtrlClick
{
    static readonly Action<Inventory> changed = inv => (AccessTools.Field(typeof(Inventory), "OnChangeInventory")?.GetValue(inv) as Action)?.Invoke();

    [HarmonyPatch(typeof(UISlot), nameof(UISlot.OnPointerClick)), HarmonyPrefix]
    static bool Click(UISlot __instance, PointerEventData eventData)
    {
        var kb = Keyboard.current;
        if (eventData == null || eventData.button != PointerEventData.InputButton.Left || kb == null || !kb.ctrlKey.isPressed) return true;
        var ui = UnityEngine.Object.FindAnyObjectByType<InventoryUI>();
        if (!ui || (ui.onScreenSection.Value & InventorySection.Chest) == 0) return true;   // (no chest open: the game's click)
        var (from, index) = Where(ui, __instance);
        if (from == InventorySection.None) return true;
        var inv = Singleton<Inventory>.main;
        var item = inv.allItems.Find(x => x.section == from && x.slotIndex == index);
        if (item == null) return false;
        var to = from == InventorySection.Chest ? InventorySection.Main : InventorySection.Chest;
        string name = Name(item);
        if (inv.ChangeSection(item, from, to)) changed(inv);
        else Plugin.Log.LogInfo($"Ctrl+click: {name} doesn't fit (or that chest doesn't take it)");
        return false;   // (not also the game's pick-up / select)
    }

    static (InventorySection, int) Where(InventoryUI ui, UISlot slot)
    {
        int i;
        if (ui.mainSlots != null && (i = ui.mainSlots.IndexOf(slot)) >= 0) return (InventorySection.Main, i);
        if (ui.beltSlots != null && (i = ui.beltSlots.IndexOf(slot)) >= 0) return (InventorySection.Belt, i);
        if (ui.chestSlots != null && (i = ui.chestSlots.IndexOf(slot)) >= 0) return (InventorySection.Chest, i);
        return (InventorySection.None, -1);
    }

    static string Name(InventoryItem item)
    {
        try { return item.itemObject.ToString(); } catch { return item.itemObject?.data ? item.itemObject.data.name : "?"; }
    }
}

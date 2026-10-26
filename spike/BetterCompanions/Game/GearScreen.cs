using System.Collections.Generic;
using System.Linq;
using BetterCompanions.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BetterCompanions;

/// <summary>
/// F at the companion: the game's own inventory screen, with its top half (character, gear slots, stats) showing the
/// companion, tinted teal, and the bottom half your own bag. Dragging from your bag into a gear slot dresses the
/// companion; dragging its gear down (or double-clicking it) puts it in your bag. The game's character panel (its
/// attribute screen), opened while in this mode, shows the companion too: spend its points there, and Save writes
/// them to the companion's save.
///
/// How: the screen reads "the player's" CharacterStats (InventoryUI.player.characterStats); while in companion mode
/// that one field is swapped for the companion's for the length of each of the screen's own calls, and always put
/// back (a finalizer). Equipping and unequipping go through the player's Inventory (TryToEquip / TryToUnequip), which
/// in companion mode move the item between your bag and the companion instead; a double-click on gear in your bag
/// (UseItem) goes the same way, never through the game's own equip. The mode ends the moment neither the inventory
/// nor the character panel shows (GearMode), so pressing I afterwards is the ordinary screen, with your own
/// hide-gear switches as they were.
/// </summary>
[HarmonyPatch]
internal static class GearScreen
{
    static readonly GearMode mode = new();
    public static bool CompanionMode => mode.On;
    static int refreshIn;
    static readonly List<(CharGearSlot slot, bool? state, bool isOn)> hideSwitches = new();
    static readonly Color Teal = new(0.247f, 0.659f, 0.710f, 1f);   // #3FA8B5
    static readonly Dictionary<Graphic, Color> tinted = new();

    static CompanionBody Body => Companion.Body;
    static bool Ready => Body != null && !Body.Lost && Body.Items != null && Body.Stats;

    /// <summary>Each frame: F next to the companion opens its screen; no panel open any more ends the mode.</summary>
    public static void Tick(PlayerMaster me)
    {
        if (mode.On)
        {
            if (refreshIn > 0 && --refreshIn == 0) Refresh();
            var panels = Object.FindAnyObjectByType<PanelsManager>();
            mode.Tick(panels && panels.panelInventory && panels.panelInventory.activeInHierarchy, panels && panels.panelStats && panels.panelStats.activeInHierarchy);
            if (!mode.On) Cleanup();
            return;
        }
        // (F no longer opens it: the companion's own panel opens beside your inventory instead, CompanionPanel)
    }

    public static void Open()
    {
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        if (!panels || !Ready) return;
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        hideSwitches.Clear();
        if (ui) foreach (var g in ui.gearSlots) if (g != null) hideSwitches.Add((g, g.currentState, g.toggle && g.toggle.isOn));
        mode.Opened(true);
        refreshIn = 3;   // (the panel opens over a few frames)
        panels.TogglePanel(PanelName.Inventory);
    }

    public static void Close()
    {
        if (!mode.On) return;
        mode.Closed();
        Cleanup();
    }

    /// <summary>Back to the player: colours, the hide-gear switches as they were, the gear slots showing yours.</summary>
    static void Cleanup()
    {
        foreach (var (g, state, isOn) in hideSwitches)
        {
            g.currentState = state;
            if (g.toggle) g.toggle.SetIsOnWithoutNotify(isOn);
        }
        hideSwitches.Clear();
        foreach (var kv in tinted) if (kv.Key) kv.Key.color = kv.Value;
        tinted.Clear();
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (ui && ui.isActiveAndEnabled) ui.UpdateGearInventory();   // (back to your own gear, if it's still showing)
        if (CompanionSave.Dirty) { CompanionSave.Dirty = false; CompanionSave.Write(); }
    }

    static void Refresh()
    {
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (!ui) return;
        ui.UpdateGearInventory();
        ui.SetPortraitCamera(true);
        Tint(ui);
    }

    static void Tint(InventoryUI ui)
    {
        foreach (var g in ui.gearSlots)
        {
            if (g?.slot == null) continue;
            foreach (var img in g.slot.GetComponentsInChildren<Graphic>(true)) TintOne(img, 0.55f);
        }
        // and the top half's own backdrop: the panels behind the portrait and gear slots (not your bag below)
        var top = TopHalf(ui);
        if (!top) return;
        float wide = ((RectTransform)top).rect.width * 0.35f;
        foreach (var img in top.GetComponentsInChildren<Image>(true))
            if (img.rectTransform.rect.width >= wide && !ui.gearSlots.Any(g => g?.slot && img.transform.IsChildOf(g.slot.transform)))
                TintOne(img, 0.4f);
    }

    static void TintOne(Graphic img, float how)
    {
        if (!img || tinted.ContainsKey(img) || img is Text || img is TMPro.TMP_Text) return;
        tinted[img] = img.color;
        img.color = Color.Lerp(img.color, Teal * new Color(1f, 1f, 1f, img.color.a), how);
    }

    /// <summary>The part of the inventory panel holding the gear slots but not your bag's slots.</summary>
    static Transform TopHalf(InventoryUI ui)
    {
        var slot = ui.gearSlots.FirstOrDefault(g => g?.slot)?.slot;
        var bag = ui.mainSlotsHolder;
        if (!slot || !bag) return null;
        var t = slot.transform;
        while (t.parent && !bag.IsChildOf(t.parent)) t = t.parent;
        return t.parent && t is RectTransform ? t : null;
    }

    // ---- the screen reads the companion's stats while in companion mode ----

    static CharacterStats swappedOut;
    static int depth;

    static void SwapIn(InventoryUI ui)
    {
        if (!mode.On || !Ready || !ui || !ui.player) return;
        if (depth++ == 0) { swappedOut = ui.player.characterStats; ui.player.characterStats = Body.Stats; }
    }

    static void SwapOut(InventoryUI ui)
    {
        if (depth == 0) return;
        if (--depth == 0 && ui && ui.player && swappedOut) { ui.player.characterStats = swappedOut; swappedOut = null; }
    }

    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UpdateGearInventory)), HarmonyPrefix]
    static void GearIn(InventoryUI __instance) => SwapIn(__instance);
    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UpdateGearInventory)), HarmonyFinalizer]
    static void GearOut(InventoryUI __instance) => SwapOut(__instance);

    /// <summary>
    /// A double-click on gear in your bag while the companion's screen is up: onto the companion, through the same
    /// path as a drag. (The game's own way would equip it on whatever stats the screen shows and rebuild YOUR
    /// equipped list from them.) Anything else (a potion) is yours to use as usual.
    /// </summary>
    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UseItem)), HarmonyPrefix]
    static bool Use(InventoryUI __instance, InventoryItem item, InventorySection fromSection)
    {
        if (!mode.On || item?.itemObject == null || SlotOf(item.itemObject.data) is not GearSlot slot) return true;
        if (fromSection == InventorySection.Main && Ready && __instance.inventoryMain.TryToEquip(item.slotIndex, fromSection, slot))
        {
            __instance.UpdateMain();
            __instance.UpdateGearInventory();
        }
        return false;
    }

    /// <summary>The live portrait at the top: the companion, not you.</summary>
    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.SetPortraitCamera)), HarmonyPostfix]
    static void Portrait(InventoryUI __instance, bool value)
    {
        if (value && mode.On && Ready && __instance.playerPortraitCamera)
            __instance.playerPortraitCamera.transform.position = Body.Position;
    }

    // ---- equipping moves items between your bag and the companion ----

    static GearSlot? SlotOf(ItemData d) => d is CollectableWeapon w ? w.weapon.gearSlot : d is CollectableGear g ? g.gear.gearSlot : null;

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.TryToEquip)), HarmonyPrefix]
    static bool Equip(Inventory __instance, int fromIndex, InventorySection fromSection, GearSlot toGearSlot, ref bool __result)
    {
        if (!mode.On) return true;
        __result = false;
        if (!Ready) return false;
        var inv = __instance;
        var item = inv.allItems.Find(x => x.section == fromSection && x.slotIndex == fromIndex);
        if (item == null || SlotOf(item.itemObject.data) != toGearSlot) return false;
        var old = Body.Items.Find(x => x.section == InventorySection.Gear && x.itemObject != null && SlotOf(x.itemObject.data) == toGearSlot);
        inv.allItems.Remove(item);
        if (old != null)
        {
            Body.Stats.RemoveItem(old.itemObject);
            Body.Items.Remove(old);
            inv.allItems.Add(new InventoryItem(old.itemObject, item.slotIndex, item.section));   // (theirs goes where yours was)
        }
        Body.Stats.EquipItem(item.itemObject);
        Body.Items.Add(new InventoryItem(item.itemObject, (int)toGearSlot, InventorySection.Gear));
        Changed(inv);
        __result = true;
        return false;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.TryToUnequip)), HarmonyPrefix]
    static bool Unequip(Inventory __instance, GearSlot fromGearSlot, int toIndex, InventorySection toSection, ref bool __result)
    {
        if (!mode.On) return true;
        __result = false;
        if (!Ready || toSection != InventorySection.Main) return false;   // (only into your bag: not thrown away, not a chest)
        var inv = __instance;
        var theirs = Body.Items.Find(x => x.section == InventorySection.Gear && x.itemObject != null && SlotOf(x.itemObject.data) == fromGearSlot);
        if (theirs == null) return false;
        int cap = inv.CurrentInventoryCapacity;
        if (inv.GetCurrentUsed(InventorySection.Main) >= cap) { Hooks.Say("Companion: your bag is full."); return false; }
        bool Free(int i) => !inv.allItems.Any(x => x.section == InventorySection.Main && x.slotIndex == i);
        int to = toIndex >= 0 && toIndex < cap && Free(toIndex) ? toIndex : Enumerable.Range(0, cap).FirstOrDefault(Free);
        Body.Stats.RemoveItem(theirs.itemObject);
        if ((fromGearSlot & GearSlot.MainHand) != GearSlot.None) { ItemObject none = null; Body.Unit.EquipPrimaryWeapon(ref none); }
        Body.Items.Remove(theirs);
        inv.allItems.Add(new InventoryItem(theirs.itemObject, to, InventorySection.Main));
        Changed(inv);
        __result = true;
        return false;
    }

    /// <summary>After a move: your inventory knows, the companion's save is written, its health and favourites follow.</summary>
    static void Changed(Inventory inv)
    {
        inv.UpdateEquippedList();
        inv.SetInventoryChanged();
        CompanionSave.FromItems(Body.Items);
        CompanionSave.Write();
        Body.Unit.RefreshHealth();
        Arms.PickFavourites(Body);
        Log.Info($"Companion: gear now {string.Join(", ", Body.Stats.EquippedItems.Select(x => x.data.name))}");
    }

    // ---- the attribute screen ----

    [HarmonyPatch(typeof(UIStatsHandler), nameof(UIStatsHandler.GatherStats)), HarmonyPrefix]
    static void StatsOf(ref CharacterStats characterStats)
    {
        if (mode.On && Ready) characterStats = Body.Stats;
    }

    /// <summary>Save on the attribute screen in companion mode: into the companion's save, never yours.</summary>
    [HarmonyPatch(typeof(UIStatsHandler), "ApplyChanges"), HarmonyPrefix]
    static bool Apply(UIStatsHandler __instance)
    {
        if (!mode.On || !Ready || __instance.activeCharStats != Body.Stats) return true;
        Body.Stats.UpdateBaseValues(__instance.primCopy, __instance.secCopy);
        CompanionSave.Data.SavePlayerStats(__instance.primCopy, __instance.secCopy);
        CompanionSave.Write();
        Body.Unit.RefreshHealth();
        __instance.GatherStats(Body.Stats);
        Log.Info("Companion: attributes saved");
        return false;
    }
}

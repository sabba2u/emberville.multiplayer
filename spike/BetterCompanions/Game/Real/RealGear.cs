using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// What the real companion has: what it wears (its CharacterStats, as yours), its bag (35 slots) and its swap weapons (kept in its bag list at SpareIndex and the SwapSlots after it). Every change is written to its save.
/// Its bag and belt items only ever change its own gear, never your bag's slots (the game's would: CollectableBag).
/// </summary>
[HarmonyPatch]
internal static class RealGear
{
    public const int SpareIndex = 999, SwapSlots = 4;
    /// <summary>Where something goes when there's no room anywhere (kept in its save, never lost).</summary>
    public static int Overflow => 5000 + (Bag?.Count ?? 0);
    const int BagSlots = 35;

    /// <summary>Its bag and spare (section Main); null until its save's items have loaded.</summary>
    public static List<InventoryItem> Bag;
    /// <summary>Anything else its save has (belt, quest items): kept as it is.</summary>
    public static List<InventoryItem> Other = new();
    public static event Action Changed;

    static PlayerMaster P => RealPlayer.Current;

    public static GearSlot? SlotOf(ItemObject o) =>
        o?.data is CollectableWeapon w ? w.weapon.gearSlot : o?.data is CollectableGear g ? g.gear.gearSlot : null;

    public static bool IsFallback(ItemObject o) => P && o != null && o == P.fallbackWeaponItem;

    /// <summary>What it wears in that slot (not its bare-hands fallback).</summary>
    public static ItemObject Worn(GearSlot slot) =>
        !P ? null : P.characterStats.EquippedItems.FirstOrDefault(o => !IsFallback(o) && SlotOf(o) is { } s && (s & slot) != 0);

    public static IEnumerable<ItemObject> AllWorn => !P ? Enumerable.Empty<ItemObject>() : P.characterStats.EquippedItems.Where(o => !IsFallback(o));

    /// <summary>Its bag's slots: 35, all open (a bag it wears adds the game's side slots, which are its swap weapons here).</summary>
    public static int Capacity => BagSlots;

    public static InventoryItem At(int index) => Bag?.Find(i => i.section == InventorySection.Main && i.slotIndex == index);
    public static bool IsSwap(int index) => index >= SpareIndex && index < SpareIndex + SwapSlots;
    /// <summary>The weapon in swap slot k (0 to SwapSlots-1).</summary>
    public static ItemObject Swap(int k) => At(SpareIndex + k)?.itemObject;
    /// <summary>Its first swap weapon (any).</summary>
    public static ItemObject Spare => Enumerable.Range(0, SwapSlots).Select(Swap).FirstOrDefault(o => o != null);

    public static int FreeIndex()
    {
        if (Bag == null) return -1;
        for (int i = 0; i < Capacity; i++) if (At(i) == null) return i;
        return -1;
    }

    public static void Put(ItemObject o, int index) { if (o != null) Bag.Add(new InventoryItem(o, index, InventorySection.Main)); }
    public static ItemObject Take(int index) { var it = At(index); if (it == null) return null; Bag.Remove(it); return it.itemObject; }

    /// <summary>Wear it; returns what it took off to do so (a two-hander can take off two).</summary>
    public static List<ItemObject> Wear(ItemObject o)
    {
        var off = new List<ItemObject>();
        if (!P || SlotOf(o) is not { } slot) return off;
        off.AddRange(AllWorn.Where(x => SlotOf(x) is { } s && (s & slot) != 0));
        P.characterStats.EquipItem(o);
        return off;
    }

    public static void TakeOff(ItemObject o)
    {
        if (!P || o == null) return;
        P.characterStats.RemoveItem(o);
        if (o.data is CollectableWeapon w && (w.weapon.gearSlot & GearSlot.MainHand) != 0) P.characterStats.SetFallbackWeapon();   // (bare hands)
    }

    /// <summary>Swap slot k to hand, the one in hand into that swap slot (RealBrain, when it wants the other kind).</summary>
    public static bool SwapSpare(int k)
    {
        var spare = Swap(k);
        if (spare == null || !P) return false;
        Take(SpareIndex + k);
        var off = Wear(spare);
        var was = off.FirstOrDefault(x => x.data is CollectableWeapon);
        if (was != null) { Put(was, SpareIndex + k); off.Remove(was); }
        foreach (var x in off) { int i = FreeIndex(); Put(x, i >= 0 ? i : Overflow); }   // (never lost)
        Save(write: false);
        return true;
    }

    /// <summary>Into its save: what it wears and what it carries.</summary>
    public static void Save(bool write = true)
    {
        if (Bag == null || !P) return;
        var all = AllWorn.Select(o => new InventoryItem(o, -1, InventorySection.Gear)).Concat(Bag).Concat(Other).ToList();
        CompanionSave.FromItems(all);
        if (write) { CompanionSave.Dirty = false; CompanionSave.Write(); }
        else CompanionSave.Dirty = true;
        Changed?.Invoke();
    }

    public static void Forget() => Bag = null;

    // ---- a bag or belt on a companion changes ITS gear, never your inventory's slots ----

    static bool Yours(CharacterStats owner) => !owner || !Singleton<GameManager>.HasMain || owner == Singleton<GameManager>.main.player?.characterStats;

    [HarmonyPatch(typeof(CollectableBag), nameof(CollectableBag.Execute)), HarmonyPrefix]
    static bool BagOn(CharacterStats owner, ItemObject item, ref bool __result) { if (Yours(owner)) return true; owner.EquipItem(item); __result = true; return false; }
    [HarmonyPatch(typeof(CollectableBag), nameof(CollectableBag.Remove)), HarmonyPrefix]
    static bool BagOff(CharacterStats owner, ItemObject item) { if (Yours(owner)) return true; owner.RemoveItem(item); return false; }
    [HarmonyPatch(typeof(CollectableBelt), nameof(CollectableBelt.Execute)), HarmonyPrefix]
    static bool BeltOn(CharacterStats owner, ItemObject item, ref bool __result) { if (Yours(owner)) return true; owner.EquipItem(item); __result = true; return false; }
    [HarmonyPatch(typeof(CollectableBelt), nameof(CollectableBelt.Remove)), HarmonyPrefix]
    static bool BeltOff(CharacterStats owner, ItemObject item) { if (Yours(owner)) return true; owner.RemoveItem(item); return false; }
}

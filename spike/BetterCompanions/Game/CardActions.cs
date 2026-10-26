using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// What the companion's card offers (right-click it; the multiplayer mod draws the card and its menu): heal it with one
/// of your health potions, or trade, which opens your bag with its panel beside it wherever it is.
/// </summary>
internal static class CardActions
{
    /// <summary>Your health potions (bag and belt).</summary>
    static IEnumerable<InventoryItem> Potions() =>
        !Singleton<Inventory>.HasMain ? Enumerable.Empty<InventoryItem>() :
        Singleton<Inventory>.main.allItems.Where(i => i?.itemObject?.data is CollectableHealing h && h.healType == CollectableHealing.HealType.Health
                                                      && (i.section == InventorySection.Main || i.section == InventorySection.Belt));

    /// <summary>The potion that best fits what it's missing: the smallest that fills it up, else the biggest you have.</summary>
    static InventoryItem Pick(int missing)
    {
        var all = Potions().ToList();
        if (all.Count == 0) return null;
        int Heals(InventoryItem i) => ((CollectableHealing)i.itemObject.data).FinalHealingPoints;
        return all.Where(i => Heals(i) >= missing).OrderBy(Heals).FirstOrDefault() ?? all.OrderByDescending(Heals).First();
    }

    static int Missing(PlayerMaster p)
    {
        try { return Mathf.Max(0, (int)p.characterStats.GetSecondaryStatValue(SecondaryStat.HealthPoints) - p.CurrentHealthPoints); }
        catch { return 0; }
    }

    static string NameOf(ItemData d)
    {
        try { var n = d.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return d.name;
    }

    /// <summary>The Heal button's words, and whether it can be pressed now.</summary>
    public static (string label, bool can) HealOption()
    {
        var p = RealPlayer.Current;
        if (!p) return ("Heal (no companion out)", false);
        if (RealPlayer.Down(p)) return ("Heal (it's down: hold F)", false);
        int missing = Missing(p);
        if (missing <= 0) return ("Heal (full health)", false);
        var potion = Pick(missing);
        if (potion == null) return ("Heal (no health potions)", false);
        int left = Potions().Where(i => i.itemObject.data == potion.itemObject.data).Sum(i => Mathf.Max(1, i.itemObject.amount));
        return ($"Heal: {NameOf(potion.itemObject.data)} ({left} left)", true);
    }

    /// <summary>One of your potions, drunk by it (the game's own potion: its sound, and one gone from your bag).</summary>
    public static string Heal()
    {
        var p = RealPlayer.Current;
        if (!p || RealPlayer.Down(p)) return "";
        int missing = Missing(p);
        var potion = Pick(missing);
        if (missing <= 0 || potion == null) return "";
        bool ok;
        try { ok = potion.itemObject.data.Execute(p.characterStats, potion.itemObject); }
        catch (System.Exception e) { Log.Warn($"Companion: healing: {e.Message}"); ok = false; }
        if (!ok) return "";
        CompanionSave.Dirty = true;
        return $"{CompanionSave.Data?.playerName} drinks your {NameOf(potion.itemObject.data)}.";
    }

    /// <summary>Your bag, with its panel beside it (from the card: however far away it is).</summary>
    public static void Trade()
    {
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        if (!panels || !RealPlayer.Current) return;
        CompanionPanel.Asked = true;
        if (!panels.panelInventory.activeInHierarchy) panels.TogglePanel(PanelName.Inventory);
    }
}

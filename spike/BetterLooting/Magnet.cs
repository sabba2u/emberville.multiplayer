using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;

namespace BetterLooting;

/// <summary>
/// Auto magnet (hands-free F): items lying within range of our player are picked up by themselves, nearest first, one
/// every few frames, through the same ItemDropObject.OnInteract the F key uses (so the multiplayer mod's dupe-proof pickup and
/// instanced loot apply). Filters per item category, coins and key items; only what fits in the bag (else it would
/// bounce back out at our feet). Never while the game covers the screen (menus, dialogue, cutscenes) or we're dead.
/// Settings: Insert menu > Magnet (saved in the config).
/// </summary>
public static class Magnet
{
    static ConfigEntry<bool> on, materials, consumables, weapons, armor, gear, placeables, other, coins, keyItems;
    static ConfigEntry<float> range;
    const float Interval = 0.15f;
    static float next;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Magnet", "Enabled", false, "Pick up items near you by themselves (no F)");
        range = config.Bind("Magnet", "Range", 2.5f, "How far the magnet reaches (world units; F reaches about 1)");
        materials = config.Bind("Magnet", "Materials", true, "Wood, stone, ore, monster parts...");
        consumables = config.Bind("Magnet", "Consumables", true, "Food, potions");
        weapons = config.Bind("Magnet", "Weapons", true, "Weapons");
        armor = config.Bind("Magnet", "Armor", true, "Armour");
        gear = config.Bind("Magnet", "Gear", true, "Rings, amulets and other gear");
        placeables = config.Bind("Magnet", "Placeables", true, "Things you can build / place");
        other = config.Bind("Magnet", "Other", true, "Anything else");
        coins = config.Bind("Magnet", "Coins", true, "Coins");
        keyItems = config.Bind("Magnet", "KeyItems", true, "Keys and quest items");
    }

    public static void Tick()
    {
        if (on == null || !on.Value || Time.unscaledTime < next) return;
        next = Time.unscaledTime + Interval;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        // playing: not in a menu or a game screen, the controls on (chat and other mods' text boxes turn them off)
        if (!me || me.IsDead || !Singleton<Inventory>.HasMain || Singleton<GameManager>.main.gameState != GameState.Playing ||
            UIPanels.HasOpenedPanel || !MainInput.Actions.Player.enabled) return;

        Vector2 at = me.transform.position;
        float best = range.Value * range.Value;
        ItemDropObject pick = null;
        foreach (var d in Object.FindObjectsByType<ItemDropObject>(FindObjectsSortMode.None))
        {
            if (!d || !d.isActiveAndEnabled || !d.CanInteract() || d.itemObj == null || d.itemObj.data == null) continue;
            float dist = ((Vector2)d.transform.position - at).sqrMagnitude;
            if (dist > best || !Wanted(d.itemObj) || !Fits(d.itemObj)) continue;
            best = dist; pick = d;
        }
        if (pick) pick.OnInteract(me.gameObject);
    }

    static bool Wanted(ItemObject item)
    {
        var data = item.data;
        if (data is CollectableCoin) return coins.Value;
        if (data.itemBehaviour == ItemBehaviour.KeyItem) return keyItems.Value;
        var c = data.itemCategory;
        if ((c & ItemCategory.Material) != 0) return materials.Value;
        if ((c & ItemCategory.Consumable) != 0) return consumables.Value;
        if ((c & ItemCategory.Weapon) != 0) return weapons.Value;
        if ((c & ItemCategory.Armor) != 0) return armor.Value;
        if ((c & ItemCategory.Gear) != 0) return gear.Value;
        if ((c & ItemCategory.Placeable) != 0) return placeables.Value;
        return other.Value;
    }

    /// <summary>Would Inventory.AddItem take all of it (coins, key items, a free slot, or a stack with room)?</summary>
    static bool Fits(ItemObject item)
    {
        var inv = Singleton<Inventory>.main;
        var data = item.data;
        if (data is CollectableCoin || data.itemBehaviour == ItemBehaviour.KeyItem) return true;
        if (inv.GetCurrentUsed(InventorySection.Main) < inv.CurrentInventoryCapacity) return true;
        if (data.itemCategory == ItemCategory.Consumable && inv.GetCurrentUsed(InventorySection.Belt) < inv.CurrentBeltCapacity) return true;
        if (!data.IsStackable) return false;
        foreach (var x in inv.allItems)
            if ((x.section & (InventorySection.Main | InventorySection.Belt)) != 0 && x.itemObject.IsSame(item) && x.itemObject.amount + item.amount <= inv.stackLimit)
                return true;
        return false;
    }

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Magnet");
        bool v = on.Value;
        if (ImGui.Checkbox("Pick up items near you by themselves (no F)", ref v)) on.Value = v;
        if (!on.Value) ImGui.BeginDisabled();
        float r = range.Value;
        ImGui.SetNextItemWidth(280);
        if (ImGui.SliderFloat("Range##magnet", ref r, 1f, 8f, "%.1f steps")) range.Value = r;
        ImGui.SameLine(); ImGui.TextDisabled("F reaches about 1");
        ImGui.TextDisabled("What it picks up:");
        Box("Materials", materials); ImGui.SameLine(140); Box("Food / potions", consumables); ImGui.SameLine(300); Box("Coins", coins);
        Box("Weapons", weapons); ImGui.SameLine(140); Box("Armour", armor); ImGui.SameLine(300); Box("Gear", gear);
        Box("Placeables", placeables); ImGui.SameLine(140); Box("Key items", keyItems); ImGui.SameLine(300); Box("Other", other);
        if (!on.Value) ImGui.EndDisabled();
    }

    static void Box(string label, ConfigEntry<bool> e)
    {
        bool v = e.Value;
        if (ImGui.Checkbox(label + "##magnet", ref v)) e.Value = v;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EmbervilleFavorites;

/// <summary>
/// Weapon favourites: in the inventory, hover a weapon and press F5-F8 to put it on that key (again on the same key
/// clears it); its tooltip then says "Favourite: F6". While playing, F5-F8 equip that weapon into the main hand, the
/// game's own equip (Inventory.TryToEquip, as dragging it onto the weapon slot does), the one in hand going back
/// into the bag. The game has no weapon swapping of its own and doesn't use F5-F8
/// (F1 / F2 are its menus' previous / next tab; the multiplayer mod uses F9 / F10).
/// Weapons have no ids, so a favourite is remembered by the item and its rolled stats (two identical rolls are the
/// same to us), per character, next to the save: Saves/&lt;character&gt;.egfav.
/// </summary>
[HarmonyPatch]
internal static class Favorites
{
    public const int Slots = 4;
    static readonly string[] keys = new string[Slots];   // fingerprint per slot (null = empty)
    static string loadedFor;

    static ItemObject hovered;
    static ItemInfoWindow hoveredIn;

    internal static ConfigEntry<bool> barOn;

    public static void Bind(ConfigFile config)
    {
        barOn = config.Bind("Bar", "Show", true, "The F5-F8 favourite weapons bar above the HUD (click a slot to wield it)");
    }

    // ---- the game ----

    public static PlayerMaster Player => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    /// <summary>Playing, not in a menu, the game's controls on (another mod's chat / text box turns them off).</summary>
    static bool FreeToPlay =>
        Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Playing &&
        !UIPanels.HasOpenedPanel && MainInput.Actions.Player.enabled;

    // ---- the tooltip ----

    [HarmonyPatch(typeof(ItemInfoWindow), nameof(ItemInfoWindow.SetItem)), HarmonyPostfix]
    static void AfterTooltip(ItemInfoWindow __instance, ItemObject item)
    {
        hovered = item; hoveredIn = __instance;
        if (!IsWeapon(item) || !__instance.description) return;
        Load();
        int slot = SlotOf(Fingerprint(item));
        if (slot < 0) return;
        __instance.description.text = (__instance.description.text.Length > 0 ? __instance.description.text + "\n" : "") +
                                      $"<color=#FFD24A>Favourite: F{slot + 5}</color> (press F{slot + 5} to wield it)";
        __instance.descriptionBox?.SetActive(true);
    }

    // ---- keys ----

    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused) return;
        int pressed = kb.f5Key.wasPressedThisFrame ? 0 : kb.f6Key.wasPressedThisFrame ? 1 : kb.f7Key.wasPressedThisFrame ? 2 : kb.f8Key.wasPressedThisFrame ? 3 : -1;
        if (kb.shiftKey.isPressed) return;   // Shift+F5: the how-to popup (Tutorial)
        if (pressed < 0 || !Player || !Singleton<Inventory>.HasMain) return;
        Load();

        // Over a weapon in our bag in the inventory screen: put it on that key.
        if (hoveredIn && hoveredIn.gameObject.activeInHierarchy && !FavoritesBar.IsOurTip(hoveredIn) && IsWeapon(hovered) && Mine(hovered))
        {
            Assign(pressed, hovered);
            return;
        }
        if (FreeToPlay) Wield(pressed);
    }

    static void Assign(int slot, ItemObject item)
    {
        FavoritesBar.Poke();
        string fp = Fingerprint(item);
        if (keys[slot] == fp) { keys[slot] = null; Note(item, $"{ItemName(item.data)}: no longer on F{slot + 5}"); }
        else
        {
            for (int i = 0; i < Slots; i++) if (keys[i] == fp) keys[i] = null;   // one key per weapon
            keys[slot] = fp;
            Note(item, $"{ItemName(item.data)} on F{slot + 5}");
        }
        Save();
        FavoritesBar.Refresh();
        SlotBadges.Refresh();
        if (hoveredIn && hoveredIn.gameObject.activeInHierarchy) hoveredIn.SetItem(item, Player);   // the tooltip line
    }

    /// <summary>Equip the weapon on this key (F5-F8, or a click on the bar).</summary>
    public static void Wield(int slot)
    {
        Load();
        FavoritesBar.Poke();   // the bar shows for a moment
        if (keys[slot] == null) { Note(null, $"F{slot + 5}: no weapon yet (hover one in the inventory and press F{slot + 5})"); return; }
        var found = Find(slot);
        if (found == null) { Note(null, $"F{slot + 5}: that weapon isn't in your bag any more"); return; }
        if (found.section == InventorySection.Gear) return;   // already in hand
        if (found.section != InventorySection.Main && found.section != InventorySection.Belt)
        { Note(found.itemObject, $"F{slot + 5}: take it out of the chest first"); return; }
        if (Singleton<Inventory>.main.TryToEquip(found.slotIndex, found.section, GearSlot.MainHand))
            Note(found.itemObject, $"{ItemName(found.itemObject.data)} (F{slot + 5})");
        FavoritesBar.Refresh();
    }

    /// <summary>The bag item on this key (the one in hand first), or null.</summary>
    public static InventoryItem Find(int slot)
    {
        Load();
        if (keys[slot] == null || !Singleton<Inventory>.HasMain) return null;
        InventoryItem found = null;
        foreach (var x in Singleton<Inventory>.main.allItems)
        {
            if (x.section == InventorySection.Chest || !IsWeapon(x.itemObject) || Fingerprint(x.itemObject) != keys[slot]) continue;
            found = x;
            if (x.section == InventorySection.Gear) break;
        }
        return found;
    }

    public static bool HasKey(int slot) { Load(); return keys[slot] != null; }

    /// <summary>The key (0-3 = F5-F8) this weapon is on, or -1.</summary>
    public static int KeyOf(ItemObject item) { if (!IsWeapon(item)) return -1; Load(); return SlotOf(Fingerprint(item)); }

    // ---- helpers ----

    static bool IsWeapon(ItemObject item) =>
        item?.data is CollectableWeapon w && w.weapon != null && (w.weapon.gearSlot & GearSlot.MainHand) != 0;

    static bool Mine(ItemObject item)
    {
        foreach (var x in Singleton<Inventory>.main.allItems)
            if (ReferenceEquals(x.itemObject, item)) return x.section != InventorySection.Chest;
        return false;
    }

    static string Fingerprint(ItemObject item) => item.data.addressKey + "|" + JsonUtility.ToJson(item.randomValues);

    static int SlotOf(string fp)
    {
        for (int i = 0; i < Slots; i++) if (keys[i] == fp) return i;
        return -1;
    }

    static NotificationsManager notifications;

    /// <summary>A line in the game's notification list on the right (icon + text).</summary>
    static void Note(ItemObject item, string text)
    {
        if (!notifications) notifications = UnityEngine.Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(typeof(Favorites), item?.data ? item.data.itemIconUI : null, text, 0f, 1);
    }

    public static string NameOf(ItemData data) => ItemName(data);

    /// <summary>An item's name in the player's language (its asset name if that fails).</summary>
    static string ItemName(ItemData data)
    {
        try { var s = data.Name == null || data.Name.IsEmpty ? null : data.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(s)) return s; }
        catch { }
        return data.name;
    }

    static string FileFor(string prefix) => SaveLoad.GetPath(prefix + ".egfav");

    /// <summary>This character's favourites (once per character).</summary>
    static void Load()
    {
        string prefix = GameData.HasPlayerData ? GameData.playerFilePrefix : null;
        if (prefix == loadedFor) return;
        loadedFor = prefix;
        Array.Clear(keys, 0, Slots);
        if (prefix == null) return;
        try
        {
            string path = FileFor(prefix);
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && int.TryParse(line.Substring(0, tab), out int i) && i >= 0 && i < Slots) keys[i] = line.Substring(tab + 1);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Favourites: {e.Message}"); }
    }

    static void Save()
    {
        if (loadedFor == null) return;
        try
        {
            var lines = new List<string>();
            for (int i = 0; i < Slots; i++) if (keys[i] != null) lines.Add($"{i}\t{keys[i]}");
            File.WriteAllLines(FileFor(loadedFor), lines);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Favourites: {e.Message}"); }
    }
}

using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace BetterChests;

/// <summary>
/// X at a chest: walk up to a chest (it shows its arrow) and press X; every stackable item in your bag that the chest
/// already holds goes in, onto its stacks and then into free slots while there's room, and each one shows in the
/// game's list on the right ("Stored Carrot (12)"). That's the chest screen's own "send common" button
/// (Inventory.SendCommonToChest), run without opening the screen: the chest's items join the inventory as its chest
/// section (as opening does), the button runs, and taking the section out again writes the chest back (as closing does).
///
/// For other mods: Run(chest) is the whole action (the multiplayer mod patches it to go through the area owner), and
/// StackInto(chest) is the deposit itself on this copy's contents. Both return how many items went in.
/// </summary>
public static class QuickStack
{
    static ConfigEntry<Key> key;
    static ConfigEntry<float> nearbyRange;
    public static string KeyName => key.Value.ToString();

    public static void Bind(ConfigFile config)
    {
        key = config.Bind("QuickStack", "Key", Key.X, "Press at a chest to put in everything it already holds (with Shift: every chest nearby)");
        nearbyRange = config.Bind("QuickStack", "NearbyRange", 10f, "Shift + the key: chests this close (world units) get their matching items");
    }

    /// <summary>Playing, not in a menu, the game's controls on (another mod's chat / text box turns them off).</summary>
    static bool FreeToPlay =>
        Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Playing &&
        !UIPanels.HasOpenedPanel && MainInput.Actions.Player.enabled;

    public static void Tick()
    {
        var kb = Keyboard.current;
        Chest chest = null;
        if (FreeToPlay && Singleton<GameManager>.main.player)
        {
            var handler = Singleton<GameManager>.main.player.GetComponentInChildren<InteractablesHandler>();
            if (handler && handler.CurrentTarget is Chest c && c && !c.isOpened) chest = c;
        }
        Hint.Tick(ChestLooks.Editing ? null : chest);   // "X: quick deposit" / "V: customize" under the chest's name
        ChestLooks.Tick(chest);   // V: its name and colour
        if (kb == null || !Application.isFocused || ChestLooks.Editing) return;
        KeyControl k = kb[key.Value];
        if (k == null || !k.wasPressedThisFrame) return;
        if (kb.shiftKey.isPressed) { if (FreeToPlay) Nearby(); }   // Shift+X: every chest around
        else if (chest != null) Run(chest);
    }

    // ---- Shift+X: every chest nearby gets what it already holds (Terraria's "quick stack to nearby chests") ----

    static float quietUntil;   // a few seconds after Shift+X: no "nothing to put in" per chest (in a session they come in late)
    static bool sweeping => Time.unscaledTime < quietUntil;

    static void Nearby()
    {
        var me = Singleton<GameManager>.main.player;
        if (!me) return;
        var at = me.transform.position;
        var chests = new List<Chest>();
        foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsSortMode.None))
        {
            if (!c || !c.isActiveAndEnabled || c.isOpened || string.IsNullOrEmpty(c.ID)) continue;
            if (((Vector2)(c.transform.position - at)).magnitude > nearbyRange.Value) continue;
            chests.Add(c);
        }
        if (chests.Count == 0) { Note(null, "No chests nearby"); return; }
        chests.Sort((a, b) => ((Vector2)(a.transform.position - at)).sqrMagnitude.CompareTo(((Vector2)(b.transform.position - at)).sqrMagnitude));
        int moved = 0;
        quietUntil = Time.unscaledTime + 4f;
        foreach (var c in chests) moved += Run(c);   // (in a session the multiplayer mod queues these through each chest's owner)
        Note(null, moved > 0 ? $"Quick stacked into {chests.Count} nearby chests" : $"Checking {chests.Count} nearby chests");
    }

    /// <summary>X on this chest. (The multiplayer mod patches this one.)</summary>
    public static int Run(Chest chest) => StackInto(chest);

    /// <summary>The chest screen's "send common" on this chest's list, without the screen. Returns how many items went in.</summary>
    public static int StackInto(Chest chest)
    {
        ChestFilter.QuickId = chest ? chest.ID : null;   // what it takes, for this deposit
        try { return StackIntoChest(chest); }
        finally { ChestFilter.QuickId = null; }
    }

    static int StackIntoChest(Chest chest)
    {
        var list = Singleton<ChestManager>.main.GetChestItems(chest.ID);
        if (list == null) { if (!sweeping) Note(null, "That chest isn't ready yet: open it once first"); return 0; }
        var inv = Singleton<Inventory>.main;
        var before = BagByItem(inv);
        inv.AddSectionItems(list);
        try { inv.SendCommonToChest(); }
        finally { inv.RemoveSectionItems(InventorySection.Chest); }   // writes the chest section back into its list
        var after = BagByItem(inv);

        int moved = 0;
        foreach (var pair in before)
        {
            after.TryGetValue(pair.Key, out var now);
            int gone = pair.Value.count - now.count;
            if (gone <= 0) continue;
            moved += gone;
            var data = pair.Value.data;
            Note("BC_stored_" + pair.Key, data.itemIconUI, "Stored " + ItemName(data), gone);
        }
        if (moved == 0 && !sweeping) Note(null, "Nothing in your bag that this chest already has");
        return moved;
    }

    /// <summary>How many of each item (by address key) are in the bag.</summary>
    static Dictionary<string, (ItemData data, int count)> BagByItem(Inventory inv)
    {
        var counts = new Dictionary<string, (ItemData, int)>();
        foreach (var it in inv.allItems)
        {
            if (it.section != InventorySection.Main || it.itemObject?.data == null) continue;
            string k = it.itemObject.data.addressKey ?? it.itemObject.data.name;
            counts.TryGetValue(k, out var c);
            counts[k] = (it.itemObject.data, c.Item2 + it.itemObject.amount);
        }
        return counts;
    }

    static NotificationsManager notifications;

    static void Note(object owner, string text) => Note(owner ?? typeof(QuickStack), null, text, 1);

    /// <summary>A line in the game's notification list on the right (icon + text, "(12)" when the same owner adds up).</summary>
    internal static void Note(object owner, Sprite icon, string text, int amount)
    {
        if (!notifications) notifications = Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(owner, icon, text, 0f, amount);
        else Plugin.Log.LogInfo(text);
    }

    /// <summary>An item's name in the player's language (its asset name if that fails).</summary>
    static string ItemName(ItemData data)
    {
        try { var s = data.Name == null || data.Name.IsEmpty ? null : data.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(s)) return s; }
        catch { }
        return data.name;
    }
}

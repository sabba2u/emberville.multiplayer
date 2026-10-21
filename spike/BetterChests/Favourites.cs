using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// F over an item in the inventory (worn and held ones too) or a chest: a favourite (a little heart at the slot's top right), F again: not.
/// Favourites never leave the bag by themselves: X, Shift+X and the chest screen's "send all / send commons" leave
/// them where they are (dragging one in by hand still works). The mark is kept on the item itself (a number attribute
/// the game saves with it; the favourite-weapons mod's F5-F8 keys ignore number attributes, so they still find it).
/// </summary>
[HarmonyPatch]
internal static class Favourites
{
    const ItemRandomValues.Attr Mark = (ItemRandomValues.Attr)85;   // (77-79: the staffs and the hook)
    static readonly Dictionary<UISlot, (InventorySection section, int index)> slots = new Dictionary<UISlot, (InventorySection, int)>();
    static Sprite heart;

    public static bool Is(ItemObject item) =>
        item?.randomValues?.strAttributes != null && item.randomValues.strAttributes.Exists(a => a != null && a.Key == Mark && a.Value == "1");

    static void Set(ItemObject item, bool on)
    {
        var list = item.randomValues.strAttributes;
        list.RemoveAll(a => a != null && a.Key == Mark);
        if (on) list.Add(new ItemRandomValues.StrItemAttribute(Mark, "1"));
    }

    /// <summary>Every frame: F over a slot (there's one under the mouse only while the inventory or a chest is open).</summary>
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused || !kb.fKey.wasPressedThisFrame) return;   // (a slot under the mouse = the screen is open)
        if (ChestLooks.Editing || ChestSearch.Typing) return;
        if (!Singleton<Inventory>.HasMain) return;
        var inv = Singleton<Inventory>.main;
        ItemObject item = null;
        foreach (var kv in slots)
        {
            if (!kv.Key || !kv.Key.isMouseOn || !kv.Key.isActiveAndEnabled) continue;
            var (section, index) = kv.Value;
            item = inv.allItems.Find(x => x.section == section && x.slotIndex == index)?.itemObject;
            break;
        }
        if (item == null)   // what you wear / hold: the equipment slots
            foreach (var kv in worn)
                if (kv.Key && kv.Key.isMouseOn && kv.Key.isActiveAndEnabled) { item = kv.Value; break; }
        if (item?.data == null) return;
        bool now = !Is(item);
        Set(item, now);
        QuickStack.Note(typeof(Favourites), item.data.itemIconUI, (now ? "Favourite: " : "No longer a favourite: ") + item.ToString(), 1);
        inv.SetInventoryChanged();   // the slots again, with the heart
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (ui) ui.UpdateGearInventory();
    }

    /// <summary>The equipment slots and what's in them (filled by InventoryUI.UpdateGearInventory, not with the rest).</summary>
    static readonly Dictionary<UISlot, ItemObject> worn = new Dictionary<UISlot, ItemObject>();

    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UpdateGearInventory)), HarmonyPostfix]
    static void AfterGear(InventoryUI __instance)
    {
        worn.Clear();
        if (__instance.player == null || __instance.player.characterStats == null) return;
        foreach (var item in __instance.player.characterStats.EquippedItems)
        {
            GearSlot? at = item?.data is CollectableGear g ? g.gear.gearSlot : item?.data is CollectableWeapon w ? w.weapon.gearSlot : (GearSlot?)null;
            if (!at.HasValue) continue;
            var cs = __instance.gearSlots.Find(x => (x.gearSlot & at.Value) != 0);
            if (cs?.slot) worn[cs.slot] = item;
        }
        foreach (var cs in __instance.gearSlots)
        {
            if (!cs?.slot) continue;
            bool fav = worn.TryGetValue(cs.slot, out var it) && Is(it);
            var h = HeartOf(cs.slot, fav);
            if (h) h.SetActive(fav);
        }
    }

    // ---- the heart on the slots ----

    [HarmonyPatch(typeof(InventoryUI), "UpdateInventoryUI"), HarmonyPostfix]
    static void AfterFill(List<UISlot> slotList, List<InventoryItem> items, InventorySection section)
    {
        foreach (var gone in new List<UISlot>(slots.Keys)) if (!gone) slots.Remove(gone);
        for (int i = 0; i < slotList.Count; i++)
        {
            var slot = slotList[i];
            if (!slot) continue;
            slots[slot] = (section, i);
            var item = items.Find(x => x.section == section && x.slotIndex == i);
            bool fav = Is(item?.itemObject);
            var h = HeartOf(slot, fav);
            if (h) h.SetActive(fav);
        }
    }

    static GameObject HeartOf(UISlot slot, bool make)
    {
        var t = slot.transform.Find("BC_Favourite");
        if (t) { t.SetAsLastSibling(); return t.gameObject; }
        if (!make) return null;
        var go = new GameObject("BC_Favourite", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(slot.transform, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;   // top right
        rt.anchoredPosition = new Vector2(-3f, -3f);
        var size = ((RectTransform)slot.transform).rect.size;
        float side = Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.3f, 9f, 22f);
        rt.sizeDelta = new Vector2(side, side * 7f / 8f);
        var img = go.AddComponent<Image>();
        img.sprite = Heart();
        img.raycastTarget = false;
        rt.SetAsLastSibling();
        return go;
    }

    /// <summary>A pixel heart (8x7, outlined), crisp like the game's own art.</summary>
    static Sprite Heart()
    {
        if (heart) return heart;
        string[] rows =
        {
            ".OO.OO..",
            "ORROWRO.",
            "ORRRRRO.",
            "ORRRRRO.",
            ".ORRRO..",
            "..ORO...",
            "...O....",
        };
        var tex = new Texture2D(8, 7, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 8; x++)
            {
                char c = rows[6 - y][x];
                tex.SetPixel(x, y, c == 'O' ? new Color32(40, 12, 16, 255) : c == 'R' ? new Color32(226, 58, 72, 255) : c == 'W' ? new Color32(255, 190, 196, 255) : new Color32(0, 0, 0, 0));
            }
        tex.Apply();
        return heart = Sprite.Create(tex, new Rect(0, 0, 8, 7), new Vector2(0.5f, 0.5f), 8f);
    }

    // ---- favourites sit out every sweep into a chest (X, Shift+X, "send all / send commons") ----

    const InventorySection Aside = (InventorySection)(1 << 21);

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyPrefix]
    static void BeforeSweep(Inventory __instance, InventorySection fromSection, InventorySection toSection, out List<InventoryItem> __state)
    {
        __state = null;
        if (toSection != InventorySection.Chest) return;
        foreach (var it in __instance.allItems)
        {
            if (it.section != fromSection || !Is(it.itemObject)) continue;
            (__state ??= new List<InventoryItem>()).Add(it);
            it.section = Aside;
        }
    }

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyFinalizer]
    static void AfterSweep(Inventory __instance, InventorySection fromSection, List<InventoryItem> __state)
    {
        if (__state == null) return;
        foreach (var it in __state) it.section = fromSection;
        __instance.SetInventoryChanged();
    }
}

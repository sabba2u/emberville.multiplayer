using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EmbervilleFavorites;

/// <summary>
/// "F5".."F8" in the bottom-right corner of a weapon's slot in the inventory (and chests / stores) when it's on that
/// key. The label is a copy of the slot's own amount number, so it's the game's font and size.
/// The inventory fills a slot with InventoryUI.SetUISlot (each slot starts with SetIcon, which clears our label, so a
/// slot that's emptied or given another item loses it).
/// </summary>
[HarmonyPatch]
internal static class SlotBadges
{
    static readonly Dictionary<UISlot, ItemObject> shown = new Dictionary<UISlot, ItemObject>();
    static readonly Dictionary<UISlot, TextMeshProUGUI> labels = new Dictionary<UISlot, TextMeshProUGUI>();
    static readonly Color Gold = new Color(1f, 0.82f, 0.29f, 1f);

    [HarmonyPatch(typeof(UISlot), nameof(UISlot.SetIcon)), HarmonyPrefix]
    static void Cleared(UISlot __instance)
    {
        if (!shown.Remove(__instance)) return;
        if (labels.TryGetValue(__instance, out var l) && l) l.gameObject.SetActive(false);
    }

    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.SetUISlot)), HarmonyPostfix]
    static void Filled(UISlot slot, ItemObject item)
    {
        if (!slot || item == null) return;
        shown[slot] = item;
        Show(slot, item);
    }

    /// <summary>After a weapon is put on / taken off a key: redo the labels on screen.</summary>
    public static void Refresh()
    {
        var gone = new List<UISlot>();
        foreach (var pair in shown)
        {
            if (!pair.Key) { gone.Add(pair.Key); continue; }
            Show(pair.Key, pair.Value);
        }
        foreach (var s in gone) shown.Remove(s);
    }

    static void Show(UISlot slot, ItemObject item)
    {
        int key = Favorites.KeyOf(item);
        labels.TryGetValue(slot, out var label);
        if (key < 0) { if (label) label.gameObject.SetActive(false); return; }
        if (!label)
        {
            label = Make(slot);
            if (!label) return;
            labels[slot] = label;
        }
        label.text = $"F{key + 5}";
        label.gameObject.SetActive(true);
        label.transform.SetAsLastSibling();
    }

    /// <summary>Our label: the slot's amount number, copied, on the slot itself, bottom-right.</summary>
    static TextMeshProUGUI Make(UISlot slot)
    {
        var amount = slot.amountDisplay;
        TextMeshProUGUI label;
        if (amount)
        {
            label = Object.Instantiate(amount, slot.transform, false);
            label.gameObject.name = "EF_FavKey";
        }
        else
        {
            var go = new GameObject("EF_FavKey", typeof(RectTransform));
            go.transform.SetParent(slot.transform, false);
            label = go.AddComponent<TextMeshProUGUI>();
            label.fontSize = 14;
        }
        label.raycastTarget = false;
        label.color = Gold;
        label.alignment = TextAlignmentOptions.BottomRight;
        label.enableWordWrapping = false;
        var rt = label.rectTransform;
        var slotRect = (RectTransform)slot.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(slotRect.rect.width * 0.8f, slotRect.rect.height * 0.5f);
        rt.anchoredPosition = new Vector2(-3f, 2f);
        return label;
    }
}

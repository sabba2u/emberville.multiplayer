using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// Search boxes on the inventory screen: one over a chest's slots (while it's open), one over your bag's (whenever the
/// inventory is up). Type "swo" and every slot whose item's name (in the player's language) doesn't have "swo" in it
/// goes dark. The boxes are copies of the game's own text fields (GameTextField), placed in the inventory screen just
/// above their slots; while one has the keyboard, the game's keys are off (typing "i" doesn't close the bag). Each
/// clears when its slots go away.
/// </summary>
[HarmonyPatch]
internal static class ChestSearch
{
    sealed class Box
    {
        public string Query = "", Control, Hint;
        public InventorySection Section;
        public System.Func<InventoryUI, List<UISlot>> Slots;
        public TMPro.TMP_InputField Field;
        public bool WasFocused;
    }

    static readonly Box chest = new Box { Control = "bcChestSearch", Hint = "Search this chest...", Section = InventorySection.Chest, Slots = u => u.chestSlots };
    static readonly Box bag = new Box { Control = "bcBagSearch", Hint = "Search your bag...", Section = InventorySection.Main, Slots = u => u.mainSlots };
    static readonly Box[] boxes = { chest, bag };

    static InventoryUI ui;
    static readonly Dictionary<ItemData, string> names = new Dictionary<ItemData, string>();

    public static bool Typing { get; private set; }

    static bool Matches(Box b, ItemObject item)
    {
        if (b.Query.Length == 0 || item?.data == null) return true;
        if (!names.TryGetValue(item.data, out var n))
        {
            n = item.data.name;
            try { var s = item.data.Name == null || item.data.Name.IsEmpty ? null : item.data.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(s)) n = s; }
            catch { }
            names[item.data] = n = n.ToLowerInvariant();
        }
        return n.Contains(b.Query.ToLowerInvariant());
    }

    // ---- the slots: the ones that don't match go dark ----

    [HarmonyPatch(typeof(InventoryUI), "UpdateInventoryUI"), HarmonyPostfix]
    static void AfterFill(InventoryUI __instance, List<UISlot> slotList, List<InventoryItem> items, InventorySection section)
    {
        ui = __instance;
        foreach (var b in boxes)
            if (b.Section == section && slotList == b.Slots(__instance)) Dim(b, slotList, items);
    }

    static void Dim(Box b, List<UISlot> slotList, List<InventoryItem> items)
    {
        for (int i = 0; i < slotList.Count; i++)
        {
            var slot = slotList[i];
            if (!slot) continue;
            var item = b.Query.Length == 0 ? null : items.Find(x => x.section == b.Section && x.slotIndex == i);
            bool dim = item != null && !Matches(b, item.itemObject);
            var t = slot.transform.Find("BC_SearchDim");
            if (!t && dim)
            {
                var go = new GameObject("BC_SearchDim", typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(slot.transform, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                var img = go.AddComponent<Image>();
                img.color = new Color(0.06f, 0.05f, 0.05f, 0.82f);
                img.raycastTarget = false;   // just dark: still yours to take if you mean to
                t = rt;
            }
            if (t) { t.SetAsLastSibling(); t.gameObject.SetActive(dim); }
        }
        var refused = null as Transform;   // the chest filter's grey boxes stay on top of ours
        foreach (var s in slotList) if (s && (refused = s.transform.Find("BC_Refused"))) refused.SetAsLastSibling();
    }

    static void Redraw(Box b)
    {
        if (!ui) ui = Object.FindAnyObjectByType<InventoryUI>();
        if (!ui) return;
        if (b == chest) ui.UpdateChest(); else ui.UpdateMain();
    }

    // ---- the boxes: the game's own text fields, in the inventory screen above each set of slots ----

    /// <summary>Every frame (Plugin.Update): each box made, placed, shown with its slots; the keys off while one's typed in.</summary>
    public static void Tick()
    {
        if (!ui) ui = Object.FindAnyObjectByType<InventoryUI>();
        bool typing = false;
        foreach (var b in boxes)
        {
            var slots = ui ? b.Slots(ui) : null;
            bool shown = slots != null && slots.Count > 0 && slots[0] && slots[0].gameObject.activeInHierarchy &&
                         (b != chest || ChestFilter.OpenChestId != null && !ChestFilter.OpenChestId.StartsWith("bnq_"));   // (not an NPC's give box)
            if (!shown)
            {
                if (b.Field && b.Field.gameObject.activeSelf) b.Field.gameObject.SetActive(false);
                if (b.Query.Length > 0) { b.Query = ""; if (b.Field) b.Field.SetTextWithoutNotify(""); Redraw(b); }
                continue;
            }
            if (!b.Field) Make(b, slots);
            if (!b.Field) continue;
            if (!b.Field.gameObject.activeSelf) b.Field.gameObject.SetActive(true);
            if (b == bag && BesideGold(b.Field)) { } else Place(b.Field, slots, below: b == chest);
            if (b.Field.isFocused != b.WasFocused)
            {
                b.WasFocused = b.Field.isFocused;
                Plugin.Log.LogInfo($"Search box ({b.Hint}): {(b.WasFocused ? "typing" : "done")}, readOnly {b.Field.readOnly}, selected {UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.name}");
            }
            typing |= b.Field.isFocused;
        }
        SetTyping(typing);
    }

    static void Make(Box b, List<UISlot> slots)
    {
        // on the screen's top canvas: inside the slots' own panel a scroll view's mask could clip it (Tick shows / hides it)
        var canvas = slots[0].GetComponentInParent<Canvas>();
        var panel = canvas ? canvas.rootCanvas.transform : null;
        if (!panel) return;
        try
        {
            b.Field = GameTextField.Make(panel, "BC_" + b.Control, b.Hint, 24);
            var box = b;
            b.Field.onValueChanged.AddListener(v => { box.Query = v ?? ""; Redraw(box); });
            b.Field.onSubmit.AddListener(_ => b.Field.DeactivateInputField());   // Enter: done typing, the search stays
        }
        catch (System.Exception e) { Plugin.Log.LogWarning($"Search box: {e.Message}"); }
    }

    /// <summary>The bag's box: in the strip at the bottom of the bag, just right of the coins, as tall as their box and
    /// to the bag's right edge. False when the coins aren't there (then above the slots).</summary>
    static bool BesideGold(TMPro.TMP_InputField field)
    {
        var gold = ui ? ui.goldAmount : null;
        var pill = gold ? gold.transform.parent as RectTransform : null;
        var slots = ui ? ui.mainSlots : null;
        if (!pill || !pill.gameObject.activeInHierarchy || slots == null || slots.Count == 0) return false;
        var parent = (RectTransform)field.transform.parent;
        var corners = new Vector3[4];
        pill.GetWorldCorners(corners);
        var lo = parent.InverseTransformPoint(corners[0]);
        var hi = parent.InverseTransformPoint(corners[2]);
        float right = float.MinValue;
        foreach (var s in slots)
        {
            if (!s || !s.gameObject.activeInHierarchy) continue;
            ((RectTransform)s.transform).GetWorldCorners(corners);
            right = Mathf.Max(right, parent.InverseTransformPoint(corners[2]).x);
        }
        if (right <= hi.x) return false;
        var rt = (RectTransform)field.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        float h = Mathf.Max(26f, hi.y - lo.y);
        float left = hi.x + 10f;
        rt.sizeDelta = new Vector2(Mathf.Min(right - left, 320f), h);
        var centre = parent.rect.center;
        rt.anchoredPosition = new Vector2(left - centre.x, (lo.y + hi.y) / 2f - centre.y);
        rt.SetAsLastSibling();
        return true;
    }

    /// <summary>Just above the slots (or, for the chest, just under them, like the bag's box at the bag's foot), from
    /// their left edge, at most about a third of a screen wide.</summary>
    static void Place(TMPro.TMP_InputField field, List<UISlot> slots, bool below = false)
    {
        var parent = (RectTransform)field.transform.parent;
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, minY = float.MaxValue;
        var corners = new Vector3[4];
        foreach (var s in slots)
        {
            if (!s || !s.gameObject.activeInHierarchy) continue;
            ((RectTransform)s.transform).GetWorldCorners(corners);
            foreach (var c in corners)
            {
                var p = parent.InverseTransformPoint(c);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); minY = Mathf.Min(minY, p.y);
            }
        }
        if (minX > maxX) return;
        var rt = (RectTransform)field.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, below ? 1f : 0f);
        float h = Mathf.Max(28f, rt.sizeDelta.y > 0 ? Mathf.Min(rt.sizeDelta.y, 44f) : 32f);
        rt.sizeDelta = new Vector2(Mathf.Min(maxX - minX, below ? 320f : 300f), h);
        // positions in the parent's space, measured from its centre (the anchors)
        var centre = parent.rect.center;
        rt.anchoredPosition = new Vector2(minX - centre.x, (below ? minY - 6f : maxY + 6f) - centre.y);
        rt.SetAsLastSibling();
    }

    // ---- the keyboard stays with the box while you type ----

    /// <summary>
    /// The inventory selects whatever slot the mouse passes over (keyboard navigation), which would take the keyboard
    /// away from the box after one letter. While a box is being typed in, the selection only moves on a click.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem), nameof(UnityEngine.EventSystems.EventSystem.SetSelectedGameObject),
        new[] { typeof(GameObject), typeof(UnityEngine.EventSystems.BaseEventData) }), HarmonyPrefix]
    static bool KeepTyping(GameObject selected)
    {
        foreach (var b in boxes)
        {
            if (!b.Field || !b.Field.isFocused || selected == b.Field.gameObject) continue;
            var m = Mouse.current;
            bool clicked = m != null && (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame);
            return clicked;   // a click elsewhere: fine; the mouse just passing over a slot: not now
        }
        return true;
    }

    // ---- the game's keys off while typing ----

    static List<InputActionMap> switchedOff;

    static void SetTyping(bool on)
    {
        if (on == Typing) return;
        Typing = on;
        if (on)
        {
            switchedOff = new List<InputActionMap>();
            foreach (var map in MainInput.Actions.asset.actionMaps)
            {
                if (!map.enabled || map.name == "UI") continue;   // the screen's own clicks still work
                map.Disable();
                switchedOff.Add(map);
            }
        }
        else if (switchedOff != null)
        {
            foreach (var map in switchedOff) map.Enable();
            switchedOff = null;
        }
    }

    /// <summary>The chest closed: its search goes (the bag's stays while the inventory's up).</summary>
    internal static void Reset()
    {
        chest.Query = "";   // (the keys come back on the next Tick, when its box is gone)
        if (chest.Field) chest.Field.SetTextWithoutNotify("");
    }
}

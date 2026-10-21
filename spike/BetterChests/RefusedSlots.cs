using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// With a chest open that only takes some kinds of things, the bag's and belt's slots holding anything else get a grey
/// box over them (and their frame faded, as the game's own filter tabs do): it takes the mouse, so they can't be
/// dragged or clicked, and hovering one says why. Put on after the game fills the slots (InventoryUI.UpdateInventoryUI),
/// taken off when the chest closes.
/// </summary>
[HarmonyPatch]
internal static class RefusedSlots
{
    static readonly List<GameObject> boxes = new List<GameObject>();

    /// <summary>The note for the slot under the mouse ("" = none): shown by the cursor (ChestUi.Tooltip, from ChestLooks.Tick).</summary>
    internal static string Hover = "";

    [HarmonyPatch(typeof(InventoryUI), "UpdateInventoryUI"), HarmonyPostfix]
    static void AfterFill(List<UISlot> slotList, List<InventoryItem> items, InventorySection section)
    {
        if (section != InventorySection.Main && section != InventorySection.Belt) return;
        int takes = ChestFilter.OpenTakes;
        for (int i = 0; i < slotList.Count; i++)
        {
            var slot = slotList[i];
            if (!slot) continue;
            var item = takes == 0 ? null : items.Find(x => x.section == section && x.slotIndex == i);
            bool refuse = item?.itemObject?.data != null && ((int)item.itemObject.data.itemCategory & takes) == 0;
            var box = BoxOf(slot, refuse);
            if (box) box.SetActive(refuse);
            if (refuse) slot.SetFrameAlpha(0.2f);
        }
    }

    static GameObject BoxOf(UISlot slot, bool make)
    {
        var t = slot.transform.Find("BC_Refused");
        if (t) { t.SetAsLastSibling(); return t.gameObject; }
        if (!make) return null;
        var go = new GameObject("BC_Refused", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(slot.transform, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.color = new Color(0.22f, 0.22f, 0.24f, 0.72f);
        img.raycastTarget = true;   // takes the mouse: nothing reaches the slot
        go.AddComponent<Blocker>();
        rt.SetAsLastSibling();
        boxes.Add(go);
        return go;
    }

    /// <summary>The chest closed: every box off.</summary>
    internal static void Clear()
    {
        foreach (var b in boxes) if (b) b.SetActive(false);
        boxes.RemoveAll(b => !b);
        Hover = "";
    }

    /// <summary>Eats the pointer (no drag, no click, no double-click on the slot under it) and explains on hover.</summary>
    sealed class Blocker : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnPointerEnter(PointerEventData e) => Hover = "This chest doesn't allow this to be stored (it only takes " + ChestLooks.TakesText(ChestFilter.OpenTakes) + ")";
        public void OnPointerExit(PointerEventData e) => Hover = "";
        public void OnPointerClick(PointerEventData e) { }
        public void OnPointerDown(PointerEventData e) { }
        public void OnPointerUp(PointerEventData e) { }
        public void OnInitializePotentialDrag(PointerEventData e) { e.useDragThreshold = true; }
        public void OnBeginDrag(PointerEventData e) { }
        public void OnDrag(PointerEventData e) { }
        public void OnEndDrag(PointerEventData e) { }
        void OnDisable() { if (Hover.Length > 0) Hover = ""; }
    }
}

using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BetterNPCsAndQuests;

/// <summary>
/// Handing things to an NPC: (trigger: "give &lt;quest&gt;") opens, once the conversation's over, the game's own chest
/// screen with an empty box for them (NPCs have no inventory: the box exists only while it's open, and is never
/// saved), your bag beside it. Drag in what they asked for and close it: they take what the quest's current step
/// needs, everything else comes back to your bag, and they talk again ($just_gave or $give_failed set for that
/// conversation). The chest screen is the game's: a hidden Chest that's never woken (its scripts never start); its
/// close goes through us. While it's open, the line above the box says what they're looking for, and every slot in
/// your bag holding something else is greyed over (it can't be dragged in; "send all" leaves it out).
/// </summary>
[HarmonyPatch]   // (without it PatchAll skips the patches in this class)
internal static class GiveBox
{
    const string BoxId = "bnq_give";
    static string pendingQuest;
    static Npc pendingNpc;
    static Chest box;
    static string openFor;
    static Npc openNpc;
    static List<ItemSpec> wanted = new List<ItemSpec>();
    static TMPro.TextMeshProUGUI header;
    static readonly List<GameObject> covers = new List<GameObject>();

    static bool IsOpen => openFor != null;

    /// <summary>From the dialogue: open the box for this quest once the conversation has closed.</summary>
    public static void Ask(string questId)
    {
        pendingQuest = questId;
        pendingNpc = Talk.Speaking;
    }

    public static void Tick()
    {
        PlaceHeader();
        if (pendingQuest == null || Talk.IsOpen) return;
        string q = pendingQuest;
        pendingQuest = null;
        Open(q, pendingNpc);
    }

    static void Open(string questId, Npc npc)
    {
        if (!Singleton<ChestManager>.HasMain || State.Lib.Quest(questId) == null) return;
        var mgr = Singleton<ChestManager>.main;
        mgr.RemoveChest(BoxId);
        mgr.AddChest(BoxId, new List<InventoryItem>(), false, ChestShare.None);
        if (!box)
        {
            var go = new GameObject("BNQ_GiveBox");
            go.SetActive(false);   // never woken: the Chest's own scripts (save, art, sounds) never start
            Object.DontDestroyOnLoad(go);
            box = go.AddComponent<Chest>();
            box.chestID = BoxId;
        }
        openFor = questId;
        openNpc = npc;
        wanted = Wanted(questId);
        box.isOpened = true;
        Bus<ChestEvent>.Invoke(new ChestEvent { chest = box, EventType = ChestEvent.Event_Type.Open });
        string who = npc != null ? npc.Def.Name : "They";
        ShowHeader($"{who} is looking for: {string.Join(", ", wanted.Select(w => (w.Count > 1 ? w.Count + " " : "") + Items.Label(w)))}");
        Redraw();
    }

    /// <summary>What the quest's current step still wants handed in.</summary>
    static List<ItemSpec> Wanted(string questId)
    {
        var q = State.Lib.Quest(questId);
        var p = State.Book.Progress(questId);
        if (q == null || p.State != QState.Active) return new List<ItemSpec>();
        var step = q.Steps[p.Step];
        return step.Objectives.Where((o, i) => o.Kind == "collect" && (i >= p.Counts.Length || p.Counts[i] < o.Count)).Select(o => o.Item).ToList();
    }

    static bool Fits(ItemObject item) => item?.data != null && wanted.Any(w => Items.Matches(item.data, w));

    static void Redraw()
    {
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (!ui) return;
        ui.UpdateMain();
        ui.UpdateBeltInventory();
    }

    // ---- the line above the box: on a dark card, like the name cards over heads ----

    static RectTransform card;
    static RectTransform give;
    static readonly List<GameObject> hidden = new List<GameObject>();

    static void ShowHeader(string text)
    {
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        var first = ui ? ui.chestSlots.FirstOrDefault(s => s) : null;
        var canvas = first ? first.GetComponentInParent<Canvas>()?.rootCanvas : null;
        if (!canvas) return;
        if (!card || card.parent != canvas.transform)
        {
            if (card) Object.Destroy(card.gameObject);
            if (give) Object.Destroy(give.gameObject);
            card = NpcUi.Rect("BNQ_LookingFor", canvas.transform);
            var bg = card.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.88f);
            bg.raycastTarget = false;
            header = NpcUi.Label(card, "", 22, NpcUi.Gold, TMPro.TextAlignmentOptions.Center);
            NpcUi.Stretch(header.rectTransform, 12, 3, 12, 3);
            // hand it over: the same as closing the box (Esc)
            give = (RectTransform)NpcUi.Button(canvas.transform, "Give", () => { if (box && IsOpen) box.CloseChest(); }).transform;
            give.sizeDelta = new Vector2(120f, 38f);
        }
        header.text = text;
        var size = header.GetPreferredValues(text);
        card.sizeDelta = new Vector2(size.x + 24f, size.y + 8f);
        card.gameObject.SetActive(true);
        give.gameObject.SetActive(true);
        PlaceHeader();
    }

    static void PlaceHeader()
    {
        if (!card) return;
        if (!IsOpen)
        {
            if (card.gameObject.activeSelf) card.gameObject.SetActive(false);
            if (give && give.gameObject.activeSelf) give.gameObject.SetActive(false);
            ShowChestButtons();
            return;
        }
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        var slots = ui ? ui.chestSlots.Where(s => s && s.gameObject.activeInHierarchy).ToList() : null;
        if (slots == null || slots.Count == 0) return;
        HideChestButtons();
        var parent = (RectTransform)card.parent;
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, minY = float.MaxValue;
        var corners = new Vector3[4];
        void Take(RectTransform r, bool bottom)
        {
            r.GetWorldCorners(corners);
            foreach (var c in corners)
            {
                var p = parent.InverseTransformPoint(c);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
                if (bottom) minY = Mathf.Min(minY, p.y);
            }
        }
        foreach (var s in slots) Take((RectTransform)s.transform, true);
        // above the chest window itself (its frame and the buttons along its top), not just the slots
        var frame = ChestWindow(ui);
        if (frame) Take(frame, false);
        var centre = parent.rect.center;
        float midX = (minX + maxX) / 2f - centre.x;
        card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
        card.pivot = new Vector2(0.5f, 0f);
        card.anchoredPosition = new Vector2(midX, maxY + 4f - centre.y);
        card.SetAsLastSibling();
        // the Give button: under the box's slots, in the middle
        give.anchorMin = give.anchorMax = new Vector2(0.5f, 0.5f);
        give.pivot = new Vector2(0.5f, 1f);
        give.anchoredPosition = new Vector2(midX, minY - 6f - centre.y);
        give.SetAsLastSibling();
    }

    /// <summary>The chest screen's own buttons (take all, send all, ...): hidden while it's an NPC's box.</summary>
    static void HideChestButtons()
    {
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        if (!panels || !panels.panelChest) return;
        foreach (var b in panels.panelChest.GetComponentsInChildren<Button>())
        {
            if (!b.gameObject.activeSelf || b.GetComponentInParent<UISlot>()) continue;
            b.gameObject.SetActive(false);
            hidden.Add(b.gameObject);
        }
    }

    static void ShowChestButtons()
    {
        foreach (var go in hidden) if (go) go.SetActive(true);
        hidden.Clear();
    }

    /// <summary>The chest window: the nearest parent of the chest slots that draws a background.</summary>
    static RectTransform ChestWindow(InventoryUI ui)
    {
        for (var t = ui.chestSlotsHolder ? ui.chestSlotsHolder.parent : null; t && t.GetComponent<Canvas>() == null; t = t.parent)
            if (t.GetComponent<Image>() is Image img && img.enabled && img.sprite) return (RectTransform)t;
        return null;
    }

    // ---- everything else in the bag: greyed over, not draggable ----

    [HarmonyPatch(typeof(InventoryUI), "UpdateInventoryUI"), HarmonyPostfix]
    static void AfterFill(List<UISlot> slotList, List<InventoryItem> items, InventorySection section)
    {
        if (section != InventorySection.Main && section != InventorySection.Belt) return;
        for (int i = 0; i < slotList.Count; i++)
        {
            var slot = slotList[i];
            if (!slot) continue;
            var item = IsOpen ? items.Find(x => x.section == section && x.slotIndex == i) : null;
            bool grey = item?.itemObject != null && !Fits(item.itemObject);
            var t = slot.transform.Find("BNQ_NotWanted");
            if (!t && grey)
            {
                var go = new GameObject("BNQ_NotWanted", typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(slot.transform, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var img = go.AddComponent<Image>();
                img.color = new Color(0.22f, 0.22f, 0.24f, 0.72f);
                img.raycastTarget = true;   // takes the mouse: it can't be picked up
                go.AddComponent<Blocker>();
                covers.Add(go);
                t = rt;
            }
            if (t) { t.SetAsLastSibling(); t.gameObject.SetActive(grey); }
        }
    }

    /// <summary>Eats the pointer: no drag, no click on what's under it.</summary>
    sealed class Blocker : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnPointerClick(PointerEventData e) { }
        public void OnPointerDown(PointerEventData e) { }
        public void OnPointerUp(PointerEventData e) { }
        public void OnInitializePotentialDrag(PointerEventData e) { e.useDragThreshold = true; }
        public void OnBeginDrag(PointerEventData e) { }
        public void OnDrag(PointerEventData e) { }
        public void OnEndDrag(PointerEventData e) { }
    }

    // "send all" into the box: only what fits
    const InventorySection Aside = (InventorySection)(1 << 22);

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyPrefix]
    static void BeforeSweep(Inventory __instance, InventorySection fromSection, InventorySection toSection, out List<InventoryItem> __state)
    {
        __state = null;
        if (!IsOpen || toSection != InventorySection.Chest) return;
        foreach (var it in __instance.allItems)
        {
            if (it.section != fromSection || Fits(it.itemObject)) continue;
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

    /// <summary>Our box closing (the chest screen's close / Esc): the game writes the box back, then we hand it over.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.CloseChest)), HarmonyPrefix]
    static bool Closing(Chest __instance)
    {
        if (!box || __instance != box) return true;
        box.isOpened = false;
        Bus<ChestEvent>.Invoke(new ChestEvent { chest = box, EventType = ChestEvent.Event_Type.Close });   // (the screen writes the box's slots back to its list)
        try { Hand(); }
        catch (System.Exception e) { Log.Warn($"NPCs and quests: the give box: {e}"); }
        PlaceHeader();
        Redraw();   // (the grey covers off)
        return false;   // (not the chest's own close: its art and sounds were never set up)
    }

    static void Hand()
    {
        var mgr = Singleton<ChestManager>.main;
        var items = mgr.GetChestItems(BoxId) ?? new List<InventoryItem>();
        var bag = new BoxBag(items);
        string q = openFor;
        openFor = null;
        bool ok = false;
        if (q != null && items.Count > 0)
        {
            int step = State.Book.Progress(q).Step;
            ok = State.Book.TurnIn(q, bag);
            if (ok && Hooks.IsClient()) Hooks.Ask($"handed {q} {step}");
        }
        // everything they didn't take: back to the bag (at your feet if it's full)
        var me = LocalPlayer.Get();
        Items.Ours = true;
        try
        {
            foreach (var it in items.ToList())
            {
                if (it?.itemObject == null || it.itemObject.amount <= 0) continue;
                int rest = Singleton<Inventory>.main.AddItem(it.itemObject);
                if (rest > 0 && me) it.itemObject.DropToWorld(me.transform.position, rest, 1f);
            }
        }
        finally { Items.Ours = false; }
        mgr.RemoveChest(BoxId);
        if (items.Count == 0) return;   // closed empty: nothing to say
        State.Book.Flags.Set(ok ? "just_gave" : "give_failed", 1);
        var npc = openNpc;
        openNpc = null;
        if (npc != null && npc.Body.Root) Talk.Open(npc);   // they answer (the dialogue clears the flag)
    }

    /// <summary>The box's contents as a bag: what the quest step takes comes out of it.</summary>
    sealed class BoxBag : IBag
    {
        readonly List<InventoryItem> items;
        public BoxBag(List<InventoryItem> items) => this.items = items;

        public int Count(ItemSpec spec) => items.Where(i => i?.itemObject != null && Items.Matches(i.itemObject.data, spec)).Sum(i => System.Math.Max(1, i.itemObject.amount));

        public bool Take(ItemSpec spec, int n)
        {
            if (Count(spec) < n) return false;
            foreach (var i in items.Where(i => i?.itemObject != null && Items.Matches(i.itemObject.data, spec)).ToList())
            {
                if (n <= 0) break;
                int use = System.Math.Min(System.Math.Max(1, i.itemObject.amount), n);
                i.itemObject.amount -= use;
                n -= use;
                if (i.itemObject.amount <= 0) items.Remove(i);
            }
            return true;
        }
    }
}

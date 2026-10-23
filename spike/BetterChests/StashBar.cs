using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// The stash's tabs: a row of square buttons just above the chest window's slots (the search box is below them), in the
/// game's copper with a stripe of the tab's own colour and its name, the open one edged in gold. Right-click a tab for
/// its name and colour (a small window: a name box, colour swatches, Save / Cancel). And a close button on the chest
/// panel's corner: a copy of the inventory's own.
/// </summary>
internal static class Bar
{
    static RectTransform row;
    static readonly List<(Image edge, Image fill, Image stripe, TextMeshProUGUI label)> tabs = new();
    static bool shown;
    const float RowHeight = 44f;
    static readonly Color Copper = new Color(0.36f, 0.22f, 0.13f), CopperOpen = new Color(0.5f, 0.32f, 0.18f), Edge = new Color(0.55f, 0.4f, 0.26f);

    /// <summary>The tabs' colours: clear, readable ones (a stripe, and the name in them).</summary>
    internal static readonly (string name, string hex)[] Colours =
    {
        ("None", ""), ("Red", "D9534F"), ("Orange", "E8873A"), ("Yellow", "E8C547"), ("Green", "5CB85C"), ("Teal", "3FB0AC"),
        ("Blue", "4A90D9"), ("Purple", "9B6BD6"), ("Pink", "E06AA8"), ("Brown", "9C6B3C"), ("Grey", "9A9A9A"), ("White", "EDEDED"),
    };

    /// <summary>A right-click on a tab: its name and colour.</summary>
    sealed class RightClick : MonoBehaviour, IPointerClickHandler
    {
        public int Tab;
        public void OnPointerClick(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right) Edit(Tab);
        }
    }

    public static void Show(bool on)
    {
        shown = on;
        if (on) return;
        if (row) row.gameObject.SetActive(false);
        if (closeButton) closeButton.SetActive(false);
        HideChestButtons(false);
        Close();
    }

    public static void Tick()
    {
        if (!shown) return;
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        var slots = ui ? ui.chestSlots : null;
        if (slots == null || slots.Count == 0 || !slots[0] || !slots[0].gameObject.activeInHierarchy) { if (row) row.gameObject.SetActive(false); return; }
        if (!row) Build(slots);
        if (!row) return;
        row.gameObject.SetActive(true);
        Place(slots);
        CloseButton();
        HideChestButtons(true);
        Refresh();
        if (editing && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
    }

    static void Build(List<UISlot> slots)
    {
        var canvas = slots[0].GetComponentInParent<Canvas>();
        if (!canvas) return;
        row = ChestUi.Rect("BC_StashTabs", canvas.rootCanvas.transform);
        var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 4; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
        tabs.Clear();
        for (int i = 0; i < Stash.Tabs; i++)
        {
            int tab = i;
            var b = ChestUi.Button(row, "", () => Stash.Switch(tab), square: true);
            b.transition = Selectable.Transition.None;
            var fill = b.transform.Find("Fill")?.GetComponent<Image>();
            var inside = fill ? fill.transform : b.transform;
            var stripe = ChestUi.Rect("Stripe", inside);
            stripe.anchorMin = new Vector2(0f, 0f); stripe.anchorMax = new Vector2(1f, 0f);
            stripe.pivot = new Vector2(0.5f, 0f); stripe.sizeDelta = new Vector2(0f, 6f); stripe.anchoredPosition = Vector2.zero;
            var stripeImg = stripe.gameObject.AddComponent<Image>();
            stripeImg.raycastTarget = false;
            var label = ChestUi.Label(inside, "", 21, ChestUi.Text, TextAlignmentOptions.Center);
            ChestUi.Stretch(label.rectTransform, 4, 6, 4, 2);
            label.enableAutoSizing = true; label.fontSizeMin = 14; label.fontSizeMax = 21;
            b.gameObject.AddComponent<RightClick>().Tab = tab;
            tabs.Add((b.GetComponent<Image>(), fill, stripeImg, label));
        }
    }

    /// <summary>Just above the chest's slots, as wide as them.</summary>
    static void Place(List<UISlot> slots)
    {
        var parent = (RectTransform)row.parent;
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var corners = new Vector3[4];
        foreach (var s in slots)
        {
            if (!s || !s.gameObject.activeInHierarchy) continue;
            ((RectTransform)s.transform).GetWorldCorners(corners);
            foreach (var c in corners) { var p = parent.InverseTransformPoint(c); minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); }
        }
        if (minX > maxX) return;
        row.anchorMin = row.anchorMax = new Vector2(0.5f, 0.5f);
        row.pivot = new Vector2(0f, 0f);
        row.sizeDelta = new Vector2(maxX - minX, RowHeight);
        var centre = parent.rect.center;
        row.anchoredPosition = new Vector2(minX - centre.x, maxY + 6f - centre.y);
        row.SetAsLastSibling();
    }

    static void Refresh()
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            var (edge, fill, stripe, label) = tabs[i];
            string hex = Stash.HexOf(i);
            bool coloured = hex.Length > 0 && ColorUtility.TryParseHtmlString("#" + hex, out _);
            ColorUtility.TryParseHtmlString("#" + hex, out var tint);
            bool open = i == Stash.Current;
            if (fill) fill.color = open ? CopperOpen : Copper;
            if (edge) edge.color = open ? ChestUi.Gold : Edge;
            if (stripe) { stripe.enabled = coloured; stripe.color = tint; }
            if (!label) continue;
            label.color = coloured ? Color.Lerp(tint, Color.white, 0.35f) : ChestUi.Text;
            string name = Stash.NameOf(i);
            if (label.text != name) label.text = name;
        }
    }

    // ---- a tab's name and colour (right-click) ----

    static RectTransform box;
    static TMP_InputField nameField;
    static string hexEdit;
    static readonly List<(string hex, GameObject mark)> swatches = new();
    static List<(InputActionMap map, bool on)> savedMaps;
    static bool editing;
    public static bool Editing => editing;
    internal static TMP_InputField NameField => editing ? nameField : null;

    static void Edit(int tab)
    {
        if (editing) return;
        editing = true;
        hexEdit = Stash.HexOf(tab);
        swatches.Clear();
        const float W = 520, H = 260;   // (the 12 colours fit: 126 + 12 x 30)
        box = ChestUi.Window($"Tab {tab + 1}", new Vector2(W, H), out _);
        var l = ChestUi.Label(box, "Name", 20, ChestUi.Dim);
        ChestUi.Place(l.rectTransform, 36, 64, 90, 34);
        nameField = GameTextField.Make(box, "BC_StashTabName", "Swords, Shields, Ores...", 16);
        ChestUi.Place((RectTransform)nameField.transform, 126, 64, W - 162, 34);
        nameField.text = Stash.NameOf(tab) == (tab + 1).ToString() ? "" : Stash.NameOf(tab);
        l = ChestUi.Label(box, "Colour", 20, ChestUi.Dim);
        ChestUi.Place(l.rectTransform, 36, 116, 90, 30);
        for (int i = 0; i < Colours.Length; i++)
        {
            var (_, hex) = Colours[i];
            var b = ChestUi.Button(box, "", () => { hexEdit = hex; Mark(); });
            var r = (RectTransform)b.transform;
            ChestUi.Place(r, 126 + i * 30, 114, 27, 30);
            var fill = ChestUi.Rect("Colour", r);
            ChestUi.Stretch(fill, 5, 5, 5, 5);
            var img = fill.gameObject.AddComponent<Image>();
            img.color = hex == "" ? new Color(0.22f, 0.2f, 0.2f) : (ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.white);
            img.raycastTarget = false;
            var mark = ChestUi.Rect("Picked", r);
            ChestUi.Stretch(mark, -3, -3, -3, -3);
            var mi = ChestUi.Img(mark.gameObject, "UI_Hud_CommonElements2_16", ChestUi.Gold, 1f);
            mi.raycastTarget = false;
            if (!mi.sprite) mi.color = new Color(1f, 0.85f, 0.4f, 0.6f);
            swatches.Add((hex, mark.gameObject));
        }
        Mark();
        var save = ChestUi.Button(box, "Save", () => { Stash.Rename(tab, nameField.text, hexEdit); Close(); }, square: true);
        ChestUi.Place((RectTransform)save.transform, 36, H - 76, 140, 40);
        var cancel = ChestUi.Button(box, "Cancel", Close, square: true);
        ChestUi.Place((RectTransform)cancel.transform, W - 176, H - 76, 140, 40);
        // the game's controls off while it's up (its UI on: the buttons); not its Esc (ours closes the box)
        savedMaps = new List<(InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            savedMaps.Add((map, map.enabled));
            if (map.name == "UI") { map.Enable(); MainInput.Actions.UI.Cancel.Disable(); }
            else map.Disable();
        }
        nameField.ActivateInputField();
    }

    static void Mark() { foreach (var (hex, mark) in swatches) if (mark) mark.SetActive(hex == hexEdit); }

    static void Close()
    {
        if (!editing) return;
        editing = false;
        if (box) Object.Destroy(box.gameObject);
        box = null;
        MainInput.Actions.UI.Cancel.Enable();
        if (savedMaps != null) foreach (var (map, was) in savedMaps) { if (was) map.Enable(); else map.Disable(); }
        savedMaps = null;
    }

    // ---- the chest panel's own buttons (collect all, collect commons...): not for the stash ----

    static readonly List<GameObject> hidden = new();

    /// <summary>While the stash is open: the chest panel's buttons off (not its slots, not ours); back for real chests.</summary>
    static void HideChestButtons(bool hide)
    {
        if (!hide)
        {
            foreach (var go in hidden) if (go) go.SetActive(true);
            hidden.Clear();
            return;
        }
        var pm = Object.FindAnyObjectByType<PanelsManager>();
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (!pm || !pm.panelChest) return;
        foreach (var b in pm.panelChest.GetComponentsInChildren<Button>(false))
        {
            var go = b.gameObject;
            if (go.name.StartsWith("BC_") || b.GetComponentInParent<UISlot>() || (ui && ui.chestSlotsHolder && go.transform.IsChildOf(ui.chestSlotsHolder))) continue;
            go.SetActive(false);
            hidden.Add(go);
        }
    }

    // ---- a close button on the chest panel (a copy of the inventory's own) ----

    static GameObject closeButton;

    static void CloseButton()
    {
        if (closeButton) { if (!closeButton.activeSelf) closeButton.SetActive(true); return; }
        var pm = Object.FindAnyObjectByType<PanelsManager>();
        var chestPanel = pm ? pm.panelChest : null;
        if (!chestPanel) return;
        Button source = null;
        if (pm.panelInventory)
            foreach (var b in pm.panelInventory.GetComponentsInChildren<Button>(true))
            {
                string n = b.name.ToLowerInvariant(), sp = b.image && b.image.sprite ? b.image.sprite.name.ToLowerInvariant() : "";
                if (n.Contains("close") || n.Contains("exit") || sp.Contains("close") || sp.Contains("exit")) { source = b; break; }
            }
        Button made;
        if (source)
        {
            var copy = Object.Instantiate(source.gameObject, chestPanel.transform, false);
            copy.name = "BC_StashClose";
            var srt = (RectTransform)source.transform; var crt = (RectTransform)copy.transform;
            crt.anchorMin = srt.anchorMin; crt.anchorMax = srt.anchorMax; crt.pivot = srt.pivot;
            crt.anchoredPosition = srt.anchoredPosition; crt.sizeDelta = srt.sizeDelta;
            made = copy.GetComponent<Button>();
            made.onClick = new Button.ButtonClickedEvent();   // (not the inventory's own close)
            Plugin.Log.LogInfo($"Stash: close button copied from the inventory's {source.name} ({(source.image && source.image.sprite ? source.image.sprite.name : "no sprite")})");
        }
        else
        {
            made = ChestUi.Button(chestPanel.transform, "X", null, square: true);
            var r = (RectTransform)made.transform;
            r.anchorMin = r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(1f, 1f);
            r.sizeDelta = new Vector2(36f, 36f); r.anchoredPosition = new Vector2(-6f, -6f);
            Plugin.Log.LogInfo("Stash: no close button in the inventory to copy; a square X instead");
        }
        made.onClick.AddListener(() => { if (Stash.IsOpen) Stash.Toggle(); });
        made.transform.SetAsLastSibling();
        closeButton = made.gameObject;
    }
}

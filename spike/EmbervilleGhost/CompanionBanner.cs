using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// Your companion's card, in the game's own UI (its frame, its font): bottom middle, just above the F5-F8 bar. A live
/// portrait on dark teal (its ring's colour, darker) in a teal outline, its name, health and Kymia bars, and what it's doing.
/// On its own canvas just under the game's HUD canvas, so the game's hover boxes (an item's, a skill's) draw over it.
/// TeamHud says what goes on it (Show / Hide) and handles the right-click menu.
/// Beside it, its four powers (Better Companions' Powers slots) as your skill bar shows yours, copies of your bar's own
/// slots without the key numbers: what it has, what it just used (a flash), what's running, and each cooldown.
/// Left click: it uses that power now. Right click: the powers it has unlocked, stacked above the slot (as your bar's
/// right click), and an X to empty it.
/// </summary>
internal static class CompanionBanner
{
    static RectTransform root, healthFill, kymiaRow, kymiaFill;
    static RawImage portrait;
    static TextMeshProUGUI nameText, lineText;
    static Canvas canvas;

    const float W = 300f, H = 80f, Pic = 62f;
    const float Slot = 62f, Gap = 8f, Space = 6f;   // (its powers: four slots at its right)

    sealed class PowerBox
    {
        public RectTransform Root;
        public Image Icon, Timer, Shade;
        public TextMeshProUGUI Secs;
        public Outline Glow;
    }
    static readonly PowerBox[] powers = new PowerBox[4];
    static RectTransform powersRow;
    static readonly Color Teal = new(0.31f, 0.84f, 0.9f, 1f), DarkTeal = new(0.07f, 0.2f, 0.22f, 1f);

    public static bool Shown => root && root.gameObject.activeSelf;

    /// <summary>The mouse is over it (screen coordinates, Unity's bottom-left).</summary>
    public static bool Contains(Vector2 screen) => Shown && RectTransformUtility.RectangleContainsScreenPoint(root, screen, null);

    /// <summary>The middle of its top edge on screen (Unity's bottom-left): where its menu opens.</summary>
    public static Vector2 TopCentre()
    {
        var c = new Vector3[4];
        root.GetWorldCorners(c);
        return new Vector2((c[1].x + c[2].x) / 2f, c[1].y);
    }

    public static void Hide() { ClosePicker(); if (root) root.gameObject.SetActive(false); }

    /// <param name="favouritesTop">The top of the F5-F8 bar, in screen pixels from the bottom (it sits above that).</param>
    public static void Show(string name, string line, float health01, float kymia01, bool hasKymia, bool down, Texture picture, float favouritesTop)
    {
        if (!root) Build();
        if (!root) return;
        if (!root.gameObject.activeSelf) root.gameObject.SetActive(true);

        // above the F5-F8 bar, centred
        var space = (RectTransform)root.parent;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(space, new Vector2(Screen.width / 2f, favouritesTop), null, out var at);
        // (the card and its powers together, centred: the card left of the middle)
        root.anchoredPosition = new Vector2(-(Gap + 4f * Slot + 3f * Space) / 2f, at.y - space.rect.yMin + 6f);
        if (!powersRow) BuildPowers();
        ShowPowers();
        PickerTick();

        nameText.text = name;
        lineText.text = line;
        portrait.texture = picture;
        portrait.enabled = picture;
        portrait.color = down ? new Color(0.5f, 0.5f, 0.5f, 1f) : Color.white;
        health01 = Mathf.Clamp01(health01);
        healthFill.anchorMax = new Vector2(health01, 1f);
        healthFill.GetComponent<Image>().color = health01 < 0.3f ? new Color(0.9f, 0.25f, 0.2f) : new Color(0.25f, 0.85f, 0.35f);
        kymiaRow.gameObject.SetActive(hasKymia);
        kymiaFill.anchorMax = new Vector2(Mathf.Clamp01(kymia01), 1f);
    }

    static void Build()
    {
        var parent = GameUi.Root;
        if (!parent) return;
        // its own canvas, sorted just under the game's HUD canvas (the game's hover boxes are over that)
        var holder = GameUi.Rect("EG_CompanionBannerCanvas", parent.transform);
        GameUi.Stretch(holder);
        canvas = holder.gameObject.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        var hud = GameObject.Find("CanvasOverlay");
        var hudCanvas = hud ? hud.GetComponent<Canvas>() : null;
        canvas.sortingOrder = hudCanvas ? hudCanvas.sortingOrder - 1 : 0;
        holder.gameObject.AddComponent<GraphicRaycaster>();

        root = GameUi.Rect("EG_CompanionBanner", holder);
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
        root.pivot = new Vector2(0.5f, 0f);
        root.sizeDelta = new Vector2(W, H);
        GameUi.Img(root.gameObject, "UI_Hud_CommonElements_10", new Color(0.13f, 0.11f, 0.1f, 0.96f), 1.5f);

        // the portrait: dark teal behind it, a teal outline
        var box = GameUi.Rect("Portrait", root);
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
        box.pivot = new Vector2(0f, 0.5f);
        box.sizeDelta = new Vector2(Pic, Pic);
        box.anchoredPosition = new Vector2(10f, 0f);
        var back = box.gameObject.AddComponent<Image>();
        back.color = DarkTeal;
        back.raycastTarget = false;
        var outline = box.gameObject.AddComponent<Outline>();
        outline.effectColor = Teal;
        outline.effectDistance = new Vector2(2f, 2f);
        var pic = GameUi.Rect("Picture", box);
        GameUi.Stretch(pic, 2, 2, 2, 2);
        portrait = pic.gameObject.AddComponent<RawImage>();
        portrait.raycastTarget = false;

        float left = 10f + Pic + 10f;
        nameText = Text("Name", 22, GameUi.Text, left, -8f, 26f);
        // health, and Kymia under it
        Bar("Health", left, -38f, 10f, out healthFill);
        kymiaRow = Bar("Kymia", left, -51f, 6f, out kymiaFill);
        kymiaFill.GetComponent<Image>().color = new Color(0.35f, 0.6f, 1f);
        lineText = Text("Line", 15, GameUi.Dim, left, -58f, 18f);
        root.gameObject.SetActive(false);
    }

    // ---- its powers ----

    static void BuildPowers()
    {
        if (!root) return;
        powersRow = GameUi.Rect("Powers", root);
        powersRow.anchorMin = powersRow.anchorMax = new Vector2(1f, 0.5f);
        powersRow.pivot = new Vector2(0f, 0.5f);
        powersRow.sizeDelta = new Vector2(4f * Slot + 3f * Space, Slot);
        powersRow.anchoredPosition = new Vector2(Gap, 0f);
        var me = LocalPlayer.Get();
        var template = me && me.SkillHandler ? me.SkillHandler.mainSlots?.FirstOrDefault(s => s) : null;
        for (int i = 0; i < powers.Length; i++) powers[i] = Box(i, template);
        NetLog.Info($"Companion card: four power slots ({(template ? "copies of your skill bar's" : "plain boxes: no skill bar found")})", toast: false);
    }

    static PowerBox Box(int i, UISlot template)
    {
        var b = new PowerBox();
        if (template)
        {
            var go = Object.Instantiate(template.gameObject, powersRow, false);
            go.name = "Power" + (i + 1);
            var us = go.GetComponent<UISlot>();
            b.Icon = us ? us.iconHolder : null;
            b.Timer = us ? us.timer : null;
            if (us)
            {
                foreach (var g in new[] { us.lockIcon, us.highlightElement, us.badge }) if (g) g.SetActive(false);
                if (us.iconOutlineHolder) us.iconOutlineHolder.gameObject.SetActive(false);
                if (us.amountIcon) us.amountIcon.gameObject.SetActive(false);
            }
            // just its looks: the game's slot code, animators and key numbers off
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(a);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true).ToList())
                if (mb && !(mb is Graphic || mb is Mask || mb is RectMask2D || mb is LayoutGroup || mb is LayoutElement || mb is ContentSizeFitter || mb is Shadow))
                    Object.DestroyImmediate(mb);
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                t.gameObject.SetActive(false);
                var holder = t.transform.parent;   // (the key's little box)
                if (holder && holder != go.transform && (!b.Icon || holder != b.Icon.transform) && holder.GetComponent<Image>() && holder.childCount <= 2)
                    holder.gameObject.SetActive(false);
            }
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            b.Root = (RectTransform)go.transform;
            float h = b.Root.rect.height > 1f ? b.Root.rect.height : Slot;
            b.Root.localScale = Vector3.one * (Slot / h);
            HideKey(b.Root, b.Icon, b.Timer, h, i == 0);
        }
        else
        {
            b.Root = GameUi.Rect("Power" + (i + 1), powersRow);
            b.Root.sizeDelta = new Vector2(Slot, Slot);
            GameUi.Img(b.Root.gameObject, "UI_Hud_CommonElements_10", new Color(0.13f, 0.11f, 0.1f, 0.96f), 1.5f).raycastTarget = false;
            var ic = GameUi.Rect("Icon", b.Root);
            GameUi.Stretch(ic, 6, 6, 6, 6);
            b.Icon = ic.gameObject.AddComponent<Image>();
            b.Icon.raycastTarget = false;
        }
        b.Root.anchorMin = b.Root.anchorMax = new Vector2(0f, 0.5f);
        b.Root.pivot = new Vector2(0f, 0.5f);
        b.Root.anchoredPosition = new Vector2(i * (Slot + Space), 0f);
        // our own shade (its cooldown, from the bottom, when the slot has no timer of its own; else a flash when used)
        // and the seconds left
        var over = b.Icon ? b.Icon.transform : b.Root;
        var shade = GameUi.Rect("Shade", over);
        shade.anchorMin = Vector2.zero; shade.anchorMax = Vector2.one; shade.offsetMin = shade.offsetMax = Vector2.zero;
        b.Shade = shade.gameObject.AddComponent<Image>();
        b.Shade.raycastTarget = false;
        b.Secs = GameUi.Label(over, "", 20, Color.white, TextAlignmentOptions.Center);
        GameUi.Stretch(b.Secs.rectTransform);
        b.Secs.raycastTarget = false;
        b.Secs.enableWordWrapping = false;
        if (b.Icon) { b.Glow = b.Icon.gameObject.AddComponent<Outline>(); b.Glow.effectColor = Teal; b.Glow.effectDistance = new Vector2(3f, 3f); b.Glow.enabled = false; }
        // clickable: its own see-through hit box over the whole slot
        var hit = GameUi.Rect("Hit", b.Root);
        GameUi.Stretch(hit);
        var hitImage = hit.gameObject.AddComponent<Image>();
        hitImage.color = new Color(0f, 0f, 0f, 0f);
        hitImage.raycastTarget = true;
        int slot = i;
        hit.gameObject.AddComponent<PowerClick>().On = right => { if (right) OpenPicker(slot); else Use(slot); };
        return b;
    }

    /// <summary>The key number on your bar's slot (a picture, at its bottom edge): off on ours. Logged once, the slot's
    /// pieces, to see what's there.</summary>
    static void HideKey(RectTransform slot, Image icon, Image timer, float h, bool log)
    {
        var lines = new System.Collections.Generic.List<string>();
        var c = new Vector3[4];
        slot.GetWorldCorners(c);
        float bottom = c[0].y, height = Mathf.Max(1f, c[1].y - c[0].y);
        foreach (var g in slot.GetComponentsInChildren<Graphic>(true))
        {
            if (!g || g.transform == slot) continue;
            var r = g.rectTransform;
            r.GetWorldCorners(c);
            float gh = c[1].y - c[0].y, mid = ((c[0].y + c[1].y) / 2f - bottom) / height;
            bool mine = icon && (g == icon || g.transform.IsChildOf(icon.transform) || icon.transform.IsChildOf(g.transform)) || timer && g == timer;
            bool key = !mine && gh < height * 0.45f && mid < 0.3f;   // (small, along the bottom edge)
            if (key) g.gameObject.SetActive(false);
            if (log) lines.Add($"{g.name}{(g is Image im && im.sprite ? "[" + im.sprite.name + "]" : "")} h{gh / height:0.00} y{mid:0.00}{(key ? " (key: off)" : "")}");
        }
        if (log) NetLog.Info("Companion card: a power slot's pieces: " + string.Join(", ", lines), toast: false);
    }

    static void Use(int slot)
    {
        ClosePicker();
        string why = Integrations.CompanionUsePower(slot);
        if (!string.IsNullOrEmpty(why)) Chat.System(why);
    }

    // ---- the right-click stack: its unlocked powers above the slot, and an X ----

    static RectTransform picker;
    static readonly System.Collections.Generic.List<RectTransform> pickerCells = new();
    static int pickerFor = -1;

    static void OpenPicker(int slot)
    {
        bool same = picker && pickerFor == slot;
        ClosePicker();
        if (same || powers[slot] == null || !powers[slot].Root) return;
        var choices = Integrations.CompanionPowerChoices();
        if (choices.Count == 0) Chat.System("Your companion has no powers yet: spend its class points (Class on its inventory).");
        picker = GameUi.Rect("PowerPicker", powersRow);
        picker.anchorMin = picker.anchorMax = new Vector2(0f, 0.5f);
        picker.pivot = new Vector2(0f, 0.5f);
        picker.anchoredPosition = Vector2.zero;
        pickerFor = slot;
        int k = 0;
        foreach (var ch in choices)
        {
            var c = ch;
            Cell(++k, c.icon, c.rank.ToString(), () => { ClosePicker(); Integrations.CompanionSetPower(slot, c.id); });
        }
        Cell(++k, null, "X", () => { ClosePicker(); Integrations.CompanionSetPower(slot, null); });

        void Cell(int up, Sprite icon, string label, System.Action click)
        {
            var src = powers[slot];
            var go = Object.Instantiate(src.Root.gameObject, picker, false);
            go.name = "Choice";
            var r = (RectTransform)go.transform;
            r.anchoredPosition = new Vector2(slot * (Slot + Space), up * (Slot + Space));
            foreach (var pc in go.GetComponentsInChildren<PowerClick>(true)) pc.On = _ => click();
            foreach (var o in go.GetComponentsInChildren<Outline>(true)) o.enabled = false;
            var im = go.GetComponentsInChildren<Image>(true).FirstOrDefault(x => src.Icon && x.name == src.Icon.name);
            if (im) { im.sprite = icon; im.enabled = icon; im.color = Color.white; }
            foreach (var t in go.GetComponentsInChildren<Image>(true)) if (t.name == "Shade") t.color = new Color(0f, 0f, 0f, 0f);
            if (src.Timer) foreach (var t in go.GetComponentsInChildren<Image>(true)) if (t.name == src.Timer.name) t.gameObject.SetActive(false);
            var text = go.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(x => x.gameObject.activeSelf);
            if (text)
            {
                text.text = label;
                if (icon)   // (its rank: small, bottom right, as your bar's stack shows it)
                {
                    text.alignment = TextAlignmentOptions.BottomRight;
                    text.fontSize = 16f;
                }
                else text.fontSize = 34f;
            }
            pickerCells.Add(r);
        }
    }

    /// <summary>Each frame: a click anywhere but the stack (or the slots) closes it.</summary>
    static void PickerTick()
    {
        if (!picker) return;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || !(mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return;
        var at = mouse.position.ReadValue();
        var cam = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        foreach (var r in pickerCells) if (r && RectTransformUtility.RectangleContainsScreenPoint(r, at, cam)) return;
        foreach (var b in powers) if (b != null && b.Root && RectTransformUtility.RectangleContainsScreenPoint(b.Root, at, cam)) return;
        ClosePicker();
    }

    static void ClosePicker()
    {
        if (picker) Object.Destroy(picker.gameObject);
        picker = null;
        pickerCells.Clear();
        pickerFor = -1;
    }

    /// <summary>A power slot (or a choice above it): left or right click.</summary>
    sealed class PowerClick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public System.Action<bool> On;

        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e)
        {
            if (e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Middle) return;
            try { On?.Invoke(e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right); }
            catch (System.Exception x) { NetLog.Warn($"Companion card: {x.Message}", toast: false); }
        }
    }

    static void ShowPowers()
    {
        for (int i = 0; i < powers.Length; i++)
        {
            var b = powers[i];
            if (b == null || !b.Root) continue;
            bool has = Integrations.CompanionPower(i, out var icon, out float left, out float total, out bool running, out float castAt);
            if (b.Icon) { b.Icon.sprite = icon; b.Icon.enabled = has && icon; b.Icon.color = Color.white; b.Icon.preserveAspect = true; }
            float cd = has && left > 0f ? Mathf.Clamp01(left / total) : 0f;
            float flash = has ? 1f - Mathf.Clamp01((Time.time - castAt) / 0.5f) : 0f;   // (just used: a bright flash)
            if (b.Timer)
            {
                b.Timer.gameObject.SetActive(cd > 0f);
                b.Timer.fillAmount = cd;
            }
            var sh = b.Shade.rectTransform;
            if (!b.Timer && cd > 0f) { sh.anchorMax = new Vector2(1f, cd); b.Shade.color = new Color(0f, 0f, 0f, 0.65f); }
            else { sh.anchorMax = Vector2.one; b.Shade.color = new Color(Teal.r, Teal.g, Teal.b, 0.55f * flash); }
            b.Secs.text = cd > 0f ? (left >= 10f ? $"{left:0}" : $"{left:0.0}") : "";
            if (b.Glow) b.Glow.enabled = running;   // (running now: a teal ring)
        }
    }

    static TextMeshProUGUI Text(string name, float size, Color colour, float left, float top, float height)
    {
        var t = GameUi.Label(root, "", size, colour);
        t.name = name;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.raycastTarget = false;
        var r = t.rectTransform;
        r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f);
        r.pivot = new Vector2(0f, 1f);
        r.offsetMin = new Vector2(left, top - height);
        r.offsetMax = new Vector2(-12f, top);
        return t;
    }

    static RectTransform Bar(string name, float left, float top, float height, out RectTransform fill)
    {
        var bar = GameUi.Rect(name, root);
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0f, 1f);
        bar.offsetMin = new Vector2(left, top - height);
        bar.offsetMax = new Vector2(-14f, top);
        var back = bar.gameObject.AddComponent<Image>();
        back.color = new Color(0f, 0f, 0f, 0.7f);
        back.raycastTarget = false;
        fill = GameUi.Rect("Fill", bar);
        fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one;
        fill.offsetMin = fill.offsetMax = Vector2.zero;
        var img = fill.gameObject.AddComponent<Image>();
        img.color = new Color(0.25f, 0.85f, 0.35f);
        img.raycastTarget = false;
        return bar;
    }
}

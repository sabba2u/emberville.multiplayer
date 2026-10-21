using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleFavorites;

/// <summary>
/// The F5-F8 bar, drawn by the game's own UI: four copies of the HUD's first belt slot (UISlot: frame, icon,
/// highlight, click handling), in a row in the middle of the HUD just above the experience bar (or else above the
/// menu buttons / the belt), inside the same HUD group (so it
/// scales with the HUD and hides with it during cutscenes). Each shows its favourite's icon, highlighted while it's
/// the weapon in hand, with "F5".."F8" under it; a click wields it. Rebuilt whenever the HUD is (new scene).
/// Hovering a slot shows the game's own item tooltip (a copy of the inventory's ItemInfoWindow on the HUD): what the
/// weapon is, and how it compares with the one in hand (damage / speed / stat differences in green and red; the
/// walk key, Ctrl, toggles the side panel with the equipped weapon, as in the inventory).
/// </summary>
internal static class FavoritesBar
{
    const float Gap = 6f, Above = 14f;

    static GameObject bar;
    static readonly UISlot[] slots = new UISlot[Favorites.Slots];
    static readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[Favorites.Slots];
    static float nextLook, nextRefresh, nextSettle;
    static Vector3 baseLocal;
    static bool settles;

    /// <summary>
    /// The row of menu buttons sitting on the health bar's frame (Journal, Inventory, ...): its right edge, its top
    /// and a button's height, in world space. Small = well under a skill slot's height; on the frame = above the skills,
    /// not far above.
    /// </summary>
    static (float right, float top, float height)? MenuButtons(UIManager ui, UISlot lastSkill)
    {
        if (!lastSkill) return null;
        var canvas = ui.GetComponentInParent<Canvas>()?.rootCanvas ?? lastSkill.GetComponentInParent<Canvas>()?.rootCanvas;
        if (!canvas) return null;
        var c = new Vector3[4];
        ((RectTransform)lastSkill.transform).GetWorldCorners(c);
        float skillH = c[1].y - c[0].y, skillTop = c[1].y, right = c[2].x;
        int found = 0; float rowRight = float.MinValue, rowTop = float.MinValue, rowH = 0f;
        var hudSlots = new System.Collections.Generic.HashSet<UISlot>();
        foreach (var list in new[] { ui.skillSlots, ui.beltSlots, ui.inheritSlots }) if (list != null) foreach (var x in list) if (x) hudSlots.Add(x);
        if (ui.mainSkill) hudSlots.Add(ui.mainSkill);
        if (ui.dodgeSkill) hudSlots.Add(ui.dodgeSkill);
        // buttons, or slots that aren't the skills / belt (the Journal and Inventory buttons may be either)
        var candidates = canvas.GetComponentsInChildren<Selectable>().Select(x => (Component)x)
            .Concat(canvas.GetComponentsInChildren<UISlot>().Where(x => !hudSlots.Contains(x)).Select(x => (Component)x));
        foreach (var b in candidates)
        {
            if (!b.gameObject.activeInHierarchy || (bar && b.transform.IsChildOf(bar.transform))) continue;
            var owner = b.GetComponentInParent<UISlot>();
            if (owner && (hudSlots.Contains(owner) || owner != b && owner.transform != b.transform)) continue;   // a slot's inner button: the slot itself counts
            var r = (RectTransform)b.transform;
            r.GetWorldCorners(c);
            float h = c[1].y - c[0].y, cy = (c[0].y + c[1].y) / 2f, cx = (c[0].x + c[2].x) / 2f;
            if (h <= 0f || h > skillH * 0.75f) continue;
            if (cy < skillTop || cy > skillTop + skillH * 2.2f) continue;
            if (cx > right || cx < right - skillH * 8f) continue;
            found++;
            rowRight = Mathf.Max(rowRight, c[2].x);
            rowTop = Mathf.Max(rowTop, c[1].y);
            rowH = Mathf.Max(rowH, h);
        }
        if (found == 0) return null;
        Plugin.Log.LogInfo($"Favourites bar: above the {found} menu buttons");
        return (rowRight, rowTop, rowH);
    }
    static ItemInfoWindow tip;
    static int tipFor = -1;

    public static void Refresh() => nextRefresh = 0f;

    // ---- showing only when used ----

    const float ShowFor = 3f, FadeTime = 0.5f;
    static float shownUntil;
    static CanvasGroup fade;

    /// <summary>A favourite key was used (or set): the bar shows for a few seconds, then fades away.</summary>
    public static void Poke() => shownUntil = Time.unscaledTime + ShowFor;

    /// <summary>Fade in when poked or the mouse is on it, out after a while; clicks go through when it's gone.</summary>
    static void Fade()
    {
        if (!fade) fade = bar.GetComponent<CanvasGroup>() ?? bar.AddComponent<CanvasGroup>();
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null && bar.activeInHierarchy)
        {
            var c = new Vector3[4];
            ((RectTransform)bar.transform).GetWorldCorners(c);
            var cam = bar.GetComponentInParent<Canvas>()?.rootCanvas;
            var p = mouse.position.ReadValue();
            Vector2 lo = RectTransformUtility.WorldToScreenPoint(cam && cam.renderMode != RenderMode.ScreenSpaceOverlay ? cam.worldCamera : null, c[0]);
            Vector2 hi = RectTransformUtility.WorldToScreenPoint(cam && cam.renderMode != RenderMode.ScreenSpaceOverlay ? cam.worldCamera : null, c[2]);
            if (p.x >= lo.x && p.x <= hi.x && p.y >= lo.y && p.y <= hi.y) shownUntil = Mathf.Max(shownUntil, Time.unscaledTime + 1f);
        }
        float target = Time.unscaledTime < shownUntil ? 1f : 0f;
        fade.alpha = Mathf.MoveTowards(fade.alpha, target, Time.unscaledDeltaTime / (target > fade.alpha ? 0.15f : FadeTime));
        bool on = fade.alpha > 0.05f;
        fade.blocksRaycasts = on;
        fade.interactable = on;
        if (!on && tipFor >= 0) HideTip();
    }

    public static void Tick()
    {
        if (!bar)
        {
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            Build();
            if (!bar) return;
        }
        var player = Favorites.Player;
        bool show = Favorites.barOn.Value && player && !player.IsDead && Singleton<GameManager>.HasMain &&
                    Singleton<GameManager>.main.gameState == GameState.Playing;
        if (bar.activeSelf != show) bar.SetActive(show);
        // always shown now (small, beside the Journal / Inventory buttons); Fade() is kept for a fading bar
        if (!show) HideTip();
        if (show && settles && Time.unscaledTime >= nextSettle) { nextSettle = Time.unscaledTime + 2f; Settle(); }
        if (show && xpRect && Time.unscaledTime >= nextXpPlace) { nextXpPlace = Time.unscaledTime + 1f; PlaceOverXp(); }
        if (!show || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.5f;
        for (int i = 0; i < Favorites.Slots; i++)
        {
            var item = Favorites.Find(i);
            var icon = item?.itemObject?.data ? item.itemObject.data.itemIconUI : null;
            slots[i].SetIcon(icon);
            slots[i].SetHighlight(item != null && item.section == InventorySection.Gear);
            if (labels[i])
                labels[i].color = item != null ? (item.section == InventorySection.Gear ? new Color(1f, 0.82f, 0.3f) : Color.white)
                                               : new Color(1f, 1f, 1f, Favorites.HasKey(i) ? 0.35f : 0.6f);
        }
    }

    static void Build()
    {
        var ui = Object.FindAnyObjectByType<UIManager>();
        var template = ui ? ui.beltSlots?.FirstOrDefault(s => s) : null;
        if (!template || !(template.transform.parent is RectTransform group) || !(group.parent is RectTransform hud)) return;

        // On top of the player HUD (health bar, skills 1-4), right-aligned with its last skill slot; it hides with it.
        var panel = ui.panelPlayerHud ? ui.panelPlayerHud.transform as RectTransform : null;
        var lastSkill = ui.skillSlots?.Where(s => s).OrderBy(s => s.transform.position.x).LastOrDefault();
        var parent = panel ? panel : hud;
        bar = new GameObject("EmbervilleFavorites_Bar", typeof(RectTransform));
        var rt = (RectTransform)bar.transform;
        rt.SetParent(parent, false);
        bar.AddComponent<LayoutElement>().ignoreLayout = true;   // a layout group in the HUD mustn't move it
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(1f, 0f);   // bottom-right corner
        var size = ((RectTransform)template.transform).rect.size;
        float width = Favorites.Slots * size.x + (Favorites.Slots - 1) * Gap;
        rt.sizeDelta = new Vector2(width, size.y);
        rt.localScale = Vector3.one;

        var corners = new Vector3[4];   // 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
        Vector3 anchor = Vector3.zero;
        settles = true;
        var xp = ui.experienceHUD ? ui.experienceHUD.currentExpProgress : null;
        var row = xp ? null : MenuButtons(ui, lastSkill);
        if (xp)
        {
            xpRect = (RectTransform)xp.transform;
            barWidth = width;
            settles = false;
            PlaceOverXp(log: true);
        }
        else if (row.HasValue)
        {
            // small (the menu buttons' size), on top of the row of menu buttons (Journal, Inventory... the game adds
            // more as you unlock them, growing left), right-aligned with the last one
            var (right, top, h) = row.Value;
            float scale = h / (size.y * parent.lossyScale.y);
            rt.localScale = new Vector3(scale, scale, 1f);
            var at = parent.InverseTransformPoint(new Vector3(right, top + h * 0.2f, 0f));
            baseLocal = at;
            rt.localPosition = at;
            settles = false;
        }
        else if (lastSkill && panel)
        {
            ((RectTransform)lastSkill.transform).GetWorldCorners(corners);
            float right = corners[2].x;
            float left = right - width * parent.lossyScale.x;
            // the highest bit of HUD art across that width (the health bar's frame and its ornaments)
            float top = corners[2].y;
            float near = corners[2].y + (corners[2].y - corners[3].y) * 2.5f;   // only the HUD around the skills, not things far above
            foreach (var g in panel.GetComponentsInChildren<Graphic>())
            {
                if (!g.isActiveAndEnabled || g.transform.IsChildOf(rt) || g.color.a <= 0.01f) continue;
                g.rectTransform.GetWorldCorners(corners);
                if (corners[2].x < left || corners[0].x > right) continue;
                if (corners[1].y - corners[0].y > Screen.height * 0.5f) continue;   // backdrops
                if (corners[0].y > near) continue;
                top = Mathf.Max(top, corners[1].y);
            }
            anchor = parent.InverseTransformPoint(new Vector3(right, top, 0f));
        }
        else
        {
            group.GetWorldCorners(corners);   // no skills found: on top of the belt, as before
            anchor = parent.InverseTransformPoint(corners[2]);
        }
        if (settles)
        {
            baseLocal = new Vector3(anchor.x, anchor.y + Above, 0f);
            rt.localPosition = baseLocal;
        }
        nextSettle = 0f;

        for (int i = 0; i < Favorites.Slots; i++)
        {
            var go = Object.Instantiate(template.gameObject, rt);
            go.name = $"Favourite_F{i + 5}";
            var srt = (RectTransform)go.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0f, 1f);
            srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = new Vector2(i * (size.x + Gap), 0f);
            srt.localScale = Vector3.one;
            foreach (var le in go.GetComponents<LayoutElement>()) le.ignoreLayout = true;

            var slot = go.GetComponent<UISlot>();
            StripExtras(go, slot);
            slot.ClearAllClickCallbacks();
            int index = i;
            slot.SetLeftClickCallback(() => Favorites.Wield(index));
            slot.SetDraggable(false);
            slot.SetOnMouseEnter(() => ShowTip(index));
            slot.SetOnMouseExit(HideTip);
            slot.SetLocked(false);
            slot.SetBadge(false);
            slot.SetAmount(-1);
            slot.SetIcon(null);
            slot.SetHighlight(false);
            slots[i] = slot;
            labels[i] = MakeLabel(slot, srt, $"F{i + 5}", size);
            labels[i].transform.SetAsLastSibling();   // over the slot's art
        }
        nextRefresh = 0f;
        Plugin.Log.LogInfo("Favourite weapons bar added above the belt");
    }

    /// <summary>
    /// Clear of the HUD's other bits: from the spot above the skills, up past anything of the game's HUD the bar would
    /// cover (the "Journal" button...). The notification list and tooltips come and go, so they don't count.
    /// </summary>
    static RectTransform xpRect;
    static float barWidth, nextXpPlace;

    /// <summary>
    /// In the middle of the HUD, between the two skulls: centred on the experience bar, just above it, as wide as it at
    /// most. Again every second: the HUD slides and grows in when it appears, so one look can catch it half-way (the bar
    /// then sat off where the HUD was a moment before).
    /// </summary>
    static void PlaceOverXp(bool log = false)
    {
        if (!xpRect || !bar) return;
        var rt = (RectTransform)bar.transform;
        var parent = rt.parent;
        var corners = new Vector3[4];
        xpRect.GetWorldCorners(corners);
        float cx = (corners[0].x + corners[2].x) / 2f, top = corners[1].y, room = corners[2].x - corners[0].x;
        if (room <= 0f || parent.lossyScale.x <= 0f) return;
        float scale = Mathf.Min(1f, room * 0.95f / (barWidth * parent.lossyScale.x));
        rt.pivot = new Vector2(0.5f, 0f);
        rt.localScale = new Vector3(scale, scale, 1f);
        var at = parent.InverseTransformPoint(new Vector3(cx, top + (corners[1].y - corners[0].y) * 1.5f, 0f));
        baseLocal = at;
        rt.localPosition = at;
        if (log) Plugin.Log.LogInfo($"Favourites bar: over the experience bar (centre {cx:0}, top {top:0}, {room:0} wide; scale {scale:0.00})");
    }

    static void Settle()
    {
        var rt = (RectTransform)bar.transform;
        rt.localPosition = baseLocal;
        var canvas = rt.GetComponentInParent<Canvas>()?.rootCanvas;
        if (!canvas) return;
        var notes = Object.FindAnyObjectByType<NotificationsManager>();
        var graphics = canvas.GetComponentsInChildren<Graphic>();
        var c = new Vector3[4];
        for (int pass = 0; pass < 12; pass++)
        {
            rt.GetWorldCorners(c);
            float l = c[0].x, r = c[2].x, b = c[0].y, t = c[2].y;
            float raise = 0f;
            foreach (var g in graphics)
            {
                if (!g || !g.isActiveAndEnabled || g.color.a <= 0.01f || g.transform.IsChildOf(rt)) continue;
                if (notes && g.transform.IsChildOf(notes.transform)) continue;
                if (tip && g.transform.IsChildOf(tip.transform)) continue;
                if (g.GetComponentInParent<ItemInfoWindow>()) continue;
                g.rectTransform.GetWorldCorners(c);
                float gw = c[2].x - c[0].x, gh = c[2].y - c[0].y;
                if (gw > Screen.width * 0.6f || gh > Screen.height * 0.4f || gw <= 0f || gh <= 0f) continue;   // backdrops, empty
                if (c[2].x <= l || c[0].x >= r || c[2].y <= b || c[0].y >= t) continue;   // not overlapping
                raise = Mathf.Max(raise, c[2].y - b);
            }
            if (raise <= 0.5f) break;
            var world = rt.position;
            world.y += raise + 4f * rt.lossyScale.y;
            rt.position = world;
        }
    }

    /// <summary>The game's item tooltip for this key's weapon, beside its slot (with the compare to the one in hand).</summary>
    static void ShowTip(int index) => ShowTip(index, slots[index] ? slots[index].transform : null);

    /// <summary>The same tooltip at another spot (the weapon wheel's slot).</summary>
    internal static void ShowTip(int index, Transform at)
    {
        var item = Favorites.Find(index);
        var player = Favorites.Player;
        if (!at || item?.itemObject?.data == null || !player || !EnsureTip()) { HideTip(); return; }
        tip.transform.position = at.position;   // as the inventory places it on a slot
        tip.SetItem(item.itemObject, player, atTop: true, showWeightDiff: false);
        tip.transform.SetAsLastSibling();
        tipFor = index;
    }

    /// <summary>Our tooltip on the bar (hovering a bar slot isn't putting a weapon on a key).</summary>
    public static bool IsOurTip(ItemInfoWindow w) => tip && w == tip;

    internal static void HideTip()
    {
        if (tip && tip.gameObject.activeSelf) tip.gameObject.SetActive(false);
        tipFor = -1;
    }

    /// <summary>A copy of the inventory's tooltip window on the HUD's canvas (once per HUD).</summary>
    static bool EnsureTip()
    {
        if (tip) return true;
        ItemInfoWindow source = null;
        foreach (var inv in Resources.FindObjectsOfTypeAll<InventoryUI>())
            if (inv && inv.gameObject.scene.IsValid() && inv.panelInfo) { source = inv.panelInfo; break; }
        var canvas = bar ? bar.GetComponentInParent<Canvas>()?.rootCanvas : null;
        if (!source || !canvas) return false;
        bool was = source.gameObject.activeSelf;
        source.gameObject.SetActive(false);   // the copy starts switched off (its Awake finds our canvas when shown)
        try { tip = Object.Instantiate(source.gameObject, canvas.transform).GetComponent<ItemInfoWindow>(); }
        finally { source.gameObject.SetActive(was); }
        tip.name = "EmbervilleFavorites_Tooltip";
        tip.autoFindCanvasRect = true;
        tip.canvasRect = (RectTransform)canvas.transform;
        foreach (var g in tip.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;   // never in the way of the mouse
        return true;
    }

    /// <summary>
    /// The belt slot's own extras: its key hint (Q / E / R / T, as text or a key picture) and any game scripts besides
    /// the slot itself (cooldown and binding watchers belong to the real belt slot).
    /// </summary>
    internal static void StripExtras(GameObject go, UISlot slot)
    {
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!mb || mb == slot || mb.GetType().Assembly != typeof(UISlot).Assembly) continue;
            // the key picture (an input icon that follows the binding): its art would stay behind without the script
            if (mb.GetType().Name.Contains("InputIcon") && mb.gameObject != go) mb.gameObject.SetActive(false);
            Object.Destroy(mb);
        }
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t == go.transform) continue;
            string n = t.name.ToLowerInvariant();
            if (n.Contains("key") || n.Contains("input") || n.Contains("bind") || n.Contains("prompt") || n.Contains("hint") || n.Contains("shortcut"))
                t.gameObject.SetActive(false);
        }
        foreach (var text in go.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (text != slot.amountDisplay && text.text.Trim().Length <= 2) text.gameObject.SetActive(false);   // "Q", "1"...

        // Whatever art is left that isn't the slot's own (frame, icon, outline, cooldown, lock, highlight, badge,
        // amount, button): the key picture under the belt slot ("Q"). Switched off, not removed.
        // exactly these pieces of the slot (the key cap can sit inside the slot's button, so their children don't count)...
        var exact = new System.Collections.Generic.HashSet<Transform>();
        foreach (Component c in new Component[] { slot.frameImage, slot.iconHolder, slot.iconOutlineHolder, slot.timer, slot.amountDisplay, slot.button, slot.button ? slot.button.targetGraphic : null })
            if (c) exact.Add(c.transform);
        // ...and everything under these
        var trees = new System.Collections.Generic.List<Transform>();
        foreach (var g in new[] { slot.lockIcon, slot.highlightElement, slot.badge }) if (g) trees.Add(g.transform);
        if (slot.amountIcon) slot.amountIcon.enabled = false;   // the little key cap / amount badge: our slots show neither
        foreach (var g in go.GetComponentsInChildren<Graphic>(true))
        {
            if (g.transform == go.transform) continue;   // the slot's own background
            bool mine = exact.Contains(g.transform);
            foreach (var t in trees) if (g.transform == t || g.transform.IsChildOf(t)) { mine = true; break; }
            if (mine) continue;
            // only small things on the slot's lower edge (the key cap sits there), never a frame or backdrop
            var slotRect = ((RectTransform)go.transform).rect;
            var r = g.rectTransform;
            Vector3 local = go.transform.InverseTransformPoint(r.TransformPoint(r.rect.center));
            if (r.rect.width < slotRect.width * 0.6f && local.y < slotRect.center.y) g.enabled = false;
        }
    }

    /// <summary>"F5" in the slot's bottom-right corner (like the inventory's), in the HUD's own font (the slot's amount text).</summary>
    internal static TextMeshProUGUI MakeLabel(UISlot slot, RectTransform parent, string text, Vector2 size)
    {
        var go = new GameObject("FavouriteKey", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-4f, 3f);
        rt.sizeDelta = new Vector2(size.x * 0.8f, size.y * 0.5f);
        var label = go.AddComponent<TextMeshProUGUI>();
        var like = slot.amountDisplay;
        if (like) { label.font = like.font; label.fontSharedMaterial = like.fontSharedMaterial; }
        label.fontSize = like ? Mathf.Max(12f, like.fontSize) : 14f;
        label.alignment = TextAlignmentOptions.BottomRight;
        label.enableWordWrapping = false;
        label.text = text;
        label.raycastTarget = false;
        return label;
    }
}

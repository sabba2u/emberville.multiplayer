using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace EmbervilleFavorites;

/// <summary>
/// The weapon wheel: hold Left Alt (or the gamepad's left trigger, which normal play doesn't use) and the four
/// favourites come up in a ring around our character, F5 at the top, F6 right, F7 bottom, F8 left. Point the mouse (or
/// the right stick) toward one: it lights up and grows, its name shows in the middle. Let go: we wield it (letting go
/// near the middle picks nothing). Attacks are off while it's open, so picking doesn't swing.
/// Drawn by the game's own UI like the bar: copies of the HUD's belt slot on the HUD's canvas, plus a ring we draw.
/// </summary>
internal static class WeaponWheel
{
    static ConfigEntry<Key> key;
    const float Radius = 92f, DeadZonePixels = 28f, StickDeadZone = 0.45f, GrowBy = 1.18f;

    static GameObject wheel;
    static readonly UISlot[] slots = new UISlot[Favorites.Slots];
    static TextMeshProUGUI title;
    static bool open, viaPad, attacksOff;
    static int picked = -1;
    static Vector2 openedAt;   // screen pixels: the middle of the wheel

    public static void Bind(ConfigFile config) =>
        key = config.Bind("Wheel", "Key", Key.LeftAlt, "Hold to bring up the weapon wheel (gamepad: hold the left trigger)");

    public static void Tick()
    {
        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool keyHeld = kb != null && kb[key.Value] is KeyControl k && k.isPressed;
        bool padHeld = pad != null && pad.leftTrigger.ReadValue() > 0.5f;

        if (!open)
        {
            if ((keyHeld || padHeld) && Application.isFocused && FreeToPlay() && Build()) Open(padHeld && !keyHeld);
            return;
        }
        if (!(keyHeld || padHeld) || !FreeToPlay())
        {
            int choice = picked;
            Close();
            if (choice >= 0 && FreeToPlay()) Favorites.Wield(choice);
            return;
        }
        Point(pad);
    }

    static bool FreeToPlay() =>
        Favorites.Player && !Favorites.Player.IsDead && Singleton<GameManager>.HasMain &&
        Singleton<GameManager>.main.gameState == GameState.Playing && !UIPanels.HasOpenedPanel &&
        (open || MainInput.Actions.Player.enabled);

    static void Open(bool pad)
    {
        open = true; viaPad = pad; picked = -1;
        var cam = Camera.main;
        openedAt = cam ? (Vector2)cam.WorldToScreenPoint(Favorites.Player.transform.position + Vector3.up * 0.6f) : new Vector2(Screen.width / 2f, Screen.height / 2f);
        var canvas = wheel.GetComponentInParent<Canvas>().rootCanvas;
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform, openedAt,
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var local);
        ((RectTransform)wheel.transform).localPosition = local;
        for (int i = 0; i < Favorites.Slots; i++)
        {
            var item = Favorites.Find(i);
            slots[i].SetIcon(item?.itemObject?.data ? item.itemObject.data.itemIconUI : null);
            slots[i].SetHighlight(false);
            slots[i].transform.localScale = Vector3.one;
        }
        title.text = "";
        wheel.SetActive(true);
        wheel.transform.SetAsLastSibling();
        // No swinging while picking (our own player's attack actions only; they come back on close).
        var p = MainInput.Actions.Player;
        attacksOff = p.PrimaryAttack.enabled || p.SecondaryAttack.enabled;
        p.PrimaryAttack.Disable(); p.SecondaryAttack.Disable();
    }

    static void Close()
    {
        open = false; picked = -1;
        FavoritesBar.HideTip();
        if (wheel) wheel.SetActive(false);
        if (attacksOff) { MainInput.Actions.Player.PrimaryAttack.Enable(); MainInput.Actions.Player.SecondaryAttack.Enable(); }
        attacksOff = false;
    }

    /// <summary>Which slice the mouse / right stick points at: up = F5, right = F6, down = F7, left = F8.</summary>
    static void Point(Gamepad pad)
    {
        Vector2 dir = Vector2.zero;
        Vector2 stick = pad != null ? pad.rightStick.ReadValue() : Vector2.zero;
        if (viaPad || stick.magnitude > StickDeadZone) { if (stick.magnitude > StickDeadZone) dir = stick; }
        else if (Mouse.current != null)
        {
            Vector2 d = Mouse.current.position.ReadValue() - openedAt;
            if (d.magnitude > DeadZonePixels) dir = d;
        }
        int now = -1;
        if (dir != Vector2.zero)
        {
            float angle = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;   // 0 = up, 90 = right
            if (angle < 0) angle += 360f;
            now = Mathf.RoundToInt(angle / 90f) % 4;   // up, right, down, left
        }
        if (now == picked) return;
        picked = now;
        for (int i = 0; i < Favorites.Slots; i++)
        {
            slots[i].SetHighlight(i == picked);
            slots[i].transform.localScale = Vector3.one * (i == picked ? GrowBy : 1f);
        }
        var item = picked >= 0 ? Favorites.Find(picked) : null;
        title.text = picked < 0 ? "" : item?.itemObject?.data ? $"F{picked + 5}  {Favorites.NameOf(item.itemObject.data)}" : $"F{picked + 5}  (empty)";
        // the game's tooltip for it (stats, and the compare with the one in hand), by its slot
        if (picked >= 0 && item?.itemObject?.data != null) FavoritesBar.ShowTip(picked, slots[picked].transform);
        else FavoritesBar.HideTip();
    }

    /// <summary>Make the wheel once per HUD (on the HUD's canvas), from copies of its belt slot.</summary>
    static bool Build()
    {
        if (wheel) return true;
        var ui = Object.FindAnyObjectByType<UIManager>();
        var template = ui ? ui.beltSlots?.FirstOrDefault(s => s) : null;
        var canvas = template ? template.GetComponentInParent<Canvas>()?.rootCanvas : null;
        if (!template || !canvas) return false;

        wheel = new GameObject("EmbervilleFavorites_Wheel", typeof(RectTransform));
        var rt = (RectTransform)wheel.transform;
        rt.SetParent(canvas.transform, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(Radius * 2.8f, Radius * 2.8f);
        rt.localScale = Vector3.one;

        var ring = new GameObject("Ring", typeof(RectTransform)).AddComponent<Image>();
        ring.transform.SetParent(rt, false);
        ((RectTransform)ring.transform).sizeDelta = new Vector2(Radius * 2.6f, Radius * 2.6f);
        ring.sprite = RingSprite();
        ring.color = new Color(0.05f, 0.05f, 0.07f, 0.72f);
        ring.raycastTarget = false;

        var size = ((RectTransform)template.transform).rect.size;
        Vector2[] spots = { new Vector2(0, Radius), new Vector2(Radius, 0), new Vector2(0, -Radius), new Vector2(-Radius, 0) };
        for (int i = 0; i < Favorites.Slots; i++)
        {
            var go = Object.Instantiate(template.gameObject, rt);
            go.name = $"Wheel_F{i + 5}";
            var srt = (RectTransform)go.transform;
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = spots[i];
            srt.localScale = Vector3.one;
            foreach (var le in go.GetComponents<LayoutElement>()) le.ignoreLayout = true;
            var slot = go.GetComponent<UISlot>();
            FavoritesBar.StripExtras(go, slot);
            slot.ClearAllClickCallbacks();
            slot.SetDraggable(false); slot.SetLocked(false); slot.SetBadge(false); slot.SetAmount(-1);
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;   // pointed at, not clicked
            slots[i] = slot;
            FavoritesBar.MakeLabel(slot, srt, $"F{i + 5}", size).transform.SetAsLastSibling();   // same key-cap clean-up and label as the bar
        }

        var t = new GameObject("Name", typeof(RectTransform));
        t.transform.SetParent(rt, false);
        ((RectTransform)t.transform).sizeDelta = new Vector2(Radius * 1.6f, 40f);
        title = t.AddComponent<TextMeshProUGUI>();
        var like = slots[0].amountDisplay;
        if (like) { title.font = like.font; title.fontSharedMaterial = like.fontSharedMaterial; }
        title.fontSize = 16f;
        title.alignment = TextAlignmentOptions.Center;
        title.enableWordWrapping = true;
        title.raycastTarget = false;

        wheel.SetActive(false);
        // (the belt slots it's copied from sort on canvases of their own: here they sort as the wheel, under the tooltip)
        foreach (var c in wheel.GetComponentsInChildren<Canvas>(true)) c.overrideSorting = false;
        return true;
    }

    static Sprite ring;

    /// <summary>A soft filled circle with four faint dividers (the slices), made once.</summary>
    static Sprite RingSprite()
    {
        if (ring) return ring;
        const int n = 256;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        float c = (n - 1) / 2f, outer = n / 2f - 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(outer - r);   // anti-aliased edge
                // Dividers on the diagonals (between up / right / down / left).
                float diag = Mathf.Min(Mathf.Abs(Mathf.Abs(dx) - Mathf.Abs(dy)), 99f);
                bool divider = r > n * 0.16f && diag < 1.5f;
                byte v = divider ? (byte)180 : (byte)255;
                byte alpha = (byte)(a * (divider ? 255 : r < n * 0.16f ? 140 : 255));
                px[y * n + x] = new Color32(v, v, v, alpha);
            }
        tex.SetPixels32(px);
        tex.Apply();
        ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        return ring;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// Better Chests' windows in the game's own look: an overlay canvas scaled like the game's (CanvasOverlay), its framed
/// window (UI_Hud_CommonElements_10) and title ribbon (_51), copper pill buttons (UI_Buttons_0, hover _2, pressed _5,
/// with the game's click sounds: ButtonSounds), its checkbox (UI_Buttons_58 + tick _63), Emberfont text, and a
/// tooltip panel. Sprites are found by name among what's loaded; a missing one becomes a plain tinted box.
/// </summary>
internal static class ChestUi
{
    static Canvas root;
    static TMP_FontAsset font;
    static Material fontMat;
    static ButtonSoundsSO sounds;
    static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

    public static readonly Color Text = new Color(1f, 0.93f, 0.82f);
    public static readonly Color Dim = new Color(0.75f, 0.67f, 0.56f);
    public static readonly Color Gold = new Color(1f, 0.8f, 0.4f);

    public static Canvas Root
    {
        get
        {
            if (root) return root;
            var go = new GameObject("BC_Canvas");
            UnityEngine.Object.DontDestroyOnLoad(go);
            root = go.AddComponent<Canvas>();
            root.renderMode = RenderMode.ScreenSpaceOverlay;
            root.sortingOrder = 500;   // over the game's screens (the inventory, for the tooltip)
            var scaler = go.AddComponent<CanvasScaler>();
            var game = UnityEngine.Object.FindObjectsByType<CanvasScaler>(FindObjectsSortMode.None).FirstOrDefault(s => s && s.gameObject.name == "CanvasOverlay");
            if (game)
            {
                scaler.uiScaleMode = game.uiScaleMode; scaler.referenceResolution = game.referenceResolution;
                scaler.screenMatchMode = game.screenMatchMode; scaler.matchWidthOrHeight = game.matchWidthOrHeight;
                scaler.scaleFactor = game.scaleFactor; scaler.referencePixelsPerUnit = game.referencePixelsPerUnit;
            }
            else { scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f; }
            go.AddComponent<GraphicRaycaster>();
            return root;
        }
    }

    public static Sprite Sprite(string name)
    {
        if (sprites.TryGetValue(name, out var s) && s) return s;
        s = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(x => x && x.name == name);
        sprites[name] = s;
        return s;
    }

    static void Fonts()
    {
        if (font) return;
        var live = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>().FirstOrDefault(t => t && t.font && t.font.name == "Emberfont" && t.gameObject.scene.IsValid());
        font = live ? live.font : Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "Emberfont") ?? TMP_Settings.defaultFontAsset;
        fontMat = live ? live.fontSharedMaterial : font ? font.material : null;
    }

    static ButtonSoundsSO Sounds()
    {
        if (sounds) return sounds;
        foreach (var b in Resources.FindObjectsOfTypeAll<ButtonSounds>()) if (b && b.buttonSounds) { sounds = b.buttonSounds; break; }
        return sounds;
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    public static Image Img(GameObject go, string sprite, Color fallback, float ppuMul = 0.5f)
    {
        var img = go.AddComponent<Image>();
        var s = Sprite(sprite);
        if (s) { img.sprite = s; img.type = s.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple; img.pixelsPerUnitMultiplier = ppuMul; }
        else img.color = fallback;
        return img;
    }

    public static void Place(RectTransform r, float x, float y, float w, float h)
    {
        r.anchorMin = r.anchorMax = new Vector2(0f, 1f);   // from the top-left of the parent
        r.pivot = new Vector2(0f, 1f);
        r.anchoredPosition = new Vector2(x, -y);
        r.sizeDelta = new Vector2(w, h);
    }

    public static void Stretch(RectTransform r, float l = 0, float b = 0, float rt = 0, float t = 0)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rt, -t);
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 20, Color? colour = null, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        Fonts();
        var r = Rect("Label", parent);
        var t = r.gameObject.AddComponent<TextMeshProUGUI>();
        if (font) t.font = font;
        if (fontMat) t.fontSharedMaterial = fontMat;
        t.fontSize = size;
        t.color = colour ?? Text;
        t.alignment = align;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        t.text = text;
        return t;
    }

    /// <param name="square">Square corners instead: a flat copper block with a thin lighter edge (as the Online Players screen's).</param>
    public static Button Button(Transform parent, string text, Action onClick, bool square = false)
    {
        var r = Rect("BC_Button_" + text, parent);
        Button b;
        if (square)
        {
            var edge = r.gameObject.AddComponent<Image>();
            edge.color = new Color(0.78f, 0.55f, 0.33f);
            var fillRect = Rect("Fill", r);
            Stretch(fillRect, 2, 2, 2, 2);
            var fill = fillRect.gameObject.AddComponent<Image>();
            fill.color = Color.white;   // (the button's colours below tint it)
            b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = fill;
            b.transition = Selectable.Transition.ColorTint;
            var c = b.colors;
            c.normalColor = new Color(0.36f, 0.22f, 0.13f);
            c.highlightedColor = new Color(0.52f, 0.33f, 0.19f);
            c.selectedColor = c.normalColor;
            c.pressedColor = new Color(0.25f, 0.15f, 0.09f);
            c.disabledColor = new Color(0.25f, 0.22f, 0.2f, 0.7f);
            c.colorMultiplier = 1f;
            c.fadeDuration = 0.05f;
            b.colors = c;
        }
        else
        {
            var img = Img(r.gameObject, "UI_Buttons_0", new Color(0.45f, 0.3f, 0.18f));
            b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var hover = Sprite("UI_Buttons_2"); var press = Sprite("UI_Buttons_5");
            if (img.sprite && hover && press)
            {
                b.transition = Selectable.Transition.SpriteSwap;
                b.spriteState = new SpriteState { highlightedSprite = hover, selectedSprite = img.sprite, pressedSprite = press, disabledSprite = img.sprite };
            }
        }
        b.onClick.AddListener(new UnityAction(() => { try { onClick?.Invoke(); } catch (Exception e) { Plugin.Log.LogWarning(e); } }));
        AddSounds(r.gameObject);
        if (text.Length > 0)
        {
            var label = Label(r, text, 19, Text, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 6, 2, 6, 2);
        }
        return b;
    }

    public static void AddSounds(GameObject go)
    {
        var snd = Sounds();
        if (snd) { var bs = go.AddComponent<ButtonSounds>(); bs.buttonSounds = snd; }
    }

    /// <summary>The game's checkbox with a label; returns a setter for its tick.</summary>
    public static Action<bool> Checkbox(Transform parent, string text, bool on, Action<bool> changed, float x, float y, float w)
    {
        var r = Rect("BC_Check_" + text, parent);
        Place(r, x, y, w, 28);
        var box = Rect("Box", r);
        Place(box, 0, 3, 22, 22);
        var boxImg = Img(box.gameObject, "UI_Buttons_58", new Color(0.2f, 0.17f, 0.14f), 1f);
        var tick = Rect("Tick", box);
        Stretch(tick, 4, 4, 4, 4);
        var tickImg = Img(tick.gameObject, "UI_Buttons_63", Gold, 1f);
        tickImg.raycastTarget = false;
        var label = Label(r, text, 19);
        Place(label.rectTransform, 30, 0, w - 30, 28);
        var b = r.gameObject.AddComponent<Button>();
        b.targetGraphic = boxImg;
        AddSounds(r.gameObject);
        bool state = on;
        void Set(bool v) { state = v; tick.gameObject.SetActive(v); }
        Set(on);
        b.onClick.AddListener(new UnityAction(() => { Set(!state); changed?.Invoke(state); }));
        // the whole row clicks (an invisible graphic over the label)
        var hit = r.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0.001f);
        return Set;
    }

    /// <summary>The game's framed window, title ribbon on top. Children are placed from its top-left.</summary>
    public static RectTransform Window(string title, Vector2 size, out TextMeshProUGUI titleLabel)
    {
        var w = Rect("BC_Window_" + title, Root.transform);
        w.anchorMin = w.anchorMax = new Vector2(0.5f, 0.5f);
        w.pivot = new Vector2(0.5f, 0.5f);
        w.sizeDelta = size;
        Img(w.gameObject, "UI_Hud_CommonElements_10", new Color(0.13f, 0.11f, 0.1f, 0.96f));
        var ribbon = Rect("Title", w);
        ribbon.anchorMin = ribbon.anchorMax = new Vector2(0.5f, 1f);
        ribbon.pivot = new Vector2(0.5f, 0.5f);
        ribbon.sizeDelta = new Vector2(Mathf.Min(size.x - 60, 420), 52);
        ribbon.anchoredPosition = new Vector2(0, -6);
        Img(ribbon.gameObject, "UI_Hud_CommonElements_51", new Color(0.4f, 0.27f, 0.15f));
        titleLabel = Label(ribbon, title, 26, Gold, TextAlignmentOptions.Center);
        Stretch(titleLabel.rectTransform, 36, 4, 36, 4);
        return w;
    }

    // ---- a tooltip by the mouse ----

    static RectTransform tip;
    static TextMeshProUGUI tipText;

    public static void Tooltip(string text)
    {
        if (string.IsNullOrEmpty(text)) { if (tip) tip.gameObject.SetActive(false); return; }
        if (!tip)
        {
            tip = Rect("BC_Tooltip", Root.transform);
            tip.anchorMin = tip.anchorMax = Vector2.zero;
            tip.pivot = new Vector2(0f, 1f);
            var bg = Img(tip.gameObject, "UI_Hud_CommonElements_18", new Color(0.1f, 0.08f, 0.07f, 0.95f));
            bg.raycastTarget = false;
            tipText = Label(tip, "", 18);
            Stretch(tipText.rectTransform, 12, 6, 12, 6);
        }
        tip.gameObject.SetActive(true);
        tip.SetAsLastSibling();
        if (tipText.text != text) tipText.text = text;
        var size = tipText.GetPreferredValues(text);
        tip.sizeDelta = new Vector2(size.x + 26, size.y + 14);
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return;
        float k = Root.scaleFactor > 0 ? Root.scaleFactor : 1f;
        var p = mouse.position.ReadValue() / k;   // screen pixels -> canvas units
        float maxX = Screen.width / k - tip.sizeDelta.x - 4;
        tip.anchoredPosition = new Vector2(Mathf.Min(p.x + 18, maxX), p.y - 18);
    }
}

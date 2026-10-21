using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// A small kit for windows in the game's own look (notes/emberville-ui-kit.md): our overlay canvas (scaled like the
/// game's CanvasOverlay), the game's window frame and title ribbon, its copper pill buttons (with its click sounds:
/// ButtonSounds), its row panels, the Emberfont text, and a scrolling list. Sprites are found by name among what the
/// scene has loaded; anything missing falls back to a plain tinted box, so a window always comes up.
/// The game's EventSystem (in every gameplay scene) drives the clicks.
/// </summary>
internal static partial class GameUi
{
    static Canvas root;
    static TMP_FontAsset font;
    static Material fontMat;
    static ButtonSoundsSO sounds;
    static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

    public static readonly Color Text = new Color(1f, 0.93f, 0.82f);
    public static readonly Color Dim = new Color(0.72f, 0.65f, 0.55f);
    public static readonly Color Gold = new Color(1f, 0.8f, 0.4f);
    public static readonly Color Blue = new Color(0.55f, 0.82f, 1f);

    // ---- what we build with ----

    public static Canvas Root
    {
        get
        {
            if (root) return root;
            var go = new GameObject("EG_GameUiCanvas");
            UnityEngine.Object.DontDestroyOnLoad(go);
            root = go.AddComponent<Canvas>();
            root.renderMode = RenderMode.ScreenSpaceOverlay;
            root.sortingOrder = 400;   // over the game's HUD
            var scaler = go.AddComponent<CanvasScaler>();
            var game = UnityEngine.Object.FindObjectsByType<CanvasScaler>(FindObjectsSortMode.None).FirstOrDefault(s => s && s.gameObject.name == "CanvasOverlay");
            if (game)
            {
                scaler.uiScaleMode = game.uiScaleMode;
                scaler.referenceResolution = game.referenceResolution;
                scaler.screenMatchMode = game.screenMatchMode;
                scaler.matchWidthOrHeight = game.matchWidthOrHeight;
                scaler.scaleFactor = game.scaleFactor;
                scaler.referencePixelsPerUnit = game.referencePixelsPerUnit;
            }
            else
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
            }
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

    static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static Image Img(GameObject go, string sprite, Color fallback, float ppuMul = 1f)
    {
        var img = go.AddComponent<Image>();
        var s = Sprite(sprite);
        if (s)
        {
            img.sprite = s;
            img.type = s.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.pixelsPerUnitMultiplier = ppuMul;
        }
        else img.color = fallback;
        return img;
    }

    public static void Stretch(RectTransform r, float l = 0, float b = 0, float rt = 0, float t = 0)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rt, -t);
    }

    // ---- pieces ----

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 22, Color? colour = null, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
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
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.raycastTarget = false;
        t.text = text;
        return t;
    }

    /// <summary>A copper pill button (the game's UI_Buttons_0, with its hover/pressed looks and click sounds).</summary>
    /// <param name="square">Square corners instead: a flat copper block with a thin lighter edge (no rounded pill).</param>
    public static Button Button(Transform parent, string text, Action onClick, float width = 130, float height = 36, bool square = false)
    {
        var r = Rect("EG_Button_" + text, parent);
        r.sizeDelta = new Vector2(width, height);
        var le = r.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width; le.preferredHeight = height; le.minWidth = width;
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
            var img = Img(r.gameObject, "UI_Buttons_0", new Color(0.45f, 0.3f, 0.18f), 0.5f);
            b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var hover = Sprite("UI_Buttons_2"); var press = Sprite("UI_Buttons_5");
            if (img.sprite && hover && press)
            {
                b.transition = Selectable.Transition.SpriteSwap;
                b.spriteState = new SpriteState { highlightedSprite = hover, selectedSprite = img.sprite, pressedSprite = press, disabledSprite = img.sprite };
            }
            var cb = b.colors; cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f); b.colors = cb;
        }
        b.onClick.AddListener(new UnityAction(() => { try { onClick?.Invoke(); } catch (Exception e) { NetLog.Warn($"{text}: {e.Message}", toast: false); } }));
        var snd = Sounds();
        if (snd) { var bs = r.gameObject.AddComponent<ButtonSounds>(); bs.buttonSounds = snd; }
        var label = Label(r, text, 19, Text, TextAlignmentOptions.Center);
        Stretch(label.rectTransform, 6, 2, 6, 2);
        return b;
    }

    public static void SetLabel(Button b, string text)
    {
        var t = b ? b.GetComponentInChildren<TextMeshProUGUI>(true) : null;
        if (t) t.text = text;
    }

    /// <summary>A dark row panel laid out left to right.</summary>
    public static RectTransform Row(Transform parent, float height, float spacing = 10)
    {
        var r = Rect("EG_Row", parent);
        Img(r.gameObject, "UI_Hud_CommonElements_18", new Color(0.12f, 0.1f, 0.09f, 0.9f), 0.5f);
        var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.padding = new RectOffset(14, 14, 8, 8);
        h.spacing = spacing;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = false;
        var le = r.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height; le.preferredHeight = height;
        return r;
    }

    /// <summary>A plain vertical group (no art), for stacking inside a row.</summary>
    public static RectTransform Column(Transform parent, float width, float spacing = 2)
    {
        var r = Rect("EG_Column", parent);
        var v = r.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing;
        v.childControlWidth = true; v.childControlHeight = true;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        var le = r.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width; le.minWidth = width;
        return r;
    }

    public static LayoutElement Fixed(Component c, float width = -1, float height = -1)
    {
        var le = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
        if (width >= 0) { le.preferredWidth = width; le.minWidth = width; }
        if (height >= 0) { le.preferredHeight = height; le.minHeight = height; }
        return le;
    }

    public static void Flexible(Component c) { var le = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = 1; }

    // ---- a window ----

    public sealed class Window
    {
        public RectTransform Root, Content;
        public TextMeshProUGUI Title;
        public GameObject Go => Root.gameObject;
        public bool Shown => Root && Root.gameObject.activeSelf;
        public void Show(bool on) { if (Root) Root.gameObject.SetActive(on); }
        public void Destroy() { if (Root) UnityEngine.Object.Destroy(Root.gameObject); }
    }

    /// <summary>The game's framed window (UI_Hud_CommonElements_10), a title ribbon on top, a close button, and a
    /// scrolling content column (add rows to Content).</summary>
    public static Window MakeWindow(string title, Vector2 size, Action onClose)
    {
        var w = new Window();
        w.Root = Rect("EG_Window_" + title, Root.transform);
        w.Root.anchorMin = w.Root.anchorMax = new Vector2(0.5f, 0.5f);
        w.Root.pivot = new Vector2(0.5f, 0.5f);
        w.Root.sizeDelta = size;
        Img(w.Root.gameObject, "UI_Hud_CommonElements_10", new Color(0.13f, 0.11f, 0.1f, 0.96f), 0.5f);

        // title ribbon across the top edge
        var ribbon = Rect("Title", w.Root);
        ribbon.anchorMin = ribbon.anchorMax = new Vector2(0.5f, 1f);
        ribbon.pivot = new Vector2(0.5f, 0.5f);
        ribbon.sizeDelta = new Vector2(Mathf.Min(size.x - 80, 520), 56);
        ribbon.anchoredPosition = new Vector2(0, -6);
        Img(ribbon.gameObject, "UI_Hud_CommonElements_51", new Color(0.4f, 0.27f, 0.15f), 0.5f);
        w.Title = Label(ribbon, title, 28, Gold, TextAlignmentOptions.Center);
        Stretch(w.Title.rectTransform, 40, 4, 40, 4);

        // close (top right)
        var close = Button(w.Root, "X", onClose, 40, 36);
        var cr = (RectTransform)close.transform;
        cr.anchorMin = cr.anchorMax = new Vector2(1f, 1f);
        cr.pivot = new Vector2(1f, 1f);
        cr.anchoredPosition = new Vector2(-26, -22);
        close.GetComponent<LayoutElement>().ignoreLayout = true;

        // the scrolling column
        var view = Rect("Scroll", w.Root);
        Stretch(view, 36, 40, 36, 70);
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;
        var viewport = Rect("Viewport", view);
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        var vpImg = viewport.gameObject.AddComponent<Image>(); vpImg.color = new Color(0, 0, 0, 0.001f);   // catches the wheel
        scroll.viewport = viewport;
        w.Content = Rect("Content", viewport);
        w.Content.anchorMin = new Vector2(0, 1); w.Content.anchorMax = new Vector2(1, 1);
        w.Content.pivot = new Vector2(0.5f, 1f);
        w.Content.offsetMin = w.Content.offsetMax = Vector2.zero;
        var v = w.Content.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = 8; v.padding = new RectOffset(0, 0, 4, 4);
        v.childControlWidth = true; v.childControlHeight = true;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        w.Content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = w.Content;
        w.Root.gameObject.SetActive(false);
        return w;
    }

    public static void Clear(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
    }

    /// <summary>Is the mouse over one of our game-UI windows (so a click there isn't also an attack)?</summary>
    public static bool MouseOver()
    {
        if (!root || EventSystem.current == null) return false;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var data = new PointerEventData(EventSystem.current) { position = mouse.position.ReadValue() };
        var hits = new List<RaycastResult>();
        root.GetComponent<GraphicRaycaster>().Raycast(data, hits);
        return hits.Count > 0;
    }
}

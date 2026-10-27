using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// What the wheel and the dice are drawn with: their own canvas over everything (the game's scaling, so they're the
/// same size on any screen), white shapes made once in code and tinted (a disc, a ring, a triangle, a rounded
/// square), the game's font, and the game's own UI sounds.
/// </summary>
internal static class GambleUi
{
    static Canvas root;
    static TMP_FontAsset font;
    static readonly Dictionary<string, Sprite> shapes = new();

    public static RectTransform Root
    {
        get
        {
            if (!root)
            {
                var go = new GameObject("BS_Gambles");
                Object.DontDestroyOnLoad(go);
                root = go.AddComponent<Canvas>();
                root.renderMode = RenderMode.ScreenSpaceOverlay;
                root.sortingOrder = 900;   // (over the game's screens and our menus' canvases)
                var scaler = go.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
            }
            return (RectTransform)root.transform;
        }
    }

    public static TMP_FontAsset Font
    {
        get
        {
            if (!font) font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f && f.name == "Emberfont")
                              ?? Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f && f.name.Contains("Ember"))
                              ?? TMP_Settings.defaultFontAsset;
            return font;
        }
    }

    public static RectTransform Box(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.sizeDelta = size;
        return rt;
    }

    public static Image Shape(string name, Transform parent, string shape, Vector2 size, Color colour)
    {
        var rt = Box(name, parent, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Sprite(shape);
        img.color = colour;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>A rounded box any size (a cabinet, a board, a tile), its corners kept round.</summary>
    public static Image Panel(string name, Transform parent, Vector2 size, Color colour, float corner = 0.5f)
    {
        var img = Shape(name, parent, "rounded", size, colour);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = 1f / Mathf.Max(0.05f, corner);
        return img;
    }

    public static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Color colour, Vector2 box)
    {
        var rt = Box(name, parent, box);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (Font) t.font = Font;
        t.text = text;
        t.fontSize = size;
        t.color = colour;
        t.alignment = TextAlignmentOptions.Center;
        t.raycastTarget = false;
        t.outlineWidth = 0.2f;
        t.outlineColor = new Color32(20, 12, 8, 255);
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return t;
    }

    /// <summary>One of the game's sounds (its UI ticks and chimes), from the camera.</summary>
    public static void Sound(string evt)
    {
        var at = Camera.main ? Camera.main.gameObject : Effects.Player ? Effects.Player.gameObject : null;
        if (!at) return;
        try { AkSoundEngine.PostEvent(evt, at); } catch (System.Exception e) { Log.Warn($"Streaming: sound {evt}: {e.Message}"); }
    }

    /// <summary>A white shape: "disc", "ring", "triangle" (pointing left), "rounded".</summary>
    public static Sprite Sprite(string shape)
    {
        if (shapes.TryGetValue(shape, out var s) && s) return s;
        const int N = 256;
        var px = new Color32[N * N];
        float c = (N - 1) / 2f;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float a = shape switch
                {
                    "disc" => Edge(c - Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c))),
                    "ring" => Mathf.Min(Edge(c - Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c))), Edge(Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - (c - 18f))),
                    "triangle" => Mathf.Min(Edge(x - 2f), Mathf.Min(Edge((N - 1 - x) * 0.5f - Mathf.Abs(y - c) + 1f), 1f)),
                    _ => Rounded(x, y, N, 40f),
                };
                px[y * N + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
            }
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
        tex.SetPixels32(px);
        tex.Apply();
        // (the rounded square's corners are its border: as a panel it stretches without bending them)
        s = UnityEngine.Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                      shape == "rounded" ? new Vector4(48f, 48f, 48f, 48f) : Vector4.zero);
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        shapes[shape] = s;
        return s;
    }

    static float Edge(float inside) => Mathf.Clamp01(inside + 0.5f);   // (a pixel of soft edge)

    static float Rounded(int x, int y, int n, float r)
    {
        float dx = Mathf.Max(r - x, x - (n - 1 - r), 0f), dy = Mathf.Max(r - y, y - (n - 1 - r), 0f);
        return Edge(r - Mathf.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>A pop: 0 to 1 with a little overshoot.</summary>
    public static float Pop(float t)
    {
        t = Mathf.Clamp01(t);
        const float k = 1.70158f;
        t -= 1f;
        return t * t * ((k + 1f) * t + k) + 1f;
    }
}

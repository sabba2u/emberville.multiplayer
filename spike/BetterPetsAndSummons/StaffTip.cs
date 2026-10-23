using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterPetsAndSummons;

/// <summary>
/// The staffs' own text (what they do, their level, the chickens / risen) in a panel of its own beside the game's item
/// tooltip, in the tooltip's own frame and font, instead of inside it. On the right; on the left when there's no room.
/// </summary>
internal sealed class StaffTip : MonoBehaviour
{
    const float Width = 300f, Gap = 6f, Pad = 14f;

    RectTransform box, panel;
    TextMeshProUGUI text;

    /// <summary>From the staffs' ItemInfoWindow.SetItem postfixes: this text beside the tooltip, or none (null).</summary>
    public static void Show(ItemInfoWindow window, string body)
    {
        if (!window || !window.infoBoxRect) return;
        var tip = window.infoBoxRect.GetComponentInChildren<StaffTip>(true);
        if (string.IsNullOrEmpty(body)) { if (tip) tip.gameObject.SetActive(false); return; }
        // (a copy of the tooltip made while ours was in it, the Favourites bar's: its panel came along without its
        // links, so it's made again)
        if (tip && (!tip.text || tip.box != window.infoBoxRect)) { tip.gameObject.SetActive(false); Destroy(tip.gameObject); tip = null; }
        if (!tip) tip = Make(window);
        tip.gameObject.SetActive(true);
        tip.text.text = body;
        tip.Fit();
    }

    static StaffTip Make(ItemInfoWindow window)
    {
        var go = new GameObject("BP_StaffTip", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(window.infoBoxRect, false);
        go.AddComponent<LayoutElement>().ignoreLayout = true;   // the tooltip's layout group leaves it alone
        var tip = go.AddComponent<StaffTip>();
        tip.box = window.infoBoxRect;
        tip.panel = rt;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        // the tooltip's own frame
        var frame = go.AddComponent<Image>();
        var theirs = window.infoBoxRect.GetComponent<Image>();
        if (theirs && theirs.sprite) { frame.sprite = theirs.sprite; frame.type = theirs.type; frame.color = theirs.color; frame.pixelsPerUnitMultiplier = theirs.pixelsPerUnitMultiplier; }
        else frame.color = new Color(0.12f, 0.09f, 0.08f, 0.96f);
        frame.raycastTarget = false;
        var t = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.rectTransform.SetParent(rt, false);
        t.rectTransform.anchorMin = Vector2.zero;
        t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = new Vector2(Pad, Pad);
        t.rectTransform.offsetMax = new Vector2(-Pad, -Pad);
        var like = window.description;
        if (like) { t.font = like.font; t.fontSize = like.fontSize; t.color = like.color; t.fontSharedMaterial = like.fontSharedMaterial; }
        t.enableWordWrapping = true;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.raycastTarget = false;
        tip.text = t;
        return tip;
    }

    void Fit()
    {
        var size = text.GetPreferredValues(text.text, Width - 2 * Pad, 0f);
        panel.sizeDelta = new Vector2(Width, size.y + 2 * Pad);
    }

    /// <summary>The game places its tooltip after SetItem: keep beside it, and inside the screen.</summary>
    /// <summary>The game places its tooltip after SetItem: keep beside it, inside the screen, and off the other item cards
    /// (the game shows the hovered item and the one you hold side by side): its right, else its left, else below it.</summary>
    void LateUpdate()
    {
        if (!box) return;
        var mine = ScreenRect(box);
        var canvas = box.GetComponentInParent<Canvas>();
        float scale = canvas ? canvas.rootCanvas.scaleFactor : 1f;
        float w = Width * scale, h = panel.sizeDelta.y * scale, gap = Gap * scale;
        var others = new System.Collections.Generic.List<Rect>();
        foreach (var win in FindObjectsByType<ItemInfoWindow>(FindObjectsSortMode.None))
            if (win && win.infoBoxRect && win.infoBoxRect != box && win.infoBoxRect.gameObject.activeInHierarchy) others.Add(ScreenRect(win.infoBoxRect));
        bool Free(Rect r) => r.xMin >= 0 && r.xMax <= Screen.width && r.yMin >= 0 && r.yMax <= Screen.height && !others.Exists(o => o.Overlaps(r));
        var right = new Rect(mine.xMax + gap, mine.yMax - h, w, h);
        var left = new Rect(mine.xMin - gap - w, mine.yMax - h, w, h);
        if (Free(right)) Place(new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(Gap, 0f));
        else if (Free(left)) Place(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-Gap, 0f));
        else Place(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, -Gap));   // below it
    }

    void Place(Vector2 anchor, Vector2 pivot, Vector2 at)
    {
        panel.anchorMin = panel.anchorMax = anchor;
        panel.pivot = pivot;
        panel.anchoredPosition = at;
    }

    /// <summary>A rect's place on the screen, in pixels (an overlay canvas: its world corners are screen points).</summary>
    static Rect ScreenRect(RectTransform rt)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);   // 0 bottom-left, 2 top-right
        return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
    }
}

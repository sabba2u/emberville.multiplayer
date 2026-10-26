using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// An item's own text (the Hunter's Hook: what it does) in a panel of its own beside the game's item tooltip, in the
/// tooltip's own frame and font, instead of inside it. On the right; on the left when there's no room.
/// (The same as Better Pets and Summons' StaffTip, for the staffs.)
/// </summary>
internal sealed class SideTip : MonoBehaviour
{
    const float Width = 300f, Gap = 6f, Pad = 14f;

    RectTransform box, panel;
    TextMeshProUGUI text;

    /// <summary>From ItemInfoWindow.SetItem postfixes: this text beside the tooltip, or none (null).</summary>
    public static void Show(ItemInfoWindow window, string body)
    {
        if (!window || !window.infoBoxRect) return;
        // every one in it: a copy of the tooltip (the Favourites bar's, the weapon wheel's) can carry an old one along,
        // without its links; those go, and only one of ours stays
        SideTip tip = null;
        foreach (var t in window.infoBoxRect.GetComponentsInChildren<SideTip>(true))
        {
            if (string.IsNullOrEmpty(body)) { t.gameObject.SetActive(false); continue; }
            if (tip || !t.text || t.box != window.infoBoxRect) { t.gameObject.SetActive(false); Destroy(t.gameObject); continue; }
            tip = t;
        }
        if (string.IsNullOrEmpty(body)) return;
        if (!tip) tip = Make(window);
        tip.gameObject.SetActive(true);
        tip.text.text = body;
        tip.Fit();
    }

    static SideTip Make(ItemInfoWindow window)
    {
        var go = new GameObject("EG_SideTip", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(window.infoBoxRect, false);
        go.AddComponent<LayoutElement>().ignoreLayout = true;   // the tooltip's layout group leaves it alone
        var tip = go.AddComponent<SideTip>();
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
    void LateUpdate()
    {
        if (!box) return;
        var corners = new Vector3[4];
        box.GetWorldCorners(corners);   // 0 bottom-left, 2 top-right (screen space for an overlay canvas)
        var canvas = box.GetComponentInParent<Canvas>();
        float scale = canvas ? canvas.rootCanvas.scaleFactor : 1f;
        bool room = corners[2].x + (Width + Gap) * scale <= Screen.width;
        panel.anchorMin = panel.anchorMax = new Vector2(room ? 1f : 0f, 1f);
        panel.pivot = new Vector2(room ? 0f : 1f, 1f);
        panel.anchoredPosition = new Vector2(room ? Gap : -Gap, 0f);
    }
}

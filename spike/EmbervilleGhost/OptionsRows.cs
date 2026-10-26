using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// Options > Accessibility: the Insert menu's Font Options as the game's own rows, under its Gore row (copies of it).
/// Story Text Size  - [12 (default)] +   (a point each click)
/// Label Size       - [100% (default)] + (10% each click)
/// Font             &lt; [Emberfont (default)] &gt;
/// The same settings as Insert > Font Options (StoryText): change one, the other shows it.
/// </summary>
internal static class OptionsRows
{
    static readonly List<IDisposable> subs = new();
    static bool logged;

    [HarmonyPatch(typeof(AccessibilityPanel), nameof(AccessibilityPanel.Setup)), HarmonyPostfix]
    static void Added(AccessibilityPanel __instance)
    {
        try { Build(__instance); }
        catch (Exception e) { NetLog.Warn($"Options rows: {e.Message}", toast: false); }
    }

    static void Build(AccessibilityPanel panel)
    {
        if (!StoryText.Bound || !panel.goreUI) return;
        foreach (var s in subs) s?.Dispose();
        subs.Clear();

        int b = StoryText.BaseSize;
        int lo = Mathf.Max(6, Mathf.RoundToInt(b * 0.5f)), hi = Mathf.RoundToInt(b * 2.5f);
        var sizes = Enumerable.Range(lo, hi - lo + 1).ToList();
        Row(panel, 1, "Story Text Size", sizes.Select(v => v == b ? $"{v} (default)" : $"{v}").ToArray(),
            sizes.IndexOf(Mathf.Clamp(StoryText.StorySize, lo, hi)), i => StoryText.StorySize = sizes[i], plusMinus: true);

        var pcts = Enumerable.Range(5, 26).Select(k => k * 10).ToList();   // (50% to 300%)
        Row(panel, 2, "Label Size", pcts.Select(v => v == 100 ? "100% (default)" : $"{v}%").ToArray(),
            Mathf.Max(0, pcts.IndexOf(StoryText.LabelPct / 10 * 10)), i => StoryText.LabelPct = pcts[i], plusMinus: true);

        var fonts = new List<string> { "" };
        fonts.AddRange(StoryText.FontNames());
        Row(panel, 3, "Font", fonts.Select(f => f == "" ? "Emberfont (default)" : f).ToArray(),
            Mathf.Max(0, fonts.IndexOf(StoryText.FontName)), i => StoryText.FontName = fonts[i], plusMinus: false);
    }

    /// <summary>Row k under Gore: made once (a copy of the Gore row), set up each time the options are.</summary>
    static void Row(AccessibilityPanel panel, int k, string name, string[] options, int at, Action<int> chosen, bool plusMinus)
    {
        var gore = panel.goreUI;
        var parent = gore.transform.parent;
        string id = "EV_FontRow" + k;
        var t = parent.Find(id);
        CarouselPropertyUI row;
        if (t) row = t.GetComponent<CarouselPropertyUI>();
        else
        {
            var go = UnityEngine.Object.Instantiate(gore.gameObject, parent, false);
            go.name = id;
            go.transform.SetSiblingIndex(gore.transform.GetSiblingIndex() + k);
            // (no layout to stack it: below Gore by the rows' own spacing)
            if (!parent.GetComponent<LayoutGroup>() && go.transform is RectTransform r && gore.transform is RectTransform g)
            {
                var above = panel.tinnitusUI ? (RectTransform)panel.tinnitusUI.transform : null;
                float step = above ? g.anchoredPosition.y - above.anchoredPosition.y : -g.rect.height;
                r.anchoredPosition = g.anchoredPosition + new Vector2(0f, step * k);
            }
            row = go.GetComponent<CarouselPropertyUI>();
            if (plusMinus) PlusMinus(row);
        }
        if (!row) return;
        row.selector.loopable = !plusMinus;   // (sizes stop at their ends; fonts go round)
        var model = new MultipleSelectionProperty(name, Mathf.Clamp(at, 0, options.Length - 1), options);
        row.Setup(model);
        subs.Add(model.SelectedIndex.Skip(1).Subscribe(i => { if (i >= 0 && i < options.Length) chosen(i); }));
    }

    /// <summary>Its arrows as "-" and "+": the arrows' pictures see-through (still clickable), a sign in front.</summary>
    static void PlusMinus(CarouselPropertyUI row)
    {
        var sel = row.selector;
        var like = sel.mainLabel;
        foreach (var (arrow, sign) in new[] { (sel.arrowLeft, "-"), (sel.arrowRight, "+") })
        {
            if (!arrow) continue;
            if (!logged)
                NetLog.Info($"Options rows: arrow {arrow.name}: " + string.Join(", ", arrow.GetComponentsInChildren<Graphic>(true)
                    .Select(x => $"{x.name}{(x is Image im && im.sprite ? "[" + im.sprite.name + "]" : "")}")), toast: false);
            foreach (var img in arrow.GetComponentsInChildren<Image>(true))
            {
                var c = img.color;
                img.color = new Color(c.r, c.g, c.b, 0f);
            }
            var lr = new GameObject("Sign", typeof(RectTransform)).GetComponent<RectTransform>();
            lr.SetParent(arrow.transform, false);
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            var label = lr.gameObject.AddComponent<TextMeshProUGUI>();
            if (like) { label.font = like.font; label.fontSharedMaterial = like.fontSharedMaterial; label.color = like.color; }
            label.fontSize = like ? like.fontSize * 1.4f : 36f;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.raycastTarget = false;
            label.text = sign;
            arrow.targetGraphic = label;   // (its hover and press tint on the sign)
        }
        logged = true;
    }
}

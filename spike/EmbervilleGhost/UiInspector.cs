using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// Menu > UI Inspector: the game's UI (and ours), outlined on screen: buttons green, slots cyan, text yellow, panels
/// orange. Under the mouse: the smallest thing drawn there, outlined in pink, with its path, size, canvas and
/// components beside the mouse; what a click there would hit is listed too. Shift+U pins it to the tab (and the log),
/// since the mouse leaves it to get there. Not saved.
/// </summary>
internal static class UiInspector
{
    public static bool On, Outlines = true;
    static List<string> pinned;
    static System.DateTime pinnedWhen;

    static readonly List<(RectTransform rt, Canvas canvas)> drawn = new();
    static float nextScan;

    public static void DrawMenu()
    {
        ImGui.TextWrapped("Outlines the UI on screen and names what's under the mouse. Shift+U pins it here (and to the log).");
        ImGui.Checkbox("Inspect the UI", ref On);
        ImGui.SameLine();
        ImGui.Checkbox("Outline everything", ref Outlines);
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "buttons"); ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 0.9f, 1f, 1f), "slots"); ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), "text"); ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "panels"); ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.8f, 1f), "under the mouse");
        ImGui.Separator();
        if (pinned == null) { ImGui.TextDisabled("Nothing pinned yet: point at something and press Shift+U."); return; }
        ImGui.TextDisabled($"Pinned {pinnedWhen:HH:mm:ss}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(string.Join("\n", pinned));
        ImGui.Separator();
        foreach (var l in pinned) ImGui.TextUnformatted(l);
    }

    /// <summary>From Hud.Draw.</summary>
    public static void Draw()
    {
        if (!On) return;
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; Scan(); }
        var dl = ImGui.GetForegroundDrawList();
        var mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : UnityEngine.Vector2.zero;

        RectTransform under = null;
        float underArea = float.MaxValue;
        Canvas underCanvas = null;
        foreach (var (rt, canvas) in drawn)
        {
            if (!rt || !rt.gameObject.activeInHierarchy || !canvas) continue;
            if (!Screen(rt, canvas, out var min, out var max)) continue;
            if (Outlines) dl.AddRect(min, max, ColourOf(rt), 0f, ImDrawFlags.None, 1f);
            if (mouse.x < min.x || mouse.x > max.x || UnityEngine.Screen.height - mouse.y < min.y || UnityEngine.Screen.height - mouse.y > max.y) continue;
            float area = (max.x - min.x) * (max.y - min.y);
            if (area > 4f && area <= underArea) { underArea = area; under = rt; underCanvas = canvas; }
        }
        if (!under) return;
        Screen(under, underCanvas, out var a, out var b);
        uint pink = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.3f, 0.8f, 1f));
        dl.AddRect(a, b, pink, 0f, ImDrawFlags.None, 2.5f);

        var lines = Describe(under, underCanvas, mouse);
        var at = new Vector2(mouse.x + 18f, UnityEngine.Screen.height - mouse.y + 18f);
        float w = lines.Max(l => ImGui.CalcTextSize(l).x) + 12f, h = lines.Count * ImGui.GetTextLineHeightWithSpacing() + 8f;
        if (at.x + w > UnityEngine.Screen.width) at.x = mouse.x - w - 10f;
        if (at.y + h > UnityEngine.Screen.height) at.y = UnityEngine.Screen.height - h - 4f;
        dl.AddRectFilled(at, new Vector2(at.x + w, at.y + h), ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.04f, 0.08f, 0.9f)), 4f);
        dl.AddRect(at, new Vector2(at.x + w, at.y + h), pink, 4f);
        for (int i = 0; i < lines.Count; i++)
            dl.AddText(new Vector2(at.x + 6f, at.y + 4f + i * ImGui.GetTextLineHeightWithSpacing()), i == 0 ? pink : 0xFFEEEEEE, lines[i]);

        var kb = Keyboard.current;
        if (kb != null && kb.shiftKey.isPressed && kb.uKey.wasPressedThisFrame && !ImGuiUnity.Typing)
        {
            pinned = lines;
            pinnedWhen = System.DateTime.Now;
            NetLog.Info("UI under the mouse: " + string.Join(" | ", lines), toast: false);
            NetLog.Info($"Pinned: {under.name} ({ImGuiUnity.MenuKey.Value} > UI Inspector)", toast: true);
        }
    }

    /// <summary>Everything drawn on the screen's canvases (every half second).</summary>
    static void Scan()
    {
        drawn.Clear();
        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (!canvas || !canvas.isRootCanvas || !canvas.isActiveAndEnabled || canvas.renderMode == RenderMode.WorldSpace) continue;
            foreach (var g in canvas.GetComponentsInChildren<Graphic>(false))
                if (g && g.enabled) drawn.Add((g.rectTransform, canvas));
            foreach (var s in canvas.GetComponentsInChildren<Selectable>(false))   // (a button with no picture of its own)
                if (s && !s.GetComponent<Graphic>()) drawn.Add(((RectTransform)s.transform, canvas));
        }
    }

    static bool Screen(RectTransform rt, Canvas canvas, out Vector2 min, out Vector2 max)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var w in c)
        {
            var s = RectTransformUtility.WorldToScreenPoint(cam, w);
            x0 = Mathf.Min(x0, s.x); x1 = Mathf.Max(x1, s.x);
            y0 = Mathf.Min(y0, s.y); y1 = Mathf.Max(y1, s.y);
        }
        min = new Vector2(x0, UnityEngine.Screen.height - y1);
        max = new Vector2(x1, UnityEngine.Screen.height - y0);
        return x1 - x0 >= 1f && y1 - y0 >= 1f && x1 >= 0 && y1 >= 0 && x0 <= UnityEngine.Screen.width && y0 <= UnityEngine.Screen.height;
    }

    static uint ColourOf(RectTransform rt)
    {
        Vector4 c =
            rt.GetComponent<UISlot>() ? new Vector4(0.3f, 0.9f, 1f, 0.7f) :
            rt.GetComponent<Selectable>() ? new Vector4(0.4f, 1f, 0.4f, 0.7f) :
            rt.GetComponent<TMP_Text>() || rt.GetComponent<Text>() ? new Vector4(1f, 0.9f, 0.3f, 0.5f) :
            rt.rect.width > 150f && rt.rect.height > 150f ? new Vector4(1f, 0.6f, 0.2f, 0.6f) :
            new Vector4(0.7f, 0.7f, 0.7f, 0.25f);
        return ImGui.ColorConvertFloat4ToU32(c);
    }

    static List<string> Describe(RectTransform rt, Canvas canvas, UnityEngine.Vector2 mouse)
    {
        var lines = new List<string> { rt.name, Path(rt) };
        var r = rt.rect;
        lines.Add($"size {r.width:0} x {r.height:0}   anchored {rt.anchoredPosition.x:0}, {rt.anchoredPosition.y:0}   scale {rt.lossyScale.x:0.##}   sibling {rt.GetSiblingIndex()} of {(rt.parent ? rt.parent.childCount : 1)}");
        lines.Add($"canvas {canvas.name} ({canvas.renderMode}, order {canvas.sortingOrder})");
        lines.Add("components: " + string.Join(", ", rt.GetComponents<Component>().Where(c => c && !(c is RectTransform)).Select(c => c.GetType().Name)));
        var tmp = rt.GetComponent<TMP_Text>();
        if (tmp) lines.Add($"text \"{Short(tmp.text)}\"  ({tmp.fontSize:0}pt)");
        var img = rt.GetComponent<Image>();
        if (img) lines.Add($"image {(img.sprite ? img.sprite.name : "(none)")}  colour {ColorUtility.ToHtmlStringRGBA(img.color)}  raycast {img.raycastTarget}");
        var raw = rt.GetComponent<RawImage>();
        if (raw) lines.Add($"raw image {(raw.texture ? raw.texture.name : "(none)")}");
        var sel = rt.GetComponentInParent<Selectable>();
        if (sel) lines.Add($"in {sel.GetType().Name} \"{sel.name}\"  interactable {sel.interactable}");
        var slot = rt.GetComponentInParent<UISlot>();
        if (slot) lines.Add($"in slot \"{slot.name}\" ({Path(slot.transform)})");
        var group = rt.GetComponentInParent<CanvasGroup>();
        if (group) lines.Add($"canvas group on \"{group.name}\": alpha {group.alpha:0.##}  raycasts {group.blocksRaycasts}");

        // what a click here would hit (raycast targets only), top first
        if (EventSystem.current)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = mouse }, hits);
            if (hits.Count > 0) lines.Add("a click hits: " + string.Join("  >  ", hits.Take(4).Select(h => h.gameObject ? h.gameObject.name : "?")));
        }
        return lines;
    }

    static string Path(Transform t)
    {
        var parts = new List<string>();
        for (; t; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 40 ? s.Substring(0, 40) + "..." : s).Replace("\n", " ");
}

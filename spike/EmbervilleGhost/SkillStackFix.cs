using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Two fixes for the game's skill-bar stacks (UISlotStack: right-click a skill slot, the other skills you could put
/// there unfold above it):
///  - opening, closing and selecting call each other (select opens, deselect closes, closing deselects...); clicked
///    fast, that can go round and round until the game runs out of stack and crashes. They're not let in more than a
///    few deep.
///  - the unfolding is an animation; cut short (the bar switched off, another click) it can stay half way, the icons
///    piled on each other ([[[[] instead of [][][][]). An open stack whose animation is over is put where it should be.
/// </summary>
[HarmonyPatch]
internal static class SkillStackFix
{
    const int Deepest = 4;
    static int depth;

    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.Show)), HarmonyPrefix]
    static bool ShowIn() => Enter();
    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.Show)), HarmonyFinalizer]
    static void ShowOut() => depth--;

    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.OnSelect)), HarmonyPrefix]
    static bool SelectIn() => Enter();
    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.OnSelect)), HarmonyFinalizer]
    static void SelectOut() => depth--;

    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.OnDeselect)), HarmonyPrefix]
    static bool DeselectIn() => Enter();
    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.OnDeselect)), HarmonyFinalizer]
    static void DeselectOut() => depth--;

    /// <summary>In, unless it's already this deep (then skipped: the finalizer still counts it out).</summary>
    static bool Enter()
    {
        depth++;
        if (depth <= Deepest) return true;
        NetLog.Info("Skill bar: an open/close loop stopped", toast: false);
        return false;
    }

    // ---- half-open stacks ----

    static readonly Dictionary<UISlotStack, float> shown = new();   // (when each was last opened or closed)

    [HarmonyPatch(typeof(UISlotStack), nameof(UISlotStack.Show)), HarmonyPostfix]
    static void Shown(UISlotStack __instance) => shown[__instance] = Time.unscaledTime;

    static float nextCheck;

    /// <summary>Every frame (Plugin.Update), a few times a second: an open stack done animating, laid out in full.</summary>
    public static void Tick()
    {
        if (Time.unscaledTime < nextCheck || shown.Count == 0) return;
        nextCheck = Time.unscaledTime + 0.25f;
        foreach (var kv in new List<KeyValuePair<UISlotStack, float>>(shown))
        {
            var s = kv.Key;
            if (!s) { shown.Remove(s); continue; }
            if (!s.isOpened || !s.isActiveAndEnabled || Time.unscaledTime - kv.Value < s.moveTime * 1.5f + 0.3f) continue;
            try { LayOut(s); } catch (System.Exception e) { NetLog.Info($"Skill bar: {e.Message}", toast: false); shown.Remove(s); }
        }
    }

    /// <summary>Where the game's unfolding ends: each icon above the last, the stack's frame around them all.</summary>
    static void LayOut(UISlotStack s)
    {
        if (!s.folder || !s.myRectTransform) return;
        var at = new Vector2(0f, s.upSideDown ? 0f : s.originSize.y + s.spacing);
        foreach (RectTransform item in s.folder)
        {
            if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);
            if ((item.anchoredPosition - at).sqrMagnitude > 0.25f) item.anchoredPosition = at;
            at.y += item.sizeDelta.y + s.spacing;
        }
        at.x = s.originSize.x;
        at.y -= s.upSideDown ? 0 : s.spacing;
        at.y += s.upSideDown ? s.originSize.y : 0f;
        if ((s.myRectTransform.sizeDelta - at).sqrMagnitude > 0.25f) s.myRectTransform.sizeDelta = at;
    }
}

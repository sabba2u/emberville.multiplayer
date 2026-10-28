using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;

namespace EmbervilleGhost;

/// <summary>
/// Level buttons beside the level number on the game's own screens:
/// - Character Stats: the character level − / + / Reset, yours or your companion's (whichever it shows).
/// - The class screen: the class's level − / + / Reset (level 1, every point back) and Refund points (every point
///   back, the level kept), for whoever it shows: you, or your companion (Better Companions shows its class there).
/// The same as the Respec rows (Insert > Respec, Insert > Companions).
/// </summary>
internal static class ClassScreen
{
    static PanelsManager panels;
    static UIStatsHandler statsScreen;
    static UIClassUpgrade classScreen;
    static float lookAt;
    static Vector2 boxMin, boxMax;
    static int shownFrame = -10;

    /// <summary>The mouse is over the buttons (ImGuiUnity: let them have it; the game's screen keeps it elsewhere).</summary>
    public static bool WantsMouse
    {
        get
        {
            if (Time.frameCount - shownFrame > 2) return false;
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return false;
            var p = m.position.ReadValue();
            float x = p.x, y = Screen.height - p.y;
            return x >= boxMin.x - 4 && x <= boxMax.x + 4 && y >= boxMin.y - 4 && y <= boxMax.y + 4;
        }
    }

    public static void Draw()
    {
        if (!GameData.HasPlayerData) return;
        if (!panels || !statsScreen || !classScreen)
        {
            if (Time.unscaledTime < lookAt) return;
            lookAt = Time.unscaledTime + 2f;
            panels = Object.FindAnyObjectByType<PanelsManager>();
            statsScreen = Object.FindAnyObjectByType<UIStatsHandler>(FindObjectsInactive.Include);
            classScreen = Object.FindAnyObjectByType<UIClassUpgrade>(FindObjectsInactive.Include);
            if (!panels) return;
        }
        if (panels.panelClass && panels.panelClass.activeInHierarchy)
        {
            var t = Integrations.CompanionClassShowing() ?? Respec.You();
            if (t != null) Strip("##classlevel", Number(classScreen ? classScreen.levelDisplay : null), panels.panelClass, () => Respec.DrawClassStrip(t));
        }
        else if (panels.panelStats && panels.panelStats.activeInHierarchy)
        {
            var t = Integrations.CompanionStatsShowing() ?? Respec.You();
            if (t != null) Strip("##charlevel", Number(statsScreen ? statsScreen.levelDisplay : null), panels.panelStats, () => Respec.DrawCharacterStrip(t));
        }
    }

    /// <summary>The screen's big level number (null: not found).</summary>
    static RectTransform Number(UIStatsLevel level) =>
        level && level.currentLevelLabel && level.currentLevelLabel.gameObject.activeInHierarchy ? level.currentLevelLabel.rectTransform : null;

    /// <summary>The buttons just right of the level number (or, not found, under the screen).</summary>
    static void Strip(string id, RectTransform number, GameObject panel, System.Action draw)
    {
        var vp = ImGui.GetMainViewport();
        var at = new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y - 8f);
        var pivot = new Vector2(0.5f, 1f);
        if (number && Corners(number, out var min, out var max))
        {
            at = new Vector2(max.x + 10f, max.y + 6f);   // (just below the number, to its right)
            pivot = new Vector2(0f, 0f);
        }
        else if (panel.transform is RectTransform rt && Corners(rt, out min, out max) && max.y + 50f < vp.WorkPos.y + vp.WorkSize.y)
        {
            at = new Vector2((min.x + max.x) * 0.5f, max.y + 6f);
            pivot = new Vector2(0.5f, 0f);
        }
        ImGui.SetNextWindowPos(at, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowBgAlpha(0.8f);
        if (ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove))
        {
            draw();
            boxMin = ImGui.GetWindowPos();
            boxMax = boxMin + ImGui.GetWindowSize();
            shownFrame = Time.frameCount;
        }
        ImGui.End();
    }

    /// <summary>A UI element's box on screen, ImGui's way up (y from the top).</summary>
    static bool Corners(RectTransform r, out Vector2 min, out Vector2 max)
    {
        var c4 = new UnityEngine.Vector3[4];
        r.GetWorldCorners(c4);
        var canvas = r.GetComponentInParent<Canvas>()?.rootCanvas;
        var cam = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var a = RectTransformUtility.WorldToScreenPoint(cam, c4[0]);
        var b = RectTransformUtility.WorldToScreenPoint(cam, c4[2]);
        min = new Vector2(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y));
        max = new Vector2(Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        return max.x > min.x;
    }
}

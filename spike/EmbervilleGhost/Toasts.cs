using System;
using System.Collections.Generic;
using ImGuiNET;
using Time = UnityEngine.Time;

namespace EmbervilleGhost;

/// <summary>
/// Stacked pop-up messages in the top-right corner of the game. They draw whether or not
/// the menu is open. Show() and Draw() both run on the main thread.
/// </summary>
public static class Toasts
{
    const float Lifetime = 4f;
    const float FadeTime = 0.5f;
    const float Width = 380f;
    const int MaxVisible = 5;

    class Toast
    {
        public LogKind Kind;
        public string Text;
        public float Born;
        public int Id;
    }

    static readonly List<Toast> active = new List<Toast>();
    static int nextId;

    /// <summary>Pixels to leave free at the top (something else is drawn top-right above the toasts).</summary>
    public static float TopOffset;

    public static void Show(LogKind kind, string text)
    {
        active.Add(new Toast { Kind = kind, Text = text, Born = Time.unscaledTime, Id = nextId++ });
        if (active.Count > MaxVisible) active.RemoveAt(0);
    }

    public static void Draw()
    {
        if (active.Count == 0) return;

        float now = Time.unscaledTime;
        active.RemoveAll(t => now - t.Born > Lifetime);

        var vp = ImGui.GetMainViewport();
        float y = vp.WorkPos.y + 10f + TopOffset;
        foreach (var t in active)
        {
            float age = now - t.Born;
            float alpha = Math.Max(0f, Math.Min(1f, Math.Min(age / 0.15f, (Lifetime - age) / FadeTime)));

            ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x - Width - 12f, y));
            ImGui.SetNextWindowSize(new Vector2(Width, 0f));
            ImGui.SetNextWindowBgAlpha(0.85f * alpha);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 8f));
            ImGui.Begin($"##toast{t.Id}",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);

            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(t.Text);
            ImGui.PopTextWrapPos();

            // Coloured accent bar on the left edge.
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var accent = AccentFor(t.Kind);
            accent.w = alpha;
            ImGui.GetWindowDrawList().AddRectFilled(pos, new Vector2(pos.x + 4f, pos.y + size.y), ImGui.ColorConvertFloat4ToU32(accent));

            ImGui.End();
            ImGui.PopStyleVar(2);

            y += size.y + 6f;
        }
    }

    static Vector4 AccentFor(LogKind kind) => kind switch
    {
        LogKind.Success => new Vector4(0.3f, 0.85f, 0.4f, 1f),
        LogKind.Warning => new Vector4(1f, 0.75f, 0.2f, 1f),
        LogKind.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
        _ => new Vector4(0.35f, 0.7f, 1f, 1f),
    };
}

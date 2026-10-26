using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The emote wheel: hold Y (config Emotes.Key), point the mouse at one, let go: it's said over your head (and goes to
/// chat as a line, so teammates get it in their speech bubble over you). Letting go in the middle says nothing.
/// The lines are the config's (Emotes.Lines, | between them, up to 8).
/// </summary>
internal static class Emotes
{
    static ConfigEntry<Key> key;
    static ConfigEntry<string> linesEntry;
    static string[] lines;
    static bool open;
    static int hover = -1;
    static string said;
    static float saidAt = -100f;
    const float Radius = 150f, Dead = 40f, SayFor = 4f;

    public static void Bind(ConfigFile config)
    {
        key = config.Bind("Emotes", "Key", Key.Y, "Hold for the emote wheel (T, Tab, P, I, M, K, J are the game's)");
        linesEntry = config.Bind("Emotes", "Lines", "Hello!|Follow me!|Help!|Thanks!|Wait!|Over here!|Nice!|Let's go!",
            "The emote wheel's lines, | between them (up to 8)");
    }

    static string[] Lines()
    {
        if (lines == null)
        {
            lines = (linesEntry.Value ?? "").Split('|');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].Trim();
            lines = System.Array.FindAll(lines, l => l.Length > 0);
            if (lines.Length > 8) System.Array.Resize(ref lines, 8);
        }
        return lines;
    }

    public static void Tick()
    {
        var kb = Keyboard.current;
        bool can = kb != null && LocalPlayer.Get() && !PlayerStatusUtil.LocalBusy && !ImGuiUnity.Typing && !Chat.IsOpen && Application.isFocused;
        if (!open)
        {
            if (can && kb[key.Value].wasPressedThisFrame && Lines().Length > 0) { open = true; hover = -1; GameInput.Block("emotes"); }
            return;
        }
        if (kb == null || !kb[key.Value].isPressed || !can)
        {
            open = false;
            GameInput.Unblock("emotes");
            if (can && hover >= 0 && hover < Lines().Length) Say(Lines()[hover]);
        }
    }

    static void Say(string text)
    {
        said = text;
        saidAt = Time.unscaledTime;
        Chat.Emote(text);
    }

    /// <summary>The wheel (while held), and what you said over your own head.</summary>
    public static void Draw()
    {
        DrawMine();
        if (!open) return;
        var all = Lines();
        var dl = ImGui.GetForegroundDrawList();
        var vp = ImGui.GetMainViewport();
        var c = new Vector2(vp.Pos.x + vp.Size.x * 0.5f, vp.Pos.y + vp.Size.y * 0.5f);
        var m = ImGui.GetMousePos();
        var d = new Vector2(m.x - c.x, m.y - c.y);
        float len = Mathf.Sqrt(d.x * d.x + d.y * d.y);
        float step = 2f * Mathf.PI / all.Length;
        hover = -1;
        if (len > Dead)
        {
            // slice 0 at the top, going round clockwise
            float a = Mathf.Atan2(d.x, -d.y);
            if (a < 0f) a += 2f * Mathf.PI;
            hover = (int)((a + step / 2f) / step) % all.Length;
        }
        uint bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.07f, 0.06f, 0.82f));
        uint edge = ImGui.ColorConvertFloat4ToU32(new Vector4(0.78f, 0.55f, 0.33f, 1f));
        uint lit = ImGui.ColorConvertFloat4ToU32(new Vector4(0.52f, 0.33f, 0.19f, 0.95f));
        dl.AddCircleFilled(c, Radius + 60f, bg, 64);
        dl.AddCircle(c, Radius + 60f, edge, 64, 2f);
        dl.AddCircle(c, Dead, edge, 32, 1.5f);
        for (int i = 0; i < all.Length; i++)
        {
            float ang = i * step;
            var at = new Vector2(c.x + Mathf.Sin(ang) * Radius, c.y - Mathf.Cos(ang) * Radius);
            var size = ImGui.CalcTextSize(all[i]);
            var min = new Vector2(at.x - size.x / 2f - 10f, at.y - size.y / 2f - 6f);
            var max = new Vector2(at.x + size.x / 2f + 10f, at.y + size.y / 2f + 6f);
            dl.AddRectFilled(min, max, i == hover ? lit : bg, 6f);
            dl.AddRect(min, max, edge, 6f);
            dl.AddText(new Vector2(at.x - size.x / 2f, at.y - size.y / 2f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.93f, 0.82f, 1f)), all[i]);
        }
        string hint = hover >= 0 ? "let go to say it" : "point at one, let go";
        var hs = ImGui.CalcTextSize(hint);
        dl.AddText(new Vector2(c.x - hs.x / 2f, c.y + Dead + 6f), ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.65f, 0.55f, 1f)), hint);
    }

    static void DrawMine()
    {
        if (said == null || Time.unscaledTime - saidAt > SayFor) return;
        var me = LocalPlayer.Get();
        var cam = Camera.main;
        if (!me || !cam) return;
        var sp = cam.WorldToScreenPoint(me.transform.position + new Vector3(0f, 2.3f, 0f));
        if (sp.z < 0f) return;
        var dl = ImGui.GetBackgroundDrawList();
        var size = ImGui.CalcTextSize(said);
        var at = new Vector2(sp.x, Screen.height - sp.y);
        var min = new Vector2(at.x - size.x / 2f - 8f, at.y - size.y - 10f);
        var max = new Vector2(at.x + size.x / 2f + 8f, at.y);
        float fade = Mathf.Clamp01((SayFor - (Time.unscaledTime - saidAt)) / 0.5f);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.97f, 0.9f, 0.92f * fade)), 6f);
        dl.AddText(new Vector2(min.x + 8f, min.y + 5f), ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.1f, 0.08f, fade)), said);
    }
}

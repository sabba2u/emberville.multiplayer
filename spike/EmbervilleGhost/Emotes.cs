using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
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
    static ConfigEntry<GamepadButton> padButton;
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
        padButton = config.Bind("Emotes", "PadButton", GamepadButton.DpadUp, "Hold on a controller for the emote wheel; point with either stick, let go to say it");
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

    static bool KeyHeld(bool pressedNow)
    {
        var kb = Keyboard.current;
        var pad = Gamepad.current;
        bool k = kb != null && (pressedNow ? kb[key.Value].wasPressedThisFrame : kb[key.Value].isPressed);
        bool p = pad != null && (pressedNow ? pad[padButton.Value].wasPressedThisFrame : pad[padButton.Value].isPressed);
        return k || p;
    }

    /// <summary>Where you point: a stick pushed over halfway (either), else the mouse from the screen's middle
    /// (screen pixels, y down). Null: neither (the mouse in the middle).</summary>
    static UnityEngine.Vector2 Pointing(UnityEngine.Vector2 centre)
    {
        var pad = Gamepad.current;
        if (pad != null)
        {
            var st = pad.rightStick.ReadValue();
            if (st.sqrMagnitude < 0.25f) st = pad.leftStick.ReadValue();
            if (st.sqrMagnitude >= 0.25f) { usingStick = true; return new UnityEngine.Vector2(st.x, -st.y) * (Radius + 1f); }
        }
        if (usingStick && Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude < 1f) return lastPoint;   // (stick let go: keep its pick)
        usingStick = false;
        if (Mouse.current == null) return UnityEngine.Vector2.zero;
        var m = Mouse.current.position.ReadValue();
        return new UnityEngine.Vector2(m.x - centre.x, (Screen.height - m.y) - centre.y);
    }
    static bool usingStick;
    static UnityEngine.Vector2 lastPoint;

    public static void Tick()
    {
        bool can = LocalPlayer.Get() && !PlayerStatusUtil.LocalBusy && !ImGuiUnity.Typing && !Chat.IsOpen && Application.isFocused;
        if (!open)
        {
            if (can && KeyHeld(true) && Lines().Length > 0) { open = true; hover = -1; usingStick = false; lastPoint = UnityEngine.Vector2.zero; GameInput.Block("emotes"); }
            return;
        }
        if (!KeyHeld(false) || !can)
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
        // (not ImGui's mouse: with no menu open it isn't tracked, and sat far off the top-left: always the last line)
        var pt = Pointing(new UnityEngine.Vector2(c.x - vp.Pos.x, c.y - vp.Pos.y));
        lastPoint = pt;
        var d = new Vector2(pt.x, pt.y);
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
        // the slices: lines between them, the pointed one filled
        for (int i = 0; i < all.Length; i++)
        {
            float a0 = i * step - step / 2f;
            if (all.Length > 1) dl.AddLine(new Vector2(c.x + Mathf.Sin(a0) * Dead, c.y - Mathf.Cos(a0) * Dead), new Vector2(c.x + Mathf.Sin(a0) * (Radius + 60f), c.y - Mathf.Cos(a0) * (Radius + 60f)), edge, 1f);
            if (i != hover) continue;
            uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.52f, 0.33f, 0.19f, 0.45f));
            for (int k = 0; k < 12; k++)   // (in thin pieces: each one convex)
            {
                float a = a0 + step * k / 12f, b = a0 + step * (k + 1) / 12f;
                float ro = Radius + 58f, ri = Dead + 2f;
                dl.AddQuadFilled(new Vector2(c.x + Mathf.Sin(a) * ri, c.y - Mathf.Cos(a) * ri), new Vector2(c.x + Mathf.Sin(a) * ro, c.y - Mathf.Cos(a) * ro),
                                 new Vector2(c.x + Mathf.Sin(b) * ro, c.y - Mathf.Cos(b) * ro), new Vector2(c.x + Mathf.Sin(b) * ri, c.y - Mathf.Cos(b) * ri), fill);
            }
        }
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

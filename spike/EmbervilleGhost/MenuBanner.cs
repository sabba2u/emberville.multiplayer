using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// A note on the game's title screen, between the Emberville logo and PLAY: clients should join from here (F9), the
/// host loads its world first (F8). While connected it says what's going on instead. Only on the main screen of the
/// main menu (MainMenuManager.state == MainScreen), not in character / world selection or the options.
/// </summary>
internal static class MenuBanner
{
    public static Plugin Plugin;
    static MainMenuManager menu;

    public static void Draw()
    {
        if (Plugin == null || JoinWindow.Visible) return;
        if (!menu) menu = Object.FindAnyObjectByType<MainMenuManager>();
        if (!menu || menu.state != MainMenuState.MainScreen) return;

        string title, line1, line2;
        if (Plugin.IsClient && GuestWorld.Ready)
        {
            title = "MULTIPLAYER: connected";
            line1 = $"The host's world \"{GuestWorld.WorldName}\" is ready.";
            line2 = "Press PLAY and pick your character to join.";
        }
        else if (Plugin.IsClient)
        {
            title = "MULTIPLAYER: connected";
            line1 = "Getting the host's world...";
            line2 = "";
        }
        else
        {
            title = "MULTIPLAYER";
            line1 = "Joining as a client? Press F9 here, on this main menu.";
            line2 = "Hosting? Load your world first, then press F8.";
        }

        var vp = ImGui.GetMainViewport();
        var dl = ImGui.GetForegroundDrawList();
        float y = vp.WorkPos.y + vp.WorkSize.y * 0.47f;   // under the logo, above PLAY
        float lineH = ImGui.GetTextLineHeight() + 4f;
        float w = Mathf.Max(ImGui.CalcTextSize(title).x, Mathf.Max(ImGui.CalcTextSize(line1).x, ImGui.CalcTextSize(line2).x)) + 28f;
        float h = lineH * (line2.Length > 0 ? 3 : 2) + 10f;
        float cx = vp.WorkPos.x + vp.WorkSize.x * 0.5f;
        dl.AddRectFilled(new Vector2(cx - w / 2, y - 6), new Vector2(cx + w / 2, y + h - 6), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.6f)), 6f);
        dl.AddRect(new Vector2(cx - w / 2, y - 6), new Vector2(cx + w / 2, y + h - 6), ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.85f, 0.75f, 0.8f)), 6f);
        Centered(dl, cx, ref y, lineH, title, new Vector4(0.45f, 0.95f, 0.85f, 1f));
        Centered(dl, cx, ref y, lineH, line1, new Vector4(1f, 1f, 1f, 1f));
        if (line2.Length > 0) Centered(dl, cx, ref y, lineH, line2, new Vector4(0.85f, 0.85f, 0.85f, 1f));

        // Which mod and version, bottom centre of the title screen.
        string version = Plugin.Title;
        var vs = ImGui.CalcTextSize(version);
        float vy = vp.WorkPos.y + vp.WorkSize.y - vs.y - 12f;
        dl.AddRectFilled(new Vector2(cx - vs.x / 2 - 8, vy - 3), new Vector2(cx + vs.x / 2 + 8, vy + vs.y + 3), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.5f)), 4f);
        dl.AddText(new Vector2(cx - vs.x / 2, vy), ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.95f, 0.85f, 0.95f)), version);
    }

    static void Centered(ImDrawListPtr dl, float cx, ref float y, float lineH, string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        dl.AddText(new Vector2(cx - size.x / 2, y), ImGui.ColorConvertFloat4ToU32(color), text);
        y += lineH;
    }
}

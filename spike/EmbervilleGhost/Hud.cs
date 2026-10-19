using ImGuiNET;

namespace EmbervilleGhost;

/// <summary>
/// Everything drawn with ImGui each frame (main thread, via ImGuiUnity.OnRender).
/// Always: status line top-left, teammate name + health bars, toasts top-right. Menu open (Insert): the debug panel too.
/// </summary>
internal static class Hud
{
    public static Plugin Plugin;

    const ImGuiWindowFlags HudFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoBringToFrontOnFocus;

    static void DrawTeammates()
    {
        var cam = UnityEngine.Camera.main;
        if (!cam) return;
        foreach (var p in Plugin.Players) DrawTeammate(cam, p);

        // Our own tags too, so you can tell the windows apart at a glance.
        var me = LocalPlayer.Get();
        if (Plugin.Connected && me)
        {
            float y = UnderFeet(cam, me.transform.position, out float x);
            y = Tag(x, y, Plugin.IsHost ? "[host]" : "[client]", RoleColor(Plugin.IsHost), Plugin.IOwnHere);
        }
    }

    static readonly Vector4 White = new Vector4(1, 1, 1, 1), Orange = new Vector4(1f, 0.6f, 0.3f, 1f);
    static Vector4 RoleColor(bool host) => host ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f);

    /// <summary>Screen y just under a character's feet (the sprite pivot is at the feet); Unity is bottom-left, ImGui top-left.</summary>
    static float UnderFeet(UnityEngine.Camera cam, UnityEngine.Vector3 feet, out float x)
    {
        var s = cam.WorldToScreenPoint(feet);
        x = s.x;
        return UnityEngine.Screen.height - s.y + 4f;
    }

    /// <summary>
    /// One centred tag on a dark pill at (x, y); returns the y for the next tag below it. <paramref name="diamond"/>:
    /// that player runs the area they're in (its creatures, breakables, drops and chests are theirs to decide).
    /// </summary>
    static float Tag(float x, float y, string text, Vector4 color, bool diamond = false)
    {
        var dl = ImGui.GetBackgroundDrawList();
        uint c = ImGui.ColorConvertFloat4ToU32(color);
        var size = ImGui.CalcTextSize(text);
        float d = size.y * 0.35f;                        // diamond half-size
        float extra = diamond ? d * 2 + 5 : 0;
        var at = new Vector2(x - (size.x + extra) / 2, y);
        dl.AddRectFilled(new Vector2(at.x - 4, at.y - 1), new Vector2(at.x + size.x + extra + 4, at.y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.55f)), 3f);
        dl.AddText(at, c, text);
        if (diamond)
        {
            var m = new Vector2(at.x + size.x + 5 + d, at.y + size.y / 2);
            dl.AddQuadFilled(new Vector2(m.x, m.y - d), new Vector2(m.x + d, m.y), new Vector2(m.x, m.y + d), new Vector2(m.x - d, m.y), c);
        }
        return y + size.y + 4f;
    }

    /// <summary>Above the head: health bar only. Under the feet: [host]/[client] (+ diamond), name, what they're busy with.</summary>
    static void DrawTeammate(UnityEngine.Camera cam, RemotePlayer p)
    {
        var avatar = p.Avatar;
        if (avatar == null || !avatar.Visible) return;

        if (p.MaxHealth > 0)
        {
            var head = cam.WorldToScreenPoint(avatar.HeadPosition);
            float hx = head.x, hy = UnityEngine.Screen.height - head.y;
            const float w = 70f, h = 7f;
            float frac = UnityEngine.Mathf.Clamp01((float)p.Health / p.MaxHealth);
            var dl = ImGui.GetBackgroundDrawList();
            var min = new Vector2(hx - w / 2, hy);
            dl.AddRectFilled(new Vector2(min.x - 1, min.y - 1), new Vector2(min.x + w + 1, min.y + h + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.7f)));
            dl.AddRectFilled(min, new Vector2(min.x + w * frac, min.y + h), ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.85f, 0.35f, 1f)));
        }

        float y = UnderFeet(cam, avatar.Root.transform.position, out float x);
        y = Tag(x, y, p.Id == 0 ? "[host]" : "[client]", RoleColor(p.Id == 0), Plugin.OwnerOf(p) == p.Id);
        y = Tag(x, y, p.Name, White);
        string status = PlayerStatusUtil.Label(p.Status);   // so you know why they're standing still
        if (status.Length > 0) Tag(x, y, status, Orange);
    }

    public static void Draw()
    {
        if (Plugin == null) return;

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + 10, vp.WorkPos.y + 10));
        ImGui.SetNextWindowBgAlpha(0.45f);
        if (ImGui.Begin("##status", HudFlags))
        {
            string line = $"[EmbervilleGhost #{SaveIsolation.CopyNumber}] {Plugin.Status}";
            if (Plugin.IsClient) line += $" | ping {Plugin.Remote.Ping}ms";
            if (Plugin.Connected) line += $" | {Plugin.PlayerCount} players";
            line += $" | {Plugin.Fps:0} fps, mod {Plugin.ModMs:0.0} ms";
            ImGui.TextUnformatted(line);
            if (!ImGuiUnity.IsOpen) ImGui.TextDisabled($"{ImGuiUnity.MenuKey.Value}: menu");
        }
        ImGui.End();

        // Top-right (the game uses both bottom corners): who runs the area we're in. Toasts stack below it.
        string hostHere = Chat.HostHereLine();
        Toasts.TopOffset = 0f;
        if (hostHere.Length > 0)
        {
            ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x - 10, vp.WorkPos.y + 10), ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.SetNextWindowBgAlpha(0.45f);
            if (ImGui.Begin("##hosthere", HudFlags))
            {
                ImGui.TextColored(new Vector4(0.95f, 0.9f, 0.6f, 1f), hostHere);
                Toasts.TopOffset = ImGui.GetWindowSize().y + 6f;
            }
            ImGui.End();
        }

        // Menus, the journal, dialogue, cutscenes: the game covers the world, so no name tags / labels on top of it.
        if (!PlayerStatusUtil.LocalBusy) DrawTeammates();
        Chat.Draw();
        Toasts.Draw();
        if (ImGuiUnity.IsOpen) DebugPanel.Draw();
    }
}

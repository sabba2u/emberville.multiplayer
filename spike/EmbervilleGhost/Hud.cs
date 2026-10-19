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
        placed.Clear();

        // Our own tags first (they stay at our feet), so you can tell the windows apart at a glance.
        var me = LocalPlayer.Get();
        if (Plugin.Connected && me)
        {
            float y = Place(UnderFeet(cam, me.transform.position, out float x), x, 1);
            Tag(x, y, Plugin.IsHost ? "[host]" : "[client]", RoleColor(Plugin.IsHost), Plugin.IOwnHere);
        }
        foreach (var p in Plugin.Players) DrawTeammate(cam, p);
        if (me) foreach (var p in Plugin.Players) DrawEdgeArrow(cam, me.transform.position, p);
    }

    /// <summary>
    /// A teammate in our scene + area but off our screen: an arrow on the screen edge where they are, pointing at
    /// them, in their tag colour, with name and distance. Nothing once they're on screen.
    /// </summary>
    static void DrawEdgeArrow(UnityEngine.Camera cam, UnityEngine.Vector3 myPos, RemotePlayer p)
    {
        if (!p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area)) return;
        var world = p.Avatar != null && p.Avatar.Visible ? p.Avatar.Root.transform.position : p.LastPosition;
        var s = cam.WorldToScreenPoint(world);
        float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
        float x = s.x, y = sh - s.y;                                // ImGui is top-left
        const float onScreenPad = 20f;
        if (s.z >= 0 && x > -onScreenPad && x < sw + onScreenPad && y > -onScreenPad && y < sh + onScreenPad) return;   // visible: no arrow

        // From the screen centre towards them, stopped at a margin inside the edges.
        const float margin = 46f;
        float cx = sw / 2, cy = sh / 2, dx = x - cx, dy = y - cy;
        if (s.z < 0) { dx = -dx; dy = -dy; }
        if (UnityEngine.Mathf.Abs(dx) < 0.001f && UnityEngine.Mathf.Abs(dy) < 0.001f) dy = 1f;
        float scale = UnityEngine.Mathf.Min((cx - margin) / UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(dx), 0.001f),
                                            (cy - margin) / UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(dy), 0.001f));
        float ax = cx + dx * scale, ay = cy + dy * scale;
        float len = UnityEngine.Mathf.Sqrt(dx * dx + dy * dy);
        float ux = dx / len, uy = dy / len;                        // pointing direction
        float px = -uy, py = ux;                                   // sideways

        var dl = ImGui.GetBackgroundDrawList();
        uint color = ImGui.ColorConvertFloat4ToU32(RoleColor(p.Id == 0));
        uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.7f));
        const float size = 16f;
        var tip = new Vector2(ax + ux * size, ay + uy * size);
        var left = new Vector2(ax - ux * size * 0.6f + px * size * 0.8f, ay - uy * size * 0.6f + py * size * 0.8f);
        var right = new Vector2(ax - ux * size * 0.6f - px * size * 0.8f, ay - uy * size * 0.6f - py * size * 0.8f);
        dl.AddTriangleFilled(new Vector2(tip.x + 2, tip.y + 2), new Vector2(left.x + 2, left.y + 2), new Vector2(right.x + 2, right.y + 2), shadow);
        dl.AddTriangleFilled(tip, left, right, color);

        // Name + distance, a little inward from the arrow so it stays on screen.
        string name = string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName;
        string label = $"{name}  {UnityEngine.Vector2.Distance(myPos, world):0}m";
        var ts = ImGui.CalcTextSize(label);
        float lx = UnityEngine.Mathf.Clamp(ax - ux * 30f - ts.x / 2, 4f, sw - ts.x - 4f);
        float ly = UnityEngine.Mathf.Clamp(ay - uy * 30f - ts.y / 2, 4f, sh - ts.y - 4f);
        dl.AddRectFilled(new Vector2(lx - 4, ly - 1), new Vector2(lx + ts.x + 4, ly + ts.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.55f)), 3f);
        dl.AddText(new Vector2(lx, ly), color, label);
    }

    /// <summary>Tag stacks drawn this frame; one that would overlap another (two players on the same spot) moves below it.</summary>
    static readonly System.Collections.Generic.List<(float x0, float y0, float x1, float y1)> placed =
        new System.Collections.Generic.List<(float, float, float, float)>();

    static float Place(float y, float x, int lines)
    {
        float h = lines * (ImGui.GetTextLineHeight() + 4f), half = 75f;
        for (int pass = 0; pass < 8; pass++)
        {
            bool moved = false;
            foreach (var r in placed)
                if (x - half < r.x1 && x + half > r.x0 && y < r.y1 && y + h > r.y0) { y = r.y1 + 2f; moved = true; }
            if (!moved) break;
        }
        placed.Add((x - half, y, x + half, y + h));
        return y;
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

        string status = PlayerStatusUtil.Label(p.Status);   // so you know why they're standing still
        float y = Place(UnderFeet(cam, avatar.Root.transform.position, out float x), x, status.Length > 0 ? 3 : 2);
        y = Tag(x, y, p.Id == 0 ? "[host]" : "[client]", RoleColor(p.Id == 0), Plugin.OwnerOf(p) == p.Id);
        y = Tag(x, y, p.Name, White);
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
            string line = $"[Emberville MP v{Plugin.Version} #{SaveIsolation.CopyNumber}] {Plugin.Status}";
            if (Plugin.IsClient) line += $" | ping {Plugin.Remote.Ping}ms";
            if (Plugin.Connected) line += $" | {Plugin.PlayerCount} players" + (Session.Pvp ? " | PvP ON" : "");
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
        MenuBanner.Draw();
        JoinWindow.Draw();
        HostWindow.Draw();
        Toasts.Draw();
        if (ImGuiUnity.IsOpen) DebugPanel.Draw();
    }
}

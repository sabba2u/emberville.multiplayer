using System;
using Cysharp.Threading.Tasks;
using ImGuiNET;

namespace EmbervilleGhost;

/// <summary>
/// The Insert menu: connection controls, who's where, and the message log. Drawn by Hud while the menu is open,
/// on Unity's main thread.
/// </summary>
internal static class DebugPanel
{
    static bool autoScroll = true;

    public static void Draw()
    {
        var plugin = Hud.Plugin;
        ImGui.SetNextWindowPos(new Vector2(60, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(560, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{Plugin.Title}###menu"))
        {
            ImGui.End();
            return;
        }

        // ---- Connection ----
        ImGui.Text($"Copy #{SaveIsolation.CopyNumber}   Data: {SaveIsolation.DataPath}");
        ImGui.Text($"Status: {plugin.Status}");
        var me = LocalPlayer.Get();
        ImGui.Text($"Me: P{plugin.MyId + 1}   {LocalPlayer.Scene} / {LocalPlayer.Area}" + (me ? $"   at {me.transform.position}" : "   (no player)"));
        foreach (var p in plugin.Players)
        {
            ImGui.Text($"  {p.Name}: {p.Scene} / {p.Area}   {p.Health}/{p.MaxHealth} hp" + (p.Avatar != null && p.Avatar.Visible ? "   (visible)" : ""));
            ImGui.SameLine();
            bool sameScene = p.HaveState && p.Scene == LocalPlayer.Scene;
            if (!sameScene || !me) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Teleport to##tp{p.Id}")) TeleportTo(p);
            if (!sameScene || !me) ImGui.EndDisabled();
            if (p.HaveState && !sameScene) { ImGui.SameLine(); ImGui.TextDisabled("(another scene)"); }
        }
        if (plugin.IsClient) ImGui.Text($"Ping to host: {plugin.Remote.Ping} ms");

        string address = plugin.AddressSetting.Value;
        if (ImGui.InputText("Address", ref address, 64)) plugin.AddressSetting.Value = address;
        ImGui.SameLine();
        if (ImGui.SmallButton("Paste address")) NetInfo.PasteAddress(plugin);
        if (plugin.IsHost) NetInfo.DrawHostAddresses(plugin);
        int port = plugin.PortSetting.Value;
        if (ImGui.InputInt("Port", ref port)) plugin.PortSetting.Value = Math.Max(1, Math.Min(65535, port));

        if (ImGui.Button("Host LAN (F8)")) HostWindow.Show();
        ImGui.SameLine();
        if (ImGui.Button("Join LAN (F9)")) plugin.Join();
        ImGui.SameLine();
        if (ImGui.Button("Disconnect (F10)")) plugin.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Test toast")) NetLog.Info("Hello from the menu", toast: true);
        ImGui.Checkbox("Log every hit (recon)", ref HitProbe.Enabled);
        if (ImGui.Button(SeedTest.Running ? "Map seed test (running...)" : "Map seed test")) SeedTest.Run().Forget();
        if (SeedTest.Result.Length > 0) { ImGui.SameLine(); ImGui.TextWrapped(SeedTest.Result); }
        Session.DrawSettings();

        string perf = $"Player packet {plugin.StateBytes} bytes.  Mod ms/frame:";
        foreach (var pair in Perf.Ms) perf += $"  {pair.Key} {pair.Value:0.0}";
        ImGui.TextDisabled(perf);

        ImGui.Separator();

        // ---- Log ----
        if (ImGui.Button("Clear log")) NetLog.Entries.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref autoScroll);

        ImGui.BeginChild("log", new Vector2(0, 0), ImGuiChildFlags.Border);
        foreach (var e in NetLog.Entries)
            ImGui.TextColored(ColorFor(e.Kind), $"{e.Time:HH:mm:ss}  {e.Text}");
        if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 5f)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();

        ImGui.End();
    }

    /// <summary>
    /// Next to a teammate (same scene), with the game's own teleport: fade out, switch to their GameArea if it's another
    /// one, place us, camera, fade in (GameManager.TeleportPlayerFade).
    /// </summary>
    static void TeleportTo(RemotePlayer p)
    {
        var me = LocalPlayer.Get();
        if (!me || !Singleton<GameManager>.HasMain) return;
        var target = p.Avatar != null && p.Avatar.Visible ? p.Avatar.Root.transform.position : p.LastPosition;
        target += new UnityEngine.Vector3(p.Flip ? 0.8f : -0.8f, 0f, 0f);   // beside them, not on top

        GameArea area = null;
        if (p.Area != LocalPlayer.Area)
            foreach (var a in UnityEngine.Object.FindObjectsByType<GameArea>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None))
                if (a.name == p.Area) { area = a; break; }

        Singleton<GameManager>.main.TeleportPlayerFade(target, p.Flip ? Direction.Left : Direction.Right, area, area ? area.CameraConfiner : null).Forget();
        NetLog.Info($"Teleporting to {p.Name}" + (area ? $" ({p.Area})" : ""));
    }

    static Vector4 ColorFor(LogKind kind) => kind switch
    {
        LogKind.Success => new Vector4(0.4f, 0.9f, 0.5f, 1f),
        LogKind.Warning => new Vector4(1f, 0.8f, 0.3f, 1f),
        LogKind.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };
}

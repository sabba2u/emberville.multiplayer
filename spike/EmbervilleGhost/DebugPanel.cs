using System;
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
        if (!ImGui.Begin("EmbervilleGhost"))
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
            ImGui.Text($"  {p.Name}: {p.Scene} / {p.Area}   {p.Health}/{p.MaxHealth} hp" + (p.Avatar != null && p.Avatar.Visible ? "   (visible)" : ""));
        if (plugin.IsClient) ImGui.Text($"Ping to host: {plugin.Remote.Ping} ms");

        string address = plugin.AddressSetting.Value;
        if (ImGui.InputText("Address", ref address, 64)) plugin.AddressSetting.Value = address;
        int port = plugin.PortSetting.Value;
        if (ImGui.InputInt("Port", ref port)) plugin.PortSetting.Value = Math.Max(1, Math.Min(65535, port));

        if (ImGui.Button("Host LAN (F8)")) plugin.Host();
        ImGui.SameLine();
        if (ImGui.Button("Join LAN (F9)")) plugin.Join();
        ImGui.SameLine();
        if (ImGui.Button("Disconnect (F10)")) plugin.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Test toast")) NetLog.Info("Hello from the menu", toast: true);
        ImGui.Checkbox("Log every hit (recon)", ref HitProbe.Enabled);
        bool shared = EnemySync.SharedXp.Value;
        if (ImGui.Checkbox("Shared XP (everyone in the area gets kill XP)", ref shared)) EnemySync.SharedXp.Value = shared;

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

    static Vector4 ColorFor(LogKind kind) => kind switch
    {
        LogKind.Success => new Vector4(0.4f, 0.9f, 0.5f, 1f),
        LogKind.Warning => new Vector4(1f, 0.8f, 0.3f, 1f),
        LogKind.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };
}

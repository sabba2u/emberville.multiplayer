using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Our addresses for friends: the public one (what the internet sees; asked once from api.ipify.org, on a background
/// thread) and the LAN one; copy / paste through the system clipboard (GUIUtility.systemCopyBuffer: our ImGui text
/// fields aren't wired to the clipboard, so pasting goes through buttons and Ctrl+V in chat).
/// Joining over the internet by IP needs the host to forward UDP <port> on their router (Steam joining: next version).
/// </summary>
internal static class NetInfo
{
    public static string PublicIp { get; private set; }   // null = not asked / asking
    public static bool Asking { get; private set; }

    public static void FetchPublicIp()
    {
        if (Asking || PublicIp != null) return;
        Asking = true;
        new Thread(() =>
        {
            string ip;
            try
            {
                using var web = new WebClient();
                ip = web.DownloadString("http://api.ipify.org").Trim();
                if (!IPAddress.TryParse(ip, out _)) ip = "unknown";
            }
            catch (Exception e) { ip = "unknown"; Plugin.Log.LogWarning($"Public IP lookup failed: {e.Message}"); }
            PublicIp = ip;
            Asking = false;
        }) { IsBackground = true }.Start();
    }

    /// <summary>This PC's address on the local network (first non-loopback IPv4 of an active adapter).</summary>
    public static string LanIp
    {
        get
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = nic.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;   // skip virtual adapters without a router
                    foreach (var a in props.UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }

    public static void Copy(string text)
    {
        GUIUtility.systemCopyBuffer = text;
        NetLog.Info($"Copied {text}: paste it to your friend (chat: Ctrl+V)", toast: true);
    }

    public static string Clipboard => GUIUtility.systemCopyBuffer ?? "";

    /// <summary>"1.2.3.4:9060" / "1.2.3.4" from the clipboard into the join address (and port).</summary>
    public static bool PasteAddress(Plugin plugin)
    {
        string text = Clipboard.Trim();
        if (text.Length == 0) return false;
        string host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text.Substring(colon + 1), out int port))
        {
            host = text.Substring(0, colon);
            plugin.PortSetting.Value = Math.Max(1, Math.Min(65535, port));
        }
        if (host.Length == 0 || host.Contains(" ")) return false;
        plugin.AddressSetting.Value = host;
        NetLog.Info($"Join address set to {host}:{plugin.PortSetting.Value}", toast: true);
        return true;
    }

    /// <summary>The "your address for friends" lines with Copy buttons (host window, Insert menu).</summary>
    public static void DrawHostAddresses(Plugin plugin)
    {
        FetchPublicIp();
        int port = plugin.PortSetting.Value;
        string pub = PublicIp == null ? "asking..." : $"{PublicIp}:{port}";
        ImGuiNET.ImGui.Text($"Internet: {pub}");
        ImGuiNET.ImGui.SameLine();
        if (PublicIp == null || PublicIp == "unknown") ImGuiNET.ImGui.BeginDisabled();
        if (ImGuiNET.ImGui.SmallButton("Copy##pub")) Copy($"{PublicIp}:{port}");
        if (PublicIp == null || PublicIp == "unknown") ImGuiNET.ImGui.EndDisabled();
        string lan = $"{LanIp}:{port}";
        ImGuiNET.ImGui.Text($"Same network (LAN): {lan}");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Copy##lan")) Copy(lan);
        ImGuiNET.ImGui.TextDisabled($"Over the internet, forward UDP port {port} on your router to this PC.");
    }
}

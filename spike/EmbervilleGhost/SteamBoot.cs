using System;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;

namespace EmbervilleGhost;

/// <summary>
/// Steam for a game that doesn't use it: the Emberville demo ships no steam_api64.dll and never calls SteamAPI, so the
/// mod brings Steamworks.NET (MIT) and Valve's redistributable steam_api64.dll in its plugin folder and starts Steam
/// itself, as the demo (app 5355880): Steam then shows friends "Playing Emberville Demo", the overlay's invite dialog
/// works, and a friend's "Join Game" launches / tells the demo.
///
/// The native DLL sits in our plugin folder, which isn't on the DLL search path: we LoadLibrary it by full path first,
/// so Steamworks.NET's [DllImport("steam_api64")] finds it already loaded. Launched from Steam, Steam sets SteamAppId;
/// started from the exe (testing), we set it to the demo's ourselves. Callbacks are pumped every frame.
/// </summary>
internal static class SteamBoot
{
    public const uint AppId = 5355880;   // Emberville Demo

    public static bool Ready { get; private set; }
    static bool tried;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string path);

    /// <summary>Start Steam once (true = it's up). Safe to call again.</summary>
    public static bool Init()
    {
        if (Ready || tried) return Ready;
        tried = true;
        try
        {
            string folder = Path.GetDirectoryName(typeof(SteamBoot).Assembly.Location);
            string dll = Path.Combine(folder, "steam_api64.dll");
            if (!File.Exists(dll) || LoadLibrary(dll) == IntPtr.Zero)
            {
                NetLog.Warn("Steam: steam_api64.dll missing from the mod folder: Steam joining is off");
                return false;
            }
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
            {
                Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
            }
            Ready = SteamAPI.Init();
            if (Ready) NetLog.Info($"Steam ready: {SteamFriends.GetPersonaName()} (friends can join through Steam)");
            else NetLog.Warn("Steam isn't running (or you're not logged in): Steam joining is off, IP joining still works");
        }
        catch (Exception e)
        {
            NetLog.Warn($"Steam couldn't start: {e.Message}");
            Ready = false;
        }
        return Ready;
    }

    /// <summary>Every frame: Steam's callbacks (lobbies, invites, connections).</summary>
    public static void Tick()
    {
        if (!Ready) return;
        try { SteamAPI.RunCallbacks(); }
        catch (Exception e) { NetLog.Error($"Steam callbacks: {e.Message}"); }
    }

    public static void Shutdown()
    {
        if (!Ready) return;
        Ready = false;
        try { SteamAPI.Shutdown(); } catch { }
    }
}

using BepInEx;
using BepInEx.Logging;

namespace BetterStreaming;

/// <summary>
/// Better Streaming: the streamer's Twitch chat plays along. A cheer (by its message's keyword, or its amount) buys an
/// effect in the game with the viewer's name on it: a raid, chickens, a heal, poison... Mean ones wait their turn on the
/// chaos dial, in a queue everyone can see; a death Twitch caused costs nothing. Sign-in is Twitch's device code (no
/// password in the game). Works on its own; next to the multiplayer mod it gets its menu tab, overlay and raids.
/// </summary>
[BepInPlugin(Guid, "Better Streaming", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterstreaming";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;

    void Awake()
    {
        Logger_ = Logger;
        Log.MarkMainThread();
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        TwitchConfig.Bind(Config);
        Overlay.Bind(Config);
        TwitchDeath.Bind(Config);
        ChainLightning.Bind(Config);
        Streamer.Init(Config);
        Effects.Register();
        TwitchAuth.LoadSaved();
        if (TwitchAuth.SignedIn || TwitchConfig.Mock) TwitchSocket.Start();
        Logger.LogInfo($"Better Streaming v{Version}");
    }

    void OnDestroy() => TwitchSocket.Stop();

    void Update()
    {
        try { Log.Flush(); Streamer.Tick(); ChainLightning.Tick(); TwitchDeath.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod ----

    /// <summary>The "Streaming" tab of the mods' menu (ImGui, drawn by the multiplayer mod).</summary>
    public static void DrawMenu() => Panel.Draw();

    /// <summary>The overlay: banners, the effects list, the queue (ImGui, with the multiplayer mod's HUD).</summary>
    public static void DrawHud() => Overlay.Draw();
}

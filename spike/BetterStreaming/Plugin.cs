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
    public const string Version = "0.2.1";
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
        Wheel.Bind(Config);
        Dice.Bind(Config);
        Slots.Bind(Config);
        Plinko.Bind(Config);
        Coin.Bind(Config);
        CasinoRoulette.Bind(Config);
        Race.Bind(Config);
        ChatterEnemies.Bind(Config);
        Party.Bind(Config);
        ChatVotes.Bind(Config);
        Polls.Bind(Config);
        Prediction.Bind(Config);
        Board.Bind(Config);
        Streamer.Init(Config);
        Effects.Register();
        TwitchAuth.LoadSaved();
        if (TwitchAuth.SignedIn || TwitchConfig.Mock) TwitchSocket.Start();
        Logger.LogInfo($"Better Streaming v{Version}");
    }

    static bool coinAsked;

    void OnGUI() { try { FakeCursors.Draw(); } catch (System.Exception e) { Log.Warn($"Streaming: fake cursors: {e.Message}"); } }

    void OnDestroy() => TwitchSocket.Stop();

    void Update()
    {
        if (!coinAsked && Singleton<GameManager>.HasMain) { coinAsked = true; NiceEffects.Preload(); }
        try { Log.Flush(); Streamer.Tick(); ChainLightning.Tick(); TwitchDeath.Tick(); Controls.Tick(); CameraFx.Tick(); Filters.Tick(); Storm.Tick(); Storm.TickHits(); WeaponLock.Tick(); WeaponRoulette.Tick(); PixelPerfect.Tick(); NiceEffects.Tick(); FireSound.Tick(); Bubble.Tick(); Stage.Tick(); Race.Tick(); ChatterEnemies.Tick(); Party.Tick(); Polls.Tick(); Prediction.Tick(); Board.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod ----

    /// <summary>The player is down but getting up again (a free Twitch death, second wind): not a real death.</summary>
    public static bool GettingUp => TwitchDeath.Falling;

    /// <summary>The "Streaming" tab of the mods' menu (ImGui, drawn by the multiplayer mod).</summary>
    public static void DrawMenu() => Panel.Draw();

    /// <summary>The overlay: banners, the effects list, the queue (ImGui, with the multiplayer mod's HUD).</summary>
    public static void DrawHud() => Overlay.Draw();
}

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BetterWeather;

/// <summary>
/// Better Weather: a rain storm, or snow that settles.  Pixel rain over the whole screen, slanted by a wind that shifts and gusts; a tiny
/// splash where each drop lands (a ripple on water); lightning striking the ground now and then; the sky darker and
/// greyer while it lasts. Outside only. Switched on from the Insert menu (Weather) or /rain. Works on its own; next to
/// the multiplayer mod, the storm is everyone's and so are the strikes.
/// </summary>
[BepInPlugin(Guid, "Better Weather", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterweather";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;
    internal static ConfigEntry<float> density, darkness, lightningEvery, snowDensity, snowSettle, snowDarkness;
    internal static ConfigEntry<string> outdoors;

    void Awake()
    {
        Logger_ = Logger;
        density = Config.Bind("Rain", "Density", 1f, "How hard it rains (1 = a medium storm; 0.1 - 4)");
        darkness = Config.Bind("Rain", "Darkness", 0.9f, "How much darker the world gets in a storm (0 - 0.9)");
        if (UnityEngine.Mathf.Approximately(darkness.Value, 0.4f)) darkness.Value = 0.9f;   // (the old default: to the new one)
        lightningEvery = Config.Bind("Rain", "LightningEvery", 10f, "Seconds between lightning strikes, about (0 = none)");
        snowDensity = Config.Bind("Snow", "Density", 1f, "How hard it snows (1 = steady; 0.1 - 4)");
        snowDarkness = Config.Bind("Snow", "Darkness", 0.7f, "How much darker the world gets while it snows (0 - 0.9)");
        snowSettle = Config.Bind("Snow", "Settle", 1f, "How fast snow covers the ground (1 = about 7-8 minutes to white; 0.2 - 5)");
        outdoors = Config.Bind("Rain", "OutdoorScenes", "Town,TutorialArea", "The scenes where it rains (the rest are inside)");
        Logger.LogInfo($"Better Weather v{Version}: /rain or Insert > Weather");
    }

    void Update()
    {
        try { Weather.Tick(); }
        catch (Exception e) { Logger.LogWarning(e); }
    }

    void LateUpdate()
    {
        try { Sky.Tick(); }
        catch (Exception e) { Logger.LogWarning(e); }
    }

    void OnDestroy() => Sky.Restore();

    // ---- for the multiplayer mod ----

    /// <summary>A chat command (/rain); true if ours.</summary>
    public static bool Command(string cmd)
    {
        string low = (cmd ?? "").Trim().ToLowerInvariant();
        if (low == "rain" || low == "storm") { Weather.Set(Weather.Now == Kind.Rain ? Kind.Clear : Kind.Rain, local: true); Hooks.Say(Weather.Now == Kind.Rain ? "A storm rolls in" : "The storm passes"); return true; }
        if (low == "snow") { Weather.Set(Weather.Now == Kind.Snow ? Kind.Clear : Kind.Snow, local: true); Hooks.Say(Weather.Now == Kind.Snow ? "It starts to snow" : "The snow stops (it'll melt)"); return true; }
        if (low == "rain on") { Weather.Set(Kind.Rain, local: true); return true; }
        if (low == "snow on") { Weather.Set(Kind.Snow, local: true); return true; }
        if (low == "rain off" || low == "snow off" || low == "clear") { Weather.Set(Kind.Clear, local: true); return true; }
        return false;
    }

    /// <summary>The Insert menu's Weather tab (ImGui).</summary>
    public static void DrawMenu() => Weather.DrawMenu();

    /// <summary>Someone else changed the weather (the multiplayer mod): 0 clear, 1 rain, 2 snow.</summary>
    public static void OnWeather(int kind) => Weather.Set((Kind)Mathf.Clamp(kind, 0, 2), local: false);

    /// <summary>Lightning someone else saw strike (the multiplayer mod): here too, if it's in our view.</summary>
    public static void OnStrike(Vector3 at) => Lightning.Strike(at, local: false);

    /// <summary>The weather now: 0 clear, 1 rain, 2 snow.</summary>
    public static int WeatherNow => (int)Weather.Now;
}

/// <summary>What the multiplayer mod tells us / does for us (defaults: on our own).</summary>
public static class Hooks
{
    /// <summary>A reply to /rain: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>We changed the weather (0 clear, 1 rain, 2 snow): tell everyone.</summary>
    public static Action<int> SendWeather = _ => { };
    /// <summary>Lightning struck in our view: tell the others here.</summary>
    public static Action<Vector3> SendStrike = _ => { };
    /// <summary>Under a roof the scene doesn't show (the multiplayer mod's dungeons and the descent).</summary>
    public static Func<bool> Underground = () => false;
}

/// <summary>Messages: the BepInEx log, and (toast) a line in the game's notification list on the right.</summary>
internal static class Log
{
    static NotificationsManager notifications;

    public static void Info(string text) => Plugin.Logger_.LogInfo(text);
    public static void Warn(string text) => Plugin.Logger_.LogWarning(text);

    public static void Note(string text)
    {
        if (!notifications) notifications = UnityEngine.Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(typeof(Log), null, text, 0f, 1);
    }
}

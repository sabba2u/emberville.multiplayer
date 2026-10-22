using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BetterWeather;

/// <summary>
/// Better Weather: a rain storm. Pixel rain over the whole screen, slanted by a wind that shifts and gusts; a tiny
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
    internal static ConfigEntry<float> density, darkness, lightningEvery;
    internal static ConfigEntry<string> outdoors;

    void Awake()
    {
        Logger_ = Logger;
        density = Config.Bind("Rain", "Density", 1f, "How hard it rains (1 = a medium storm; 0.1 - 4)");
        darkness = Config.Bind("Rain", "Darkness", 0.4f, "How much darker the world gets in a storm (0 - 0.9)");
        lightningEvery = Config.Bind("Rain", "LightningEvery", 10f, "Seconds between lightning strikes, about (0 = none)");
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
        if (low == "rain" || low == "storm") { Weather.Set(!Weather.On, local: true); Hooks.Say(Weather.On ? "A storm rolls in" : "The storm passes"); return true; }
        if (low == "rain on") { Weather.Set(true, local: true); return true; }
        if (low == "rain off") { Weather.Set(false, local: true); return true; }
        return false;
    }

    /// <summary>The Insert menu's Weather tab (ImGui).</summary>
    public static void DrawMenu() => Weather.DrawMenu();

    /// <summary>Someone else switched the storm (the multiplayer mod).</summary>
    public static void OnStorm(bool on) => Weather.Set(on, local: false);

    /// <summary>Lightning someone else saw strike (the multiplayer mod): here too, if it's in our view.</summary>
    public static void OnStrike(Vector3 at) => Lightning.Strike(at, local: false);

    public static bool StormOn => Weather.On;
}

/// <summary>What the multiplayer mod tells us / does for us (defaults: on our own).</summary>
public static class Hooks
{
    /// <summary>A reply to /rain: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>We switched the storm: tell everyone.</summary>
    public static Action<bool> SendStorm = _ => { };
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

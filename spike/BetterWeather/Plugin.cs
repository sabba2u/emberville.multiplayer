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

    // ---- the API (for other mods, NPC scripts...) ----

    /// <summary>Clear, rain or snow, with each player's own settings (their Weather sliders). Shared with everyone.</summary>
    public static void SetWeather(Kind kind) => Weather.Set(kind, local: true);

    /// <summary>Clear, rain or snow with settings of its own (any left null: the sliders'), for this spell of weather
    /// only (the next SetWeather replaces them; the saved sliders never change). Shared with everyone.</summary>
    public static void SetWeather(Kind kind, WeatherOptions options) => Weather.Set(kind, local: true, options);

    /// <summary>Lightning at this spot (in the world), seen by everyone there. Just to look at: it hurts nobody.</summary>
    public static void StrikeAt(Vector3 at) => Lightning.Strike(at, local: true);

    /// <summary>The weather now: Clear, Rain or Snow (and its own settings, if any).</summary>
    public static Kind Current => Weather.Now;
    public static WeatherOptions CurrentOptions => Weather.Options;

    /// <summary>Someone else changed the weather (the multiplayer mod): 0 clear, 1 rain, 2 snow, and its settings.</summary>
    public static void OnWeather(int kind, WeatherOptions options = null) => Weather.Set((Kind)Mathf.Clamp(kind, 0, 2), local: false, options);

    /// <summary>Lightning someone else saw strike (the multiplayer mod): here too, if it's in our view.</summary>
    public static void OnStrike(Vector3 at) => Lightning.Strike(at, local: false);

    /// <summary>The weather now: 0 clear, 1 rain, 2 snow.</summary>
    public static int WeatherNow => (int)Weather.Now;
}

/// <summary>
/// Settings for one spell of weather (SetWeather): any left null use that player's own slider.
/// </summary>
public sealed class WeatherOptions
{
    /// <summary>How many drops or flakes (x; 1 = medium).</summary>
    public float? Amount;
    /// <summary>How dark the sky gets (0 - 0.9).</summary>
    public float? Darkness;
    /// <summary>How fast snow settles (x; 1 = about 7-8 minutes to white).</summary>
    public float? Settle;
    /// <summary>Seconds between lightning strikes, about (0 = none). Rain only.</summary>
    public float? LightningEvery;
    /// <summary>How strong the wind is (x; 1 = as usual, 0 = still).</summary>
    public float? Wind;

    public bool Any => Amount.HasValue || Darkness.HasValue || Settle.HasValue || LightningEvery.HasValue || Wind.HasValue;

    public bool Same(WeatherOptions o) => o != null && Amount == o.Amount && Darkness == o.Darkness && Settle == o.Settle && LightningEvery == o.LightningEvery && Wind == o.Wind;

    /// <summary>As five floats (NaN = not set), for sending.</summary>
    public float[] ToArray() => new[] { Amount ?? float.NaN, Darkness ?? float.NaN, Settle ?? float.NaN, LightningEvery ?? float.NaN, Wind ?? float.NaN };

    public static WeatherOptions FromArray(float[] a)
    {
        float? F(int i) => a != null && i < a.Length && !float.IsNaN(a[i]) ? a[i] : null;
        return new WeatherOptions { Amount = F(0), Darkness = F(1), Settle = F(2), LightningEvery = F(3), Wind = F(4) };
    }

    public override string ToString()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (Amount.HasValue) parts.Add($"amount {Amount:0.##}");
        if (Darkness.HasValue) parts.Add($"darkness {Darkness:0.##}");
        if (Settle.HasValue) parts.Add($"settle {Settle:0.##}");
        if (LightningEvery.HasValue) parts.Add($"lightning every {LightningEvery:0.#} s");
        if (Wind.HasValue) parts.Add($"wind {Wind:0.##}");
        return string.Join(", ", parts);
    }
}

/// <summary>What the multiplayer mod tells us / does for us (defaults: on our own).</summary>
public static class Hooks
{
    /// <summary>A reply to /rain: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>We changed the weather (0 clear, 1 rain, 2 snow, and its own settings): tell everyone.</summary>
    public static Action<int, WeatherOptions> SendWeather = (_, _) => { };
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

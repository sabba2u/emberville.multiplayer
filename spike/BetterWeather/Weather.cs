using System;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BetterWeather;

/// <summary>
/// The storm: on or off, how far it has rolled in (Strength fades 0..1 over a few seconds, so the rain, the dark and
/// the lightning come and go gently), the wind, and whether we're out in it (an outdoor scene, not in a dungeon).
/// The wind is the same on every game without sending it: a slow noise of the clock (UTC seconds), shifting left and
/// right with gusts on top.
/// </summary>
internal static class Weather
{
    public static bool On { get; private set; }
    /// <summary>0 = clear, 1 = the full storm (fading in and out).</summary>
    public static float Strength { get; private set; }
    /// <summary>Strength where we are: 0 inside.</summary>
    public static float Here => Outside ? Strength : 0f;
    public static bool Outside { get; private set; }
    /// <summary>Sideways speed of the rain (world units / s; + = to the right).</summary>
    public static float Wind { get; private set; }

    const float FadeSeconds = 4f, MaxWind = 3.2f;
    static float nextOutsideCheck;

    public static void Set(bool on, bool local)
    {
        if (On == on) return;
        On = on;
        Log.Info($"Better Weather: storm {(on ? "on" : "off")}{(local ? "" : " (from another player)")}");
        if (local) Hooks.SendStorm(on);
    }

    public static void Tick()
    {
        float dt = Time.unscaledDeltaTime;
        Strength = Mathf.MoveTowards(Strength, On ? 1f : 0f, dt / FadeSeconds);
        if (Time.unscaledTime >= nextOutsideCheck) { nextOutsideCheck = Time.unscaledTime + 0.5f; Outside = IsOutside(); }
        double t = (DateTime.UtcNow - new DateTime(2026, 1, 1)).TotalSeconds;
        float slow = Mathf.PerlinNoise((float)(t * 0.03 % 10000.0), 0.37f) * 2f - 1f;          // the prevailing wind, drifting
        float gust = Mathf.PerlinNoise((float)(t * 0.35 % 10000.0), 5.1f);                     // gusts
        gust = Mathf.Max(0f, gust - 0.55f) * 2.2f;                                             // (mostly calm, now and then a push)
        Wind = (slow + Mathf.Sign(slow) * gust) * MaxWind;
        Rain.Tick(Here);
        Lightning.Tick(Here);
    }

    static bool IsOutside()
    {
        if (!Singleton<GameManager>.HasMain || !Singleton<GameManager>.main.player) return false;
        if (Hooks.Underground()) return false;
        string scene = SceneManager.GetActiveScene().name;
        return Plugin.outdoors.Value.Split(',').Any(s => s.Trim().Equals(scene, StringComparison.OrdinalIgnoreCase));
    }

    public static void DrawMenu()
    {
        bool on = On;
        if (ImGui.Checkbox("Storm (rain, wind, lightning)   - also /rain", ref on)) Set(on, local: true);
        ImGui.TextDisabled(Outside ? (Strength > 0f ? $"Out in it: {Strength * 100f:0}%" : "Outside: clear") : "Inside here: no rain (it rains in " + Plugin.outdoors.Value.Replace(",", ", ") + ")");
        float d = Plugin.density.Value;
        if (ImGui.SliderFloat("Rain drops (fewer / more)", ref d, 0.1f, 4f, "x%.1f")) Plugin.density.Value = d;
        float k = Plugin.darkness.Value;
        if (ImGui.SliderFloat("Sky (lighter / darker)", ref k, 0f, 0.9f, "%.2f")) Plugin.darkness.Value = k;
        float l = Plugin.lightningEvery.Value;
        if (ImGui.SliderFloat("Lightning every (s, 0 = none)", ref l, 0f, 40f, "%.0f")) Plugin.lightningEvery.Value = l;
        ImGui.TextDisabled($"Wind now: {(Wind < 0 ? "to the left" : "to the right")}, {Mathf.Abs(Wind):0.0}");
        if (ImGui.SmallButton("Lightning now (test)")) Lightning.StrikeSomewhere();
    }
}

using System;
using System.Linq;
using ImGuiNET;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BetterWeather;

/// <summary>What's falling: nothing, a rain storm, or snow.</summary>
public enum Kind { Clear = 0, Rain = 1, Snow = 2 }

/// <summary>
/// The weather: clear, a rain storm or snow; how far it has rolled in (each fades 0..1 over a few seconds, so the
/// rain, the snow, the dark and the lightning come and go gently, and rain can turn to snow), the wind, and whether
/// we're out in it (an outdoor scene, not in a dungeon). The wind is the same on every game without sending it: a
/// slow noise of the clock (UTC seconds), shifting left and right with gusts on top.
/// </summary>
internal static class Weather
{
    public static Kind Now { get; private set; }
    public static bool On => Now != Kind.Clear;
    /// <summary>0..1: how much rain / snow is falling (fading in and out).</summary>
    public static float RainStrength { get; private set; }
    public static float SnowStrength { get; private set; }
    /// <summary>Where we are: 0 inside.</summary>
    public static float RainHere => Outside ? RainStrength : 0f;
    public static float SnowHere => Outside ? SnowStrength : 0f;
    /// <summary>Either, for the sky.</summary>
    public static float Here => Mathf.Max(RainHere, SnowHere);
    public static bool Outside { get; private set; }
    /// <summary>Sideways speed of the rain (world units / s; + = to the right). Snow drifts on a share of it.</summary>
    public static float Wind { get; private set; }

    const float FadeSeconds = 4f, MaxWind = 3.2f;
    static float nextOutsideCheck;

    /// <summary>This spell of weather's own settings (null: the sliders'), from SetWeather.</summary>
    public static WeatherOptions Options { get; private set; } = new();

    public static float RainAmount => Options.Amount ?? Plugin.density.Value;
    public static float SnowAmount => Options.Amount ?? Plugin.snowDensity.Value;
    public static float RainDarkness => Mathf.Clamp(Options.Darkness ?? Plugin.darkness.Value, 0f, 0.95f);
    public static float SnowDarkness => Mathf.Clamp(Options.Darkness ?? Plugin.snowDarkness.Value, 0f, 0.95f);
    public static float Settle => Options.Settle ?? Plugin.snowSettle.Value;
    public static float LightningEvery => Options.LightningEvery ?? Plugin.lightningEvery.Value;
    public static float WindScale => Options.Wind ?? 1f;

    public static void Set(Kind kind, bool local, WeatherOptions options = null)
    {
        options ??= new WeatherOptions();
        if (Now == kind && options.Same(Options)) return;
        Now = kind;
        Options = options;
        Log.Info($"Better Weather: {kind.ToString().ToLowerInvariant()}{(options.Any ? " " + options : "")}{(local ? "" : " (from another player)")}");
        if (local) Hooks.SendWeather((int)kind, options);
    }

    static bool hooked;

    public static void Tick()
    {
        if (!hooked) { hooked = true; SceneManager.sceneLoaded += (_, _) => nextOutsideCheck = 0f; }
        float dt = Time.unscaledDeltaTime;
        RainStrength = Mathf.MoveTowards(RainStrength, Now == Kind.Rain ? 1f : 0f, dt / FadeSeconds);
        SnowStrength = Mathf.MoveTowards(SnowStrength, Now == Kind.Snow ? 1f : 0f, dt / FadeSeconds);
        if (Time.unscaledTime >= nextOutsideCheck)
        {
            Outside = IsOutside();
            // (until the player's in the new scene, every frame: then the snow and the sky are there from the start)
            nextOutsideCheck = Time.unscaledTime + (Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player ? 0.5f : 0f);
        }
        double t = (DateTime.UtcNow - new DateTime(2026, 1, 1)).TotalSeconds;
        float slow = Mathf.PerlinNoise((float)(t * 0.03 % 10000.0), 0.37f) * 2f - 1f;          // the prevailing wind, drifting
        float gust = Mathf.PerlinNoise((float)(t * 0.35 % 10000.0), 5.1f);                     // gusts
        gust = Mathf.Max(0f, gust - 0.55f) * 2.2f;                                             // (mostly calm, now and then a push)
        Wind = (slow + Mathf.Sign(slow) * gust) * MaxWind * WindScale;
        Rain.Tick(RainHere);
        Lightning.Tick(RainHere);
        Snow.Tick(SnowHere, Outside);
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
        int k = (int)Now;
        ImGui.Text("Weather:");
        ImGui.SameLine(); if (ImGui.RadioButton("Clear", ref k, 0)) Set(Kind.Clear, local: true);
        ImGui.SameLine(); if (ImGui.RadioButton("Rain storm  (/rain)", ref k, 1)) Set(Kind.Rain, local: true);
        ImGui.SameLine(); if (ImGui.RadioButton("Snow  (/snow)", ref k, 2)) Set(Kind.Snow, local: true);
        ImGui.TextDisabled(Outside ? (Here > 0f ? $"Out in it: {Here * 100f:0}%" : "Outside: clear") : "Inside here: nothing falls (outside is " + Plugin.outdoors.Value.Replace(",", ", ") + ")");
        if (Options.Any) ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.8f, 0.4f, 1f), $"This weather has its own settings ({Options}): the sliders count for what it leaves out");
        float d = Plugin.density.Value;
        if (ImGui.SliderFloat("Rain drops (fewer / more)", ref d, 0.1f, 4f, "x%.1f")) Plugin.density.Value = d;
        float f = Plugin.snowDensity.Value;
        if (ImGui.SliderFloat("Snowflakes (fewer / more)", ref f, 0.1f, 4f, "x%.1f")) Plugin.snowDensity.Value = f;
        float s = Plugin.snowSettle.Value;
        if (ImGui.SliderFloat("Snow settles (slower / faster)", ref s, 0.2f, 5f, "x%.1f")) Plugin.snowSettle.Value = s;
        float dk = Plugin.darkness.Value;
        if (ImGui.SliderFloat("Rain sky (lighter / darker)", ref dk, 0f, 0.9f, "%.2f")) Plugin.darkness.Value = dk;
        float sk = Plugin.snowDarkness.Value;
        if (ImGui.SliderFloat("Snow sky (lighter / darker)", ref sk, 0f, 0.9f, "%.2f")) Plugin.snowDarkness.Value = sk;
        float l = Plugin.lightningEvery.Value;
        if (ImGui.SliderFloat("Lightning every (s, 0 = none)", ref l, 0f, 40f, "%.0f")) Plugin.lightningEvery.Value = l;
        ImGui.TextDisabled($"Wind now: {(Wind < 0 ? "to the left" : "to the right")}, {Mathf.Abs(Wind):0.0}   Snow on the ground: {Snow.Covered} tiles");
        if (ImGui.SmallButton("Lightning now (test)")) Lightning.StrikeSomewhere();
        ImGui.SameLine();
        if (ImGui.SmallButton("Melt the snow now")) Snow.MeltAll();
    }
}

using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BetterWeather;

/// <summary>
/// The storm's sky: the world darker and a little blue-grey, and lightning's flash. Both are the game's own world
/// light (GlobalLightManager's Light2D, which the day/night cycle colours: WorldLight2D sets its colour from the time of
/// day). After the game sets it, we multiply ours on (LateUpdate); a colour we didn't write is the game's new one,
/// so the day's colour keeps coming through and nothing builds up. Back as it was when the storm is gone.
/// </summary>
internal static class Sky
{
    static Light2D light;
    static Color baseColour, written;
    static bool touched;
    static float flash;   // 0..1, decays
    static float here, share = 1f;   // how stormy the sky is (0..1); rain's share of it (1 rain, 0 snow)
    static Kind kind = Kind.Rain;

    static readonly Color StormTint = new Color(0.78f, 0.84f, 1f);   // (blue-grey, before darkening)
    static readonly Color SnowTint = new Color(0.9f, 0.94f, 1f);

    /// <summary>Lightning: the world lights up for a moment.</summary>
    public static void Flash(float amount = 1f) => flash = Mathf.Max(flash, amount);

    public static void Tick()
    {
        flash = Mathf.MoveTowards(flash, 0f, Time.unscaledDeltaTime * 3.5f);
        // the sky turns quicker than the rain builds (about 1.5 s): dark almost as soon as it starts, light again as fast
        bool falling = Weather.Outside && Weather.Now != Kind.Clear;
        here = Mathf.MoveTowards(here, falling ? 1f : 0f, Time.unscaledDeltaTime / 1.5f);
        if (falling) kind = Weather.Now;
        if (here <= 0f && flash <= 0f) { Restore(); return; }
        if (!light) { light = Find(); touched = false; if (!light) return; }
        var now = light.color;
        if (!touched || now != written) baseColour = now;   // the game's own colour (new, or first look)
        // rain: blue-grey; snow: a paler, whiter grey; each its own darkness
        share = Mathf.MoveTowards(share, kind == Kind.Snow ? 0f : 1f, Time.unscaledDeltaTime / 1.5f);   // (rain to snow: blended)
        var tint = Color.Lerp(SnowTint, StormTint, share);
        float dark = Mathf.Lerp(Plugin.snowDarkness.Value, Plugin.darkness.Value, share) * here;
        var c = baseColour * Color.Lerp(Color.white, tint, here) * (1f - dark);
        c = Color.Lerp(c, new Color(1.6f, 1.65f, 1.8f), flash * 0.85f);
        c.a = baseColour.a;
        light.color = c;
        written = c;
        touched = true;
    }

    public static void Restore()
    {
        if (touched && light && light.color == written) light.color = baseColour;
        touched = false;
    }

    static Light2D Find()
    {
        if (Singleton<GlobalLightManager>.HasMain && Singleton<GlobalLightManager>.main.light2D) return Singleton<GlobalLightManager>.main.light2D;
        var world = Object.FindAnyObjectByType<WorldLight2D>();
        return world ? world.GetComponent<Light2D>() : null;
    }
}

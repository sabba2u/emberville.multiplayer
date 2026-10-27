using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BetterStreaming;

/// <summary>
/// The filter effects, on top of the game's own picture (they stack):
///  - drunk: the picture wobbling and its colours fringing (the game's post-processing), and you staggering (Drift);
///  - colour blind: grey, or the colours wildly turning (one or the other, at random each time);
///  - tunnel vision: black all round, a small circle around you clear;
///  - lights out: the world near-black (the scene's global light dimmed just for each frame), a small lantern on you;
///  - retro: big chunky pixels (the game's pixel-perfect camera at 320x180) in grey-green, like an old handheld.
/// Our own post-processing volume (priority over the game's) holds the colour ones; each frame it's set from what runs.
/// </summary>
internal static class Filters
{
    static readonly string[] Ids = { "drunk", "colourblind", "tunnel", "lightsout", "retro" };
    static Volume volume;
    static ColorAdjustments colour;
    static ChromaticAberration fringe;
    static LensDistortion wobble;
    static Vignette vignette;
    static bool greyNotHue, hooked;
    static Light2D lantern;
    static readonly List<(Light2D light, float intensity)> dimmed = new();
    static Camera postOn;      // (the camera whose post-processing we switched on)
    static bool postWas;

    public static void Register()
    {
        foreach (var id in Ids) Streamer.Effects[id] = new Switch(id,
            start: id == "colourblind" ? _ => greyNotHue = Random.value < 0.5f : (System.Action<PlayerMaster>)null);
    }

    static bool On(string id) => Controls.On(id);

    static float lurchUntil, nextLurch;
    static float lurchAngle;

    /// <summary>
    /// Drunk: you never walk quite straight. The way you push is turned up to ~25° one way then the other (weaving),
    /// every 3-6 s a lurch (half a second veering hard off to one side), and standing still you sway a little.
    /// (From Controls' input patch, on your character only.)
    /// </summary>
    public static void Drift(PlayerMaster p)
    {
        if (!On("drunk")) return;
        float t = Time.time;
        if (t >= nextLurch) { nextLurch = t + Random.Range(3f, 6f); lurchUntil = t + 0.5f; lurchAngle = Random.value < 0.5f ? -60f : 60f; }
        var input = p.input;
        float push = input.magnitude;
        if (push < 0.1f)
        {
            p.input = new Vector2(Mathf.Sin(t * 1.7f), Mathf.Cos(t * 1.1f)) * 0.18f;   // (swaying on the spot)
            return;
        }
        float weave = Mathf.Sin(t * 1.9f) * 18f + Mathf.Sin(t * 3.7f) * 7f;   // (two waves: never a steady rhythm)
        if (t < lurchUntil) weave += lurchAngle;
        p.input = (Vector2)(Quaternion.Euler(0f, 0f, weave) * (Vector3)input);
    }

    /// <summary>Every frame (Plugin.Update): the volume set from what runs, the lantern on you.</summary>
    public static void Tick()
    {
        if (!hooked) { hooked = true; RenderPipelineManager.beginCameraRendering += Begin; RenderPipelineManager.endCameraRendering += End; }
        bool any = Ids.Any(On);
        PixelPerfect.WantRetro = On("retro");   // (chunky pixels)
        PostProcessing(On("drunk") || On("colourblind") || On("tunnel") || On("retro"));
        if (!any) { if (volume) volume.weight = 0f; Lantern(false); return; }
        Make();
        volume.weight = 1f;
        float t = Time.time;
        // colours
        colour.saturation.overrideState = colour.hueShift.overrideState = colour.colorFilter.overrideState = colour.contrast.overrideState = false;
        if (On("colourblind"))
        {
            if (greyNotHue) { colour.saturation.overrideState = true; colour.saturation.value = -100f; }
            else { colour.hueShift.overrideState = true; colour.hueShift.value = Mathf.Repeat(t * 60f, 360f) - 180f; colour.saturation.overrideState = true; colour.saturation.value = 40f; }
        }
        if (On("retro"))
        {
            colour.saturation.overrideState = true; colour.saturation.value = -100f;
            colour.colorFilter.overrideState = true; colour.colorFilter.value = new Color(0.62f, 0.78f, 0.42f);
            colour.contrast.overrideState = true; colour.contrast.value = 25f;
        }
        // drunk
        wobble.intensity.overrideState = fringe.intensity.overrideState = On("drunk");
        if (On("drunk")) { wobble.intensity.value = Mathf.Sin(t * 1.4f) * 0.35f; fringe.intensity.value = 1f; }
        vignette.intensity.overrideState = false;   // (tunnel vision is drawn over the screen: DrawTunnel)
        Lantern(On("lightsout"));
    }

    /// <summary>The game camera may not run post-processing: on while a filter needs it, back as it was after.</summary>
    static void PostProcessing(bool on)
    {
        var cam = Camera.main;
        if (postOn && (postOn != cam || !on)) { var d0 = postOn.GetUniversalAdditionalCameraData(); if (d0) d0.renderPostProcessing = postWas; postOn = null; }
        if (!on || !cam || postOn == cam) return;
        var d = cam.GetUniversalAdditionalCameraData();
        if (!d) return;
        postWas = d.renderPostProcessing;
        d.renderPostProcessing = true;
        postOn = cam;
    }

    /// <summary>Tunnel vision: black over everything but a small circle round you (a fifth of the screen's height
    /// across, a short soft edge), drawn over the game and under our banners (ImGui, with the overlay).</summary>
    public static void DrawTunnel()
    {
        if (!On("tunnel")) return;
        var me = Effects.Player; var cam = Camera.main;
        if (!me || !cam) return;
        var sp = cam.WorldToScreenPoint(me.transform.position + Vector3.up * 0.6f);
        var c = new ImGuiNET.Vector2(sp.x, UnityEngine.Screen.height - sp.y);
        float r = UnityEngine.Screen.height * 0.1f, far = (UnityEngine.Screen.width + UnityEngine.Screen.height) * 1.2f;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        // the black: one very thick ring whose inner edge is the circle (it reaches past every corner)
        dl.AddCircle(c, r + 18f + far / 2f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, 1f)), 96, far);
        // the soft edge: thin rings fading in toward the circle
        for (int i = 0; i < 6; i++)
        {
            float a = (i + 1) / 7f;
            dl.AddCircle(c, r + i * 3f + 1.5f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, a)), 96, 3f);
        }
    }

    /// <summary>Lights out, made sure of: a dark layer over the screen with a soft hole round your lantern (whatever
    /// the scene lights with). Drawn over the game, under our banners.</summary>
    public static void DrawDark()
    {
        if (!On("lightsout")) return;
        var me = Effects.Player; var cam = Camera.main;
        if (!me || !cam) return;
        var sp = cam.WorldToScreenPoint(me.transform.position + Vector3.up * 0.5f);
        var c = new ImGuiNET.Vector2(sp.x, UnityEngine.Screen.height - sp.y);
        float r = UnityEngine.Screen.height * 0.16f, far = (UnityEngine.Screen.width + UnityEngine.Screen.height) * 1.2f;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        const float Dark = 0.82f, Soft = 60f;
        dl.AddCircle(c, r + Soft + far / 2f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0.02f, Dark)), 96, far);
        // a long soft edge: the lantern's light fading out
        const int Steps = 12;
        for (int i = 0; i < Steps; i++)
        {
            float a = Dark * (i + 1) / (Steps + 1);
            dl.AddCircle(c, r + i * (Soft / Steps) + Soft / Steps / 2f, ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0.02f, a)), 96, Soft / Steps);
        }
    }

    static void Make()
    {
        if (volume) return;
        var go = new GameObject("BS_Filters");
        Object.DontDestroyOnLoad(go);
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 200f;
        volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
        colour = volume.profile.Add<ColorAdjustments>(false);
        fringe = volume.profile.Add<ChromaticAberration>(false);
        wobble = volume.profile.Add<LensDistortion>(false);
        vignette = volume.profile.Add<Vignette>(false);
        colour.active = fringe.active = wobble.active = vignette.active = true;
    }

    // ---- lights out ----

    static void Lantern(bool on)
    {
        var me = Effects.Player;
        if (!on || !me) { if (lantern) lantern.gameObject.SetActive(false); return; }
        if (!lantern)
        {
            var go = new GameObject("BS_Lantern");
            Object.DontDestroyOnLoad(go);
            lantern = go.AddComponent<Light2D>();
            lantern.lightType = Light2D.LightType.Point;
            lantern.color = new Color(1f, 0.85f, 0.6f);
            lantern.intensity = 1.2f;
            lantern.pointLightOuterRadius = 2.8f;
            lantern.pointLightInnerRadius = 0.5f;
        }
        lantern.gameObject.SetActive(true);
        lantern.transform.position = me.transform.position + Vector3.up * 0.5f;
    }

    /// <summary>Lights out: the scene's global lights near-black just for the frame (the game's day and night keep running).</summary>
    static void Begin(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != Camera.main || !On("lightsout") || dimmed.Count > 0) return;   // (already dimmed: never dim the dimmed)
        foreach (var l in Object.FindObjectsByType<Light2D>(FindObjectsSortMode.None))
        {
            if (!l || !l.isActiveAndEnabled || l == lantern) continue;
            dimmed.Add((l, l.intensity));
            l.intensity *= l.lightType == Light2D.LightType.Global ? 0f : 0.25f;   // (the world dark; torches and lamps a glimmer)
        }
    }

    static void End(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != Camera.main || dimmed.Count == 0) return;
        foreach (var (l, i) in dimmed) if (l) l.intensity = i;
        dimmed.Clear();
    }

}

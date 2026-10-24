using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Vector2 = UnityEngine.Vector2;

namespace EmbervilleGhost;

/// <summary>
/// Tilt-shift (Insert > View, or /tiltshift): the miniature look. A sharp band across the middle of the screen, the top
/// and bottom blurred more and more toward the edges, the colours a little richer. Made without shaders of our own (the
/// game's 2D sprites write no depth, so Unity's depth of field can't do it): a second camera draws the world small (half
/// the screen), that's halved again and again (each halving blends four pixels: a soft blur), and the soft picture lies
/// over the top and bottom of the screen in thin strips, each a little more see-through toward the middle. The colour:
/// a saturation / contrast boost through the game's post-processing. Works with the tilted view (TiltView) and without.
/// </summary>
internal static class TiltShift
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> focus, strength, saturation;

    const int Strips = 12;
    static Camera blurCam;
    static RenderTexture drawn, soft;
    static RenderTexture[] halves = new RenderTexture[0];
    static GameObject overlay;
    static RawImage[] top, bottom;
    static Volume volume;
    static ColorAdjustments colour;
    static bool hooked, postWas;

    public static bool On => on != null && on.Value;
    public static Camera Camera => blurCam;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("View", "TiltShift", false, "The miniature look: a sharp band in the middle, the top and bottom blurred, richer colours");
        focus = config.Bind("View", "TiltShiftFocus", 0.35f, "How much of the screen's height stays sharp, in the middle (0.1-0.8)");
        strength = config.Bind("View", "TiltShiftBlur", 3f, "How blurry the edges get (1-5)");
        saturation = config.Bind("View", "TiltShiftColour", 25f, "Colour boost, -100 to 100 (0: the game's own)");
    }

    public static void Toggle(bool value)
    {
        on.Value = value;
        if (!value) Off();
        NetLog.Info(value ? "Tilt-shift on (/tiltshift again: off)" : "Tilt-shift off", toast: true);
    }

    public static void Tick()
    {
        if (on == null) return;
        if (!hooked) { hooked = true; RenderPipelineManager.endCameraRendering += AfterBlurCam; }
        var main = Camera.main;
        if (!On || !main) return;
        if (!blurCam) Make(main);
        // follows the game's camera (the tilted view turns it at drawing time, TiltView.Begin)
        blurCam.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
        blurCam.orthographic = main.orthographic;
        blurCam.orthographicSize = main.orthographicSize;
        blurCam.fieldOfView = main.fieldOfView;
        blurCam.cullingMask = main.cullingMask & ~(1 << LayerMask.NameToLayer("UI"));
        blurCam.backgroundColor = main.backgroundColor;
        blurCam.clearFlags = main.clearFlags;
        blurCam.depth = main.depth - 1;
        Size();
        Bands();
        if (colour) { colour.saturation.Override(Mathf.Clamp(saturation.Value, -100f, 100f)); colour.contrast.Override(Mathf.Clamp(saturation.Value * 0.4f, -50f, 50f)); }
        var data = main.GetUniversalAdditionalCameraData();
        if (data && !data.renderPostProcessing) { postWas = false; data.renderPostProcessing = true; }
    }

    static void Make(Camera main)
    {
        var go = new GameObject("EG_TiltShiftCam");
        Object.DontDestroyOnLoad(go);
        blurCam = go.AddComponent<Camera>();
        blurCam.CopyFrom(main);
        blurCam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        postWas = true;

        overlay = new GameObject("EG_TiltShift");
        Object.DontDestroyOnLoad(overlay);
        var canvas = overlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -1000;   // (under the game's own HUD)
        top = new RawImage[Strips]; bottom = new RawImage[Strips];
        for (int i = 0; i < Strips; i++) { top[i] = Strip("top" + i); bottom[i] = Strip("bottom" + i); }

        var vgo = new GameObject("EG_TiltShiftColour");
        Object.DontDestroyOnLoad(vgo);
        volume = vgo.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;
        volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
        colour = volume.profile.Add<ColorAdjustments>(true);
    }

    static RawImage Strip(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(overlay.transform, false);
        var img = go.AddComponent<RawImage>();
        img.raycastTarget = false;
        return img;
    }

    /// <summary>The render textures for this screen size: the world drawn at half size, then halved again (1-5 times).</summary>
    static void Size()
    {
        int w = Mathf.Max(16, Screen.width / 2), h = Mathf.Max(16, Screen.height / 2);
        int steps = Mathf.Clamp(Mathf.RoundToInt(strength.Value), 1, 5);
        if (drawn && drawn.width == w && drawn.height == h && halves.Length == steps) return;
        Release();
        drawn = new RenderTexture(w, h, 24) { filterMode = FilterMode.Bilinear, name = "EG_TiltShiftDrawn" };
        halves = new RenderTexture[steps];
        for (int i = 0; i < steps; i++)
        {
            w = Mathf.Max(4, w / 2); h = Mathf.Max(4, h / 2);
            halves[i] = new RenderTexture(w, h, 0) { filterMode = FilterMode.Bilinear, name = "EG_TiltShiftHalf" + i };
        }
        soft = halves[steps - 1];
        blurCam.targetTexture = drawn;
    }

    /// <summary>After the blur camera has drawn: halve it down to the soft picture.</summary>
    static void AfterBlurCam(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != blurCam || !drawn || halves.Length == 0) return;
        var from = drawn;
        foreach (var h in halves) { Graphics.Blit(from, h); from = h; }
    }

    /// <summary>The strips: the soft picture over the top and bottom of the screen, see-through toward the sharp middle.</summary>
    static void Bands()
    {
        float sharp = Mathf.Clamp(focus.Value, 0.1f, 0.8f);
        float band = (1f - sharp) / 2f;   // the height of each blurred band, as a share of the screen
        for (int i = 0; i < Strips; i++)
        {
            float a = band * i / Strips, b = band * (i + 1) / Strips;   // from the edge in
            float alpha = 1f - (i + 0.5f) / Strips;
            Place(top[i], 1f - b, 1f - a, alpha);
            Place(bottom[i], a, b, alpha);
        }
    }

    static void Place(RawImage img, float y0, float y1, float alpha)
    {
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(0f, y0); rt.anchorMax = new Vector2(1f, y1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        img.texture = soft;
        img.uvRect = new Rect(0f, y0, 1f, y1 - y0);
        img.color = new Color(1f, 1f, 1f, alpha * alpha);   // (eased: the blur thins out softly)
    }

    static void Release()
    {
        if (blurCam) blurCam.targetTexture = null;
        if (drawn) { drawn.Release(); Object.Destroy(drawn); }
        foreach (var h in halves) if (h) { h.Release(); Object.Destroy(h); }
        halves = new RenderTexture[0];
        drawn = soft = null;
    }

    static void Off()
    {
        Release();
        if (blurCam) Object.Destroy(blurCam.gameObject);
        if (overlay) Object.Destroy(overlay);
        if (volume) Object.Destroy(volume.gameObject);
        blurCam = null; overlay = null; volume = null; colour = null;
        var main = Camera.main;
        var data = main ? main.GetUniversalAdditionalCameraData() : null;
        if (data && !postWas) data.renderPostProcessing = false;
        postWas = true;
    }

    public static void DrawMenu()
    {
        ImGui.Separator();
        bool v = On;
        if (ImGui.Checkbox("Tilt-shift (the miniature look: a sharp band in the middle, blurred top and bottom)", ref v)) Toggle(v);
        float f = focus.Value;
        if (ImGui.SliderFloat("Sharp band##ts", ref f, 0.1f, 0.8f, "%.2f")) focus.Value = f;
        TiltView.Reset(focus, "tsfocus");
        float s = strength.Value;
        if (ImGui.SliderFloat("Blur##ts", ref s, 1f, 5f, "%.0f")) strength.Value = Mathf.Round(s);
        TiltView.Reset(strength, "tsblur");
        float c = saturation.Value;
        if (ImGui.SliderFloat("Colour##ts", ref c, -100f, 100f, "%.0f")) saturation.Value = c;
        TiltView.Reset(saturation, "tscolour");
    }
}

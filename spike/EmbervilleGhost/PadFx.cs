using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EmbervilleGhost;

/// <summary>
/// The pads' effects: a slowly turning inner ring, motes of light drifting up out of the pad, a soft coloured light
/// on the ground around it (the game's own 2D lighting, so it glows in the dark dungeons), and a burst when it takes
/// you somewhere. All made in code from the pad's own ring sprite: no game assets needed.
/// </summary>
internal static class PadFx
{
    static Sprite dot;
    static Material unlit;

    public static void Add(GameObject pad, Sprite ring, Color color, int sortingLayer)
    {
        try
        {
            // inner ring, turning the other way
            var inner = new GameObject("InnerRing");
            inner.transform.SetParent(pad.transform, false);
            inner.transform.localScale = Vector3.one * 1.25f;
            var isr = inner.AddComponent<SpriteRenderer>();
            isr.sprite = ring;
            isr.color = new Color(color.r, color.g, color.b, 0.55f);
            isr.sortingLayerID = sortingLayer;
            isr.sortingOrder = -499;
            inner.AddComponent<Spin>().DegreesPerSecond = -40f;

            Burn(pad, color, sortingLayer);
            Motes(pad, color, sortingLayer);
            Glow(pad, color);
        }
        catch (System.Exception e) { NetLog.Warn($"Pad effects: {e.Message}", toast: false); }
    }

    // ---- the game's ground-burst flipbook (scorched_Ground_0..21: a glowing swirl spreading over the ground) ----

    static Sprite[] burnFrames;

    static Sprite[] BurnFrames()
    {
        if (burnFrames != null && burnFrames.Length > 0 && burnFrames[0]) return burnFrames;
        burnFrames = Resources.FindObjectsOfTypeAll<Sprite>()
            .Where(x => x && x.name.StartsWith("scorched_Ground_") && int.TryParse(x.name.Substring(16), out _))
            .GroupBy(x => x.name).Select(g => g.First())
            .OrderBy(x => int.Parse(x.name.Substring(16))).ToArray();
        if (burnFrames.Length == 0) NetLog.Info("Pad effects: the game's scorched_Ground frames aren't loaded here", toast: false);
        return burnFrames;
    }

    static void Burn(GameObject pad, Color color, int sortingLayer)
    {
        var frames = BurnFrames();
        if (frames.Length == 0) return;
        var go = new GameObject("GroundBurst");
        go.transform.SetParent(pad.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = frames[0];
        sr.color = Color.Lerp(Color.white, color, 0.45f);   // the art is cyan: the pad's colour shows through
        sr.sortingLayerID = sortingLayer;
        sr.sortingOrder = -498;
        float w = Mathf.Max(0.01f, frames.Max(f => f.bounds.size.x));
        go.transform.localScale = Vector3.one * (3.6f / w);   // about the ring's width
        var fb = go.AddComponent<Flipbook>();
        fb.Frames = frames;
    }

    /// <summary>Plays the frames over and over (a short rest between), offset per pad so they don't pulse together.</summary>
    sealed class Flipbook : MonoBehaviour
    {
        public Sprite[] Frames;
        public float Fps = 14f, Rest = 0.4f;
        SpriteRenderer sr;
        float t;
        void Start() { sr = GetComponent<SpriteRenderer>(); t = Random.value * 2f; }
        void Update()
        {
            if (!sr || Frames == null || Frames.Length == 0) return;
            t += Time.deltaTime;
            float length = Frames.Length / Fps + Rest;
            float at = t % length;
            int i = Mathf.FloorToInt(at * Fps);
            sr.enabled = i < Frames.Length;
            if (sr.enabled) sr.sprite = Frames[i];
        }
    }

    /// <summary>A puff of light when the pad sends you off.</summary>
    public static void Burst(GameObject pad)
    {
        var ps = pad ? pad.GetComponentInChildren<ParticleSystem>() : null;
        if (ps) ps.Emit(45);
    }

    static void Motes(GameObject pad, Color color, int sortingLayer)
    {
        var go = new GameObject("Motes");
        go.transform.SetParent(pad.transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.duration = 2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.32f);
        main.startColor = new ParticleSystem.MinMaxGradient(color, Color.Lerp(color, Color.white, 0.6f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.08f;   // drifting up (the camera looks down on the 2D world: up the screen)
        main.maxParticles = 120;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        var emission = ps.emission;
        emission.rateOverTime = 26f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.85f;
        shape.radiusThickness = 0.3f;
        shape.rotation = Vector3.zero;

        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
        vel.y = new ParticleSystem.MinMaxCurve(0.35f, 0.8f);
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.8f, 0.6f), new GradientAlphaKey(0f, 1f) });
        fade.color = g;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.3f));

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sharedMaterial = Material();
        r.sortingLayerID = sortingLayer;
        r.sortingOrder = 400;   // over the ground and most things standing on it
        ps.Play();
    }

    /// <summary>The game's 2D lighting: a soft point light in the pad's colour.</summary>
    static void Glow(GameObject pad, Color color)
    {
        var go = new GameObject("Light");
        go.transform.SetParent(pad.transform, false);
        var light = go.AddComponent<Light2D>();
        light.lightType = Light2D.LightType.Point;
        light.color = color;
        light.intensity = 1.1f;
        light.pointLightInnerRadius = 0.3f;
        light.pointLightOuterRadius = 3f;
        light.falloffIntensity = 0.6f;
        go.AddComponent<Flicker>().Base = 1.1f;
    }

    /// <summary>A soft round dot, unlit (it's light itself), additive.</summary>
    static Material Material()
    {
        if (unlit) return unlit;
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
        unlit = new Material(shader) { name = "EG_PadMotes" };
        unlit.mainTexture = Dot().texture;
        return unlit;
    }

    static Sprite Dot()
    {
        if (dot) return dot;
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * a * 255));
            }
        tex.SetPixels32(px);
        tex.Apply();
        dot = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return dot;
    }

    sealed class Spin : MonoBehaviour
    {
        public float DegreesPerSecond;
        void Update() => transform.Rotate(0f, 0f, DegreesPerSecond * Time.deltaTime);
    }

    sealed class Flicker : MonoBehaviour
    {
        public float Base = 1f;
        Light2D light;
        void Start() => light = GetComponent<Light2D>();
        void Update() { if (light) light.intensity = Base * (0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 1.7f, 0.37f)); }
    }
}

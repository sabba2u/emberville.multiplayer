using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BetterWeather;

/// <summary>
/// Lightning: now and then (Plugin.lightningEvery, more or less) a bolt from the top of the screen to a spot on the
/// ground in view (not on top of the player), drawn as a jagged pixel line that flickers, a burst of light where it
/// lands (a 2D point light and a bright ring), and the whole world flashing (Sky.Flash). Just to look at: it hurts
/// nobody. The game's own lightning (the acolyte sorcerer's attack) is an enemy attack, loaded only with that enemy
/// and doing damage, so this one is our own. Each game picks its own strikes and tells the others here (the
/// multiplayer mod); theirs show here when they're in view.
/// </summary>
internal static class Lightning
{
    static float next = -1f;
    static Material mat;
    static readonly List<Bolt> bolts = new();

    sealed class Bolt { public GameObject Go; public LineRenderer Core, Glow; public SpriteRenderer Ring; public Light2D Light; public float Born; }

    public static void Tick(float strength)
    {
        float every = Weather.LightningEvery;
        if (strength >= 0.8f && every > 0f)
        {
            if (next < 0f) next = Time.time + Random.Range(every * 0.4f, every * 1.2f);
            if (Time.time >= next) { next = Time.time + Random.Range(every * 0.5f, every * 1.5f); StrikeSomewhere(); }
        }
        else next = -1f;
        Animate();
    }

    /// <summary>A strike at a random spot in view (away from the player).</summary>
    public static void StrikeSomewhere()
    {
        var cam = Camera.main;
        if (!cam) return;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        for (int tries = 0; tries < 10; tries++)
        {
            var at = new Vector3(c.x + Random.Range(-w * 0.85f, w * 0.85f), c.y + Random.Range(-h * 0.75f, h * 0.6f), 0f);
            if (me && ((Vector2)(at - me.transform.position)).magnitude < 3f) continue;
            Strike(at, local: true);
            return;
        }
    }

    public static void Strike(Vector3 at, bool local)
    {
        var cam = Camera.main;
        if (!cam || Weather.Here <= 0f && !local) return;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var c = cam.transform.position;
        bool inView = Mathf.Abs(at.x - c.x) < w + 1f && Mathf.Abs(at.y - c.y) < h + 1f;
        if (local) Hooks.SendStrike(at);
        if (!inView) return;
        try { Draw(at, c.y + h + 1f); }
        catch (System.Exception e) { Log.Warn($"Better Weather: lightning: {e.Message}"); }
        Sky.Flash(1f);
    }

    static void Draw(Vector3 at, float top)
    {
        if (!mat)
        {
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "BW_Lightning" };
        }
        var go = new GameObject("BW_Lightning");
        go.transform.position = at;
        // a jagged path down from above the screen, snapped to the pixel grid
        var pts = new List<Vector3>();
        float y = top, x = at.x + Random.Range(-1.5f, 1.5f);
        while (y > at.y)
        {
            pts.Add(new Vector3(Snap(x), Snap(y), 0f));
            y -= Random.Range(0.35f, 0.9f);
            x = Mathf.Lerp(x, at.x, 0.25f) + Random.Range(-0.45f, 0.45f);
        }
        pts.Add(at);
        int layer = TopLayer();
        var bolt = new Bolt { Go = go, Born = Time.time };
        bolt.Glow = Line(go, "Glow", pts, 5f / Rain.Ppu, new Color(0.55f, 0.7f, 1f, 0.55f), layer, 31996);
        bolt.Core = Line(go, "Core", pts, 2f / Rain.Ppu, new Color(1f, 1f, 0.95f, 1f), layer, 31997);
        var ring = new GameObject("Ring");
        ring.transform.SetParent(go.transform, false);
        bolt.Ring = ring.AddComponent<SpriteRenderer>();
        bolt.Ring.sprite = Rain.Pixels("BW_Strike", new[] { "..#######..", ".#.......#.", "#.........#", ".#.......#.", "..#######.." }, new Vector2(0.5f, 0.5f));
        bolt.Ring.sharedMaterial = mat;
        bolt.Ring.color = new Color(1f, 0.95f, 0.75f, 1f);
        bolt.Ring.sortingLayerID = layer; bolt.Ring.sortingOrder = 31995;
        // the ground lit where it lands
        var lgo = new GameObject("Light");
        lgo.transform.SetParent(go.transform, false);
        lgo.SetActive(false);
        bolt.Light = lgo.AddComponent<Light2D>();
        bolt.Light.lightType = Light2D.LightType.Point;
        bolt.Light.pointLightOuterRadius = 6f;
        bolt.Light.pointLightInnerRadius = 0.5f;
        bolt.Light.color = new Color(0.85f, 0.9f, 1f);
        bolt.Light.intensity = 3f;
        lgo.SetActive(true);
        bolts.Add(bolt);
    }

    static void Animate()
    {
        for (int i = bolts.Count - 1; i >= 0; i--)
        {
            var b = bolts[i];
            float age = Time.time - b.Born;
            if (!b.Go || age > 0.9f) { if (b.Go) Object.Destroy(b.Go); bolts.RemoveAt(i); continue; }
            // on, off, on (the flicker), then gone; the ring and the light fade a little longer
            bool lit = age < 0.07f || (age > 0.11f && age < 0.24f);
            b.Core.enabled = b.Glow.enabled = lit;
            float fade = 1f - Mathf.Clamp01(age / 0.9f);
            b.Ring.color = new Color(1f, 0.95f, 0.75f, fade);
            b.Ring.transform.localScale = Vector3.one * (1f + age * 1.5f);
            b.Light.intensity = 3f * fade * fade;
        }
    }

    static LineRenderer Line(GameObject parent, string name, List<Vector3> pts, float width, Color colour, int layer, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
        lr.startWidth = lr.endWidth = width;
        lr.numCornerVertices = 0;
        lr.sharedMaterial = mat;
        lr.startColor = lr.endColor = colour;
        lr.sortingLayerID = layer;
        lr.sortingOrder = order;
        return lr;
    }

    static float Snap(float v) => Mathf.Round(v * Rain.Ppu) / Rain.Ppu;   // (on the game's pixel grid)

    static int TopLayer()
    {
        int best = 0, bestValue = int.MinValue;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r || !r.enabled || !(r is SpriteRenderer || r is UnityEngine.Tilemaps.TilemapRenderer) || r.name.StartsWith("BW_")) continue;
            int v = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
            if (v > bestValue) { bestValue = v; best = r.sortingLayerID; }
        }
        return best;
    }
}

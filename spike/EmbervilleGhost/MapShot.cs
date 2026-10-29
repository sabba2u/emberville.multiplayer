using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EmbervilleGhost;

/// <summary>
/// /mapshot: the whole area you're in (its camera bounds: a dungeon floor, the arena, a map's area) as one PNG, at the
/// game's own 32 pixels a unit (less if it would pass 8192 across), everything that's drawn in it: floors, walls, set
/// dressing, props, whoever stands there. Shot in squares by a camera of our own, the area's darkness lifted (its
/// global lights at full) and its fog hidden while it shoots; all put back after. Saved to BepInEx/mapshots.
/// </summary>
internal static class MapShot
{
    const int Tile = 1024, Most = 8192;
    static bool busy;

    public static void Command(string arg)
    {
        if (busy) { Chat.System("A map shot is already being taken."); return; }
        float ppu = float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && p >= 4f && p <= 64f ? p : 32f;
        Take(ppu).Forget();
    }

    static async UniTaskVoid Take(float ppu)
    {
        var gm = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;
        var area = gm ? gm.CurrentArea : null;
        var main = Camera.main;
        var confiner = area ? area.CameraConfiner : null;
        if (!main || !confiner) { Chat.System("Nothing to shoot here (no area with camera bounds)."); return; }
        busy = true;
        var b = confiner.bounds;
        ppu = Mathf.Min(ppu, Most / Mathf.Max(b.size.x, b.size.y));
        int w = Mathf.CeilToInt(b.size.x * ppu), h = Mathf.CeilToInt(b.size.y * ppu);
        float tileUnits = Tile / ppu;
        Chat.System($"Taking a map shot: {w} x {h} pixels ({ppu:0.#} a unit)...");

        var lights = new List<(Light2D l, float i)>();
        var hidden = new List<Renderer>();
        GameObject camGo = null;
        RenderTexture rt = null;
        Texture2D outTex = null, tile = null;
        try
        {
            // the darkness lifted, the fog hidden (put back in finally)
            foreach (var l in Object.FindObjectsByType<Light2D>(FindObjectsSortMode.None))
                if (l && l.lightType == Light2D.LightType.Global) { lights.Add((l, l.intensity)); l.intensity = Mathf.Max(l.intensity, 1f); }
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                if (r && r.enabled && r.name.ToLowerInvariant().Contains("fog")) { hidden.Add(r); r.enabled = false; }

            camGo = new GameObject("EG_MapShotCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.CopyFrom(main);
            // (the game's camera has its view set by hand (its pixel-perfect rig): copied, ours never moved; back to
            // following its own transform and size)
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            cam.ResetAspect();
            cam.orthographic = true;
            cam.orthographicSize = tileUnits / 2f;
            cam.aspect = 1f;
            cam.enabled = false;
            rt = new RenderTexture(Tile, Tile, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tile = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false);
            float z = main.transform.position.z;

            for (int ty = 0; ty * Tile < h; ty++)
                for (int tx = 0; tx * Tile < w; tx++)
                {
                    cam.transform.position = new Vector3(b.min.x + (tx + 0.5f) * tileUnits, b.min.y + (ty + 0.5f) * tileUnits, z);
                    cam.ResetWorldToCameraMatrix();   // (from where it now stands)
                    cam.Render();
                    var was = RenderTexture.active;
                    RenderTexture.active = rt;
                    tile.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0);
                    tile.Apply();
                    RenderTexture.active = was;
                    int cw = Mathf.Min(Tile, w - tx * Tile), ch = Mathf.Min(Tile, h - ty * Tile);
                    outTex.SetPixels(tx * Tile, ty * Tile, cw, ch, tile.GetPixels(0, 0, cw, ch));
                    await UniTask.Yield();   // (a square a frame: the game keeps going)
                }
            outTex.Apply();

            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "mapshots");
            Directory.CreateDirectory(dir);
            string what = Clean(LocalPlayer.Scene) + "_" + Clean(area.name);
            string path = Path.Combine(dir, $"{what}_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Chat.System($"Map shot saved: {path}");
            NetLog.Info($"Map shot: {path} ({w} x {h}, bounds {b.min.x:0.0},{b.min.y:0.0} to {b.max.x:0.0},{b.max.y:0.0})", toast: false);
        }
        catch (System.Exception e) { Chat.System($"The map shot failed: {e.Message}"); NetLog.Warn($"Map shot: {e}", toast: false); }
        finally
        {
            foreach (var (l, i) in lights) if (l) l.intensity = i;
            foreach (var r in hidden) if (r) r.enabled = true;
            if (camGo) Object.Destroy(camGo);
            if (rt) { rt.Release(); Object.Destroy(rt); }
            if (tile) Object.Destroy(tile);
            if (outTex) Object.Destroy(outTex);
            busy = false;
        }
    }

    static string Clean(string s) => new string((s ?? "area").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

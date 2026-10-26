using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace EmbervilleGhost;

/// <summary>
/// Diagnostics: after going back to the main menu, the character select's previews draw as black silhouettes (unlit).
/// A few seconds after each main menu load this logs the 2D lights there and how the previews are drawn, so the first
/// menu (fine) and a later one (black) can be compared.
/// </summary>
internal static class MenuLightWatch
{
    static bool hooked;
    static float at = -1f;
    static int menus;

    public static void Tick()
    {
        if (!hooked)
        {
            hooked = true;
            SceneManager.sceneLoaded += (s, _) => { if (s.name == "MainMenu") { menus++; at = Time.unscaledTime + 4f; } };
        }
        if (at < 0f || Time.unscaledTime < at) return;
        at = Time.unscaledTime + 4f;   // (again while the select screen isn't up yet)
        var previews = Object.FindObjectsByType<PreviewHandler>(FindObjectsSortMode.None);
        if (previews.Length == 0) return;
        at = -1f;
        Dump(previews);
    }

    static void Dump(PreviewHandler[] previews)
    {
        NetLog.Info($"Menu lights (main menu #{menus}):", toast: false);
        foreach (var l in Resources.FindObjectsOfTypeAll<Light2D>().Where(l => l.gameObject.scene.IsValid()))
            NetLog.Info($"  light {Path(l.transform)} [{l.gameObject.scene.name}] on {l.isActiveAndEnabled} {l.lightType} " +
                $"#{ColorUtility.ToHtmlStringRGBA(l.color)} x{l.intensity:0.##} layers {string.Join(",", l.targetSortingLayers.Select(SortingLayer.IDToName))}", toast: false);
        foreach (var p in previews.Take(3))
        {
            var r = p.GetComponentsInChildren<SpriteRenderer>().FirstOrDefault(x => x.enabled && x.sprite);
            NetLog.Info($"  preview {Path(p.transform)} at {p.transform.position} on {p.isActiveAndEnabled}: " +
                (r ? $"{r.name} layer {SortingLayer.IDToName(r.sortingLayerID)} mat {(r.sharedMaterial ? r.sharedMaterial.name + "/" + r.sharedMaterial.shader.name : "-")} " +
                     $"#{ColorUtility.ToHtmlStringRGBA(r.color)}" : "no sprite shown"), toast: false);
        }
        foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            NetLog.Info($"  camera {Path(c.transform)} on {c.isActiveAndEnabled} at {c.transform.position} mask {c.cullingMask:X} " +
                $"target {(c.targetTexture ? c.targetTexture.name : "screen")}", toast: false);
    }

    static string Path(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}

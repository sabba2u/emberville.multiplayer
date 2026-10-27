using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// The game camera's pixel-perfect component, shared by the effects that need it changed (one owner: they'd undo each
/// other): zoom wants it off (it would hold the zoom); retro wants chunky pixels (a small render size, the same view:
/// its pixels-per-unit scaled down with it). Each frame it's set from what's asked; nothing asked: back as the game had it.
/// </summary>
internal static class PixelPerfect
{
    public static bool WantOff, WantRetro;
    static Behaviour ppc;
    static Camera of;
    static readonly Dictionary<string, object> was = new();
    static bool wasEnabled, changed;
    const int RetroHeight = 180;

    public static Behaviour Find(Camera cam) => cam ? cam.GetComponents<Behaviour>().FirstOrDefault(b => b && b.GetType().Name == "PixelPerfectCamera") : null;

    /// <summary>On now (after this frame's asks)?</summary>
    public static bool Enabled => ppc && ppc.enabled;

    /// <summary>Every frame (Plugin.Update).</summary>
    public static void Tick()
    {
        var cam = Camera.main;
        if (cam != of) { if (changed) Put(); ppc = Find(cam); of = cam; }   // (a new scene: a new camera)
        if (!ppc) return;
        bool want = WantOff || WantRetro;
        if (want && !changed) { wasEnabled = ppc.enabled; changed = true; was.Clear(); }
        if (!want) { if (changed) Put(); return; }
        if (WantRetro && !was.ContainsKey("refResolutionY"))
        {
            int y = Get<int>("refResolutionY"), x = Get<int>("refResolutionX"), ppu = Get<int>("assetsPPU");
            if (y > 0 && ppu > 0)
            {
                float k = (float)RetroHeight / y;
                Set("refResolutionX", Mathf.Max(1, Mathf.RoundToInt(x * k)));
                Set("refResolutionY", RetroHeight);
                Set("assetsPPU", Mathf.Max(1, Mathf.RoundToInt(ppu * k)));   // (the same view, fewer pixels)
                var snap = ppc.GetType().GetProperty("gridSnapping");
                if (snap != null && snap.PropertyType.IsEnum) { was["gridSnapping"] = snap.GetValue(ppc); snap.SetValue(ppc, System.Enum.Parse(snap.PropertyType, "UpscaleRenderTexture")); }
                else Set("upscaleRT", true);
            }
        }
        else if (!WantRetro && was.Count > 0) PutFields();
        ppc.enabled = !WantOff && (WantRetro || wasEnabled);
    }

    static T Get<T>(string name) { var p = ppc.GetType().GetProperty(name); return p != null && p.GetValue(ppc) is T v ? v : default; }

    static void Set(string name, object value)
    {
        var p = ppc.GetType().GetProperty(name);
        if (p == null || !p.CanWrite) return;
        if (!was.ContainsKey(name)) was[name] = p.GetValue(ppc);
        p.SetValue(ppc, value);
    }

    static void PutFields()
    {
        foreach (var kv in was)
        {
            var p = ppc ? ppc.GetType().GetProperty(kv.Key) : null;
            if (p != null && p.CanWrite) p.SetValue(ppc, kv.Value);
        }
        was.Clear();
    }

    static void Put()
    {
        if (ppc) { PutFields(); ppc.enabled = wasEnabled; }
        was.Clear();
        changed = false;
    }
}

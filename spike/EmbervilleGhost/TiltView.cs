using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// A tilted 2.5D view (Insert > Tilted view, or /tilt): the world seen from behind and above at an angle, in perspective,
/// the floor receding, everything that stands (people, creatures, props, trees, labels) stood up facing the camera like a
/// pop-up book. Only the picture changes: just before the game's camera draws a frame it's turned and the standing things
/// are stood up; straight after, all of it is put back, so the game (Cinemachine, physics, AI, its own maths) never sees
/// it. The aim follows the picture (a ray from the tilted camera through the mouse, onto the ground). The game's
/// pixel-perfect camera (it only works flat) is off while it's on. A test: tiles stay flat (walls too).
/// </summary>
[HarmonyPatch]
internal static class TiltView
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> pitch, fov;
    static bool hooked;

    // what the frame changed, put back after it
    struct CamState { public Vector3 Pos; public Quaternion Rot; public bool Ortho; public float Fov, Near, Far; }
    static CamState saved;
    static readonly List<(Transform t, Quaternion rot)> stood = new();
    static readonly List<Transform> roots = new();
    static float nextScan;
    static Behaviour pixelPerfect;   // (URP's PixelPerfectCamera: found by name, its namespace moved between URP versions)
    static bool pixelPerfectWas;

    public static bool On => on != null && on.Value;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("View", "Tilted", false, "A tilted 2.5D view (the world from behind and above, things standing up); only the picture changes");
        pitch = config.Bind("View", "TiltAngle", 50f, "How far the camera leans back from looking straight down, in degrees (20-75)");
        fov = config.Bind("View", "TiltFieldOfView", 40f, "The tilted camera's field of view, in degrees");
    }

    public static void Toggle(bool value)
    {
        on.Value = value;
        if (!value) RestorePixelPerfect();
        NetLog.Info(value ? "Tilted view on (/tilt again: off)" : "Tilted view off", toast: true);
    }

    public static void Tick()
    {
        if (on == null) return;
        if (!hooked)
        {
            hooked = true;
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }
        if (!On) return;
        var cam = Camera.main;
        if (cam && !pixelPerfect)
        {
            pixelPerfect = cam.GetComponents<Behaviour>().FirstOrDefault(b => b && b.GetType().Name == "PixelPerfectCamera");
            if (pixelPerfect) { pixelPerfectWas = pixelPerfect.enabled; pixelPerfect.enabled = false; }
        }
        if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; Scan(); }
    }

    static void RestorePixelPerfect()
    {
        if (pixelPerfect) pixelPerfect.enabled = pixelPerfectWas;
        pixelPerfect = null;
    }

    // ---- what stands up ----

    /// <summary>The things to stand up: each one turned as a whole about its own feet, so its parts keep their places
    /// (a tree's leaves over its trunk). The root of a thing: the nearest one up its parents with a body or a game part
    /// (a Rigidbody2D, a creature, the player, a breakable) or an "Art" child; else the sprite itself. World labels too.</summary>
    static void Scan()
    {
        roots.Clear();
        var seen = new HashSet<Transform>();
        foreach (var sr in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
        {
            if (!sr || !sr.enabled || !sr.gameObject.activeInHierarchy) continue;
            var root = RootOf(sr.transform);
            if (root && seen.Add(root)) roots.Add(root);
        }
        foreach (var t in Object.FindObjectsByType<TMPro.TextMeshPro>(FindObjectsSortMode.None))
            if (t && t.gameObject.activeInHierarchy && seen.Add(t.transform)) roots.Add(t.transform);
        // (a root inside another root turns with it: only the outermost)
        roots.RemoveAll(r => { for (var p = r.parent; p; p = p.parent) if (seen.Contains(p)) return true; return false; });
    }

    static Transform RootOf(Transform t)
    {
        var at = t;
        for (int i = 0; at && i < 8; i++, at = at.parent)
        {
            if (at.GetComponent<Rigidbody2D>() || at.GetComponent<Enemy>() || at.GetComponent<PlayerMaster>() || at.GetComponent<DamageableObject>() || at.Find("Art"))
                return at;
            if (at.GetComponent<Canvas>()) return null;   // (UI)
        }
        return t;
    }

    // ---- the frame ----

    static Quaternion Look => Quaternion.LookRotation(Forward, Up);
    static float P => Mathf.Clamp(pitch.Value, 20f, 75f);
    static Vector3 Forward => new Vector3(0f, Mathf.Sin(P * Mathf.Deg2Rad), Mathf.Cos(P * Mathf.Deg2Rad));
    static Vector3 Up => new Vector3(0f, Mathf.Cos(P * Mathf.Deg2Rad), -Mathf.Sin(P * Mathf.Deg2Rad));

    /// <summary>The tilted camera for the game camera as it is now: looking at the same middle, as much ground in view.</summary>
    static (Vector3 pos, Quaternion rot) Pose(Camera cam)
    {
        var mid = cam.transform.position; mid.z = 0f;
        float half = Mathf.Max(0.5f, cam.orthographicSize);
        float d = half / Mathf.Tan(Mathf.Clamp(fov.Value, 15f, 90f) * 0.5f * Mathf.Deg2Rad);
        return (mid - Forward * d, Look);
    }

    static void Begin(ScriptableRenderContext ctx, Camera cam)
    {
        if (!On || cam != Camera.main || !cam.orthographic) return;
        saved = new CamState { Pos = cam.transform.position, Rot = cam.transform.rotation, Ortho = true, Fov = cam.fieldOfView, Near = cam.nearClipPlane, Far = cam.farClipPlane };
        var (pos, rot) = Pose(cam);
        cam.orthographic = false;
        cam.fieldOfView = Mathf.Clamp(fov.Value, 15f, 90f);
        cam.nearClipPlane = 0.1f; cam.farClipPlane = 2000f;
        cam.transform.SetPositionAndRotation(pos, rot);
        // stood up: turned back by the tilt about its own feet (x axis), facing the camera
        var stand = Quaternion.AngleAxis(-P, Vector3.right);
        stood.Clear();
        foreach (var r in roots)
        {
            if (!r) continue;
            stood.Add((r, r.rotation));
            r.rotation = stand * r.rotation;
        }
    }

    static void End(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != Camera.main || !saved.Ortho) return;
        foreach (var (t, rot) in stood) if (t) t.rotation = rot;
        stood.Clear();
        cam.orthographic = true;
        cam.fieldOfView = saved.Fov;
        cam.nearClipPlane = saved.Near; cam.farClipPlane = saved.Far;
        cam.transform.SetPositionAndRotation(saved.Pos, saved.Rot);
        saved.Ortho = false;
    }

    // ---- the mouse, on the ground in the tilted picture ----

    /// <summary>Where the mouse points on the ground (z = 0), in the tilted picture. False when the view is off.</summary>
    public static bool MouseWorld(out Vector3 at)
    {
        at = default;
        var cam = Camera.main;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (!On || !cam || mouse == null) return false;
        var (pos, rot) = Pose(cam);
        var mp = mouse.position.ReadValue();
        float tanY = Mathf.Tan(Mathf.Clamp(fov.Value, 15f, 90f) * 0.5f * Mathf.Deg2Rad), tanX = tanY * Screen.width / Mathf.Max(1f, Screen.height);
        var dir = rot * new Vector3((mp.x / Screen.width * 2f - 1f) * tanX, (mp.y / Screen.height * 2f - 1f) * tanY, 1f);
        if (Mathf.Abs(dir.z) < 1e-5f) return false;
        float k = -pos.z / dir.z;
        if (k <= 0f) return false;
        at = pos + dir * k;
        at.z = 0f;
        return true;
    }

    /// <summary>The player's aim from the tilted picture (after the game's own, which used the flat one).</summary>
    [HarmonyPatch(typeof(PlayerMaster), "GetLookDirection"), HarmonyPostfix]
    static void Aim(PlayerMaster __instance, bool __runOriginal)
    {
        if (!On || !__runOriginal || !__instance.mouseLook || MainInput.CurrentScheme == ControllerScheme.Gamepad) return;
        if (__instance != LocalPlayer.Get() || !MouseWorld(out var at)) return;
        Vector2 d = at - __instance.transform.position;
        if (d.sqrMagnitude < 0.0001f) return;
        __instance.lookDir = d.normalized;
        __instance.lastLookDir = __instance.lookDir;
        __instance.SetPlayerDirection();
    }

    // ---- Insert menu ----

    public static void DrawMenu()
    {
        bool v = On;
        if (ImGui.Checkbox("Tilted view (2.5D: the world from behind and above, things standing up)", ref v)) Toggle(v);
        float a = pitch.Value;
        if (ImGui.SliderFloat("Tilt##tilt", ref a, 20f, 75f, "%.0f°")) pitch.Value = a;
        float f = fov.Value;
        if (ImGui.SliderFloat("Field of view##tilt", ref f, 20f, 70f, "%.0f°")) fov.Value = f;
        ImGui.TextDisabled("A test: the ground's tiles stay flat (walls too); only the picture changes, the game plays as ever.");
    }
}

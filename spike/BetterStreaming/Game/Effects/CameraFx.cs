using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterStreaming;

/// <summary>
/// The camera effects, on the game's own camera (turned, flipped, zoomed, shaken), set again just before every frame is
/// drawn and kept through it, so the labels over the world and the aim match the picture. They stack:
///  - upside down (180°), sideways (90°), dizzy (turning round, 30° a second), seasick (rocking ±6°, a little bob);
///  - mirror world (left and right swapped);
///  - zoomed way in (x0.5) / way out (x2): the game's pixel-perfect camera is off meanwhile (it would hold the zoom);
///  - earthquake (shaking all the time).
/// </summary>
internal static class CameraFx
{
    static readonly string[] Ids = { "upsidedown", "sideways", "dizzy", "seasick", "mirror", "zoomin", "zoomout", "earthquake" };
    static bool hooked;

    public static void Register()
    {
        foreach (var id in Ids) Streamer.Effects[id] = new Switch(id);
    }

    static bool On(string id) => Controls.On(id);
    static bool Any => Ids.Any(Controls.On);

    /// <summary>Every frame (Plugin.Update): hooked to the camera once; the pixel-perfect camera off while zoomed.</summary>
    public static void Tick()
    {
        if (!hooked)
        {
            hooked = true;
            RenderPipelineManager.beginCameraRendering += Begin;
            RenderPipelineManager.endCameraRendering += End;
        }
        PixelPerfect.WantOff = On("zoomin") || On("zoomout");   // (it would hold the zoom)
    }

    // The camera stays changed through the frame (not put back after the draw): the labels and name tags drawn over
    // the world, and the aim under the mouse, then match the picture. The game's camera follow sets its position, turn
    // and zoom again every frame; if nothing did (it's still what we left), we start from what it was before us.
    static Vector3 basePos, setPos;
    static Quaternion baseRot, setRot;
    static float baseSize, setSize;
    static bool applied, mirrored, viewSet;

    static void Begin(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam != Camera.main) return;
        if (!Any) { Restore(cam); return; }
        var tr = cam.transform;
        if (!applied || tr.position != setPos) basePos = tr.position;
        if (!applied || tr.rotation != setRot) baseRot = tr.rotation;
        if (!applied || !Mathf.Approximately(cam.orthographicSize, setSize)) baseSize = cam.orthographicSize;
        float t = Time.time, roll = 0f;
        if (On("upsidedown")) roll += 180f;
        if (On("sideways")) roll += 90f;
        if (On("dizzy")) roll += t * 30f % 360f;
        if (On("seasick")) roll += Mathf.Sin(t * 1.3f) * 6f;
        var p = basePos;
        if (On("seasick")) p.y += Mathf.Sin(t * 0.9f) * 0.25f;
        if (On("earthquake")) p += (Vector3)(Random.insideUnitCircle * 0.25f);
        setPos = p;
        setRot = baseRot * Quaternion.Euler(0f, 0f, roll);
        tr.SetPositionAndRotation(setPos, setRot);
        setSize = baseSize * (On("zoomin") ? 0.5f : 1f) * (On("zoomout") ? 2f : 1f);
        if (cam.orthographic) cam.orthographicSize = setSize;
        applied = true;
        // mirror: left and right swapped in the projection itself (kept for the frame: labels and aim follow it)
        // (the pixel-perfect camera sets its own view from the camera's pose: ours, turned and shaken, goes in after it)
        if (PixelPerfect.Enabled) { cam.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * tr.worldToLocalMatrix; viewSet = true; }
        cam.ResetProjectionMatrix();
        mirrored = On("mirror");
        if (mirrored) cam.projectionMatrix = cam.projectionMatrix * Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
    }

    /// <summary>All of them over: the camera as the game had it.</summary>
    static void Restore(Camera cam)
    {
        if (!applied && !mirrored) return;
        if (applied)
        {
            if (cam.transform.position == setPos) cam.transform.position = basePos;
            if (cam.transform.rotation == setRot) cam.transform.rotation = baseRot;
            if (Mathf.Approximately(cam.orthographicSize, setSize)) cam.orthographicSize = baseSize;
        }
        if (mirrored) cam.ResetProjectionMatrix();
        if (viewSet) cam.ResetWorldToCameraMatrix();
        applied = mirrored = viewSet = false;
    }

    static void End(ScriptableRenderContext ctx, Camera cam) { }
}

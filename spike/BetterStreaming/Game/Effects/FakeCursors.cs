using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Fake cursors: ten more of the game's own mouse pointer (UI_Pointer_01, at the size Windows shows the real one), each
/// wandering the screen like someone's hand on a mouse: a dash to somewhere, a stop, a little fidget, another dash.
/// Which one is yours? Drawn with Unity's GUI on top of everything (the real cursor is still above them all).
/// </summary>
internal static class FakeCursors
{
    const int Count = 10;
    static readonly Vector2[] at = new Vector2[Count], to = new Vector2[Count];
    static readonly float[] speed = new float[Count], restUntil = new float[Count];
    static Texture2D tex;
    static Vector2 hotspot;
    static bool placed;

    public static void Register() => Streamer.Effects["cursors"] = new Switch("cursors", start: _ => placed = false);

    static bool Find()
    {
        if (tex) return true;
        if (Singleton<CursorController>.HasMain && Singleton<CursorController>.main.cursorTex)
        {
            tex = Singleton<CursorController>.main.cursorTex;
            hotspot = Singleton<CursorController>.main.cursorHotspot;
            return true;
        }
        foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            if (t && t.name.StartsWith("UI_Pointer_01")) { tex = t; hotspot = Vector2.zero; return true; }
        return false;
    }

    /// <summary>From the plugin's OnGUI: move them on (once a frame) and draw them.</summary>
    public static void Draw()
    {
        if (!Controls.On("cursors") || !Find()) return;
        float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
        if (!placed)
        {
            placed = true;
            for (int i = 0; i < Count; i++) { at[i] = new Vector2(Random.Range(0f, w), Random.Range(0f, h)); NewGoal(i, w, h); }
        }
        if (Event.current.type != EventType.Repaint) return;
        float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
        for (int i = 0; i < Count; i++)
        {
            if (now >= restUntil[i])
            {
                at[i] = Vector2.MoveTowards(at[i], to[i], speed[i] * dt);
                if ((at[i] - to[i]).sqrMagnitude < 1f)
                {
                    restUntil[i] = now + (Random.value < 0.6f ? Random.Range(0.1f, 1.2f) : 0f);   // (a stop, now and then)
                    NewGoal(i, w, h);
                }
            }
            else at[i] += Random.insideUnitCircle * 40f * dt;   // (a fidget while resting)
        }
        // the real one's size: Windows shrinks a hardware cursor to the system's cursor size (32 px at 100% scaling)
        float dpi = UnityEngine.Screen.dpi > 0f ? UnityEngine.Screen.dpi : 96f;
        float size = 32f * Mathf.Max(1f, dpi / 96f), k = size / tex.width;
        GUI.depth = -1000;
        for (int i = 0; i < Count; i++)
            GUI.DrawTexture(new Rect(at[i].x - hotspot.x * k, at[i].y - hotspot.y * k, size, tex.height * k), tex);
    }

    /// <summary>Somewhere next: mostly a short hop nearby, sometimes right across the screen.</summary>
    static void NewGoal(int i, float w, float h)
    {
        var goal = Random.value < 0.3f
            ? new Vector2(Random.Range(0f, w), Random.Range(0f, h))
            : at[i] + Random.insideUnitCircle * Random.Range(60f, 300f);
        to[i] = new Vector2(Mathf.Clamp(goal.x, 0f, w - 4f), Mathf.Clamp(goal.y, 0f, h - 4f));
        speed[i] = Random.Range(250f, 1400f);
    }
}

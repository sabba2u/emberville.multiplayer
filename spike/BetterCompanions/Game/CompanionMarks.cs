using System.Linq;
using ImGui = ImGuiNET.ImGui;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// How a companion is told apart from the creatures: a teal ring on the ground under it (like Better Combat's lock-on
/// ring, but steady), and over its head its name in a dark pill (as the pets' name tags) with a health bar under it
/// (as the summoned chickens' bars). Downed: the name in red, "hold F", and the bar shows the revive.
/// The tag is ImGui, drawn by the multiplayer mod's HUD (Hooks.DrawsHud); on our own, CompanionLabel draws it instead.
/// The multiplayer mod draws teammates' companions with Tag too, so they all look alike.
/// </summary>
public static class CompanionMarks
{
    /// <summary>One companion's tag: name and bar over the head at feet + 2.3.</summary>
    public static void Tag(Camera cam, Vector3 feet, string name, float health01, bool down, float revive01)
    {
        var s = cam.WorldToScreenPoint(feet + Vector3.up * 2.3f);
        if (s.z < 0) return;
        float x = s.x, y = Screen.height - s.y;
        var dl = ImGui.GetBackgroundDrawList();
        string text = down ? $"{name}: hold F" : name;
        var size = ImGui.CalcTextSize(text);
        float w = Mathf.Max(40f, size.x), by = y + 2f;
        float ty = by - size.y - 3f;
        dl.AddRectFilled(new ImGuiNET.Vector2(x - size.x / 2 - 4, ty - 1), new ImGuiNET.Vector2(x + size.x / 2 + 4, ty + size.y + 1), Col(0f, 0f, 0f, 0.5f), 3f);
        dl.AddText(new ImGuiNET.Vector2(x - size.x / 2, ty), down ? Col(1f, 0.48f, 0.48f, 1f) : Col(0.55f, 0.9f, 0.95f, 1f), text);
        float fill = Mathf.Clamp01(down ? revive01 : health01);
        dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, by), new ImGuiNET.Vector2(x + w / 2, by + 4), Col(0f, 0f, 0f, 0.6f));
        dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, by), new ImGuiNET.Vector2(x - w / 2 + w * fill, by + 4),
            down ? Col(1f, 0.85f, 0.3f, 1f) : Col(0.45f, 0.88f, 0.5f, 1f));
    }

    static uint Col(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(r, g, b, a));

    // ---- the spot it holds ----

    static GameObject post;
    static Sprite postSprite;

    /// <summary>A gold ring on the ground where it's holding (Shift with an order); null takes it away.</summary>
    public static void Post(Vector3? at)
    {
        if (at == null) { if (post) Object.Destroy(post); post = null; return; }
        if (!post)
        {
            if (!ringSprite) BuildRing();
            if (!postSprite) postSprite = RingSprite(new Color32(255, 214, 110, 230), 2.5f);
            post = new GameObject("BC_Post");
            var sr = post.AddComponent<SpriteRenderer>();
            sr.sprite = postSprite;
            sr.sharedMaterial = ringMat;
            // on the ground, under whoever stands on it
            var me = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player ? Singleton<GameManager>.main.player : null;
            var group = me ? me.GetComponentInChildren<UnityEngine.Rendering.SortingGroup>(true) : null;
            if (group) { sr.sortingLayerID = group.sortingLayerID; sr.sortingOrder = group.sortingOrder - 1; }
            post.transform.localScale = new Vector3(1.2f, 0.6f, 1f);
        }
        post.transform.position = at.Value;
    }

    static Sprite RingSprite(Color32 colour, float thickness)
    {
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f, r = Mathf.Sqrt(dx * dx + dy * dy);
                bool rim = r <= n / 2f - 0.5f && r >= n / 2f - 0.5f - thickness;
                bool dot = r <= 2.5f;   // (a dot in the middle: a post, not the ring it walks with)
                px[y * n + x] = rim || dot ? colour : new Color32(0, 0, 0, 0);
            }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
    }

    // ---- the ring ----

    static Sprite ringSprite;
    static Material ringMat;

    /// <summary>A teal ring under this body, on the ground (made once per body; it moves with it).</summary>
    public static GameObject Ring(Transform under)
    {
        if (!ringSprite) BuildRing();
        var go = new GameObject("BC_Ring");
        // inside the body's sorting group when it has one (the player's Graphics): the game sorts the group as one picture,
        // so the ring is in front of or behind things exactly as the body is (alone, stairs sorted lower hid it)
        var group = under.GetComponentInChildren<UnityEngine.Rendering.SortingGroup>(true);
        var parent = group ? group.transform : under;
        go.transform.SetParent(parent, false);
        go.transform.position = under.position;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ringSprite;
        sr.sharedMaterial = ringMat;
        // on the body's own layer, just under its lowest picture: over the floor, under the body itself
        var body = parent.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r && r != sr && r.enabled).ToList();
        if (body.Count > 0)
        {
            int layer = body.GroupBy(r => r.sortingLayerID).OrderByDescending(gp => gp.Count()).First().Key;
            sr.sortingLayerID = layer;
            sr.sortingOrder = body.Where(r => r.sortingLayerID == layer).Min(r => r.sortingOrder) - 1;
        }
        else
        {
            sr.sortingLayerID = SortingLayer.NameToID("Default");
            sr.sortingOrder = short.MaxValue - 1;
        }
        float sx = 1.1f / Mathf.Max(0.01f, Mathf.Abs(parent.lossyScale.x)), sy = 1.1f / Mathf.Max(0.01f, Mathf.Abs(parent.lossyScale.y));
        go.transform.localScale = new Vector3(sx, sy * 0.5f, 1f);
        return go;
    }

    static void BuildRing()
    {
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f, r = Mathf.Sqrt(dx * dx + dy * dy);
                px[y * n + x] = r <= n / 2f - 0.5f && r >= n / 2f - 3f ? new Color32(80, 215, 230, 210) : new Color32(0, 0, 0, 0);
            }
        tex.SetPixels32(px);
        tex.Apply();
        ringSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        ringMat = new Material(shader) { name = "BC_Ring" };
    }
}

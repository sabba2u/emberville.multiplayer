using System.Linq;
using UnityEngine;

namespace BetterClasses;

/// <summary>Bits of show: the game's dust puff (VFX_Animations_Dust), a plain unlit material, drawing above someone.</summary>
internal static class Fx
{
    static Material material;
    static Sprite[] dust;

    /// <summary>Unlit, for lines and particles (plain colour squares: pixel-art enough).</summary>
    public static Material Material
    {
        get
        {
            if (!material)
            {
                var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
                if (shader) material = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            return material;
        }
    }

    /// <summary>The sprite drawn for that character (for drawing above it).</summary>
    public static SpriteRenderer Look(Component who) =>
        who ? who.GetComponentsInChildren<SpriteRenderer>().Where(r => r.sprite).OrderByDescending(r => r.sortingOrder).FirstOrDefault() : null;

    /// <summary>Drawn above that character. A character drawn as a sorting group (the player) sorts as one piece
    /// against the world, so it's the group's layer and order that count, not its sprites' own.</summary>
    public static void Above(Renderer r, Component who, int by = 20)
    {
        var group = who ? who.GetComponentInChildren<UnityEngine.Rendering.SortingGroup>() : null;
        if (group) { r.sortingLayerID = group.sortingLayerID; r.sortingOrder = group.sortingOrder + by; return; }
        var like = Look(who);
        if (like) { r.sortingLayerID = like.sortingLayerID; r.sortingOrder = like.sortingOrder + by; }
    }

    /// <summary>A puff of smoke there (the game's dust, a bit bigger); none loaded: nothing.</summary>
    public static void Puff(Vector3 at, Component near, float size = 2f)
    {
        if (dust == null || dust.Length == 0 || !dust[0])
            dust = Resources.FindObjectsOfTypeAll<Sprite>().Where(s => s && s.name.StartsWith("VFX_Animations_Dust"))
                .OrderBy(s => int.TryParse(s.name.Substring(s.name.LastIndexOf('_') + 1), out var n) ? n : 0).ToArray();
        if (dust.Length == 0) return;
        var go = new GameObject("NinjaPuff");
        go.transform.position = at + Vector3.up * 0.4f;
        go.transform.localScale = Vector3.one * size;
        var r = go.AddComponent<SpriteRenderer>();
        Above(r, near, 30);
        go.AddComponent<Flipbook>().frames = dust;
    }

    /// <summary>Plays its frames once, then it's gone.</summary>
    sealed class Flipbook : MonoBehaviour
    {
        public Sprite[] frames;
        float t;
        SpriteRenderer r;

        void Start() { r = GetComponent<SpriteRenderer>(); }

        void Update()
        {
            t += Time.deltaTime;
            int i = (int)(t * 16f);
            if (i >= frames.Length) { Destroy(gameObject); return; }
            r.sprite = frames[i];
        }
    }
}

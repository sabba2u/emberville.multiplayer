using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// "sheet = Atwor_Anim" in npc.txt: an NPC drawn from one of the game's own sprite sheets (a character the demo has the
/// art of but never puts in the world) instead of a copy of your character. The sheet is the game's texture, found in
/// memory (Atwor's comes with TutorialArea) and kept from then on; the frames are cut from it by the layouts below
/// (the demo has no animations for them). Such an NPC stays at their spot (no wandering) and stops while talking.
/// </summary>
internal static class SheetBody
{
    sealed class Layout
    {
        public int Height;               // the sheet's height (frames below are top-down, as an image editor shows them)
        public RectInt[] Work;           // the loop they play: x, y from the top, w, h
        public int Still;                // the frame they hold while talking
        public float Fps = 10f, Ppu = 32f;
    }

    static RectInt[] Column(int x, int w, params (int top, int bottom)[] rows) => rows.Select(r => new RectInt(x, r.top, w, r.bottom - r.top)).ToArray();

    // each sheet's layouts, by the size the game's texture of that name has (Nagedra's comes in two: her full sheet,
    // and a single picture of her with the tutorial area)
    static readonly Dictionary<string, Layout[]> layouts = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Atwor the blacksmith: hammering a glowing blade on his anvil (the left column of his sheet)
        ["Atwor_Anim"] = new[] { new Layout { Height = 512, Still = 0,
            Work = Column(5, 28, (60, 107), (117, 164), (174, 221), (231, 278), (288, 335), (345, 393), (403, 450), (460, 507)) } },
        // Gumu: standing, facing you, leaning on his staff
        ["Gumu_Anim"] = new[] { new Layout { Height = 512, Still = 0, Fps = 6f,
            Work = Column(87, 36, (17, 50), (58, 91), (99, 132), (140, 173), (181, 214), (222, 255), (263, 296)) } },
        // Nagedra the carpenter: standing, facing you
        ["Nagedra_Anim"] = new[]
        {
            new Layout { Height = 256, Still = 0, Fps = 6f, Work = Column(4, 26, (35, 72), (80, 117), (125, 162), (170, 207), (215, 252)) },
            new Layout { Height = 64, Still = 0, Work = new[] { new RectInt(20, 11, 26, 37) } },
        },
    };

    static readonly Dictionary<string, Sprite[]> frames = new(System.StringComparer.OrdinalIgnoreCase);

    public static bool Known(string sheet) => sheet != null && layouts.ContainsKey(sheet);

    /// <summary>The copy's own looks off, the sheet's frames on. False: the sheet isn't loaded (yet): the copy stays as it is.</summary>
    public static bool Dress(GameObject copy, NpcDef def)
    {
        var sprites = Frames(def.Sheet, out var layout);
        if (sprites == null) return false;
        var like = copy.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.enabled && r.sprite).OrderByDescending(r => r.sortingOrder).FirstOrDefault();
        // the copy's own looks gone for good: its parts switched off (its animations would turn renderers back on) and
        // its animators stopped
        foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (var a in copy.GetComponentsInChildren<Animator>(true)) a.enabled = false;
        for (int i = 0; i < copy.transform.childCount; i++) copy.transform.GetChild(i).gameObject.SetActive(false);
        var go = new GameObject("BNQ_Sheet");
        go.transform.SetParent(copy.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprites[0];
        if (like) { sr.sortingLayerID = like.sortingLayerID; sr.sortingOrder = like.sortingOrder; sr.sharedMaterial = like.sharedMaterial; }
        var anim = go.AddComponent<Anim>();
        anim.Frames = sprites;
        anim.Fps = layout.Fps;
        anim.Still = layout.Still;
        anim.Root = copy;
        return true;
    }

    static bool triedLoading;

    static Sprite[] Frames(string sheet, out Layout layout)
    {
        layout = null;
        if (!layouts.TryGetValue(sheet, out var ls)) return null;
        var texes = Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => t && t.name == sheet).ToList();
        if (!ls.Any(l => texes.Any(t => t.height == l.Height)) && !triedLoading)
        {
            // (Gumu's and Nagedra's full sheets live in the game's resources: they come in with what loads them)
            triedLoading = true;
            foreach (var path in new[] { "AllBiomes", "CruqueTown", "IntroCutscene1" })
                try { Resources.Load(path); } catch (System.Exception e) { Log.Warn($"NPCs and quests: loading {path}: {e.Message}"); }
            texes = Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => t && t.name == sheet).ToList();
        }
        foreach (var l in ls)   // the best layout first
        {
            var tex = texes.FirstOrDefault(t => t.height == l.Height);
            if (!tex) continue;
            layout = l;
            string key = sheet + "/" + l.Height;
            if (frames.TryGetValue(key, out var got) && got != null && got.All(s => s)) return got;
            got = l.Work.Select((r, i) =>
            {
                var s = Sprite.Create(tex, new Rect(r.x, tex.height - (r.y + r.height), r.width, r.height), new Vector2(0.5f, 0f), l.Ppu);
                s.name = $"BNQ_{sheet}_{i}";
                Object.DontDestroyOnLoad(s);   // (and the texture with them: they hold it once its own scene is gone)
                return s;
            }).ToArray();
            Log.Info($"NPCs and quests: {got.Length} frames cut from the game's {sheet} ({tex.width}x{tex.height})");
            return frames[key] = got;
        }
        return null;
    }

    /// <summary>Plays the loop; holds still while this NPC is the one talking.</summary>
    internal sealed class Anim : MonoBehaviour
    {
        public Sprite[] Frames;
        public float Fps;
        public int Still;
        public GameObject Root;
        SpriteRenderer sr;
        float t;

        void Awake() => sr = GetComponent<SpriteRenderer>();

        void Update()
        {
            if (!sr || Frames == null || Frames.Length == 0) return;
            bool talking = Talk.IsOpen && Talk.Speaking?.Body?.Root == Root;
            if (talking) { sr.sprite = Frames[Mathf.Clamp(Still, 0, Frames.Length - 1)]; return; }
            t += Time.deltaTime * Fps;
            sr.sprite = Frames[(int)t % Frames.Length];
        }
    }
}

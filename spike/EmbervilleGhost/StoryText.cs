using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ImGuiNET;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// Insert > Font Options (and the same three rows in Options > Accessibility): the story text's size, the in-world
/// labels' size, and the font.
/// Story text (- [12] +): what NPCs say in the dialogue window (Ellie, the game's own NPCs), the
/// answers you pick, the names; speech bubbles (Rory's, the game's TextBox); the quest tracker on the side. 12 is the
/// game's own size; each step is a twelfth bigger or smaller. The text itself grows (each TextMeshPro's size, or its
/// auto-size range), and the boxes grow with it: the dialogue window and the bubbles fit themselves to their text
/// (their ContentSizeFitters, rebuilt here), the tracker's labels stack.
/// Labels (- [100%] +): the words standing in the world (the interact prompts "Open", "Examine", names over heads: the
/// game's world-space TextMeshPro texts), a share of each one's own size.
/// Font: the game's own (Emberfont, every text as the game made it) or one of the others the game carries (Noto Serif,
/// Liberation Sans, Perfect DOS VGA; the last two only as TTF files: a font asset is made from them here), for every text
/// of the game; back to the game's own puts each text's own font and look back.
/// </summary>
internal static class StoryText
{
    static ConfigEntry<int> size;   // the dialogue text's size in points (0 = the game's own)
    static ConfigEntry<int> labelScale;   // the in-world labels' size, % of their own
    static ConfigEntry<string> fontName;   // "" = the game's own
    static float nextSweep;

    struct Orig { public float Size, MinSize, MaxSize; }
    static readonly Dictionary<TMP_Text, Orig> orig = new();
    static readonly HashSet<TMP_Text> sized = new();   // at the current size
    static int sizedAt = -1;

    public static void Bind(ConfigFile config)
    {
        size = config.Bind("Text", "StoryFontSize", 0, "Story text size in points, as the dialogue's text is measured (0 = the game's own); bubbles and the quest tracker grow by the same share");
        labelScale = config.Bind("Text", "LabelSize", 100, "The labels in the world (Open, Examine, names over heads), % of their own size");
        fontName = config.Bind("Text", "Font", "", "The font for all the game's text: empty = the game's own (Emberfont); else one the game carries (Noto Serif, Liberation Sans, Perfect DOS VGA)");
    }

    /// <summary>The game's own size of the dialogue's text (its font size, or its auto-size top), once seen.</summary>
    static float baseSize;

    static void LearnBase()
    {
        if (baseSize > 0f || !Singleton<DialogueManager>.HasMain) return;
        var t = Singleton<DialogueManager>.main.text;
        if (!t) return;
        if (!orig.TryGetValue(t, out var o)) o = new Orig { Size = t.fontSize, MinSize = t.fontSizeMin, MaxSize = t.fontSizeMax };
        baseSize = t.enableAutoSizing ? o.MaxSize : o.Size;
    }

    static float Scale => size == null || size.Value <= 0 || baseSize <= 0f ? 1f : size.Value / baseSize;

    /// <summary>The number shown: the size now, in points.</summary>
    static int Shown => size.Value > 0 ? size.Value : Mathf.RoundToInt(baseSize);

    /// <summary>A few times a second: every story text at the chosen size (new ones as they appear).</summary>
    public static void Tick()
    {
        if (size == null || Time.unscaledTime < nextSweep) return;
        nextSweep = Time.unscaledTime + 0.25f;
        LearnBase();
        if (sizedAt != size.Value) { sized.Clear(); sizedAt = size.Value; }
        if (size.Value <= 0 && orig.Count == 0) return;   // (never touched: nothing to do)

        if (Singleton<DialogueManager>.HasMain)
        {
            var dm = Singleton<DialogueManager>.main;
            if (dm.dialogueWindow) Apply(dm.dialogueWindow.transform, dm.sizeFitters);
        }
        foreach (var r in Object.FindObjectsByType<QuestUIReminder>(FindObjectsSortMode.None)) Apply(r.transform, null);
        foreach (var l in Object.FindObjectsByType<QuestUIObjectiveLabel>(FindObjectsSortMode.None)) Apply(l.transform, null);
        foreach (var b in Object.FindObjectsByType<TextBox>(FindObjectsSortMode.None)) Apply(b.transform, b.toRebuild);
        orig.Keys.Where0(k => !k);
        Labels();
        Fonts();
    }

    // ---- the labels in the world ----

    static readonly Dictionary<TextMeshPro, float> labelOrig = new();
    static readonly HashSet<TextMeshPro> labelled = new();
    static int labelledAt = 100;

    static void Labels()
    {
        int pct = Mathf.Clamp(labelScale.Value, 50, 300);
        if (pct == 100 && labelOrig.Count == 0) return;
        if (labelledAt != pct) { labelled.Clear(); labelledAt = pct; }
        foreach (var t in Object.FindObjectsByType<TextMeshPro>(FindObjectsSortMode.None))
        {
            if (!t || labelled.Contains(t)) continue;
            if (!labelOrig.TryGetValue(t, out var o)) labelOrig[t] = o = t.enableAutoSizing ? t.fontSizeMax : t.fontSize;
            if (t.enableAutoSizing) t.fontSizeMax = o * pct / 100f; else t.fontSize = o * pct / 100f;
            labelled.Add(t);
        }
        if (labelOrig.Count > 512) foreach (var k in labelOrig.Keys.Where(k => !k).ToList()) { labelOrig.Remove(k); labelled.Remove(k); }
    }

    // ---- the font ----

    /// <summary>The fonts the game carries, by the name shown: its TextMeshPro fonts (one per family), and its TTF fonts
    /// that have none (a font asset is made from them when picked).</summary>
    static Dictionary<string, Object> fonts;
    static readonly Dictionary<string, TMP_FontAsset> made = new();
    static readonly Dictionary<TMP_Text, (TMP_FontAsset font, Material mat)> fontOrig = new();
    static readonly HashSet<TMP_Text> fonted = new();
    static string fontedAt = "";

    static Dictionary<string, Object> Fonts()
    {
        if (fonts == null || fonts.Count == 0)
        {
            fonts = new Dictionary<string, Object>();
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(f => f).OrderBy(f => f.name))
            {
                var family = string.IsNullOrEmpty(f.faceInfo.familyName) ? f.name : f.faceInfo.familyName;
                if (family.StartsWith("Noto Serif")) family = "Noto Serif";      // (one per region: all have the latin letters)
                if (family.StartsWith("Emberfont")) continue;                    // (the game's own: the first choice)
                if (!fonts.ContainsKey(family)) fonts[family] = f;
            }
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>().Where(f => f).OrderBy(f => f.name))
            {
                var name = f.name.StartsWith("PerfectDOS") ? "Perfect DOS VGA" : f.name.StartsWith("LiberationSans") ? "Liberation Sans" : null;
                if (name != null && !fonts.ContainsKey(name)) fonts[name] = f;
            }
        }
        string want = fontName.Value ?? "";
        if (want == "" && fontOrig.Count == 0) return fonts;
        if (fontedAt != want) { fonted.Clear(); fontedAt = want; }
        TMP_FontAsset to = null;
        if (want != "" && fonts.TryGetValue(want, out var src))
        {
            to = src as TMP_FontAsset;
            if (!to && src is Font ttf && !made.TryGetValue(want, out to))
            {
                try { to = TMP_FontAsset.CreateFontAsset(ttf); } catch (System.Exception e) { NetLog.Warn($"Font {want}: {e.Message}", toast: true); }
                made[want] = to;
            }
        }
        foreach (var t in Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None))
        {
            if (!t || fonted.Contains(t)) continue;
            if (!fontOrig.ContainsKey(t)) fontOrig[t] = (t.font, t.fontSharedMaterial);
            var o = fontOrig[t];
            if (to) { if (t.font != to) t.font = to; }
            else if (t.font != o.font) { t.font = o.font; if (o.mat) t.fontSharedMaterial = o.mat; }
            fonted.Add(t);
        }
        if (fontOrig.Count > 2048) foreach (var k in fontOrig.Keys.Where(k => !k).ToList()) { fontOrig.Remove(k); fonted.Remove(k); }
        return fonts;
    }

    static void Apply(Transform root, List<RectTransform> rebuild)
    {
        if (!root) return;
        bool changed = false;
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!t || sized.Contains(t)) continue;
            if (!orig.TryGetValue(t, out var o)) orig[t] = o = new Orig { Size = t.fontSize, MinSize = t.fontSizeMin, MaxSize = t.fontSizeMax };
            float k = Scale;
            if (t.enableAutoSizing) { t.fontSizeMin = o.MinSize * k; t.fontSizeMax = o.MaxSize * k; }
            else t.fontSize = o.Size * k;
            sized.Add(t);
            changed = true;
        }
        if (!changed) return;
        // the boxes fit their text again
        if (rebuild != null) foreach (var r in rebuild) if (r) LayoutRebuilder.ForceRebuildLayoutImmediate(r);
        if (root is RectTransform rt) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
    }

    static void Where0(this Dictionary<TMP_Text, Orig>.KeyCollection keys, System.Func<TMP_Text, bool> gone)
    {
        List<TMP_Text> dead = null;
        foreach (var k in keys) if (gone(k)) (dead ??= new List<TMP_Text>()).Add(k);
        if (dead != null) foreach (var d in dead) { orig.Remove(d); sized.Remove(d); }
    }

    // ---- Options > Accessibility (OptionsRows) ----

    /// <summary>The dialogue text's own size: as seen in a world, else 12 (the game's, at the menu before any world).</summary>
    internal static int BaseSize { get { LearnBase(); return baseSize > 0f ? Mathf.RoundToInt(baseSize) : 12; } }
    internal static int StorySize { get => size.Value > 0 ? size.Value : BaseSize; set => size.Value = value == BaseSize ? 0 : value; }
    internal static int LabelPct { get => Mathf.Clamp(labelScale.Value, 50, 300); set => labelScale.Value = value; }
    internal static string FontName { get => fontName.Value ?? ""; set => fontName.Value = value ?? ""; }
    internal static List<string> FontNames() => Fonts().Keys.OrderBy(n => n).ToList();
    internal static bool Bound => size != null;

    // ---- Insert menu ----

    public static void DrawMenu()
    {
        if (size == null) return;
        LearnBase();
        ImGui.Text("Story text size");
        ImGui.TextDisabled("Dialogue (Ellie, the game's NPCs), speech bubbles (Rory), the quest tracker. In points, as the game measures its dialogue text.");
        if (baseSize <= 0f) { ImGui.TextDisabled("(load a world: the game's own size shows once its dialogue window exists)"); return; }
        int lo = Mathf.Max(6, Mathf.RoundToInt(baseSize * 0.5f)), hi = Mathf.RoundToInt(baseSize * 2.5f);
        int now = Shown;
        if (ImGui.Button("-", new ImGuiNET.Vector2(32f, 0f)) && now > lo) size.Value = now - 1 == Mathf.RoundToInt(baseSize) ? 0 : now - 1;
        ImGui.SameLine();
        ImGui.Text($"[{now}]");
        ImGui.SameLine();
        if (ImGui.Button("+", new ImGuiNET.Vector2(32f, 0f)) && now < hi) size.Value = now + 1 == Mathf.RoundToInt(baseSize) ? 0 : now + 1;
        ImGui.SameLine();
        ImGui.TextDisabled(size.Value > 0 ? $"(the game's own: {Mathf.RoundToInt(baseSize)})" : "(the game's own)");
        if (size.Value > 0) { ImGui.SameLine(); if (ImGui.SmallButton("Reset")) size.Value = 0; }

        ImGui.Separator();
        ImGui.Text("Label size");
        ImGui.TextDisabled("The words in the world: \"Open\", \"Examine\", names over heads.");
        int pct = Mathf.Clamp(labelScale.Value, 50, 300);
        if (ImGui.Button("-##labels", new ImGuiNET.Vector2(32f, 0f)) && pct > 50) labelScale.Value = pct - 10;
        ImGui.SameLine();
        ImGui.Text($"[{pct}%]");
        ImGui.SameLine();
        if (ImGui.Button("+##labels", new ImGuiNET.Vector2(32f, 0f)) && pct < 300) labelScale.Value = pct + 10;
        if (pct != 100) { ImGui.SameLine(); if (ImGui.SmallButton("Reset##labels")) labelScale.Value = 100; }

        ImGui.Separator();
        ImGui.Text("Font");
        ImGui.TextDisabled("For all the game's text. The game's own is Emberfont; the others are fonts the game carries.");
        var names = new List<string> { "" };
        names.AddRange(Fonts().Keys.OrderBy(n => n));
        foreach (var n in names)
        {
            bool on = (fontName.Value ?? "") == n;
            if (ImGui.RadioButton(n == "" ? "The game's own (Emberfont)" : n, on) && !on) fontName.Value = n;
        }
        if (!string.IsNullOrEmpty(fontName.Value) && !Fonts().ContainsKey(fontName.Value))
            ImGui.TextDisabled($"(\"{fontName.Value}\" isn't loaded yet: it shows once the game has it)");
    }
}

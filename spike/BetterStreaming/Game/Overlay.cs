using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace BetterStreaming;

/// <summary>
/// What viewers see on stream: a banner at the top for each effect ("Twitch raid incoming by xxJoexx", their words
/// under it), one after another, 4 s each; and a corner panel cycling through what chat can buy (name, Bits, the word
/// to put in the cheer), the chaos dial, the queue ("3 curses incoming... next in 12 s") and what's running.
/// </summary>
internal static class Overlay
{
    static ConfigEntry<bool> showCorner;
    static ConfigEntry<int> corner;
    static readonly Queue<(string title, string sub, Vector4 colour)> banners = new Queue<(string, string, Vector4)>();
    static (string title, string sub, Vector4 colour) showing;
    static float shownAt = -100f;
    const float BannerFor = 4f;

    static readonly Vector4 Purple = new Vector4(0.57f, 0.27f, 1f, 1f), Text = new Vector4(1f, 0.95f, 0.88f, 1f),
        Dim = new Vector4(0.78f, 0.72f, 0.65f, 1f), Back = new Vector4(0.09f, 0.07f, 0.12f, 0.88f);

    public static void Bind(ConfigFile config)
    {
        showCorner = config.Bind("Overlay", "ShowEffects", true, "The corner panel: what chat can buy, the chaos dial and the queue");
        corner = config.Bind("Overlay", "Corner", 0, "Where the corner panel sits: 0 top left, 1 top right, 2 bottom left, 3 bottom right");
        // once: top left as the new default, over what an older version left in the file
        var defaults = config.Bind("Overlay", "DefaultsVersion", 0, "(the mod's own: which defaults this file has had; leave it)");
        if (defaults.Value < 1) { corner.Value = 0; defaults.Value = 1; }
    }

    public static bool ShowCorner { get => showCorner.Value; set => showCorner.Value = value; }
    public static int Corner { get => corner.Value; set => corner.Value = Mathf.Clamp(value, 0, 3); }

    /// <summary>An effect's own colour for its name on screen (poison: green); else the usual text colour.</summary>
    public static Vector4 ColourOf(string effectId) => effectId switch
    {
        null => Text,
        _ => ToV(Effects.ColourOf(effectId)),
    };

    static Vector4 ToV(Color c) => new Vector4(Mathf.Lerp(c.r, 1f, 0.15f), Mathf.Lerp(c.g, 1f, 0.15f), Mathf.Lerp(c.b, 1f, 0.15f), 1f);   // (a touch lighter: readable on the dark box)

    public static void Banner(string title, string sub, string effectId = null)
    {
        if (banners.Count < 20) banners.Enqueue((title, sub ?? "", ColourOf(effectId)));
        Log.Info($"Streaming banner: {title}{(string.IsNullOrEmpty(sub) ? "" : " - " + sub)}");
    }

    public static void Draw()
    {
        ChainLightning.DrawScreen();
        DrawBanner();
        if (showCorner.Value) DrawCorner();
    }

    static void DrawBanner()
    {
        if (Time.unscaledTime - shownAt > BannerFor)
        {
            if (banners.Count == 0) return;
            showing = banners.Dequeue();
            shownAt = Time.unscaledTime;
        }
        float age = Time.unscaledTime - shownAt;
        float alpha = Mathf.Clamp01(Mathf.Min(age / 0.25f, (BannerFor - age) / 0.4f));
        var vp = ImGui.GetMainViewport();
        var dl = ImGui.GetForegroundDrawList();
        ImGui.SetWindowFontScale(1f);
        var ts0 = ImGui.CalcTextSize(showing.title);
        var ts = new Vector2(ts0.x * 1.6f, ts0.y * 1.6f);
        var ss = string.IsNullOrEmpty(showing.sub) ? new Vector2(0, 0) : ImGui.CalcTextSize("\"" + showing.sub + "\"");
        float w = Mathf.Max(ts.x, ss.x) + 48f, h = ts.y + (ss.y > 0 ? ss.y + 8f : 0f) + 26f;
        var min = new Vector2(vp.Pos.x + vp.Size.x / 2f - w / 2f, vp.Pos.y + 70f);
        var max = new Vector2(min.x + w, min.y + h);
        dl.AddRectFilled(min, max, Col(Back, alpha), 10f);
        dl.AddRect(min, max, Col(Purple, alpha), 10f, ImDrawFlags.None, 2.5f);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.6f, new Vector2(min.x + (w - ts.x) / 2f, min.y + 12f), Col(showing.colour, alpha), showing.title);
        if (ss.y > 0) dl.AddText(new Vector2(min.x + (w - ss.x) / 2f, min.y + 12f + ts.y + 6f), Col(Dim, alpha), "\"" + showing.sub + "\"");
    }

    static void DrawCorner()
    {
        var buy = Streamer.Buyable();
        var vp = ImGui.GetMainViewport();
        int c = Corner;
        var pivot = new Vector2(c % 2, c / 2);
        var at = new Vector2(vp.WorkPos.x + (c % 2 == 0 ? 12f : vp.WorkSize.x - 12f), vp.WorkPos.y + (c / 2 == 0 ? 60f : vp.WorkSize.y - 12f));
        ImGui.SetNextWindowPos(at, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowBgAlpha(0.75f);
        if (ImGui.Begin("##bs_corner", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            ImGui.TextColored(Purple, $"Twitch chaos: {ChaosQueue.DialNames[Streamer.Dial]}");
            if (buy.Count > 0)
            {
                var d = buy[(int)(Time.unscaledTime / 4f) % buy.Count];
                ImGui.TextColored(Text, $"{d.Name}: {d.Bits}{(d.Scales ? "+" : "")} Bits");
                ImGui.TextColored(Dim, $"cheer with \"{d.Keywords.FirstOrDefault()}\"   (!effects for all)");
            }
            int mean = Streamer.Queue.Waiting.Count(p => p.Mood != Mood.Nice);
            if (mean > 0)
            {
                double inS = Streamer.Queue.NextMeanIn();
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), $"{mean} {(mean == 1 ? "curse" : "curses")} incoming...{(inS > 0.5 ? $" next in {inS:0} s" : "")}");
            }
            if (Streamer.Queue.BreatherLeft > 0.5) ImGui.TextColored(Dim, $"Breather: {Streamer.Queue.BreatherLeft:0} s");
            foreach (var id in Streamer.Timers.Running)
                ImGui.TextColored(ColourOf(id), $"{Streamer.Def(id)?.Name ?? id}: {Streamer.Timers.Left(id):0} s");
        }
        ImGui.End();
    }

    static uint Col(Vector4 c, float alpha) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.x, c.y, c.z, c.w * alpha));
}

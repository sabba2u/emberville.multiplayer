using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using ImGuiNET;

namespace BetterCombat;

/// <summary>
/// Before / after, for showing the difference (a video, say): "The game's own" turns every Better Combat switch off
/// (remembering how you had them, in [Compare] Saved), "Better Combat" puts them back. /gamecombat flips between them.
/// Sliders aren't touched: with their switches off they do nothing.
/// </summary>
public static class Compare
{
    static ConfigFile cfg;
    static ConfigEntry<string> saved;

    public static void Bind(ConfigFile config)
    {
        cfg = config;
        saved = config.Bind("Compare", "Saved", "", "How your switches were before \"The game's own\" (put back by \"Better Combat\"); empty = in Better Combat");
    }

    public static bool GameOwn => saved.Value != "";

    static IEnumerable<ConfigEntry<bool>> Switches() =>
        cfg.Keys.Select(k => cfg[k]).OfType<ConfigEntry<bool>>();

    static string Key(ConfigEntryBase e) => e.Definition.Section + "." + e.Definition.Key;

    public static void Toggle() { if (GameOwn) BetterCombat(); else TheGamesOwn(); }

    public static void TheGamesOwn()
    {
        if (GameOwn) return;
        var on = Switches().Where(e => e.Value).ToList();
        saved.Value = on.Count == 0 ? ";" : string.Join(";", on.Select(Key));
        foreach (var e in on) e.Value = false;
        Log.Info("Combat: the game's own (Better Combat off; /gamecombat or the menu to switch back)", toast: true);
    }

    public static void BetterCombat()
    {
        if (!GameOwn) return;
        var back = new HashSet<string>(saved.Value.Split(';'));
        foreach (var e in Switches()) if (back.Contains(Key(e))) e.Value = true;
        saved.Value = "";
        Log.Info("Combat: Better Combat (your settings back)", toast: true);
    }

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Before / after");
        if (GameOwn)
        {
            ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.75f, 0.3f, 1f), "Now: the game's own combat (everything here is off)");
            if (ImGui.Button("Switch to Better Combat")) BetterCombat();
        }
        else
        {
            ImGui.TextColored(new ImGuiNET.Vector4(0.5f, 1f, 0.5f, 1f), "Now: Better Combat");
            if (ImGui.Button("Switch to the game's own (all off)")) TheGamesOwn();
        }
        ImGui.SameLine(); ImGui.TextDisabled("to show the difference; your settings come back (/gamecombat)");
    }
}

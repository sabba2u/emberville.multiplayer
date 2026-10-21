using System.Globalization;
using System.Linq;
using BetterNPCsAndQuests.Core;
using ImGuiNET;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// The "NPCs and Quests" tab of the multiplayer mod's Insert menu (ImGui): read the files again, your position to paste
/// into an npc.txt, the NPCs and quests loaded, the flags (set / clear, for testing), and every problem in the files.
/// </summary>
internal static class Menu
{
    static string newFlag = "", newValue = "1";

    static string Spot()
    {
        var (scene, p, _) = Npcs.Readout();
        return $"scene = {scene}\nspot = {p.x.ToString("0.0", CultureInfo.InvariantCulture)}, {p.y.ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Each NPC here: their name on a dark card over the head ("(talk)" when you can), as Rory's is drawn.</summary>
    public static void DrawLabels()
    {
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGui.GetBackgroundDrawList();
        foreach (var n in Npcs.Here)
        {
            if (!n.Body.Root || !n.Body.Root.activeInHierarchy) continue;
            var s = cam.WorldToScreenPoint(n.Body.Root.transform.position + UnityEngine.Vector3.up * 2.6f);
            if (s.z < 0) continue;
            string name = n.Spot && n.Spot.Near ? n.Def.Name + "  (talk)" : n.Def.Name;
            var size = ImGui.CalcTextSize(name);
            float x = s.x, y = Screen.height - s.y;
            var c = ColorUtility.TryParseHtmlString(n.Def.Color, out var col) ? col : Color.white;
            dl.AddRectFilled(new ImGuiNET.Vector2(x - size.x / 2 - 4, y - 1), new ImGuiNET.Vector2(x + size.x / 2 + 4, y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0, 0, 0, 0.5f)), 3f);
            dl.AddText(new ImGuiNET.Vector2(x - size.x / 2, y), ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(c.r, c.g, c.b, 1f)), name);
        }
    }

    static void StartOver()
    {
        foreach (var q in State.Lib.Quests) { State.Book.Reset(q.Id); QuestBridge.Apply(q.Id, quiet: true); }
        foreach (var name in State.Book.Flags.All.Keys.ToList()) State.Book.Flags.Clear(name);
        Log.Info("NPCs and quests: started over (all quests reset, all flags cleared)", toast: true);
    }

    public static void Draw()
    {
        bool guest = Hooks.IsClient();
        if (guest) ImGui.TextDisabled("You're a guest: these are the host's NPCs, quests and flags.");
        else if (ImGui.Button("Read the files again")) State.Reload();
        ImGui.SameLine();
        ImGui.TextDisabled($"{Plugin.Folder}");

        ImGui.SeparatorText("Where you stand");
        var (scene, _, fixedScene) = Npcs.Readout();
        ImGui.TextUnformatted(Spot().Replace("\n", "    "));
        ImGui.SameLine();
        if (ImGui.Button("Copy")) GUIUtility.systemCopyBuffer = Spot();
        if (!fixedScene) ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.45f, 0.4f, 1f), $"{scene} is made fresh for each world: NPCs can't stand here.");

        ImGui.SeparatorText($"NPCs ({State.Lib.Npcs.Count})");
        foreach (var n in State.Lib.Npcs)
        {
            bool here = Npcs.Find(n.Id) != null;
            ImGui.TextUnformatted($"{n.Name}  —  {n.Scene} ({n.X:0.0}, {n.Y:0.0})");
            if (here) { ImGui.SameLine(); ImGui.TextColored(new ImGuiNET.Vector4(0.6f, 1f, 0.6f, 1f), "here"); }
        }

        ImGui.SeparatorText($"Quests ({State.Lib.Quests.Count})");
        if (!guest)
        {
            // (debug: every quest back to not started, every flag gone; the NPCs talk as if you'd never met)
            if (ImGui.Button("Start over: reset all quests and flags")) StartOver();
            ImGui.SameLine();
            ImGui.TextDisabled("(for testing)");
        }
        foreach (var q in State.Lib.Quests)
        {
            var p = State.Book.Progress(q.Id);
            string where = p.State == QState.Active ? $"step {p.Step + 1} of {q.Steps.Count}: {q.Steps[p.Step].Title}" : p.State == QState.Completed ? "done" : "not started";
            ImGui.TextUnformatted($"{q.Title} ({q.Id})  —  {where}");
            if (guest || p.State == QState.Inactive) continue;
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##" + q.Id)) { State.Book.Reset(q.Id); QuestBridge.Apply(q.Id, quiet: true); }
        }

        ImGui.SeparatorText($"Flags ({State.Book.Flags.All.Count})");
        foreach (var kv in State.Book.Flags.All.OrderBy(k => k.Key).ToList())
        {
            ImGui.TextUnformatted($"${kv.Key} = {kv.Value}");
            if (guest) continue;
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##" + kv.Key)) State.Book.Flags.Clear(kv.Key);
        }
        if (!guest)
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##flagname", ref newFlag, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.InputText("##flagvalue", ref newValue, 8);
            ImGui.SameLine();
            if (ImGui.Button("Set flag") && newFlag.Trim().Length > 0 && int.TryParse(newValue, out var v)) State.Book.Flags.Set(newFlag, v);
        }

        var problems = State.Lib.Problems.Concat(State.Lib.FlagWarnings).ToList();
        ImGui.SeparatorText($"Problems in the files ({problems.Count})");
        if (problems.Count == 0) ImGui.TextDisabled("None.");
        foreach (var p in problems) ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.75f, 0.4f, 1f), p.ToString());
        foreach (var p in State.Book.Problems.Skip(System.Math.Max(0, State.Book.Problems.Count - 5))) ImGui.TextColored(new ImGuiNET.Vector4(1f, 0.6f, 0.5f, 1f), p);
    }
}

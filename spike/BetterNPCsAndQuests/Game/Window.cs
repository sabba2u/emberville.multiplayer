using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Bootstrap;
using BetterNPCsAndQuests.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterNPCsAndQuests;

/// <summary>
/// Insert, when the multiplayer mod isn't installed (it has its own Insert menu with an NPCs and Quests tab then): a
/// window in the game's own look (NpcUi) to read the files again, copy your position for an npc.txt, see the quests,
/// and every problem in the files. Esc or Insert closes it.
/// </summary>
internal static class Window
{
    const string MultiplayerGuid = "spike.embervilleghost";
    static bool? standalone;
    static RectTransform win;
    static List<(InputActionMap map, bool on)> savedMaps;

    static bool Standalone => standalone ??= !Chainloader.PluginInfos.ContainsKey(MultiplayerGuid);
    public static bool Open => win;

    public static void Tick()
    {
        if (!Standalone) return;
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused) return;
        if (Open)
        {
            if (kb.escapeKey.wasPressedThisFrame || kb.insertKey.wasPressedThisFrame) Close();
            return;
        }
        if (!kb.insertKey.wasPressedThisFrame || !LocalPlayer.Get() || UIPanels.HasOpenedPanel || !MainInput.Actions.Player.enabled) return;
        Show();
    }

    static void Show()
    {
        try { Build(); }
        catch (System.Exception e) { Log.Warn($"NPCs and quests window: {e}"); if (win) Object.Destroy(win.gameObject); win = null; return; }
        savedMaps = new List<(InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            savedMaps.Add((map, map.enabled));
            if (map.name == "UI") { map.Enable(); MainInput.Actions.UI.Cancel.Disable(); }
            else map.Disable();
        }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    static void Close()
    {
        if (win) Object.Destroy(win.gameObject);
        win = null;
        MainInput.Actions.UI.Cancel.Enable();
        if (savedMaps != null) foreach (var (map, was) in savedMaps) { if (was) map.Enable(); else map.Disable(); }
        savedMaps = null;
    }

    static void Rebuild()
    {
        if (!win) return;
        Object.Destroy(win.gameObject);
        win = null;
        Build();
    }

    static void Build()
    {
        const float W = 760, H = 700;
        win = NpcUi.Window("NPCs and Quests", new Vector2(W, H), out _);
        float y = 64;

        var reload = NpcUi.Button(win, "Read the files again", () => { State.Reload(); Rebuild(); });
        NpcUi.Place((RectTransform)reload.transform, 40, y, 260, 38);
        y += 50;

        var (scene, p, fixedScene) = Npcs.Readout();
        string spot = $"scene = {scene}\nspot = {p.x.ToString("0.0", CultureInfo.InvariantCulture)}, {p.y.ToString("0.0", CultureInfo.InvariantCulture)}";
        var where = NpcUi.Label(win, spot.Replace("\n", "     "), 19, fixedScene ? NpcUi.Text : new Color(1f, 0.5f, 0.45f));
        NpcUi.Place(where.rectTransform, 40, y, W - 240, 30);
        var copy = NpcUi.Button(win, "Copy", () => GUIUtility.systemCopyBuffer = spot);
        NpcUi.Place((RectTransform)copy.transform, W - 180, y - 4, 140, 36);
        y += 40;

        var head = NpcUi.Label(win, $"Quests ({State.Lib.Quests.Count})", 19, NpcUi.Dim);
        NpcUi.Place(head.rectTransform, 40, y, 300, 28);
        y += 30;
        foreach (var q in State.Lib.Quests.Take(6))
        {
            var pr = State.Book.Progress(q.Id);
            string state = pr.State == QState.Active ? $"step {pr.Step + 1} of {q.Steps.Count}" : pr.State == QState.Completed ? "done" : "not started";
            var l = NpcUi.Label(win, $"{q.Title}: {state}", 18, NpcUi.Text);
            NpcUi.Place(l.rectTransform, 56, y, W - 260, 26);
            if (pr.State != QState.Inactive)
            {
                var b = NpcUi.Button(win, "Reset", () => { State.Book.Reset(q.Id); QuestBridge.Apply(q.Id, quiet: true); Rebuild(); });
                NpcUi.Place((RectTransform)b.transform, W - 180, y - 3, 140, 32);
            }
            y += 34;
        }

        var problems = State.Lib.Problems.Concat(State.Lib.FlagWarnings).Select(x => x.ToString()).ToList();
        var ph = NpcUi.Label(win, $"Problems in the files ({problems.Count})", 19, NpcUi.Dim);
        NpcUi.Place(ph.rectTransform, 40, y + 6, 400, 28);
        y += 38;
        var list = NpcUi.Label(win, problems.Count == 0 ? "None." : string.Join("\n", problems.Take(8)), 16, problems.Count == 0 ? NpcUi.Dim : NpcUi.Gold, TMPro.TextAlignmentOptions.TopLeft);
        NpcUi.Place(list.rectTransform, 56, y, W - 96, H - y - 90);

        var close = NpcUi.Button(win, "Close", Close);
        NpcUi.Place((RectTransform)close.transform, W - 180, H - 70, 140, 40);
        var keys = NpcUi.Label(win, "Insert or Esc closes this", 16, NpcUi.Dim);
        NpcUi.Place(keys.rectTransform, 40, H - 62, 300, 24);
    }
}

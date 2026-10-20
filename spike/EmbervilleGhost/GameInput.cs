using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

/// <summary>
/// The game's controls off while one of our clickable windows is up (a click on a button would also swing the weapon,
/// WASD would walk). Several windows can ask at once; the controls come back when the last one lets go, as they were.
/// </summary>
internal static class GameInput
{
    static readonly HashSet<string> blockers = new HashSet<string>();
    static List<(InputActionMap map, bool on)> saved;

    public static bool Blocked => blockers.Count > 0;

    public static void Block(string who)
    {
        if (!blockers.Add(who) || blockers.Count > 1) return;
        saved = new List<(InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            if (map.name == "UI") continue;   // the game's own boxes (yes/no prompts we show) still need clicks
            saved.Add((map, map.enabled));
            map.Disable();
        }
        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
    }

    public static void Unblock(string who)
    {
        if (!blockers.Remove(who) || blockers.Count > 0) return;
        if (saved != null) foreach (var (map, on) in saved) if (on) map.Enable();
        saved = null;
    }
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

/// <summary>
/// The game's controls off while one of our windows or text boxes is up (a click on a button would also swing the
/// weapon, WASD would walk, typing "i" would open the inventory). Several can ask at once; the controls come back
/// when the last one lets go.
///
/// They come back in the state the game has asked for since, not the one they had: the game turns its own controls on
/// and off as it goes (talking to someone, a cutscene, a shop, its menus), and each of those requests made while ours are
/// held is noted and only acted on when we let go (an "off" goes through at once: nothing changes). So a box closed
/// while someone is talking doesn't hand back walking that the conversation took away. The UI controls stay on unless a blocker asks for them too (the game's own yes / no boxes we
/// show need clicks; a text box doesn't want arrow keys moving through the game's menus).
/// </summary>
[HarmonyPatch]
internal static class GameInput
{
    static readonly HashSet<string> blockers = new HashSet<string>(), uiBlockers = new HashSet<string>();
    /// <summary>Each held action, and whether the game last asked for it on. Null: nothing held.</summary>
    static Dictionary<InputAction, bool> askedOn;
    static bool ours;   // (our own switching: not remembered)

    public static bool Blocked => blockers.Count > 0;
    /// <summary>How many of the game's switches were remembered while held (the log, tests).</summary>
    public static int Remembered { get; private set; }

    /// <param name="alsoUi">Hold the game's UI controls off too (arrow keys, Enter, Esc in its menus): for text boxes.</param>
    public static void Block(string who, bool alsoUi = false)
    {
        bool changed = blockers.Add(who);
        if (alsoUi) changed |= uiBlockers.Add(who);
        if (!changed) return;
        Apply();
        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.None;
    }

    public static void Unblock(string who)
    {
        bool changed = blockers.Remove(who);
        changed |= uiBlockers.Remove(who);
        if (changed) Apply();
    }

    static bool Held(InputActionMap map) => blockers.Count > 0 && (map.name != "UI" || uiBlockers.Count > 0);

    /// <summary>Hold off what should be held, and give back what shouldn't (in the state the game askedOn now).</summary>
    static void Apply()
    {
        var asset = MainInput.Actions?.asset;
        if (asset == null) return;
        ours = true;
        try
        {
            foreach (var map in asset.actionMaps)
            {
                bool held = Held(map);
                foreach (var a in map.actions)
                {
                    if (held)
                    {
                        askedOn ??= new Dictionary<InputAction, bool>();
                        if (!askedOn.ContainsKey(a)) askedOn[a] = a.enabled;
                        if (a.enabled) a.Disable();
                    }
                    else if (askedOn != null && askedOn.TryGetValue(a, out bool on))
                    {
                        askedOn.Remove(a);
                        if (on && !a.enabled) a.Enable();
                        else if (!on && a.enabled) a.Disable();
                    }
                }
            }
            if (askedOn != null && askedOn.Count == 0) askedOn = null;
        }
        finally { ours = false; }
    }

    /// <summary>The game asks for one of the held actions on or off: noted; an "on" waits until we let go.</summary>
    static bool Note(InputAction a, bool on)
    {
        if (ours || askedOn == null || a == null || !askedOn.ContainsKey(a)) return true;
        askedOn[a] = on;
        Remembered++;
        return !on;
    }

    static bool Note(InputActionMap map, bool on)
    {
        if (ours || askedOn == null || map == null) return true;
        bool any = false;
        foreach (var a in map.actions)
            if (askedOn.ContainsKey(a)) { askedOn[a] = on; any = true; }
        if (!any) return true;
        Remembered++;
        return !on;
    }

    // Unity's InputActionMap / InputAction Enable and Disable: every way the game turns its controls on or off
    [HarmonyPatch(typeof(InputActionMap), nameof(InputActionMap.Enable)), HarmonyPrefix]
    static bool MapOn(InputActionMap __instance) => Note(__instance, true);

    [HarmonyPatch(typeof(InputActionMap), nameof(InputActionMap.Disable)), HarmonyPrefix]
    static bool MapOff(InputActionMap __instance) => Note(__instance, false);

    [HarmonyPatch(typeof(InputAction), nameof(InputAction.Enable)), HarmonyPrefix]
    static bool ActionOn(InputAction __instance) => Note(__instance, true);

    [HarmonyPatch(typeof(InputAction), nameof(InputAction.Disable)), HarmonyPrefix]
    static bool ActionOff(InputAction __instance) => Note(__instance, false);
}

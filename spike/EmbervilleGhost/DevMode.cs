using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The game's own developer tools, left in the demo but switched off: every scene has a "CanvasDevOptions" with
/// DevOptionsManager (Ctrl+Shift+Numpad7 opens its panel: go to Town / test arena, unlock skills and masteries,
/// heal, advance time, player speed...; with Ctrl+Shift held, Numpad+ gives XP, Numpad- hurts you, Numpad. tests a
/// spawn, Numpad/ a gory death) and, in some, AcolyteSpawnOptions (Numpad1: the test arenas and enemy spawns).
/// Their GameObjects are inactive in the build. /devmode switches them on (again in every scene we enter) or off.
/// /devpanel opens the panel without a numpad.
/// </summary>
internal static class DevMode
{
    public static bool On { get; private set; }
    static string appliedScene;

    public static void Toggle()
    {
        On = !On;
        appliedScene = null;
        if (!On)
        {
            Switch(false);
            Chat.System("Dev mode off.");
            return;
        }
        Tick();
        Chat.System("Dev mode on: Ctrl+Shift+Numpad7 (or /devpanel) opens the developer panel; Numpad1 the test arenas. " +
                    "With Ctrl+Shift held: Numpad+ XP, Numpad- hurt yourself.");
    }

    /// <summary>Every frame: a new scene has its own (inactive) dev tools; switch them on too.</summary>
    public static void Tick()
    {
        if (!On || !LocalPlayer.Get()) return;
        string scene = LocalPlayer.Scene;
        if (scene == appliedScene) return;
        if (Switch(true)) appliedScene = scene;
    }

    /// <summary>The developer panel itself (for keyboards without a numpad).</summary>
    public static void TogglePanel()
    {
        if (!On) Toggle();
        foreach (var dm in Object.FindObjectsByType<DevOptionsManager>(FindObjectsSortMode.None)) { dm.TogglePanel(); return; }
        Chat.System("No developer panel in this scene yet (it switches on a moment after entering).");
    }

    static bool Switch(bool on)
    {
        bool any = false;
        foreach (var dm in Resources.FindObjectsOfTypeAll<DevOptionsManager>())
            if (dm && dm.gameObject.scene.IsValid()) { Activate(dm.gameObject, on); any = true; }
        foreach (var arena in Resources.FindObjectsOfTypeAll<AcolyteSpawnOptions>())
            if (arena && arena.gameObject.scene.IsValid()) Activate(arena.gameObject, on);
        return any;
    }

    /// <summary>On: the object and every switched-off parent above it. Off: just the object.</summary>
    static void Activate(GameObject go, bool on)
    {
        if (!on) { go.SetActive(false); return; }
        for (var t = go.transform; t; t = t.parent)
            if (!t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(true);
                if (t.gameObject != go) NetLog.Info($"Dev mode: switched on '{t.name}' to reach the dev tools");
            }
    }

    // ---- the test arenas (DevArena: a name, a GameArea, a player spawn point, an enemy spawn polygon) ----
    //
    // The developer panel's "Test Arena" button loads a scene the demo doesn't ship (CreativeArea). The arenas
    // themselves are objects in scenes; the game's spawn panel (AcolyteSpawnOptions.UpdateArena) puts the player on
    // the arena's spawn point and makes its area current. We do the same with the game's own teleport (fade, area,
    // camera bounds), switching the arena's objects on first if the build left them off.

    static List<DevArena> arenas = new List<DevArena>();
    static string arenasScene;
    static float arenasAt;

    /// <summary>This scene's test arenas (looked up again every few seconds at most: the menu asks every frame).</summary>
    public static List<DevArena> ArenasHere()
    {
        if (arenasScene == LocalPlayer.Scene && Time.unscaledTime - arenasAt < 3f) return arenas;
        arenasScene = LocalPlayer.Scene;
        arenasAt = Time.unscaledTime;
        arenas = new List<DevArena>();
        foreach (var a in Resources.FindObjectsOfTypeAll<DevArena>())
            if (a && a.gameObject.scene.IsValid() && a.PlayerSpawn) arenas.Add(a);
        return arenas;
    }

    public static void GoTo(DevArena arena)
    {
        if (!arena || !LocalPlayer.Get() || !Singleton<GameManager>.HasMain) return;
        Activate(arena.gameObject, true);
        var area = arena.Area;
        if (area) Activate(area.gameObject, true);
        Singleton<GameManager>.main.TeleportPlayerFade(arena.PlayerSpawn.position, Direction.Down, area, area ? area.CameraConfiner : null).Forget();
        NetLog.Info($"Teleporting to the test arena \"{(string.IsNullOrEmpty(arena.ArenaName) ? arena.name : arena.ArenaName)}\"", toast: true);
    }

    /// <summary>/arena: to the first test arena in this scene, or say there's none.</summary>
    public static void GoToFirst()
    {
        var arenas = ArenasHere();
        if (arenas.Count == 0) { Chat.System("No test arena in this scene (the demo may not ship one here). Try another scene."); return; }
        GoTo(arenas[0]);
    }
}

/// <summary>
/// The developer panel's Creative Area button: the demo doesn't ship that scene, and loading it crashed the game's
/// scene loader (after it had already marked story steps done in your save). Said instead, and nothing changed.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(DevOptionsManager), nameof(DevOptionsManager.GoToCreativeArea))]
internal static class NoCreativeArea
{
    static bool Prefix()
    {
        if (UnityEngine.Application.CanStreamedLevelBeLoaded("CreativeArea")) return true;
        NetLog.Warn("The Creative Area isn't in this version of the game. /arena goes to this scene's test arena instead.");
        return false;
    }
}

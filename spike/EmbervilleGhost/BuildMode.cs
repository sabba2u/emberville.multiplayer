using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EmbervilleGhost;

/// <summary>
/// Build mode anywhere (B): the game's own build mode (PlaceableController: the build bar, the ghost on its grid,
/// left click place, wheel turn, right click / Esc stop), normally only on the estate, behind a story flag the demo
/// never sets. B turns it on wherever the controller is loaded, not in dungeons. Where it isn't (the demo has none,
/// not even in Town) B opens our own placer instead (FreeBuild). While it's on, the estate's trigger can't switch it off, Roads and Terrain are hidden (those are the
/// estate's painted tiles), and every blueprint shows. Free placing, the walls check: BuildRules. Saving: BuildSave.
/// </summary>
[HarmonyPatch]
internal static class BuildMode
{
    /// <summary>Off for now: the demo has no build controller anywhere, and our own placer isn't good enough yet.
    /// While off: B does nothing, nothing built is loaded or saved, the Insert menu has no Construction tab.</summary>
    public static readonly bool Enabled = false;

    static ConfigEntry<Key> keyEntry;
    public static bool On { get; private set; }
    /// <summary>On with our own placer (no build controller here).</summary>
    public static bool Free { get; private set; }
    public static string KeyName => keyEntry != null ? keyEntry.Value.ToString() : "B";
    static bool switching;   // our own SetPlacementMode calls
    static string onScene, lastScene;
    static readonly List<PlaceableData> madeKnown = new List<PlaceableData>();
    static bool warnedLocked;

    public static void Bind(ConfigFile config) =>
        keyEntry = config.Bind("Build", "Key", Key.B, "Turns build mode on and off outside the estate (tutorial area, Town)");

    /// <summary>The build controller, if this scene has one (never Singleton.main: that makes an empty one).</summary>
    public static PlaceableController Controller => Singleton<PlaceableController>.HasMain ? Singleton<PlaceableController>.main : null;

    /// <summary>Is this spot inside the estate's zone (the game's own build area)?</summary>
    public static bool InEstate(Vector3 at)
    {
        var c = Controller;
        if (!c || !c.triggerForward) return false;
        foreach (var col in c.triggerForward.GetComponents<Collider2D>())
            if (col && col.enabled && col.OverlapPoint(at)) return true;
        return false;
    }

    static Vector3 PlayerAt => LocalPlayer.Get() ? LocalPlayer.Get().transform.position : Vector3.zero;

    /// <summary>Our mode, with the player outside the estate's zone.</summary>
    public static bool Outside => On && !InEstate(PlayerAt);

    public static bool EstateUnlocked => GameData.HasWorldData && GameData.gameBools.IsTrue(BoolID.PlaceablesActive);

    static bool Unbuildable(string scene) => scene == "DungeonScene" || scene == "MainMenu" || string.IsNullOrEmpty(scene);

    public static void Tick()
    {
        string scene = LocalPlayer.Scene;
        if (scene != lastScene)
        {
            lastScene = scene;
            Plugin.Log.LogInfo($"Build mode: scene {scene}, build controller here: {Controller != null}");
        }
        if (On && (scene != onScene || Free == (bool)Controller || !GameData.HasWorldData)) SetOn(false);
        if (On && !Free) WarnLocked();
        FreeBuild.Tick();

        var kb = Keyboard.current;
        if (kb == null || keyEntry == null || !Application.isFocused || Chat.IsOpen || ImGuiUnity.Typing) return;
        if (!kb[keyEntry.Value].wasPressedThisFrame) return;
        // (a game screen up or another mod's text box: the game's controls are off then; "b" is a letter)
        if (!MainInput.Actions.Player.enabled || UIPanels.HasOpenedPanel) return;
        if (On) { SetOn(false); return; }
        TryOn();
    }

    /// <summary>Turn it on if it can be here (also from a placeable item used from the bag).</summary>
    public static bool TryOn()
    {
        if (On) return true;
        if (PlayerStatusUtil.LocalBusy) return false;
        if (!GameData.HasWorldData || !Singleton<GameManager>.HasMain || Singleton<GameManager>.main.gameState != GameState.Playing) return false;
        if (Unbuildable(LocalPlayer.Scene)) { NetLog.Info("No building here", toast: true); return false; }
        SetOn(true);
        return On;
    }

    public static void Leave() => SetOn(false);

    static void SetOn(bool on)
    {
        if (on == On) return;
        var c = Controller;
        On = on;
        if (on && !c)
        {
            onScene = LocalPlayer.Scene;
            Free = true;
            FreeBuild.Open();
            NetLog.Info($"Build mode: {keyEntry.Value} to leave", toast: true);
            return;
        }
        if (!on && Free)
        {
            Free = false;
            FreeBuild.Close();
            onScene = null;
            return;
        }
        if (on)
        {
            onScene = LocalPlayer.Scene;
            warnedLocked = false;
            Switch(c, true);
            Tabs(c, false);
            foreach (var p in c.dataBase.placeables)
                if (p && p.type == PlaceableType.Blueprint && !p.startAsKnown) { p.startAsKnown = true; madeKnown.Add(p); }
            NetLog.Info($"Build mode: {keyEntry.Value} to leave", toast: true);
        }
        else
        {
            foreach (var p in madeKnown) if (p) p.startAsKnown = false;
            madeKnown.Clear();
            if (c)
            {
                if (c.isPlacing) c.StopPlacing();
                Tabs(c, true);
                // standing on an unlocked estate: its own build mode stays (as if we'd walked in)
                if (!(EstateUnlocked && InEstate(PlayerAt))) Switch(c, false);
            }
            onScene = null;
        }
    }

    /// <summary>The game's SetPlacementMode, past its story flag (set only for the call).</summary>
    static void Switch(PlaceableController c, bool value)
    {
        if (!c) return;
        // (straight into the flags' dictionary: its indexer fires OnValueChanged, which StorySync sends to everyone)
        var flags = GameData.gameBools.dict;
        bool had = flags.TryGetValue(BoolID.PlaceablesActive, out bool was);
        switching = true;
        try
        {
            flags[BoolID.PlaceablesActive] = true;
            c.SetPlacementMode(value);
        }
        finally
        {
            if (had) flags[BoolID.PlaceablesActive] = was; else flags.Remove(BoolID.PlaceablesActive);
            switching = false;
        }
    }

    /// <summary>Roads and Terrain: the estate's painted tiles, only saved there.</summary>
    static void Tabs(PlaceableController c, bool show)
    {
        if (c.buttonRoads) c.buttonRoads.gameObject.SetActive(show);
        if (c.buttonTerrain) c.buttonTerrain.gameObject.SetActive(show);
    }

    static void WarnLocked()
    {
        bool inside = InEstate(PlayerAt);
        if (!inside) { warnedLocked = false; return; }
        if (EstateUnlocked || warnedLocked) return;
        warnedLocked = true;
        NetLog.Info("The estate is locked: tick Insert > Construction to build on it", toast: true);
    }

    /// <summary>The scene's controller going away (a door, a pad, a teleport) with our mode on: switch it off while
    /// it still exists, so its inputs, handlers and the player's attacks aren't left behind.</summary>
    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.OnDestroy)), HarmonyPrefix]
    static void Leaving(PlaceableController __instance)
    {
        if (!On || !Singleton<PlaceableController>.HasMain || Singleton<PlaceableController>.main != __instance) return;
        try { SetOn(false); }
        catch (System.Exception e) { Plugin.Log.LogWarning($"Build mode: switching off as the area unloads: {e.Message}"); On = false; }
    }

    /// <summary>The estate's trigger (walking out of it) can't switch our mode off.</summary>
    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.SetPlacementMode)), HarmonyPrefix]
    static bool KeepOn(bool value) => switching || value || !On;
}

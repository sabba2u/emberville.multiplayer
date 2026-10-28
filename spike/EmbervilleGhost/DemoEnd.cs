using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleGhost;

/// <summary>
/// The demo's ending: after Gumu's cutscene in Town the demo freezes you (CutsceneGumu.DemoLock) and shows
/// "THANK YOU FOR PLAYING!" (a "=== ThanksForPlaying" object in the Town scene). With KeepPlaying on, both are skipped
/// so you can carry on walking around (the estate, the mansion, ...).
/// </summary>
internal static class DemoEnd
{
    static ConfigEntry<bool> keepPlaying;
    static float scanAt = -1f;
    static readonly string[] Endings = { "=== ThanksForPlaying", "ThanksForPlaying", "DemoLock" };

    public static void Bind(ConfigFile config)
    {
        keepPlaying = config.Bind("Demo", "KeepPlaying", true, "Skip the demo's 'Thank you for playing' screen and freeze, so you can keep walking around");
        SceneManager.sceneLoaded += (_, _) => scanAt = Time.unscaledTime + 0.5f;
    }

    public static bool On => keepPlaying != null && keepPlaying.Value;

    public static void Toggle(bool on)
    {
        keepPlaying.Value = on;
        if (on) { scanAt = Time.unscaledTime; Unfreeze(); }
    }

    public static void Tick()
    {
        if (scanAt < 0 || Time.unscaledTime < scanAt) return;
        scanAt = -1f;
        if (On) RemoveEnding();
    }

    static void RemoveEnding()
    {
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            var scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!t || System.Array.IndexOf(Endings, t.name) < 0) continue;
                    NetLog.Info($"Demo ending removed: {t.name} ({t.childCount} children) in {scene.name}");
                    Object.Destroy(t.gameObject);
                }
        }
    }

    static void Unfreeze()
    {
        if (Singleton<GameManager>.HasMain) Singleton<GameManager>.main.player?.RemoveStaticStack(typeof(CutsceneGumu));
    }

    [HarmonyPatch(typeof(CutsceneGumu), nameof(CutsceneGumu.DemoLock))]
    static class SkipDemoLock
    {
        static bool Prefix()
        {
            if (!On) return true;
            scanAt = Time.unscaledTime;   // and take the screen down if it's on its way up
            return false;
        }
    }

    /// <summary>The ending's timers (TimedTrigger, waiting for the game to start) never start: removing the ending
    /// later would cancel them mid-wait, and the game logs that as an OperationCanceledException.</summary>
    [HarmonyPatch(typeof(TimedTrigger), nameof(TimedTrigger.Trigger))]
    static class SkipEndingTimers
    {
        static bool Prefix(TimedTrigger __instance)
        {
            if (!On) return true;
            for (var t = __instance.transform; t; t = t.parent)
                if (System.Array.IndexOf(Endings, t.name) >= 0) return false;
            return true;
        }
    }

    /// <summary>Gumu runs a shop in Town (the game's own: a StoreComponent named "Gumu", stocked), which the demo never
    /// opens: his intro sends him off instead. By the time you reach him the demo is done, so talking to him (F) opens
    /// his shop, always.</summary>
    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.OnInteract))]
    static class GumuShop
    {
        static bool Prefix(DialogueTrigger __instance)
        {
            if (!IsGumu(__instance.transform)) return true;
            StoreComponent shop = null;
            foreach (var s in Object.FindObjectsByType<StoreComponent>(FindObjectsSortMode.None))
                if (s && s.storeName == "Gumu") { shop = s; break; }
            if (!shop || !Singleton<StoreManager>.HasMain || !Singleton<StoreManager>.main.HasID(shop.ID)) return true;   // (not set up yet: his talk)
            if (!shop.isOpened) shop.OpenStore();
            Plugin.Log.LogInfo("Gumu: his shop opened");
            return false;
        }

        static bool IsGumu(Transform t)
        {
            for (; t; t = t.parent) if (t.name.IndexOf("Gumu", System.StringComparison.OrdinalIgnoreCase) >= 0 && t.name.IndexOf("Cutscene", System.StringComparison.OrdinalIgnoreCase) < 0 && !t.name.StartsWith("===")) return true;
            return false;
        }
    }
}

using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleGhost;

/// <summary>
/// Faster loading: while the game's loading screen is up (SceneLoader's three ways of loading a scene), Unity's
/// background loading runs at High priority instead of the game's BelowNormal, through the scene switching on; the
/// game's own setting comes back once you're playing (or back on the main menu, or after a few minutes whatever
/// happens). Loading a world went from about 100 s to under 20 in a timed test; the loading screen may stutter a bit.
/// </summary>
[HarmonyPatch]
internal static class FastLoad
{
    const float GiveUpAfter = 300f;   // (seconds: the game's own setting back, however the load went)

    static ConfigEntry<bool> on;
    static bool boosted;
    static ThreadPriority before;
    static float since;
    static string from;   // (the scene the load started in: a scene change, then the end can be looked for)

    public static void Bind(ConfigFile config) =>
        on = config.Bind("Loading", "FastLoad", true, "Load scenes at high priority while the loading screen is up (much faster world loading)");

    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.CallScene)), HarmonyPrefix]
    static void Scene() => Boost();
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.CallSceneNoProgress)), HarmonyPrefix]
    static void SceneNoProgress() => Boost();
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.CallSceneAsync)), HarmonyPrefix]
    static void SceneAsync() => Boost();

    static void Boost()
    {
        if (on is { Value: false }) return;
        since = Time.realtimeSinceStartup;
        from = SceneManager.GetActiveScene().name;
        if (boosted) return;
        before = Application.backgroundLoadingPriority;
        Application.backgroundLoadingPriority = ThreadPriority.High;
        boosted = true;
        NetLog.Info($"Fast load: loading at High priority (the game's: {before})", toast: false);
    }

    /// <summary>Every frame (Plugin.Update): the load over, the game's own setting back.</summary>
    public static void Tick()
    {
        if (!boosted) return;
        float took = Time.realtimeSinceStartup - since;
        bool moved = SceneManager.GetActiveScene().name != from;
        bool playing = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player && Singleton<GameManager>.main.gameState == GameState.Playing;
        bool menu = SceneManager.GetActiveScene().name == "MainMenu";
        if (!(moved && (playing || menu)) && took < GiveUpAfter) return;
        Application.backgroundLoadingPriority = before;
        boosted = false;
        NetLog.Info($"Fast load: done in {took:0.0}s, back to {before}", toast: false);
    }
}

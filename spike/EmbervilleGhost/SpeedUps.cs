using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Cysharp.Threading.Tasks;
using DungeonGeneration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// Loading speed-ups, after snowshackle's loading patch (github.com/snowshackle/emberville-loading-patch): his ideas,
/// our own code, done as patches while the game runs (no game files changed). Each can be switched off in the config.
/// - Bar: once a load is done the game still makes its bar crawl to full (half a bar a second, at most 0.02 s of it a
///   frame); done is now full at once.
/// - Inventory: the game loads your saved items one after another (a frame or more each); each kind of item is now
///   loaded once, all at the same time, and the items made just as the game makes them.
/// - Map building: picking the next cell sorted every cell left by a sum it redid for every option of every cell;
///   now each sum is done once and the lowest found in one pass. The same numbers in the same order, so the same
///   seed builds exactly the same map as without it (other players with or without it match).
/// - Budget: the game gives its loading work 5 ms a frame (to keep the game smooth); behind the loading screen
///   there's nothing to keep smooth, so it gets 40 (the map pictures, the regions, the props).
/// - Fade: the fades into the game after a load are quicker (0.15 s, not 0.4 + 0.5 held to at least 34 frames each).
/// </summary>
[HarmonyPatch]
internal static class SpeedUps
{
    static ConfigEntry<bool> bar, inventory, wfc, budget, fade;

    public static void Bind(ConfigFile config)
    {
        bar = config.Bind("Loading", "FullBarWhenDone", true, "A finished load fills the loading bar at once (the game makes it crawl to full)");
        inventory = config.Bind("Loading", "FastInventory", true, "Load your saved items all at once (the game loads them one by one)");
        wfc = config.Bind("Loading", "FastMapBuilding", true, "Faster dungeon map building (the same seed still builds exactly the same map)");
        budget = config.Bind("Loading", "LoadingBudget", true, "Behind the loading screen the game's loading work gets 40 ms a frame instead of 5");
        fade = config.Bind("Loading", "QuickFadeIn", true, "Quicker fades into the game after a load");
    }

    // ---- the bar: done is full ----

    [HarmonyPatch(typeof(LoadingProgress), nameof(LoadingProgress.UpdateProgress)), HarmonyPrefix]
    static void FullWhenDone(LoadingProgress __instance, float value)
    {
        if (bar is { Value: false } || value < 1f || __instance.currentProgress >= 1f) return;
        __instance.currentProgress = __instance.targetProgress = 1f;
        __instance.UpdateBar(1f);
    }

    // ---- your items: every kind at once ----

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.GetPlayerInventory)), HarmonyPrefix]
    static bool AllAtOnce(PlayerData __instance, ref UniTask<List<InventoryItem>> __result)
    {
        if (inventory is { Value: false }) return true;
        __result = Items(__instance);
        return false;
    }

    /// <summary>As the game's (busy: null; the items in the save's order, any that won't load left out), but each
    /// kind loaded once and all of them at the same time.</summary>
    static async UniTask<List<InventoryItem>> Items(PlayerData data)
    {
        if (data.isLoading) return null;
        data.isLoading = true;
        try
        {
            var saved = data.playerInventory.ToList();
            var loads = new Dictionary<string, AsyncOperationHandle<ItemData>>();
            foreach (var item in saved)
                if (item.Key != null && !loads.ContainsKey(item.Key)) loads[item.Key] = Addressables.LoadAssetAsync<ItemData>(item.Key);
            foreach (var h in loads.Values) await h.Task;   // (all started already: this just waits for the last)
            var result = new List<InventoryItem>();
            foreach (var item in saved)
            {
                if (item.Key == null || !loads.TryGetValue(item.Key, out var h) || h.Status != AsyncOperationStatus.Succeeded) continue;
                var itemData = h.Result;
                item.Values.SetData(itemData);
                result.Add(new InventoryItem(new ItemObject(itemData, item.Amount) { randomValues = item.Values }, item.SlotIndex, item.Section));
            }
            return result;
        }
        finally { data.isLoading = false; }   // (the game's never lets go if a load throws)
    }

    // ---- map building: the next cell, found in one pass ----

    [HarmonyPatch(typeof(WFCMap), nameof(WFCMap.SelectCellHeuristic)), HarmonyPrefix]
    static bool NextCell(WFCMap __instance, ref ICell __result)
    {
        if (wfc is { Value: false }) return true;
        // the game's: the first of the cells left with the lowest entropy (its sort keeps the order of equals, and
        // puts NaN first: double.CompareTo does the same)
        ICell best = null;
        double low = 0.0;
        int left = 0;
        foreach (var c in __instance._mapCellsArray)
        {
            if (c.IsCollapsed) continue;
            left++;
            double e = Entropy(c);
            if (best == null || e.CompareTo(low) < 0) { best = c; low = e; }
        }
        if (best != null && __instance.needsDebug)
        {
            Debug.Log($"[WFC] {left} Remain");
            __instance.needsDebug = false;
        }
        __result = best;
        Check(__instance, best);
        return false;
    }

    // the proof: the first picks of each session worked out the game's way too, any difference logged
    const int CheckFirst = 300;
    static int checkedPicks, differed;

    static void Check(WFCMap map, ICell ours)
    {
        if (checkedPicks >= CheckFirst) return;
        checkedPicks++;
        var left = map._mapCellsArray.Where(c => !c.IsCollapsed);
        var theirs = left.Any() ? left.OrderBy(c => c.Entropy()).First() : null;
        if (!ReferenceEquals(theirs, ours) && differed++ < 5)
            NetLog.Warn($"Map building: our pick differs from the game's at pick {checkedPicks} (switch FastMapBuilding off in the config)", toast: false);
        if (checkedPicks == CheckFirst && differed == 0) NetLog.Info($"Map building: the first {CheckFirst} picks matched the game's exactly", toast: false);
    }

    /// <summary>The cell's entropy, worked out exactly as the game's (the same sums, in the same order: the same
    /// bits), its total done once instead of once per option.</summary>
    static double Entropy(ICell c)
    {
        switch (c)
        {
            case BaseCell b:
            {
                var o = b.Options;
                double sum = o.Sum(y => y.Probability);
                return Utils.ShannonEntropy(o, x => x.Probability / sum);
            }
            case HorizontalBarrierCell h:
            {
                var o = h.Options;
                double sum = o.Sum(y => y.Probability);
                return Utils.ShannonEntropy(o, x => x.Probability / sum);
            }
            case VerticalBarrierCell v:
            {
                var o = v.Options;
                double sum = o.Sum(y => y.Probability);
                return Utils.ShannonEntropy(o, x => x.Probability / sum);
            }
            default:
                return c.Entropy();
        }
    }

    // ---- the loading work's budget, behind the loading screen ----

    const int Budget = 40;
    static bool raised;

    /// <summary>Every frame (Plugin.Update): the loading screen up, the loading work gets more of each frame; gone,
    /// the game's 5 back (unless the game set its own since: the world map's 30).</summary>
    public static void Tick()
    {
        bool loading = budget is { Value: true } && Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.loadingProgress && Singleton<SceneLoader>.main.loadingProgress.IsShowing;
        if (loading && BackgroundTaskManager.AllocatedTime < Budget)
        {
            if (!raised) NetLog.Info($"Loading budget: {BackgroundTaskManager.AllocatedTime} ms a frame -> {Budget} behind the loading screen", toast: false);
            BackgroundTaskManager.AllocatedTime = Budget;
            raised = true;
        }
        else if (!loading && raised)
        {
            if (BackgroundTaskManager.AllocatedTime == Budget) BackgroundTaskManager.AllocatedTime = BackgroundTaskManager.DEFAULT_TIME;
            raised = false;
        }
    }

    // ---- the fades into the game after a load ----

    const float QuickFade = 0.15f;
    static bool starting;

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.FadeInStart)), HarmonyPrefix]
    static void Starting() => starting = fade is not { Value: false };

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartGameplay)), HarmonyPrefix]
    static void Started() => starting = false;

    /// <summary>The fades out of the loading screen and into the game: ours, quicker (the game's holds each to at
    /// least 34 frames, slow ones right after a load). Any other fade (a door, a death) is the game's.</summary>
    [HarmonyPatch(typeof(FaderManager), nameof(FaderManager.Fade)), HarmonyPrefix]
    static bool Quick(FaderManager __instance, FadeEvent fade)
    {
        if (!starting || fade.Fade == FadeType.Blackout || fade.ShowDeadScreen) return true;
        QuickFadeRun(__instance, fade).Forget();
        return false;
    }

    static async UniTaskVoid QuickFadeRun(FaderManager fm, FadeEvent fade)
    {
        try
        {
            var g = fm.mainGroup;
            bool toBlack = fade.Fade == FadeType.FadeOut;
            g.alpha = toBlack ? 0f : 1f;
            g.gameObject.SetActive(true);
            fm.deadObject.SetActive(false);
            float t = 0f;
            while (t <= 1f)
            {
                g.alpha = fm.fadeCurve.Evaluate(toBlack ? t : 1f - t);
                t += Time.unscaledDeltaTime / QuickFade;
                await UniTask.NextFrame(fm.tokenSource.Token);
            }
            g.alpha = toBlack ? 1f : 0f;
            fade.Callback?.Invoke();
            g.gameObject.SetActive(toBlack);
        }
        catch (System.OperationCanceledException) { }   // (the fader gone: a scene change, as the game's)
    }
}

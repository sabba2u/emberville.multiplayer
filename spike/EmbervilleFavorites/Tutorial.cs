using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;

namespace EmbervilleFavorites;

/// <summary>
/// How-to popup in the game's own tutorial window (TutorialManager: title, text, a looping video, its "Got it!"
/// button). The window's parts normally come from the game's tutorial assets, so we make one of those in code, and
/// two small patches supply what it can't hold: our text (the parts read localised strings) and our video file
/// (the parts take a built-in clip). Shown once the first time you play with the mod, and again with Shift+F5.
///
/// The video: Tutorials/favorites.webm (or .mp4) next to the DLL, 310x180 like the game's own; without it the game's
/// inventory clip stands in.
/// </summary>
internal static class Tutorial
{
    static ConfigEntry<bool> shown;
    static float readyAt = -1f;

    public static void Bind(ConfigFile config) =>
        shown = config.Bind("Tutorial", "Shown", false, "The how-to popup has been shown (set false to see it again on next load; Shift+F5 shows it any time)");

    public static void Tick()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.shiftKey.isPressed && kb.f5Key.wasPressedThisFrame && Playing) { Show(); return; }
        if (shown.Value) return;
        // first time: a few seconds after we're playing
        if (!Playing) { readyAt = -1f; return; }
        if (readyAt < 0f) readyAt = Time.unscaledTime + 4f;
        if (Time.unscaledTime < readyAt) return;
        shown.Value = true;
        Show();
    }

    static bool Playing => Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Playing &&
                           Singleton<TutorialManager>.HasMain && !UIPanels.HasOpenedPanel;

    // ---- the window ----

    static readonly Dictionary<TutorialModule, string> texts = new Dictionary<TutorialModule, string>();
    static string videoFile;
    static TutorialDataWindow data;

    public static void Show()
    {
        if (!Singleton<TutorialManager>.HasMain) return;
        if (!data)
        {
            data = ScriptableObject.CreateInstance<TutorialDataWindow>();
            data.name = "EF_FavoritesTutorial";
            data.id = "EF_Favorites";
            data.displayOnce = false;
            data.stopTime = true;
            data.hideFlags = HideFlags.HideAndDontSave;
            Add(new TutorialModule.Title(), "Favourite weapons");
            data.modules.Add(new TutorialModule.Video());
            Add(new TutorialModule.TextArea(), "In your bag, point at a weapon and press F5, F6, F7 or F8 to put it on that key. Its tooltip shows the key.");
            Add(new TutorialModule.TextArea(), "While playing, press the key (or click its slot above your belt) to wield it straight away.");
            Add(new TutorialModule.TextArea(), "Hold Left Alt (gamepad: left trigger) for the weapon wheel: point at one and let go.");
        }
        // the video: ours if it's there, else the game's inventory clip
        videoFile = FindVideo();
        var video = data.modules.OfType<TutorialModule.Video>().First();
        video.video = videoFile != null ? null : GameClip("tut_Inventory");
        Singleton<TutorialManager>.main.ShowModularTutorial(data);
    }

    static void Add(TutorialModule module, string text)
    {
        texts[module] = text;
        data.modules.Add(module);
    }

    static string FindVideo()
    {
        var dir = Path.Combine(Path.GetDirectoryName(typeof(Tutorial).Assembly.Location) ?? "", "Tutorials");
        foreach (var name in new[] { "favorites.webm", "favorites.mp4" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>One of the game's tutorial clips (from its own tutorial list).</summary>
    static VideoClip GameClip(string name)
    {
        var db = Singleton<TutorialManager>.main.dataBase;
        if (db)
            foreach (var t in db.tutorials.OfType<TutorialDataWindow>())
                foreach (var m in t.modules.OfType<TutorialModule.Video>())
                    if (m.video && m.video.name == name) return m.video;
        return Resources.FindObjectsOfTypeAll<VideoClip>().FirstOrDefault(c => c.name == name);
    }

    // ---- our text and video in the game's parts ----

    [HarmonyPatch(typeof(TutorialModule.Title), nameof(TutorialModule.Title.ActualText), MethodType.Getter)]
    static class TitleText
    {
        static bool Prefix(TutorialModule.Title __instance, ref string __result) => !texts.TryGetValue(__instance, out __result);
    }

    [HarmonyPatch(typeof(TutorialModule.TextArea), nameof(TutorialModule.TextArea.ActualText), MethodType.Getter)]
    static class BodyText
    {
        static bool Prefix(TutorialModule.TextArea __instance, ref string __result) => !texts.TryGetValue(__instance, out __result);
    }

    /// <summary>No clip = play our file. (The video part is pooled and reused, so a clip always switches it back.)</summary>
    [HarmonyPatch(typeof(TutorialVideoComponent), nameof(TutorialVideoComponent.SetVideo))]
    static class VideoFile
    {
        static bool Prefix(TutorialVideoComponent __instance, VideoClip clip)
        {
            var player = __instance.videoPlayer;
            if (!player) return true;
            if (clip == null && videoFile != null)
            {
                player.source = VideoSource.Url;
                player.url = "file://" + videoFile.Replace('\\', '/');
                player.isLooping = true;
                return false;
            }
            player.source = VideoSource.VideoClip;
            return true;
        }
    }
}

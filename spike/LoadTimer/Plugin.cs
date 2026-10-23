using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoadTimer;

/// <summary>
/// How long the game takes to start, and to load a world: every step timed from the moment the game's process started,
/// written to BepInEx/LoadTimes.log (one block per run, with the mods that were loaded): the plugins loaded, each scene
/// loading (the game's SceneLoader.CallScene) and loaded, the main menu ready, a world chosen, the player in it and
/// playing; and every frame that took longer than a quarter of a second while loading (where the time goes in big
/// lumps), with the total. Works with or without the other mods (to compare: the game alone vs with ours).
/// </summary>
[BepInPlugin("spike.emberville.loadtimer", "Load Timer", "0.1.0")]
[HarmonyPatch]   // (PatchAll only takes the patches of a class marked so)
public class Plugin : BaseUnityPlugin
{
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static DateTime started;
    static StreamWriter log;
    static string loadingScene;
    static float loadingSince = -1f;
    static bool menuSeen, playing;
    static readonly List<(float at, float ms)> hitches = new();
    static float lastFrame;

    static double Now => (DateTime.Now - started).TotalSeconds;

    void Awake()
    {
        started = Process.GetCurrentProcess().StartTime;
        var path = Path.Combine(Paths.BepInExRootPath, "LoadTimes.log");
        log = new StreamWriter(path, append: true) { AutoFlush = true };
        log.WriteLine();
        log.WriteLine($"==== Run {DateTime.Now:yyyy-MM-dd HH:mm:ss}  (times: seconds since the game started) ====");
        Write("our plugin awake (BepInEx is loading plugins)");
        SceneManager.sceneLoaded += (s, _) => Write($"scene loaded: {s.name}");
        new Harmony("spike.emberville.loadtimer").PatchAll(typeof(Plugin).Assembly);
    }

    void Start()
    {
        var mods = Chainloader.PluginInfos.Values.Select(p => $"{p.Metadata.Name} {p.Metadata.Version}").OrderBy(n => n);
        Write($"all plugins loaded: {string.Join(", ", mods)}");
    }

    static void Write(string what)
    {
        log?.WriteLine($"{Now,8:0.00}s  {what}");
        Plugin.Instance?.Logger.LogInfo($"Load Timer {Now:0.00}s: {what}");
    }

    static Plugin Instance;
    void OnEnable() => Instance = this;

    /// <summary>A world picked on the main menu: the clock for "clicked to playing" starts here (not the time spent in menus).</summary>
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.LoadWorldSave)), HarmonyPrefix]
    static void WorldPicked(string fileName) => Begin($"world picked: {fileName}");

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.CreateNewWorld)), HarmonyPrefix]
    static void WorldCreated() => Begin("new world");

    static void Begin(string what)
    {
        if (loadingSince >= 0f && !playing) return;
        loadingSince = Time.realtimeSinceStartup;
        playing = false;
        hitches.Clear();
        Write(what);
    }

    /// <summary>The game starting a scene load (the main menu's Play, a door to another scene).</summary>
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.CallScene)), HarmonyPrefix]
    static void Calling(string scene)
    {
        loadingScene = scene;
        Begin($"loading {scene} (the game's loading screen)");
    }

    void Update()
    {
        float now = Time.realtimeSinceStartup, dt = now - lastFrame;
        lastFrame = now;
        bool loading = loadingSince >= 0f && !playing;
        if (loading && dt > 0.25f) hitches.Add(((float)Now, dt * 1000f));

        if (!menuSeen && SceneManager.GetActiveScene().name == "MainMenu" && FindAnyObjectByType<MainMenuManager>())
        {
            menuSeen = true;
            Write("main menu up");
        }
        if (!playing && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player && Singleton<GameManager>.main.gameState == GameState.Playing)
        {
            playing = true;
            float took = loadingSince >= 0f ? now - loadingSince : -1f;
            Write($"PLAYING in {SceneManager.GetActiveScene().name}" + (took >= 0f ? $": {took:0.0}s from the click to playing" : ""));
            if (hitches.Count > 0)
            {
                float total = hitches.Sum(h => h.ms) / 1000f;
                Write($"  {hitches.Count} long frames while loading (over 0.25 s), {total:0.0}s of it; the longest: " +
                      string.Join(", ", hitches.OrderByDescending(h => h.ms).Take(8).Select(h => $"{h.ms / 1000f:0.0}s at {h.at:0.0}s")));
            }
            loadingSince = -1f;
        }
    }
}

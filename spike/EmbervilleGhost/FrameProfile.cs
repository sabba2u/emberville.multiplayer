using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// /perf: where the frame goes, among the mods. For 10 seconds every mod's Update / LateUpdate / FixedUpdate / OnGUI
/// (every plugin in BepInEx/plugins, not only ours) and every Harmony prefix and postfix on the game's methods are
/// timed; then the costliest are logged (ms a frame, calls a frame), with the frame rate, and the timing comes off
/// again. (Nested ones count twice: an Update calling a patched method shows in both.)
/// </summary>
internal sealed class FrameProfile : MonoBehaviour
{
    const float Seconds = 10f;
    const string Id = "spike.embervilleghost.perf";

    static FrameProfile running;
    static readonly object gate = new object();
    static readonly Dictionary<MethodBase, (long ticks, int calls, long worst)> spent = new Dictionary<MethodBase, (long, int, long)>();
    readonly List<float> frameMs = new List<float>();   // (every frame's length: the hitches, not just the average)
    static readonly string[] FrameMethods = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

    Harmony harmony;
    float started, frameTime;
    int frames, timed;

    // the engine's own counters (draws: some only count in a development build; those report "n/a")
    static readonly (ProfilerCategory cat, string name, bool ns)[] Counters =
    {
        (ProfilerCategory.Internal, "Main Thread", true), (ProfilerCategory.Render, "Batches Count", false),
        (ProfilerCategory.Render, "Draw Calls Count", false), (ProfilerCategory.Render, "SetPass Calls Count", false),
        (ProfilerCategory.Memory, "GC Allocated In Frame", false),
    };
    readonly List<ProfilerRecorder> recorders = new List<ProfilerRecorder>();
    readonly double[] sums = new double[Counters.Length];

    public static void Start()
    {
        if (running) { NetLog.Info("Perf: already measuring", toast: true); return; }
        var go = new GameObject("EG_FrameProfile");
        DontDestroyOnLoad(go);
        running = go.AddComponent<FrameProfile>();
    }

    void Awake()
    {
        lock (gate) spent.Clear();
        harmony = new Harmony(Id);
        var pre = new HarmonyMethod(typeof(FrameProfile), nameof(Pre));
        var post = new HarmonyMethod(typeof(FrameProfile), nameof(Post));
        var targets = new HashSet<MethodBase>();
        string plugins = System.IO.Path.GetFullPath(BepInEx.Paths.PluginPath);
        // the mods' own per-frame methods
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string at;
            try { at = asm.IsDynamic ? "" : System.IO.Path.GetFullPath(asm.Location); } catch { continue; }
            if (!at.StartsWith(plugins, StringComparison.OrdinalIgnoreCase) || asm == typeof(Harmony).Assembly) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException x) { types = x.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                // the features the plugins' Updates call each frame: their static Tick()s (and the like)
                if (!t.ContainsGenericParameters)
                    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (TickLike(m.Name) && !m.ContainsGenericParameters && !m.IsAbstract && m.GetMethodBody() != null && m.GetParameters().Length <= 2) targets.Add(m);
                if (t == typeof(FrameProfile) || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                foreach (var name in FrameMethods)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (m != null && !m.IsAbstract && m.GetMethodBody() != null) targets.Add(m);
                }
            }
        }
        // every mod's prefixes and postfixes on the game's methods
        foreach (var original in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var p in info.Prefixes.Concat(info.Postfixes))
                if (p.owner != Id && p.PatchMethod != null && !p.PatchMethod.ContainsGenericParameters && p.PatchMethod.GetMethodBody() != null) targets.Add(p.PatchMethod);
        }
        var sw = Stopwatch.StartNew();
        foreach (var m in targets)
        {
            try { harmony.Patch(m, prefix: pre, postfix: post); timed++; }
            catch (Exception x) { NetLog.Info($"Perf: can't time {Name(m)}: {x.Message}", toast: false); }
        }
        NetLog.Info($"Perf: timing {timed} methods for {Seconds:0}s (set up in {sw.ElapsedMilliseconds} ms)", toast: true);
        foreach (var c in Counters) recorders.Add(ProfilerRecorder.StartNew(c.cat, c.name));
        started = Time.unscaledTime;
    }

    void OnDestroy() { foreach (var r in recorders) r.Dispose(); }

    static bool TickLike(string n) => n.StartsWith("Tick") || n == "Keep" || n == "Update" || n == "NetTick" || n.StartsWith("Render") || n == "CheckLeftWorld" || n == "SendLocalState" || n == "SendLookIfChanged";

    static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

    static void Post(long __state, MethodBase __originalMethod) => Add(__originalMethod, Stopwatch.GetTimestamp() - __state);

    /// <summary>Measuring now (/perf running).</summary>
    public static bool Measuring => running;

    /// <summary>Time spent in one method (or one drawing handler: the menu layer's, each mod's).</summary>
    public static void Add(MethodBase __originalMethod, long took)
    {
        if (__originalMethod == null) return;
        lock (gate)
        {
            spent.TryGetValue(__originalMethod, out var was);
            spent[__originalMethod] = (was.ticks + took, was.calls + 1, System.Math.Max(was.worst, took));
        }
    }

    void Update()
    {
        if (Time.unscaledTime - started < 0.5f) { lock (gate) spent.Clear(); return; }   // (the first moments: the patching's own hitch)
        frames++;
        frameTime += Time.unscaledDeltaTime;
        frameMs.Add(Time.unscaledDeltaTime * 1000f);
        for (int i = 0; i < recorders.Count; i++) if (recorders[i].Valid) sums[i] += recorders[i].LastValue;
        if (Time.unscaledTime - started < Seconds + 0.5f) return;
        try { Report(); }
        finally
        {
            harmony.UnpatchSelf();
            running = null;
            Destroy(gameObject);
        }
    }

    void Report()
    {
        List<KeyValuePair<MethodBase, (long ticks, int calls, long worst)>> rows;
        lock (gate) rows = spent.ToList();
        double perMs = Stopwatch.Frequency / 1000.0;
        float avg = frameTime / Mathf.Max(1, frames) * 1000f;
        double mods = rows.Sum(r => r.Value.ticks) / perMs / Mathf.Max(1, frames);
        NetLog.Info($"Perf: {frames} frames, {avg:0.0} ms a frame ({1000f / Mathf.Max(0.01f, avg):0} fps); the mods' timed methods {mods:0.00} ms a frame (nested ones counted twice)", toast: true);
        var counted = new List<string>();
        for (int i = 0; i < Counters.Length; i++)
        {
            bool has = recorders[i].Valid && sums[i] > 0;
            double v = sums[i] / Mathf.Max(1, frames);
            counted.Add($"{Counters[i].name} {(!has ? "n/a" : Counters[i].ns ? $"{v / 1e6:0.0} ms" : Counters[i].name.StartsWith("GC") ? $"{v / 1024:0} KB" : $"{v:0}")}");
        }
        NetLog.Info($"Perf: a frame: {string.Join(", ", counted)}", toast: false);
        // the hitches: frames far longer than the usual one (what you see as the picture jerking)
        var sorted = frameMs.OrderBy(x => x).ToList();
        float median = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0f;
        int hitches = frameMs.Count(x => x > median * 1.6f && x > 25f);
        NetLog.Info($"Perf: frames: usual {median:0.0} ms, slowest {(sorted.Count > 0 ? sorted[sorted.Count - 1] : 0):0.0} ms, 99% under {(sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.99f)] : 0):0.0} ms; {hitches} hitches (over {median * 1.6f:0} ms), {hitches / Seconds:0.#} a second", toast: false);
        foreach (var r in rows.Where(r => r.Value.worst / perMs >= 2.0).OrderByDescending(r => r.Value.worst).Take(15))
            NetLog.Info($"Perf:   worst single call {r.Value.worst / perMs,7:0.0} ms  {Name(r.Key)}", toast: false);
        var anims = FindObjectsOfType<Animator>();
        NetLog.Info($"Perf: animators {anims.Length} ({anims.Count(a => a.GetComponentInParent<Monster>())} on our creatures' hidden bodies), sprites drawn {FindObjectsOfType<SpriteRenderer>().Count(s => s.isVisible)}", toast: false);
        foreach (var r in rows.OrderByDescending(r => r.Value.ticks).Take(30))
            NetLog.Info($"Perf:   {r.Value.ticks / perMs / Mathf.Max(1, frames),7:0.000} ms  {r.Value.calls / (float)Mathf.Max(1, frames),6:0.#}x  {Name(r.Key)}", toast: false);
        // and what's piling up: ours left behind (effects, bombs, shots never cleaned up)
        var ours = FindObjectsOfType<Transform>().Where(t => t.name.StartsWith("EG_")).GroupBy(t => t.name).OrderByDescending(g => g.Count()).Take(8);
        NetLog.Info($"Perf: our objects about: {string.Join(", ", ours.Select(g => $"{g.Key} {g.Count()}"))}; enemies {FindObjectsOfType<Enemy>().Length}", toast: false);
    }

    static string Name(MethodBase m) => $"{m.DeclaringType?.Assembly.GetName().Name}: {m.DeclaringType?.Name}.{m.Name}";
}

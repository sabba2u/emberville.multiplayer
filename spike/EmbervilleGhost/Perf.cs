using System.Collections.Generic;
using System.Diagnostics;

namespace EmbervilleGhost;

/// <summary>Tiny per-section timer for the debug panel: Perf.Begin("x") ... Perf.End("x"), smoothed ms per frame.</summary>
internal static class Perf
{
    static readonly Dictionary<string, Stopwatch> running = new Dictionary<string, Stopwatch>();
    public static readonly SortedDictionary<string, float> Ms = new SortedDictionary<string, float>();

    public static void Begin(string name)
    {
        if (!running.TryGetValue(name, out var sw)) running[name] = sw = new Stopwatch();
        sw.Restart();
    }

    public static void End(string name)
    {
        if (!running.TryGetValue(name, out var sw)) return;
        float ms = (float)sw.Elapsed.TotalMilliseconds;
        Ms[name] = Ms.TryGetValue(name, out var old) ? old + (ms - old) * 0.1f : ms;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterStreaming.Core;

/// <summary>The timed effects running: buying one that's already on adds its time instead of doubling it.</summary>
public sealed class Timers
{
    readonly Func<double> now;
    readonly Dictionary<string, double> until = new Dictionary<string, double>();

    public Timers(Func<double> now) { this.now = now; }

    public void Start(string id, double seconds)
    {
        double t = now();
        until[id] = (until.TryGetValue(id, out double u) && u > t ? u : t) + seconds;
    }

    public bool Active(string id) => until.TryGetValue(id, out double u) && u > now();
    public double Left(string id) => until.TryGetValue(id, out double u) ? Math.Max(0, u - now()) : 0;
    public IEnumerable<string> Running => until.Where(kv => kv.Value > now()).Select(kv => kv.Key).ToList();

    /// <summary>The ones that ran out since last asked (each once).</summary>
    public IEnumerable<string> Expired()
    {
        double t = now();
        var done = until.Where(kv => kv.Value <= t).Select(kv => kv.Key).ToList();
        foreach (var id in done) until.Remove(id);
        return done;
    }

    public void Stop(string id) => until.Remove(id);
}

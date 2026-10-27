using System.Collections.Generic;
using System.Linq;

namespace BetterStreaming.Core;

/// <summary>
/// A chat vote ("!raid"): it passes when enough different chatters vote within the window; then a cooldown in which
/// votes don't count, and it starts again from nothing.
/// </summary>
public sealed class VoteCounter
{
    readonly int needed;
    readonly double window, cooldown;
    readonly Dictionary<string, double> votes = new Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
    double coolUntil = double.NegativeInfinity;

    public VoteCounter(int needed, double window, double cooldown) { this.needed = needed; this.window = window; this.cooldown = cooldown; }

    public int Count => votes.Count;

    /// <summary>How long it still rests after passing (0: votes count).</summary>
    public double RestLeft(double now) => System.Math.Max(0.0, coolUntil - now);

    /// <summary>A vote at that time: true when it's the one that passes it.</summary>
    public bool Vote(string user, double now)
    {
        if (string.IsNullOrEmpty(user) || now < coolUntil) return false;
        foreach (var old in votes.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList()) votes.Remove(old);
        votes[user] = now;
        if (votes.Count < needed) return false;
        votes.Clear();
        coolUntil = now + cooldown;
        return true;
    }
}

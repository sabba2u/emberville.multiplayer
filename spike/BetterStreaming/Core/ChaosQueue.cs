using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BetterStreaming.Core;

/// <summary>One bought effect waiting its turn: what, who paid, their (filtered) words, how big, and from where.</summary>
public sealed class Pending
{
    public string EffectId;
    public string Viewer;
    public string Message;
    public float Size = 1f;
    /// <summary>The Bits a cheer paid for it (0: not a cheer).</summary>
    public int Bits;
    /// <summary>bits, points, vote, poll, wheel, dice or test.</summary>
    public string Source;
    /// <summary>A channel-point redemption to mark fulfilled when it lands (else empty).</summary>
    public string RedemptionId;
    public Mood Mood;
    /// <summary>How many times its turn came and it couldn't happen.</summary>
    public int Tries;
}

/// <summary>
/// The chaos dial and its queue. Nothing fires while the player can't fight (dead, a cutscene, a menu...). Nice
/// effects then go at once; mean ones one at a time, at least the dial's gap apart (Chill 120 s ... Mayhem 15 s),
/// and none during a breather (after a Twitch death). Gambles skip the gap but wait out a breather. The rest wait, in
/// order, where everyone can see them. Saved as JSON so a closed game owes them next time.
/// </summary>
public sealed class ChaosQueue
{
    public static readonly double[] Gaps = { 120, 60, 30, 20, 15 };
    public static readonly string[] DialNames = { "Chill", "Easy", "Spicy", "Wild", "Mayhem" };

    readonly Func<double> now;
    readonly List<Pending> waiting = new List<Pending>();
    double lastMean = double.NegativeInfinity, lastMeanBefore = double.NegativeInfinity, breatherUntil = double.NegativeInfinity;
    Pending lastOut;
    int dial = 2;

    public ChaosQueue(Func<double> now) { this.now = now; }

    public int Dial { get => dial; set => dial = Math.Max(0, Math.Min(Gaps.Length - 1, value)); }
    public IReadOnlyList<Pending> Waiting => waiting;
    public double BreatherLeft => Math.Max(0, breatherUntil - now());

    public void Add(Pending p, Mood mood)
    {
        if (p == null) return;
        p.Mood = mood;
        waiting.Add(p);
    }

    /// <summary>The next one to fire now, taken off the queue; null: nothing may go yet.</summary>
    public Pending Next(bool canFight)
    {
        if (!canFight || waiting.Count == 0) return null;
        double t = now();
        bool breather = t < breatherUntil;
        int pick = waiting.FindIndex(p => p.Mood == Mood.Nice || (p.Mood == Mood.Gamble && !breather));
        if (pick < 0 && !breather && t >= lastMean + Gaps[dial]) pick = waiting.FindIndex(p => p.Mood == Mood.Mean);
        if (pick < 0) return null;
        var next = waiting[pick];
        waiting.RemoveAt(pick);
        if (next.Mood == Mood.Mean) { lastMeanBefore = lastMean; lastMean = t; }
        lastOut = next;
        return next;
    }

    /// <summary>Seconds until a mean one may go (0: now).</summary>
    public double NextMeanIn()
    {
        double t = now();
        return Math.Max(0, Math.Max(lastMean + Gaps[dial], breatherUntil) - t);
    }

    /// <summary>The one just taken couldn't happen: back at the head, its try counted, its slot on the dial given back.</summary>
    public void Retry(Pending p)
    {
        if (p == null) return;
        p.Tries++;
        waiting.Insert(0, p);
        if (ReferenceEquals(p, lastOut) && p.Mood == Mood.Mean) lastMean = lastMeanBefore;
        lastOut = null;
    }

    public void Breather(double seconds) => breatherUntil = Math.Max(breatherUntil, now() + seconds);

    sealed class Saved { public int Dial; public List<Pending> Waiting; }

    public string Save() => JsonConvert.SerializeObject(new Saved { Dial = dial, Waiting = waiting });

    public static ChaosQueue Load(string json, Func<double> now)
    {
        var q = new ChaosQueue(now);
        try
        {
            var s = JsonConvert.DeserializeObject<Saved>(json ?? "");
            if (s != null)
            {
                q.Dial = s.Dial;
                if (s.Waiting != null) q.waiting.AddRange(s.Waiting.FindAll(p => p != null && !string.IsNullOrEmpty(p.EffectId)));
            }
        }
        catch (JsonException) { }
        return q;
    }
}

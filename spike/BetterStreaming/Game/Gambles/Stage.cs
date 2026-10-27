using System;
using System.Collections.Generic;
using System.Linq;
using BetterStreaming.Core;
using UnityEngine;

namespace BetterStreaming;

/// <summary>A gamble on screen: a frame of it (false: it's over), and gone.</summary>
internal interface IShow
{
    bool Tick();
    void Destroy();
}

/// <summary>
/// The middle of the screen, one gamble at a time: the wheel, the dice, the slots, plinko, the coin, roulette and the
/// race wait their turn for it, in the order they were bought (a "spin again" goes next).
/// </summary>
internal static class Stage
{
    static readonly LinkedList<(string what, Func<IShow> make)> waiting = new();
    static IShow current;
    static string currentWhat = "";

    public static int Waiting => waiting.Count;
    public static bool Busy => current != null;

    public static void Add(string what, Func<IShow> make, bool next = false)
    {
        if (next) waiting.AddFirst((what, make));
        else waiting.AddLast((what, make));
    }

    public static void Tick()
    {
        if (current == null)
        {
            if (waiting.Count == 0) return;
            var (what, make) = waiting.First.Value;
            waiting.RemoveFirst();
            currentWhat = what;
            try { current = make(); }
            catch (Exception e) { Log.Warn($"Streaming: {what} couldn't start: {e}"); }
            if (current == null) return;
        }
        try { if (!current.Tick()) { current.Destroy(); current = null; } }
        catch (Exception e) { Log.Warn($"Streaming: {currentWhat}: {e}"); current?.Destroy(); current = null; }
    }
}

/// <summary>
/// What a gamble's result does: an effect by id, "jackpot" (a heal, chain lightning and a loot drop at once, doubled),
/// "silly" (a random naughty 30-second one), or "" (nothing). The streamer's lists for each game are checked against the
/// effects that can happen here.
/// </summary>
internal static class Payout
{
    public static readonly HashSet<string> Games = new() { "wheel", "dice", "slots", "plinko", "coin", "casino", "race" };

    /// <summary>What a gamble can land on: the effects that are on and can happen here (not the gambles), and the two
    /// specials.</summary>
    public static IEnumerable<string> Known() =>
        Streamer.Defs.Where(d => d.Enabled && !Games.Contains(d.Id) && Streamer.Effects.TryGetValue(d.Id, out var fx) && fx.Available(out _))
            .Select(d => d.Id).Concat(new[] { DiceTable.Jackpot, DiceTable.Silly });

    public static string Name(string id) => id switch
    {
        DiceTable.Silly => "Something silly",
        DiceTable.Jackpot => "JACKPOT",
        "" or null => "Nothing",
        _ => Streamer.Def(id)?.Name ?? id,
    };

    public static bool Good(string id) => id == DiceTable.Jackpot || Streamer.Def(id)?.Mood == Mood.Nice;

    public static Color Colour(string id) => id switch
    {
        DiceTable.Jackpot => new Color(1f, 0.8f, 0.15f),
        DiceTable.Silly => new Color(1f, 0.55f, 0.75f),
        "death" => new Color(0.12f, 0.1f, 0.11f),
        "" or null => new Color(0.45f, 0.45f, 0.5f),
        _ => Streamer.Def(id)?.Mood == Mood.Nice ? Color.Lerp(Effects.ColourOf(id), new Color(0.2f, 0.62f, 0.25f), 0.5f)
                                                  : Color.Lerp(Effects.ColourOf(id), new Color(0.75f, 0.2f, 0.2f), 0.5f),
    };

    /// <summary>A random one from a streamer's list ("heal, haste:2, shield"), of those that can happen ("": none can).</summary>
    public static string Pick(string list) => Weighted.Pick(WheelSlices.Parse(list, Known()), UnityEngine.Random.value);

    /// <summary>The ids from a streamer's list that can happen, in order (a bad list: the default's).</summary>
    public static List<string> List(string list, string fallback, int least)
    {
        var known = Known().ToList();
        var ids = WheelSlices.Parse(list, known).Select(s => s.Id).ToList();
        return ids.Count >= least ? ids : WheelSlices.Parse(fallback, known).Select(s => s.Id).ToList();
    }

    /// <summary>Set it off, from the viewer who paid for the gamble.</summary>
    public static void Fire(string outcome, string viewer, string message, float size, string source)
    {
        foreach (var id in Ids(outcome))
        {
            var d = Streamer.Def(id);
            if (d != null) Streamer.Fire(new Pending { EffectId = id, Viewer = viewer, Message = message, Size = outcome == DiceTable.Jackpot ? 2f : size, Source = source, Mood = d.Mood });
        }
    }

    static IEnumerable<string> Ids(string outcome)
    {
        if (outcome == DiceTable.Jackpot) return new[] { "heal", "chainlightning", "loot" };
        if (outcome == DiceTable.Silly)
        {
            var pool = Streamer.Defs.Where(d => d.Enabled && d.Mood == Mood.Mean && d.Seconds > 0f && !d.Scales &&
                                                Streamer.Effects.TryGetValue(d.Id, out var fx) && fx.Ready(out _)).ToList();
            return pool.Count == 0 ? new string[0] : new[] { pool[UnityEngine.Random.Range(0, pool.Count)].Id };
        }
        return string.IsNullOrEmpty(outcome) ? new string[0] : new[] { outcome };
    }

    /// <summary>The title and result lines most gambles share, and their pop in and out.</summary>
    public static float PopOut(RectTransform panel, float since, float hold)
    {
        float k = 1f - Mathf.Clamp01((since - hold) / 0.3f);
        panel.localScale = Vector3.one * k;
        return k;
    }
}

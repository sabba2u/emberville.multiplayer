using System.Collections.Generic;

namespace BetterStreaming.Core;

/// <summary>How an effect treats the streamer: mean ones wait for the chaos dial, nice ones skip the queue.</summary>
public enum Mood { Mean, Nice, Gamble }

/// <summary>One effect chat can buy, with the streamer's settings for it.</summary>
public sealed class EffectDef
{
    public string Id;
    public string Name;
    public Mood Mood;
    /// <summary>Its Bits price: exactly this buys it; a scaling one also takes more, up to CapBits.</summary>
    public int Bits;
    public bool Scales;
    public int CapBits;
    /// <summary>How long a timed one lasts (0: it happens once).</summary>
    public float Seconds;
    public bool Enabled = true;
    /// <summary>Words in a cheer's message that pick it.</summary>
    public string[] Keywords = new string[0];
}

/// <summary>The effects there are, as they start (the streamer changes prices, caps, times and which are on).</summary>
public static class Catalogue
{
    public static IReadOnlyList<EffectDef> Defaults() => new List<EffectDef>
    {
        new EffectDef { Id = "raid", Name = "Raid", Mood = Mood.Mean, Bits = 500, Scales = true, CapBits = 5000, Keywords = new[] { "raid" } },
        new EffectDef { Id = "chickens", Name = "Chicken raid", Mood = Mood.Mean, Bits = 300, Scales = true, CapBits = 3000, Keywords = new[] { "chicken", "chickens" } },
        new EffectDef { Id = "heal", Name = "Heal", Mood = Mood.Nice, Bits = 100, Keywords = new[] { "heal" } },
        new EffectDef { Id = "poison", Name = "Poison", Mood = Mood.Mean, Bits = 200, Seconds = 30f, Keywords = new[] { "poison" } },
        new EffectDef { Id = "death", Name = "Instant death", Mood = Mood.Mean, Bits = 1000, Keywords = new[] { "death", "die" } },
        new EffectDef { Id = "chainlightning", Name = "Chain lightning", Mood = Mood.Nice, Bits = 400, Seconds = 30f, Keywords = new[] { "lightning", "zap" } },
        new EffectDef { Id = "freeze", Name = "Freeze", Mood = Mood.Mean, Bits = 350, Seconds = 5f, Keywords = new[] { "freeze", "ice" } },
        new EffectDef { Id = "chest", Name = "Mystery chest", Mood = Mood.Gamble, Bits = 700, Keywords = new[] { "chest", "loot" } },
    };
}

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
    /// <summary>A scaling timed one lasts longer with more Bits (false: it grows some other way, e.g. more strikes).</summary>
    public bool LongerWithBits = true;
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
        new EffectDef { Id = "backwards", Name = "Backwards walking", Mood = Mood.Mean, Bits = 150, Seconds = 30f, Keywords = new[] { "backwards", "reverse" } },
        new EffectDef { Id = "autowalk", Name = "Can't stop walking", Mood = Mood.Mean, Bits = 160, Seconds = 30f, Keywords = new[] { "walk", "autowalk" } },
        new EffectDef { Id = "swapped", Name = "Swapped buttons", Mood = Mood.Mean, Bits = 170, Seconds = 30f, Keywords = new[] { "swap", "swapped" } },
        new EffectDef { Id = "randomdodge", Name = "Random dodge", Mood = Mood.Mean, Bits = 180, Seconds = 30f, Keywords = new[] { "dodge", "roll" } },
        new EffectDef { Id = "invisible", Name = "Invisible", Mood = Mood.Mean, Bits = 190, Seconds = 30f, Keywords = new[] { "invisible", "ghost" } },
        new EffectDef { Id = "upsidedown", Name = "Upside down", Mood = Mood.Mean, Bits = 210, Seconds = 30f, Keywords = new[] { "upsidedown", "flip" } },
        new EffectDef { Id = "mirror", Name = "Mirror world", Mood = Mood.Mean, Bits = 220, Seconds = 30f, Keywords = new[] { "mirror" } },
        new EffectDef { Id = "sideways", Name = "Sideways", Mood = Mood.Mean, Bits = 230, Seconds = 30f, Keywords = new[] { "sideways" } },
        new EffectDef { Id = "dizzy", Name = "Dizzy", Mood = Mood.Mean, Bits = 240, Seconds = 30f, Keywords = new[] { "dizzy", "spin" } },
        new EffectDef { Id = "cursors", Name = "Fake cursors", Mood = Mood.Mean, Bits = 250, Seconds = 30f, Keywords = new[] { "cursors", "mouse" } },
        new EffectDef { Id = "seasick", Name = "Seasick", Mood = Mood.Mean, Bits = 260, Seconds = 30f, Keywords = new[] { "seasick", "boat" } },
        new EffectDef { Id = "earthquake", Name = "Earthquake", Mood = Mood.Mean, Bits = 270, Seconds = 30f, Keywords = new[] { "earthquake", "quake", "shake" } },
        new EffectDef { Id = "zoomin", Name = "Zoomed way in", Mood = Mood.Mean, Bits = 280, Seconds = 30f, Keywords = new[] { "zoomin", "closeup" } },
        new EffectDef { Id = "zoomout", Name = "Zoomed way out", Mood = Mood.Mean, Bits = 290, Seconds = 30f, Keywords = new[] { "zoomout", "far" } },
        new EffectDef { Id = "drunk", Name = "Drunk", Mood = Mood.Mean, Bits = 310, Seconds = 30f, Keywords = new[] { "drunk", "beer" } },
        new EffectDef { Id = "colourblind", Name = "Colour blind", Mood = Mood.Mean, Bits = 320, Seconds = 30f, Keywords = new[] { "colourblind", "colorblind", "grey" } },
        new EffectDef { Id = "tunnel", Name = "Tunnel vision", Mood = Mood.Mean, Bits = 330, Seconds = 30f, Keywords = new[] { "tunnel" } },
        new EffectDef { Id = "lightsout", Name = "Lights out", Mood = Mood.Mean, Bits = 340, Seconds = 30f, Keywords = new[] { "lights", "dark" } },
        new EffectDef { Id = "retro", Name = "Retro", Mood = Mood.Mean, Bits = 360, Seconds = 30f, Keywords = new[] { "retro", "gameboy" } },
        new EffectDef { Id = "fists", Name = "Fists only", Mood = Mood.Mean, Bits = 430, Seconds = 30f, Keywords = new[] { "fists", "punch" } },
        new EffectDef { Id = "fire", Name = "On fire", Mood = Mood.Mean, Bits = 370, Seconds = 30f, Keywords = new[] { "fire", "burn" } },
        new EffectDef { Id = "roulette", Name = "Weapon roulette", Mood = Mood.Mean, Bits = 450, Seconds = 90f, Keywords = new[] { "shuffle", "weapons" } },
        new EffectDef { Id = "random2", Name = "Random x2", Mood = Mood.Mean, Bits = 650, Keywords = new[] { "random2", "double" } },
        new EffectDef { Id = "random3", Name = "Random x3", Mood = Mood.Mean, Bits = 900, Keywords = new[] { "random3", "triple" } },
        new EffectDef { Id = "fireworks", Name = "Fireworks", Mood = Mood.Nice, Bits = 105, Seconds = 0f, Keywords = new[] { "fireworks", "party" } },
        new EffectDef { Id = "shield", Name = "Shield bubble", Mood = Mood.Nice, Bits = 110, Seconds = 30f, Keywords = new[] { "shield", "bubble" } },
        new EffectDef { Id = "gold", Name = "Gold rain", Mood = Mood.Nice, Bits = 115, Seconds = 30f, Keywords = new[] { "gold", "coins" } },
        new EffectDef { Id = "kymia", Name = "Infinite Kymia", Mood = Mood.Nice, Bits = 120, Seconds = 30f, Keywords = new[] { "kymia", "mana" } },
        new EffectDef { Id = "regen", Name = "Regeneration", Mood = Mood.Nice, Bits = 130, Seconds = 30f, Keywords = new[] { "regen", "regenerate" } },
        new EffectDef { Id = "goldenpet", Name = "Golden pet", Mood = Mood.Nice, Bits = 135, Seconds = 60f, Keywords = new[] { "goldenpet", "golden" } },
        new EffectDef { Id = "haste", Name = "Haste", Mood = Mood.Nice, Bits = 140, Seconds = 30f, Keywords = new[] { "haste", "fast", "speed" } },
        new EffectDef { Id = "berserk", Name = "Berserk", Mood = Mood.Nice, Bits = 380, Seconds = 30f, Keywords = new[] { "berserk", "rage" } },
        new EffectDef { Id = "smite", Name = "Smite", Mood = Mood.Nice, Bits = 390, Seconds = 0f, Keywords = new[] { "smite" } },
        new EffectDef { Id = "battlechickens", Name = "Battle chickens", Mood = Mood.Nice, Bits = 410, Seconds = 60f, Keywords = new[] { "battlechickens", "flock" } },
        new EffectDef { Id = "guardians", Name = "Guardians", Mood = Mood.Nice, Bits = 550, Seconds = 60f, Keywords = new[] { "guardians", "guards", "help" } },
        new EffectDef { Id = "secondwind", Name = "Second wind", Mood = Mood.Nice, Bits = 600, Seconds = 120f, Keywords = new[] { "secondwind", "revive" } },
        new EffectDef { Id = "loot", Name = "Loot drop", Mood = Mood.Nice, Bits = 720, Seconds = 0f, Keywords = new[] { "lootdrop", "gift" } },
        new EffectDef { Id = "shock", Name = "Shocked", Mood = Mood.Mean, Bits = 375, Seconds = 30f, Keywords = new[] { "shock", "zapme" } },
        new EffectDef { Id = "bleed", Name = "Bleeding", Mood = Mood.Mean, Bits = 385, Seconds = 30f, Keywords = new[] { "bleed", "blood" } },
        new EffectDef { Id = "hireling", Name = "Hireling", Mood = Mood.Nice, Bits = 850, Seconds = 180f, Keywords = new[] { "hireling", "companion" } },
        new EffectDef { Id = "wheel", Name = "Spin the Wheel", Mood = Mood.Gamble, Bits = 525, Keywords = new[] { "wheel", "fortune" } },
        new EffectDef { Id = "dice", Name = "Roll the Dice", Mood = Mood.Gamble, Bits = 475, Keywords = new[] { "dice", "gamble" } },
        new EffectDef { Id = "slots", Name = "Pull the Slots", Mood = Mood.Gamble, Bits = 495, Keywords = new[] { "slots", "slot" } },
        new EffectDef { Id = "plinko", Name = "Plinko", Mood = Mood.Gamble, Bits = 510, Keywords = new[] { "plinko" } },
        new EffectDef { Id = "coin", Name = "Coin flip", Mood = Mood.Gamble, Bits = 125, Keywords = new[] { "coin", "heads", "tails" } },
        new EffectDef { Id = "casino", Name = "Roulette", Mood = Mood.Gamble, Bits = 540, Keywords = new[] { "roulette", "casino" } },
        new EffectDef { Id = "race", Name = "Monster race", Mood = Mood.Gamble, Bits = 575, Keywords = new[] { "race", "racers" } },
        new EffectDef { Id = "storm", Name = "Lightning storm", Mood = Mood.Mean, Bits = 800, Scales = true, CapBits = 4000, Seconds = 30f, LongerWithBits = false, Keywords = new[] { "storm", "thunder" } },
    };
}

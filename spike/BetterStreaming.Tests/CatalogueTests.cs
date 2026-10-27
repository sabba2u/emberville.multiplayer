using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class CatalogueTests
{
    [Fact]
    public void DefaultsHaveTheV1Effects()
    {
        var all = Catalogue.Defaults();
        Assert.Equal(new[] { "raid", "chickens", "heal", "poison", "death", "chainlightning", "freeze", "chest" }, all.Select(e => e.Id).Take(8).ToArray());
        EffectDef E(string id) => all.Single(e => e.Id == id);
        Assert.True(E("raid").Mood == Mood.Mean && E("raid").Scales);
        Assert.True(E("chickens").Mood == Mood.Mean && E("chickens").Scales);
        Assert.Equal(Mood.Nice, E("heal").Mood);
        Assert.Equal(Mood.Nice, E("chainlightning").Mood);
        Assert.All(all, e => Assert.InRange(e.Bits, 100, 1000));
        Assert.Equal(30f, E("poison").Seconds);
        Assert.Equal(30f, E("chainlightning").Seconds);
        Assert.Equal(new[] { "raid" }, E("raid").Keywords);
        Assert.Equal(new[] { "chicken", "chickens" }, E("chickens").Keywords);
        Assert.Equal(new[] { "heal" }, E("heal").Keywords);
        Assert.Equal(new[] { "poison" }, E("poison").Keywords);
        Assert.Equal(new[] { "death", "die" }, E("death").Keywords);
        Assert.Equal(new[] { "lightning", "zap" }, E("chainlightning").Keywords);
        Assert.All(all, e => Assert.True(e.Enabled));
        Assert.Equal(Mood.Mean, E("freeze").Mood);
        Assert.Equal(5f, E("freeze").Seconds);
        Assert.Equal(new[] { "freeze", "ice" }, E("freeze").Keywords);
        Assert.Equal(Mood.Gamble, E("chest").Mood);
        Assert.False(E("chest").Scales);
        Assert.Equal(new[] { "chest", "loot" }, E("chest").Keywords);
    }

    static readonly (string id, int bits)[] PartTwo =
    {
        ("backwards", 150), ("autowalk", 160), ("swapped", 170), ("randomdodge", 180), ("invisible", 190),
        ("upsidedown", 210), ("mirror", 220), ("sideways", 230), ("dizzy", 240), ("seasick", 260), ("earthquake", 270),
        ("zoomin", 280), ("zoomout", 290), ("drunk", 310), ("colourblind", 320), ("tunnel", 330), ("lightsout", 340),
        ("retro", 360), ("fists", 430), ("storm", 800),
    };

    [Fact]
    public void PartTwoEffects()
    {
        var all = Catalogue.Defaults();
        foreach (var (id, bits) in PartTwo)
        {
            var e = all.SingleOrDefault(x => x.Id == id);
            Assert.True(e != null, id);
            Assert.Equal(Mood.Mean, e.Mood);
            Assert.Equal(bits, e.Bits);
            Assert.Equal(30f, e.Seconds);
            Assert.NotEmpty(e.Keywords);
        }
        Assert.True(all.Single(x => x.Id == "storm").Scales);
        Assert.Equal(4000, all.Single(x => x.Id == "storm").CapBits);
        Assert.False(all.Single(x => x.Id == "storm").LongerWithBits);   // (more strikes, not a longer storm)
        Assert.True(all.Single(x => x.Id == "raid").LongerWithBits);
    }

    [Fact]
    public void PricesAreUnique()
    {
        var all = Catalogue.Defaults();
        Assert.Equal(all.Count, all.Select(e => e.Bits).Distinct().Count());
    }

    [Fact]
    public void WeaponRoulette()
    {
        var e = Catalogue.Defaults().Single(x => x.Id == "roulette");
        Assert.Equal(Mood.Mean, e.Mood);
        Assert.Equal(450, e.Bits);
        Assert.Equal(90f, e.Seconds);
        Assert.Equal(new[] { "shuffle", "weapons" }, e.Keywords);   // ("roulette" picks the casino's)
    }

    [Fact]
    public void RandomPacks()
    {
        var all = Catalogue.Defaults();
        var two = all.Single(x => x.Id == "random2");
        var three = all.Single(x => x.Id == "random3");
        Assert.Equal(Mood.Mean, two.Mood);
        Assert.Equal(650, two.Bits);
        Assert.Equal(new[] { "random2", "double" }, two.Keywords);
        Assert.Equal(Mood.Mean, three.Mood);
        Assert.Equal(900, three.Bits);
        Assert.Equal(new[] { "random3", "triple" }, three.Keywords);
    }

    [Fact]
    public void OnlyTheFistsLock()
    {
        var ids = Catalogue.Defaults().Select(e => e.Id).ToList();
        Assert.DoesNotContain("staffs", ids);
        Assert.DoesNotContain("swords", ids);
        Assert.Contains("fists", ids);
        Assert.DoesNotContain("bighead", ids);
        Assert.DoesNotContain("giant", ids);
    }

    [Fact]
    public void OnFire()
    {
        var e = Catalogue.Defaults().Single(x => x.Id == "fire");
        Assert.Equal(Mood.Mean, e.Mood);
        Assert.Equal(370, e.Bits);
        Assert.Equal(30f, e.Seconds);
        Assert.Equal(new[] { "fire", "burn" }, e.Keywords);
    }

    static readonly (string id, Mood mood, int bits, float secs)[] PartThree =
    {
        ("fireworks", Mood.Nice, 105, 0f), ("shield", Mood.Nice, 110, 30f), ("gold", Mood.Nice, 115, 30f), ("kymia", Mood.Nice, 120, 30f),
        ("regen", Mood.Nice, 130, 30f), ("goldenpet", Mood.Nice, 135, 60f), ("haste", Mood.Nice, 140, 30f),
        ("berserk", Mood.Nice, 380, 30f), ("smite", Mood.Nice, 390, 0f), ("battlechickens", Mood.Nice, 410, 60f), ("guardians", Mood.Nice, 550, 60f),
        ("secondwind", Mood.Nice, 600, 120f), ("loot", Mood.Nice, 720, 0f),
        ("shock", Mood.Mean, 375, 30f), ("bleed", Mood.Mean, 385, 30f),
    };

    [Fact]
    public void NiceBatchAndStatuses()
    {
        var all = Catalogue.Defaults();
        foreach (var (id, mood, bits, secs) in PartThree)
        {
            var e = all.SingleOrDefault(x => x.Id == id);
            Assert.True(e != null, id);
            Assert.Equal(mood, e.Mood);
            Assert.Equal(bits, e.Bits);
            Assert.Equal(secs, e.Seconds);
            Assert.False(e.Scales);
            Assert.NotEmpty(e.Keywords);
        }
    }

    [Fact]
    public void Hireling()
    {
        var e = Catalogue.Defaults().Single(x => x.Id == "hireling");
        Assert.Equal(Mood.Nice, e.Mood);
        Assert.Equal(850, e.Bits);
        Assert.Equal(180f, e.Seconds);
        Assert.Equal(new[] { "hireling", "companion" }, e.Keywords);
    }

    [Fact]
    public void WheelAndDiceAreGambles()
    {
        var all = Catalogue.Defaults();
        var wheel = all.Single(x => x.Id == "wheel");
        var dice = all.Single(x => x.Id == "dice");
        Assert.Equal(Mood.Gamble, wheel.Mood);
        Assert.Equal(Mood.Gamble, dice.Mood);
        Assert.Equal(525, wheel.Bits);
        Assert.Equal(475, dice.Bits);
        Assert.Equal(new[] { "wheel", "fortune" }, wheel.Keywords);
        Assert.Equal(new[] { "dice", "gamble" }, dice.Keywords);
        Assert.Equal(0f, wheel.Seconds);
        Assert.False(wheel.Scales);
    }

    [Fact]
    public void KeywordsDontClash()
    {
        var words = Catalogue.Defaults().SelectMany(e => e.Keywords).ToList();
        Assert.Equal(words.Count, words.Distinct().Count());
    }

    [Fact]
    public void TheCasinoGames()
    {
        var all = Catalogue.Defaults();
        foreach (var (id, bits, word) in new[] { ("slots", 495, "slots"), ("plinko", 510, "plinko"), ("coin", 125, "coin"), ("casino", 540, "roulette"), ("race", 575, "race") })
        {
            var e = all.Single(x => x.Id == id);
            Assert.Equal(Mood.Gamble, e.Mood);
            Assert.Equal(bits, e.Bits);
            Assert.Equal(word, e.Keywords[0]);
            Assert.False(e.Scales);
            Assert.Equal(0f, e.Seconds);
        }
        Assert.Equal("Roulette", all.Single(x => x.Id == "casino").Name);
    }
}

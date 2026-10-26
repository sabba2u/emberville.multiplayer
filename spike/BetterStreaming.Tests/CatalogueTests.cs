using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class CatalogueTests
{
    [Fact]
    public void DefaultsHaveTheV1Effects()
    {
        var all = Catalogue.Defaults();
        Assert.Equal(new[] { "raid", "chickens", "heal", "poison", "death", "chainlightning", "freeze", "chest" }, all.Select(e => e.Id).ToArray());
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
}

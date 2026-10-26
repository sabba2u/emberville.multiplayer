using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class ChaosQueueTests
{
    double t;
    ChaosQueue Q(int dial) => new ChaosQueue(() => t) { Dial = dial };
    static Pending P(string id, string who = "joe", float size = 1f) => new Pending { EffectId = id, Viewer = who, Size = size, Source = "bits" };

    [Fact]
    public void NiceSkipsTheQueue()
    {
        var q = Q(0);
        q.Add(P("raid"), Mood.Mean);
        Assert.Equal("raid", q.Next(true).EffectId);
        q.Add(P("poison"), Mood.Mean);
        q.Add(P("heal"), Mood.Nice);
        Assert.Equal("heal", q.Next(true).EffectId);
        Assert.Null(q.Next(true));
        Assert.Single(q.Waiting);
    }

    [Fact]
    public void MeanWaitsForTheGap()
    {
        var q = Q(2);
        q.Add(P("raid"), Mood.Mean);
        Assert.Equal("raid", q.Next(true).EffectId);
        t = 1; q.Add(P("poison"), Mood.Mean);
        Assert.Null(q.Next(true));
        t = 29.9; Assert.Null(q.Next(true));
        Assert.Equal(0.1, q.NextMeanIn(), 3);
        t = 30; Assert.Equal("poison", q.Next(true).EffectId);
    }

    [Fact]
    public void CanFightFalseHoldsEverything()
    {
        var q = Q(4);
        q.Add(P("raid"), Mood.Mean);
        q.Add(P("heal"), Mood.Nice);
        Assert.Null(q.Next(false));
        Assert.Equal(2, q.Waiting.Count);
        Assert.Equal("heal", q.Next(true).EffectId);
        Assert.Equal("raid", q.Next(true).EffectId);
    }

    [Fact]
    public void BreatherHoldsMean()
    {
        var q = Q(4);
        q.Breather(20);
        q.Add(P("raid"), Mood.Mean);
        q.Add(P("heal"), Mood.Nice);
        Assert.Equal("heal", q.Next(true).EffectId);
        Assert.Null(q.Next(true));
        t = 20; Assert.Equal("raid", q.Next(true).EffectId);
    }

    [Fact]
    public void GambleSkipsTheGapButNotTheBreather()
    {
        var q = Q(0);
        q.Add(P("raid"), Mood.Mean);
        q.Next(true);
        q.Add(P("wheel"), Mood.Gamble);
        Assert.Equal("wheel", q.Next(true).EffectId);
        q.Breather(20);
        q.Add(P("dice"), Mood.Gamble);
        Assert.Null(q.Next(true));
    }

    [Fact]
    public void SaveLoadRoundTrip()
    {
        var q = Q(1);
        q.Add(P("raid", "a", 2f), Mood.Mean);
        q.Add(P("poison", "b"), Mood.Mean);
        q.Add(P("death", "c"), Mood.Mean);
        var back = ChaosQueue.Load(q.Save(), () => t);
        Assert.Equal(new[] { "raid", "poison", "death" }, back.Waiting.Select(p => p.EffectId).ToArray());
        Assert.Equal(new[] { "a", "b", "c" }, back.Waiting.Select(p => p.Viewer).ToArray());
        Assert.Equal(2f, back.Waiting[0].Size);
        Assert.Equal(Mood.Mean, back.Waiting[0].Mood);
        Assert.Equal(1, back.Dial);
    }

    [Fact]
    public void RetryGoesFirstWithoutUsingTheGap()
    {
        var q = Q(0);
        q.Add(P("raid"), Mood.Mean);
        q.Add(P("poison"), Mood.Mean);
        var first = q.Next(true);
        q.Retry(first);   // it couldn't happen: back at the head, its try counted, and the gap not used up
        Assert.Equal(1, q.Waiting[0].Tries);
        Assert.Equal("raid", q.Next(true).EffectId);
    }

    [Fact]
    public void LoadOfGarbageIsEmpty() => Assert.Empty(ChaosQueue.Load("not json", () => 0).Waiting);
}

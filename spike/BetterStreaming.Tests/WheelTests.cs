using System;
using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class WheelTests
{
    static WheelPhysics Four() => new WheelPhysics(new[] { 1f, 1f, 1f, 1f }, new WheelSettings());

    static void Run(WheelPhysics w)
    {
        for (int i = 0; i < 60 * 60 && !w.Done; i++) w.Step(1f / 60f);
    }

    [Fact]
    public void SliceWidthsFollowTheWeights()
    {
        var w = new WheelPhysics(new[] { 1f, 3f }, new WheelSettings());
        Assert.Equal(0f, w.Starts[0], 4);
        Assert.Equal(0.25f, w.Starts[1], 4);
        Assert.Equal(0.25f, w.Width(0), 4);
        Assert.Equal(0.75f, w.Width(1), 4);
    }

    [Fact]
    public void ASpinComesToRestInASlice()
    {
        var w = Four();
        w.Spin(1f);
        Run(w);
        Assert.True(w.Done);
        Assert.InRange(w.PointerSlice, 0, 3);
        Assert.True(w.Clicks > 4, $"only {w.Clicks} pegs passed");
    }

    [Fact]
    public void TooSlowToClearAPegRollsBack()
    {
        var s = new WheelSettings();
        var w = Four();
        // the peg between slice 3 and slice 0 (at 0) just short of the flapper, moving toward it too slowly to bend it over
        w.Angle = -s.Reach - 0.004f;
        int before = w.PointerSlice;
        w.Spin(s.Barrier * 0.6f);
        Run(w);
        Assert.True(w.Done);
        Assert.Equal(before, w.PointerSlice);
        Assert.Equal(0, w.Clicks);
    }

    [Fact]
    public void FastEnoughClearsThePeg()
    {
        var s = new WheelSettings();
        var w = Four();
        w.Angle = -s.Reach - 0.004f;
        int before = w.PointerSlice;
        w.Spin(s.Barrier * 2.5f);
        Run(w);
        Assert.True(w.Done);
        Assert.NotEqual(before, w.PointerSlice);
        Assert.Equal(1, w.Clicks);
    }

    [Fact]
    public void LandsRoughlyByWidth()
    {
        var rng = new Random(42);
        var counts = new int[3];
        const int spins = 1200;
        for (int i = 0; i < spins; i++)
        {
            var w = new WheelPhysics(new[] { 1f, 1f, 2f }, new WheelSettings());
            w.Angle = (float)rng.NextDouble();
            w.Spin(WheelPhysics.RandomSpeed(rng.NextDouble(), 1f));
            Run(w);
            counts[w.PointerSlice]++;
        }
        Assert.InRange(counts[2] / (float)spins, 0.44f, 0.56f);
        Assert.InRange(counts[0] / (float)spins, 0.19f, 0.31f);
    }

    [Fact]
    public void FeelsLikeAGameShowWheel()
    {
        // a typical spin lasts about 4 to 6 seconds, and now and then one rolls back off a peg it couldn't clear
        var rng = new Random(7);
        int rollBacks = 0;
        float seconds = 0f;
        const int spins = 300;
        for (int i = 0; i < spins; i++)
        {
            var w = new WheelPhysics(WheelSlices.Parse(WheelSlices.Default, null).Select(s => s.Weight).ToList(), new WheelSettings());
            w.Angle = (float)rng.NextDouble();
            w.Spin(WheelPhysics.RandomSpeed(rng.NextDouble(), 1f));
            float t = 0f;
            while (!w.Done && t < 60f) { w.Step(1f / 60f); t += 1f / 60f; }
            seconds += t;
            if (w.RolledBack) rollBacks++;
        }
        Assert.InRange(seconds / spins, 3.5f, 7f);
        Assert.InRange(rollBacks / (float)spins, 0.1f, 0.6f);
    }

    [Fact]
    public void BiggerCheersSpinHarder()
    {
        Assert.True(WheelPhysics.RandomSpeed(0.5, 4f) > WheelPhysics.RandomSpeed(0.5, 1f));
        Assert.True(WheelPhysics.RandomSpeed(0.5, 100f) <= WheelPhysics.RandomSpeed(1.0, 1f) * 1.6f);
    }

    [Fact]
    public void SlicesFromTheConfig()
    {
        var known = new[] { "heal", "raid", "death", "again" };
        var list = WheelSlices.Parse("heal:1, raid:2 ,death:0.35,nonsense:1,again:x,raid", known);
        Assert.Equal(new[] { "heal", "raid", "death", "raid" }, list.Select(s => s.Id));
        Assert.Equal(new[] { 1f, 2f, 0.35f, 1f }, list.Select(s => s.Weight));
    }

    [Fact]
    public void DefaultSlicesHaveDeathAsASliver()
    {
        var list = WheelSlices.Parse(WheelSlices.Default, null);
        Assert.True(list.Count >= 8);
        var death = list.Single(s => s.Id == "death");
        Assert.True(death.Weight < list.Where(s => s.Id != "death").Min(s => s.Weight));
    }
}

public class DiceTests
{
    [Fact]
    public void TotalsPickFromTheTable()
    {
        var t = DiceTable.Parse("3:death,4-5:storm,6-11:raid,12-17:heal,18:jackpot");
        Assert.Equal("death", t.Outcome(1, 1, 1));
        Assert.Equal("storm", t.Outcome(1, 1, 3));
        Assert.Equal("raid", t.Outcome(2, 4, 5));
        Assert.Equal("heal", t.Outcome(6, 6, 5));
        Assert.Equal("jackpot", t.Outcome(6, 6, 6));
    }

    [Fact]
    public void TriplesOverrideTheTotal()
    {
        var t = DiceTable.Parse(DiceTable.Default);
        Assert.Equal("death", t.Outcome(1, 1, 1));
        Assert.Equal("jackpot", t.Outcome(6, 6, 6));
    }

    [Fact]
    public void DefaultCoversEveryTotal()
    {
        var t = DiceTable.Parse(DiceTable.Default);
        for (int a = 1; a <= 6; a++) for (int b = 1; b <= 6; b++) for (int c = 1; c <= 6; c++)
            Assert.False(string.IsNullOrEmpty(t.Outcome(a, b, c)), $"{a}+{b}+{c}");
    }

    [Fact]
    public void BadEntriesAreSkipped()
    {
        var t = DiceTable.Parse("3:death,x:raid,7-:heal,8-9:,10-18:haste");
        Assert.Equal("death", t.Outcome(1, 1, 1));
        Assert.Equal("", t.Outcome(2, 3, 3));
        Assert.Equal("haste", t.Outcome(4, 3, 3));
    }
}

public class LeaderboardTests
{
    [Fact]
    public void TopByBits()
    {
        var b = new Leaderboard();
        b.Add("ann", Leaderboard.Raider, 500);
        b.Add("bob", Leaderboard.Raider, 300);
        b.Add("Ann", Leaderboard.Raider, 100);
        b.Add("cat", Leaderboard.Healer, 900);
        var top = b.Top(Leaderboard.Raider, 3);
        Assert.Equal(new[] { "ann", "bob" }, top.Select(t => t.Viewer));
        Assert.Equal(600, top[0].Amount);
    }

    [Fact]
    public void SavesAndLoads()
    {
        var b = new Leaderboard();
        b.Add("ann", Leaderboard.Killer, 1);
        b.Add("ann", Leaderboard.Killer, 1);
        var again = Leaderboard.Load(b.Save());
        Assert.Equal(2, again.Top(Leaderboard.Killer, 1)[0].Amount);
        Assert.Empty(Leaderboard.Load("not json").Top(Leaderboard.Killer, 5));
    }
}

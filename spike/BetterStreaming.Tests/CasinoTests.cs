using System;
using System.Collections.Generic;
using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class CasinoTests
{
    // ---- slots ----

    [Fact]
    public void ThreeOfAKindIsDoubled()
    {
        Assert.Equal(("raid", 2f), SlotMachine.Pays("raid", "raid", "raid"));
        Assert.Equal(("heal", 2f), SlotMachine.Pays("heal", "heal", "heal"));
    }

    [Fact]
    public void ThreeSevensIsTheJackpotAndThreeSkullsDeath()
    {
        Assert.Equal((DiceTable.Jackpot, 1f), SlotMachine.Pays("jackpot", "jackpot", "jackpot"));
        Assert.Equal(("death", 1f), SlotMachine.Pays("death", "death", "death"));
    }

    [Fact]
    public void TwoOfAKindIsThatEffect()
    {
        Assert.Equal(("chickens", 1f), SlotMachine.Pays("chickens", "heal", "chickens"));
        Assert.Equal(("haste", 1f), SlotMachine.Pays("raid", "haste", "haste"));
    }

    [Fact]
    public void TwoSevensOrTwoSkullsAndNoMatchPayTheNoMatch()
    {
        Assert.Equal(("", 1f), SlotMachine.Pays("jackpot", "jackpot", "heal"));
        Assert.Equal(("", 1f), SlotMachine.Pays("death", "raid", "death"));
        Assert.Equal(("", 1f), SlotMachine.Pays("heal", "raid", "haste"));
        Assert.Equal(("silly", 1f), SlotMachine.Pays("heal", "raid", "haste", "silly"));
    }

    [Fact]
    public void ReelsFollowTheirWeights()
    {
        var reel = WheelSlices.Parse("heal:9,death:1", null);
        var rng = new Random(1);
        int deaths = Enumerable.Range(0, 10000).Count(_ => Weighted.Pick(reel, rng.NextDouble()) == "death");
        Assert.InRange(deaths, 800, 1200);
        Assert.Equal("heal", Weighted.Pick(reel, 0.0));
        Assert.Equal("death", Weighted.Pick(reel, 0.999));
        Assert.Equal("", Weighted.Pick(new List<WheelSlice>(), 0.5));
    }

    // ---- plinko ----

    static int Drop(int seed, int bins = 9)
    {
        var rng = new Random(seed);
        var b = new PlinkoBoard(bins, rng);
        b.Drop((float)(rng.NextDouble() - 0.5) * 0.3f);
        for (int i = 0; i < 60 * 30 && !b.Done; i++) b.Step(1f / 60f);
        Assert.True(b.Done, $"seed {seed}: the ball never landed");
        return b.Bin;
    }

    [Fact]
    public void ThePlinkoBallAlwaysLandsInABin()
    {
        for (int s = 0; s < 200; s++) Assert.InRange(Drop(s), 0, 8);
    }

    [Fact]
    public void PlinkoMiddleBinsAreCommonEdgesRare()
    {
        var counts = new int[9];
        for (int s = 0; s < 2000; s++) counts[Drop(s)]++;
        int middle = counts[3] + counts[4] + counts[5], edges = counts[0] + counts[8];
        Assert.True(middle > 3 * edges, $"middle {middle}, edges {edges}: {string.Join(",", counts)}");
        Assert.True(edges > 0, "the edges never happen: " + string.Join(",", counts));
    }

    [Fact]
    public void PlinkoBallHitsPegsOnTheWayDown()
    {
        var b = new PlinkoBoard(9, new Random(3));
        b.Drop(0.05f);
        for (int i = 0; i < 60 * 30 && !b.Done; i++) b.Step(1f / 60f);
        Assert.True(b.Hits >= 5, $"only {b.Hits} pegs hit");
    }

    // ---- roulette ----

    [Fact]
    public void RouletteHasTheEuropeanWheel()
    {
        Assert.Equal(37, RouletteTable.Order.Length);
        Assert.Equal(Enumerable.Range(0, 37), RouletteTable.Order.OrderBy(n => n));
        Assert.Equal(RouletteTable.Green, RouletteTable.Colour(0));
        Assert.Equal(RouletteTable.Red, RouletteTable.Colour(32));
        Assert.Equal(RouletteTable.Black, RouletteTable.Colour(15));
        Assert.Equal(18, Enumerable.Range(1, 36).Count(n => RouletteTable.Colour(n) == RouletteTable.Red));
        // (round the wheel the colours take turns, after the 0)
        for (int i = 1; i + 1 < 37; i++) Assert.NotEqual(RouletteTable.Colour(RouletteTable.Order[i]), RouletteTable.Colour(RouletteTable.Order[i + 1]));
    }

    [Fact]
    public void ABetIsTheColourInTheMessage()
    {
        Assert.Equal(RouletteTable.Red, RouletteTable.Bet("roulette all on RED!"));
        Assert.Equal(RouletteTable.Black, RouletteTable.Bet("Cheer500 black please"));
        Assert.Equal(RouletteTable.Green, RouletteTable.Bet("green"));
        Assert.Equal("", RouletteTable.Bet("roulette"));
        Assert.Equal("", RouletteTable.Bet(null));
        Assert.Equal("heads", CoinCall.Of("coin HEADS"));
        Assert.Equal("tails", CoinCall.Of("tails!"));
        Assert.Equal("", CoinCall.Of("coin"));
    }

    // ---- the monster race ----

    static int Race(int seed, int boosted = -1, int boosts = 0)
    {
        var r = new RaceModel(4, new Random(seed));
        float t = 0f;
        while (r.Winner < 0 && t < 60f)
        {
            if (boosted >= 0 && boosts > 0 && (int)(t * 4) != (int)((t + 1f / 60f) * 4)) { for (int b = 0; b < boosts; b++) r.Boost(boosted); }
            r.Step(1f / 60f);
            t += 1f / 60f;
        }
        Assert.True(r.Winner >= 0, "nobody finished");
        Assert.InRange(t, 8f, 30f);
        return r.Winner;
    }

    [Fact]
    public void ARaceHasAWinnerInTime()
    {
        var wins = new int[4];
        for (int s = 0; s < 400; s++) wins[Race(s)]++;
        Assert.All(wins, w => Assert.InRange(w, 50, 160));   // (fair without chat)
    }

    [Fact]
    public void CheeringARacerHelpsButDoesntSettleIt()
    {
        int won = 0;
        for (int s = 0; s < 400; s++) if (Race(s, 2, 3) == 2) won++;
        Assert.InRange(won, 200, 380);
    }

    // ---- chatters as enemies ----

    [Fact]
    public void RecentChattersAreNamesBotsAndTheStreamerArent()
    {
        var p = new ChatterPool(600, "TheStreamer");
        p.Seen("Alice", 0);
        p.Seen("Nightbot", 0);
        p.Seen("thestreamer", 0);
        p.Seen("Bob", 10);
        Assert.Equal(2, p.Count(20));
        Assert.Equal(1, p.Count(605));   // (Alice went quiet ten minutes ago)
        Assert.Null(new ChatterPool(600, "").Pick(0, new HashSet<string>(), _ => 0));
    }

    [Fact]
    public void NamesAreSharedOutBeforeAnyIsUsedTwice()
    {
        var p = new ChatterPool(600, "");
        foreach (var n in new[] { "a", "b", "c" }) p.Seen(n, 0);
        var used = new HashSet<string>();
        var rng = new Random(5);
        for (int i = 0; i < 3; i++) used.Add(p.Pick(1, used, k => rng.Next(k)));
        Assert.Equal(new[] { "a", "b", "c" }, used.OrderBy(x => x));
        Assert.NotNull(p.Pick(1, used, k => rng.Next(k)));   // (more monsters than chatters: they double up)
    }

    // ---- the raid party ----

    [Fact]
    public void EachChatterJoinsTheRaidPartyOnce()
    {
        var party = new RaidParty(20);
        Assert.True(party.Join("Alice"));
        Assert.False(party.Join("alice"));
        Assert.True(party.Join("Bob"));
        Assert.Equal(2, party.Count);
        Assert.Equal(new[] { "Alice", "Bob" }, party.Raiders);
    }

    [Fact]
    public void TheRaidIsAsBigAsTheParty()
    {
        var party = new RaidParty(20);
        Assert.Equal(0, party.Monsters);
        party.Join("a");
        Assert.Equal(2, party.Monsters);   // (one raider still brings a little raid)
        for (int i = 0; i < 9; i++) party.Join("x" + i);
        Assert.Equal(10, party.Monsters);
        for (int i = 0; i < 50; i++) party.Join("y" + i);
        Assert.Equal(20, party.Monsters);
        Assert.True(party.Toughness > 1f);   // (more raiders than monsters: the monsters get tougher)
        Assert.Equal(1f, new RaidParty(20).Toughness);
    }

    // ---- weapon roulette ----

    [Fact]
    public void WeaponRouletteNeverPicksTheSameTypeTwiceInARow()
    {
        var types = new[] { "OneHandedSwords", "Bows", "MagicStaffs", "fists" };
        var rng = new Random(2);
        string now = "Bows";
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            string next = WeaponTypes.Next(now, types, k => rng.Next(k));
            Assert.NotEqual(now, next);
            seen.Add(next);
            now = next;
        }
        Assert.Equal(4, seen.Count);
        Assert.Equal("", WeaponTypes.Next("Bows", new[] { "Bows" }, k => 0));   // (nothing else to swap to)
    }

    // ---- chat lines ----

    [Fact]
    public void ALongListIsSplitIntoLinesTwitchTakes()
    {
        var items = Enumerable.Range(0, 60).Select(i => $"effect{i} {100 + i}").ToList();
        var lines = ChatLines.Pack("Naughty:", items, 120);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 120, l));
        Assert.All(lines, l => Assert.StartsWith("Naughty", l));
        var back = string.Join(", ", lines.Select(l => l.Substring(l.IndexOf(':') + 2)));
        Assert.Equal(string.Join(", ", items), back);   // (nothing lost or cut in half)
        Assert.Equal(new[] { "Nice: heal 100" }, ChatLines.Pack("Nice:", new[] { "heal 100" }, 480));
        Assert.Empty(ChatLines.Pack("Nice:", new string[0], 480));
    }
}

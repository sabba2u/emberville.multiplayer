using BetterStreaming.Core;
using Xunit;

public class VoteCounterTests
{
    [Fact]
    public void SameUserCountsOnce()
    {
        var v = new VoteCounter(3, 60, 120);
        Assert.False(v.Vote("a", 0));
        Assert.False(v.Vote("a", 1));
        Assert.False(v.Vote("b", 2));
        Assert.True(v.Vote("c", 3));
    }

    [Fact]
    public void CooldownAfterwards()
    {
        var v = new VoteCounter(3, 60, 120);
        v.Vote("a", 0); v.Vote("b", 1); Assert.True(v.Vote("c", 2));
        Assert.False(v.Vote("d", 3)); Assert.False(v.Vote("e", 4)); Assert.False(v.Vote("f", 5));
        Assert.False(v.Vote("a", 130)); Assert.False(v.Vote("b", 131)); Assert.True(v.Vote("c", 132));
    }

    [Fact]
    public void OldVotesDontCount()
    {
        var v = new VoteCounter(3, 60, 120);
        v.Vote("a", 0); v.Vote("b", 1);
        Assert.False(v.Vote("c", 70));
        Assert.False(v.Vote("d", 71));
        Assert.True(v.Vote("e", 72));
    }
}

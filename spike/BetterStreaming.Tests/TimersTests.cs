using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class TimersTests
{
    double t;

    [Fact]
    public void SameEffectExtends()
    {
        var tm = new Timers(() => t);
        tm.Start("poison", 30);
        t = 10; tm.Start("poison", 30);
        Assert.Equal(50, tm.Left("poison"), 3);
        Assert.True(tm.Active("poison"));
    }

    [Fact]
    public void ExpiredOnce()
    {
        var tm = new Timers(() => t);
        tm.Start("poison", 30);
        Assert.Empty(tm.Expired());
        t = 31;
        Assert.Equal(new[] { "poison" }, tm.Expired().ToArray());
        Assert.Empty(tm.Expired());
        Assert.False(tm.Active("poison"));
    }
}

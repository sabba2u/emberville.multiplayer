using BetterCompanion.Core;
using Xunit;

public class ReviveTests
{
    [Fact]
    public void Revive_FullHoldRaisesAtHalf()
    {
        var r = new Revive();
        r.Fall();
        Assert.True(r.Down);
        r.Hold(true, 1.0f);
        float p = r.Hold(true, 1.0f);
        Assert.Equal(1f, p, 3);
        Assert.False(r.Down);
        Assert.Equal(0.5f, r.Health01, 3);
    }

    [Fact]
    public void Revive_ReleaseResets()
    {
        var r = new Revive();
        r.Fall();
        r.Hold(true, 1.5f);
        r.Hold(false, 0.1f);
        Assert.True(r.Hold(true, 0.6f) < 1f);
        Assert.True(r.Down);
    }

    [Fact]
    public void Revive_SceneChangeRaisesAtHalf()
    {
        var r = new Revive();
        r.Fall();
        r.SceneChanged();
        Assert.False(r.Down);
        Assert.Equal(0.5f, r.Health01, 3);
    }
}

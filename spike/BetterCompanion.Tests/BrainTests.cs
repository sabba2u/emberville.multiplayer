using BetterCompanion.Core;
using Xunit;

public class BrainTests
{
    static Situation At((float x, float y) me, (float x, float y)? target = null, Style style = Style.Melee,
        (float x, float y)? sentTo = null, bool downed = false, float stuckFor = 0f, float sinceDash = 10f, float cutOffFor = 0f) =>
        new Situation(me, (0f, 0f), target, sentTo, downed, style, stuckFor, sinceDash, 10f, cutOffFor);

    [Fact]
    public void Decide_DownedIsIdle() =>
        Assert.Equal(Move.Idle, Brain.Decide(At((5f, 0f), target: (6f, 0f), sentTo: (3f, 3f), downed: true)).move);

    [Fact]
    public void Decide_FarFromOwnerTeleports()
    {
        var far = Brain.Decide(At((21f, 0f)));
        Assert.Equal(Move.Teleport, far.move);
        Assert.Equal((0f, 0f), far.where);
        Assert.Equal(Move.Teleport, Brain.Decide(At((3f, 0f), stuckFor: 4.1f)).move);
    }

    [Fact]
    public void Decide_TargetBeyondLeashIsIgnored() =>
        Assert.Equal(Move.Follow, Brain.Decide(At((3f, 0f), target: (13f, 0f))).move);

    [Fact]
    public void Decide_MeleeClosesInThenAttacks()
    {
        var go = Brain.Decide(At((3f, 0f), target: (6f, 0f)));
        Assert.Equal(Move.GoTo, go.move);
        Assert.Equal((6f, 0f), go.where);
        Assert.Equal(Move.Attack, Brain.Decide(At((3f, 0f), target: (3.9f, 0f))).move);
    }

    [Fact]
    public void Decide_RangedStopsAtRange() =>
        Assert.Equal(Move.Attack, Brain.Decide(At((3f, 0f), target: (8f, 0f), style: Style.Ranged)).move);

    [Fact]
    public void Decide_SentGoesThereFirst()
    {
        var d = Brain.Decide(At((1f, 0f), sentTo: (5f, 5f)));
        Assert.Equal(Move.GoTo, d.move);
        Assert.Equal((5f, 5f), d.where);
    }

    [Fact]
    public void Decide_PingBeatsTheFight()
    {
        var d = Brain.Decide(At((3f, 0f), target: (3.5f, 0f), sentTo: (-4f, 2f)));
        Assert.Equal(Move.GoTo, d.move);
        Assert.Equal((-4f, 2f), d.where);
    }

    [Fact]
    public void Decide_CutOffFromOwnerTeleportsSoon()
    {
        Assert.Equal(Move.Follow, Brain.Decide(At((5f, 0f), cutOffFor: 0.5f)).move);
        Assert.Equal(Move.Teleport, Brain.Decide(At((5f, 0f), cutOffFor: Rules.CutOffSeconds)).move);
    }

    [Fact]
    public void Decide_NearOwnerIdles()
    {
        Assert.Equal(Move.Idle, Brain.Decide(At((1.5f, 0f))).move);
        Assert.Equal(Move.Follow, Brain.Decide(At((3f, 0f))).move);
    }

    [Fact]
    public void Aggro_NearerCompanionWins() =>
        Assert.True(Brain.EnemyPrefersCompanion((0f, 0f), (5f, 0f), (2f, 0f), false, false));

    [Fact]
    public void Aggro_DownedCompanionIsNeverChosen() =>
        Assert.False(Brain.EnemyPrefersCompanion((0f, 0f), (5f, 0f), (2f, 0f), true, false));

    [Fact]
    public void Aggro_CompanionHitItLastWinsTies()
    {
        Assert.True(Brain.EnemyPrefersCompanion((0f, 0f), (3f, 0f), (0f, 3f), false, true));
        Assert.False(Brain.EnemyPrefersCompanion((0f, 0f), (3f, 0f), (0f, 3f), false, false));
    }

    [Fact]
    public void WantRanged_FarIsBowNearIsSword()
    {
        Assert.True(Brain.WantRanged(6f, 0, true, true, false));
        Assert.False(Brain.WantRanged(1f, 0, true, true, true));
        Assert.True(Brain.WantRanged(3f, 0, true, true, true));
        Assert.False(Brain.WantRanged(3f, 0, true, true, false));
    }

    [Fact]
    public void WantRanged_OnlyOneWeapon()
    {
        Assert.False(Brain.WantRanged(8f, 0, true, false, true));
        Assert.True(Brain.WantRanged(1f, 0, false, true, false));
    }

    [Fact]
    public void WantRanged_CrowdedKeepsBow() => Assert.True(Brain.WantRanged(1f, 3, true, true, true));

    [Fact]
    public void DashTo_MeleeClosesGap()
    {
        var p = Brain.DashTo(At((0f, 0f), sinceDash: 3f), (4f, 0f), 0, ranged: false);
        Assert.NotNull(p);
        Assert.Equal(3f, p.Value.x, 3);
        Assert.Equal(0f, p.Value.y, 3);
    }

    [Fact]
    public void DashTo_RangedBacksOff()
    {
        var p = Brain.DashTo(At((0f, 0f), sinceDash: 3f), (1f, 0f), 0, ranged: true);
        Assert.NotNull(p);
        Assert.Equal(-3f, p.Value.x, 3);
    }

    [Fact]
    public void DashTo_RespectsCooldown() =>
        Assert.Null(Brain.DashTo(At((0f, 0f), sinceDash: 1f), (4f, 0f), 0, ranged: false));
}

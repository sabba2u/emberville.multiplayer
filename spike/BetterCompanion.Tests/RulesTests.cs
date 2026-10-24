using BetterCompanion.Core;
using Xunit;

public class RulesTests
{
    [Fact]
    public void StyleOf_BowAndStaffAreRanged()
    {
        Assert.Equal(Style.Ranged, Rules.StyleOf(9));
        Assert.Equal(Style.Ranged, Rules.StyleOf(12));
        Assert.Equal(Style.Melee, Rules.StyleOf(1));
        Assert.Equal(Style.Melee, Rules.StyleOf(3));
        Assert.Equal(Style.Melee, Rules.StyleOf(14));
        Assert.Equal(Style.Melee, Rules.StyleOf(5));
    }

    [Fact]
    public void CompanionFile_UsesPlayingSlot() => Assert.Equal("companion_slot2", Rules.CompanionFile(2));

    [Fact]
    public void SlotFromPrefix_ParsesAndRejects()
    {
        Assert.Equal(3, Rules.SlotFromPrefix("player_slot3"));
        Assert.Null(Rules.SlotFromPrefix("world_slot3"));
        Assert.Null(Rules.SlotFromPrefix(""));
        Assert.Null(Rules.SlotFromPrefix(null));
    }

    [Fact]
    public void Save_RejectsOwnSlot()
    {
        Assert.Contains("you're playing", Rules.CanClone(2, 2, true));
        Assert.Null(Rules.CanClone(3, 2, false));
        Assert.Contains("slot", Rules.CanClone(0, 2, true));
    }

    [Fact]
    public void GearMode_ClosesWhenScreenCloses()
    {
        var m = new GearMode();
        m.Opened(true);
        Assert.True(m.On);
        m.Closed();
        m.Opened(false);
        Assert.False(m.On);

        var n = new GearMode();
        n.Opened(true);
        n.Opened(false);
        Assert.False(n.On);
    }

    [Fact]
    public void GearMode_EndsWhenNeitherPanelShows()
    {
        var m = new GearMode();
        m.Opened(true);
        m.Tick(false, false);          // (still opening: not shown yet)
        Assert.True(m.On);
        m.Tick(true, false);
        m.Tick(false, true);           // switched to the character panel: still the companion's
        Assert.True(m.On);
        m.Tick(false, false);          // closed, or another panel (quests) took over
        Assert.False(m.On);
    }

    [Fact]
    public void GearMode_GivesUpIfNeverShown()
    {
        var m = new GearMode();
        m.Opened(true);
        for (int i = 0; i < GearMode.ShowWithin; i++) m.Tick(false, false);
        Assert.False(m.On);
    }

    [Fact]
    public void Confirm_SecondAskWithinWindow()
    {
        var c = new Confirm();
        Assert.False(c.Ask("new", 0f));
        Assert.True(c.Ask("new", 10f));
        Assert.False(c.Ask("new", 11f));       // (used up)
        Assert.False(c.Ask("3", 20f));
        Assert.False(c.Ask("new", 21f));       // (a different question in between)
        Assert.False(c.Ask("4", 40f));
        Assert.False(c.Ask("4", 60f));         // (too late)
    }
}

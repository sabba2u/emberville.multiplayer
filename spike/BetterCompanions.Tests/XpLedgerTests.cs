using BetterCompanions.Core;
using Xunit;

public class XpLedgerTests
{
    [Fact]
    public void Xp_CreditWithinWindow()
    {
        var x = new XpLedger();
        x.Hit(7, 0f);
        Assert.True(x.Credit(7, 4.9f));
        Assert.False(x.Credit(7, 4.95f));
        x.Hit(8, 0f);
        Assert.False(x.Credit(8, 5.1f));
    }
}

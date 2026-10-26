using System.Linq;
using BetterStreaming.Core;
using Xunit;

public class CheerRouterTests
{
    static System.Collections.Generic.IReadOnlyList<EffectDef> All() => Catalogue.Defaults();

    [Fact]
    public void KeywordTooDearFallsToAmount()
    {
        // "raid" costs 500: 100 Bits can't buy it, so the amount decides: heal is exactly 100
        var p = CheerRouter.Route(All(), 100, "Cheer100 raid please");
        Assert.Equal("heal", p.Effect.Id);
    }

    [Fact]
    public void KeywordAffordable()
    {
        var p = CheerRouter.Route(All(), 600, "cheer600 raid");
        Assert.Equal("raid", p.Effect.Id);
        Assert.Equal(1.2f, p.Size, 3);
    }

    [Fact]
    public void ExactAmount() => Assert.Equal("poison", CheerRouter.Route(All(), 200, "").Effect.Id);

    [Fact]
    public void OddAmountBuysBestScaling()
    {
        var p = CheerRouter.Route(All(), 750, null);
        Assert.Equal("raid", p.Effect.Id);
        Assert.Equal(1.5f, p.Size, 3);
    }

    [Fact]
    public void OverCap()
    {
        var p = CheerRouter.Route(All(), 9000, "raid");
        Assert.Equal("raid", p.Effect.Id);
        Assert.Equal(10f, p.Size, 3);
    }

    [Fact]
    public void BelowCheapest() => Assert.Null(CheerRouter.Route(All(), 37, "hi").Effect);

    [Fact]
    public void DisabledSkipped()
    {
        var all = All();
        all.Single(e => e.Id == "raid").Enabled = false;
        var p = CheerRouter.Route(all, 750, "");
        Assert.Equal("chickens", p.Effect.Id);
        Assert.Equal(2.5f, p.Size, 3);
    }

    [Fact]
    public void CheermoteTokensIgnoredForKeywords()
    {
        // "Cheer1000" isn't the word "cheer": no keyword match, so the amount decides (death is exactly 1000)
        var all = All();
        all.Single(e => e.Id == "heal").Keywords = new[] { "cheer" };
        var p = CheerRouter.Route(all, 1000, "Cheer1000 Kappa");
        Assert.Equal("death", p.Effect.Id);
    }

    [Fact]
    public void AffordableButNoScalingBuysDearestFixed()
    {
        // 250 Bits: no keyword, no exact price, no scaling effect affordable (chickens 300): the dearest it can buy (poison 200)
        var p = CheerRouter.Route(All(), 250, "");
        Assert.Equal("poison", p.Effect.Id);
        Assert.Equal(1f, p.Size);
    }

    [Fact]
    public void FixedEffectSizeIsOne() => Assert.Equal(1f, CheerRouter.Route(All(), 1000, "die").Size);
}

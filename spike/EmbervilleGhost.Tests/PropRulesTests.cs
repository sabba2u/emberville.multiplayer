using EmbervilleGhost.Core;
using Xunit;

public class PropRulesTests
{
    [Theory]
    [InlineData("GrassDark2_13", "GrassDark2")]
    [InlineData("Papers2 Variant_02", "Papers2")]
    [InlineData("Light 2D (3)", "Light 2D")]
    [InlineData("Door_Iron_A_Single_R", "Door_Iron_A_Single_R")]
    [InlineData("BarrelClosed_Breakable(Clone)", "BarrelClosed_Breakable")]
    public void BaseName_StripsCopyNumbers(string name, string want) => Assert.Equal(want, PropRules.BaseName(name));

    [Theory]
    [InlineData("Vessel1_Breakable", true, "BarrelEmpty_Breakable")]          // breakables with loot: the barrel's behaviour
    [InlineData("Bookshelf_Side_Breakable", true, "BarrelEmpty_Breakable")]
    [InlineData("Table_Square_Small", true, "Table_Vert_Roots_Breakable")]    // furniture: the breakable table's
    [InlineData("BenchDes", true, "Table_Vert_Roots_Breakable")]
    [InlineData("GrassDark2", false, null)]                                    // no hit box in the game: stays as it is
    public void BorrowFrom_ByWhatItWas(string baseName, bool hadHitBox, string want) => Assert.Equal(want, PropRules.BorrowFrom(baseName, hadHitBox));

    [Fact]
    public void SaveTouching_ChestsAndBuildables()
    {
        Assert.True(PropRules.SaveTouching("Place_Chest", new string[0]));
        Assert.True(PropRules.SaveTouching("WoodChest", new[] { "SimpleEvents", "Chest" }));
        Assert.True(PropRules.SaveTouching("Thing", new[] { "PlacementObjectComponent" }));
        Assert.False(PropRules.SaveTouching("BarrelClosed_Breakable", new[] { "DamageableObject", "ItemDropper" }));
    }
}

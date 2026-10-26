using EmbervilleMapKit.Core;
using Xunit;

public class TileAssetsTests
{
    const string RuleTileScript = "m_Script: {fileID: 11500000, guid: 0fb507873f962509567a8ace1719bac8, type: 3}";
    const string SpriteRef = "m_DefaultSprite: {fileID: 21300000, guid: 136f4cf3c7c9a094f951102cd835a560, type: 2}";

    [Theory]
    [InlineData("Walls and Top_Zone1_rules_1", "Walls and Top_Zone1")]
    [InlineData("Water_Rocks_Dungeon_rules_0", "Water_Rocks_Dungeon")]
    [InlineData("BrickBig_Floor_rules_0", "BrickBig_Floor")]
    [InlineData("Spider_Floor_First_Variations_rules", "Spider_Floor_First_Variations")]
    [InlineData("Grass", "Grass")]
    public void SetName_StripsRuleAndNumberSuffixes(string asset, string set) => Assert.Equal(set, TileAssets.SetName(asset));

    [Fact] public void IsTile_RuleTile() => Assert.True(TileAssets.IsTile("MonoBehaviour:\n  " + RuleTileScript + "\n  m_Name: x\n"));

    [Fact] public void IsTile_BuiltInTile() => Assert.True(TileAssets.IsTile("MonoBehaviour:\n  m_Script: {fileID: 13312, guid: 0000000000000000e000000000000000, type: 0}\n"));

    [Fact] public void IsTile_NotTheGamesOwnScriptables() =>
        Assert.False(TileAssets.IsTile("MonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 2e30514ffeff95924bf91879b03e8323, type: 3}\n"));

    [Fact]
    public void TranslateScripts_PointsAtThePackage_LeavesTheRest()
    {
        var got = TileAssets.TranslateScripts(RuleTileScript + "\n" + SpriteRef + "\n");
        Assert.Equal("m_Script: {fileID: 11500000, guid: 9d1514134bc4fbd41bb739b1b9a49231, type: 3}\n" + SpriteRef + "\n", got);
    }

    [Fact] public void ScriptGuids_HasTheSevenTileClasses() => Assert.Equal(7, TileAssets.ScriptGuids.Count);
}

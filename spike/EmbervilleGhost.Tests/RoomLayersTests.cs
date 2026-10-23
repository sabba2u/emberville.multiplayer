using System.Collections.Generic;
using EmbervilleGhost.Core;
using Xunit;

public class RoomLayersTests
{
    [Theory]
    // the game's own layer names (its dungeon rooms')
    [InlineData("Ground0", "BrickBig_Floor_rules_0", RoomLayer.Ground0)]
    [InlineData("Ground1", "Spider_Floor_First_Variations_rules", RoomLayer.Ground1)]
    [InlineData("Ground2", "x", RoomLayer.Ground2)]
    [InlineData("Water", "Water_Rocks_Dungeon_rules_0", RoomLayer.Water)]
    [InlineData("wall", "Walls and Top_Zone1_rules_1", RoomLayer.Wall)]
    [InlineData("WallCover", "Walls and Top_Zone1_rules_0", RoomLayer.WallCover)]
    [InlineData("Fence", "x", RoomLayer.Fence)]
    [InlineData("Fog", "x", RoomLayer.Fog)]
    [InlineData("Collision", "x", RoomLayer.Collision)]
    [InlineData("RemoveCollision", "x", RoomLayer.RemoveCollision)]
    [InlineData("RemoveWater", "x", RoomLayer.RemoveWater)]
    [InlineData("Bridge", "bridge", RoomLayer.Bridge)]
    // the names the game's hand-built scenes use (TutorialArea, the house interiors)
    [InlineData("Floor", "x", RoomLayer.Ground0)]
    [InlineData("Floor1", "x", RoomLayer.Ground1)]
    [InlineData("Floor2", "x", RoomLayer.Ground2)]
    [InlineData("Wallcover", "x", RoomLayer.WallCover)]
    [InlineData("Colliders", "x", RoomLayer.Collision)]
    [InlineData("Fences", "x", RoomLayer.Fence)]
    // our first names still work
    [InlineData("Ground", "x", RoomLayer.Ground0)]
    [InlineData("Walls", "x", RoomLayer.Wall)]
    [InlineData("WallTops", "x", RoomLayer.WallCover)]
    // anywhere else: a placeholder says what it is, anything else is only drawn
    [InlineData("Tilemap", "stone_wall", RoomLayer.Wall)]
    [InlineData("Rugs", "Red Carpet_Floor_Zone1_rules_0", RoomLayer.Decoration)]
    public void Of_ByLayerThenPlaceholder(string map, string tile, RoomLayer want) => Assert.Equal(want, RoomLayers.Of(map, tile));

    [Fact]
    public void Kinds_SolidFloorHidden()
    {
        foreach (var k in new[] { RoomLayer.Wall, RoomLayer.WallCover, RoomLayer.Fence, RoomLayer.Collision }) Assert.True(RoomLayers.IsSolid(k));
        foreach (var k in new[] { RoomLayer.Ground0, RoomLayer.Water, RoomLayer.Fog, RoomLayer.Decoration, RoomLayer.RemoveCollision }) Assert.False(RoomLayers.IsSolid(k));
        foreach (var k in new[] { RoomLayer.Ground0, RoomLayer.Ground1, RoomLayer.Ground2 }) Assert.True(RoomLayers.IsFloor(k));
        foreach (var k in new[] { RoomLayer.Collision, RoomLayer.RemoveCollision, RoomLayer.RemoveWater }) Assert.True(RoomLayers.IsHidden(k));
        Assert.False(RoomLayers.IsHidden(RoomLayer.Fog));
    }

    [Fact]
    public void IsPlaceholder_OnlyTheFour()
    {
        foreach (var n in new[] { "stone_floor", "stone_wall", "water", "bridge" }) Assert.True(RoomLayers.IsPlaceholder(n));
        Assert.False(RoomLayers.IsPlaceholder("Water_Rocks_Dungeon_rules_0"));
    }

    [Fact]
    public void PlaceholderFloor_NotUnderRealFloor()
    {
        // placeholder floor at (0,0); placeholder water at (1,0) and (2,0), bridge at (3,0); real floor already painted at (2,0) and (3,0)
        var got = RoomLayers.PlaceholderFloor(
            new HashSet<(int, int)> { (0, 0) }, new HashSet<(int, int)> { (1, 0), (2, 0) }, new HashSet<(int, int)> { (3, 0) },
            realGround: new HashSet<(int, int)> { (2, 0), (3, 0) });
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (1, 0) }, got);
    }

    [Fact]
    public void SplitWalls_FaceWhereFloorBelow()
    {
        var walls = new HashSet<(int, int)> { (0, 0), (0, 1), (0, 2), (1, 0) };
        var (faces, tops) = RoomLayers.SplitWalls(walls);
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (1, 0) }, faces);
        Assert.Equal(new HashSet<(int, int)> { (0, 1), (0, 2) }, tops);
    }
}

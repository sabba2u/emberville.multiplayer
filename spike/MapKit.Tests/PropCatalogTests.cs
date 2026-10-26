using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMapKit.Core;
using Xunit;

public class PropCatalogTests
{
    static readonly List<PropEntry> Entries =
        PropCatalog.FromFile("CartographerInterior", Fixtures.Scene, Fixtures.ClassOf, new HashSet<string> { "Vessel1_Breakable" });

    [Fact]
    public void OneEntryPerLook()
    {
        // GrassDark2_13 and _14 look the same: one entry; _15 has other art: a second one
        Assert.Equal(new[] { "GrassDark2", "GrassDark2#2", "Vessel1_Breakable" }, Entries.Select(e => e.Name).OrderBy(n => n).ToArray());
        var grass = Entries.Single(e => e.Name == "GrassDark2");
        Assert.Equal(500, grass.RootId);
        Assert.Equal("GrassDark2", grass.BaseName);
        Assert.Equal("Decoration", grass.Kind);
        Assert.Equal("CartographerInterior", grass.Source);
        Assert.Equal("21300000:" + Fixtures.GrassSprite, grass.FirstSprite);
    }

    [Fact]
    public void Live_WhenTheGameHasAPrefabOfThatName()
    {
        Assert.True(Entries.Single(e => e.Name == "Vessel1_Breakable").Live);
        Assert.False(Entries.Single(e => e.Name == "GrassDark2").Live);
    }

    [Fact]
    public void SaveTouching_ChestsAndBuildables()
    {
        Assert.True(PropCatalog.IsSaveTouching("Place_Chest", new string[0]));
        Assert.True(PropCatalog.IsSaveTouching("WoodChest", new[] { "SimpleEvents", "Chest" }));
        Assert.True(PropCatalog.IsSaveTouching("Thing", new[] { "PlacementObjectComponent" }));
        Assert.False(PropCatalog.IsSaveTouching("Vessel1_Breakable", new[] { "DamageableObject", "ItemDropper" }));
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var file = Path.GetTempFileName();
        try
        {
            PropCatalog.Save(Entries, file);
            var back = PropCatalog.Load(file);
            Assert.Equal(Entries.Count, back.Count);
            var a = Entries.Single(e => e.Name == "Vessel1_Breakable"); var b = back.Single(e => e.Name == "Vessel1_Breakable");
            Assert.Equal((a.BaseName, a.Source, a.Kind, a.ArtKey, a.FirstSprite, a.RootId, a.Live, a.SaveTouching),
                         (b.BaseName, b.Source, b.Kind, b.ArtKey, b.FirstSprite, b.RootId, b.Live, b.SaveTouching));
        }
        finally { File.Delete(file); }
    }
}

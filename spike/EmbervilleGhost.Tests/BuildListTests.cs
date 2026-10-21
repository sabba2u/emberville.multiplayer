using System.Globalization;
using System.Linq;
using EmbervilleGhost.Core;
using Xunit;

public class BuildListTests
{
    static Built Chest(string scene = "TutorialArea", string id = "Place_Chest", string save = "g1", float x = -38.8f, float y = 72.9f, int dir = 2) =>
        new Built { Scene = scene, PlaceableId = id, SaveId = save, X = x, Y = y, Dir = dir };

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var list = new BuildList();
        list.Add(Chest());
        list.Add(Chest(scene: "Town", save: "g2", x: 3f, y: -4.25f, dir: 0));
        var back = BuildList.Load(list.Save());
        Assert.Equal(2, back.All.Count);
        var a = back.All[0];
        Assert.Equal(("TutorialArea", "Place_Chest", "g1", -38.8f, 72.9f, 2), (a.Scene, a.PlaceableId, a.SaveId, a.X, a.Y, a.Dir));
        var b = back.All[1];
        Assert.Equal(("Town", "g2", 3f, -4.25f, 0), (b.Scene, b.SaveId, b.X, b.Y, b.Dir));
    }

    [Fact]
    public void Load_SkipsBadLines()
    {
        var list = BuildList.Load("x|y\n\nTutorialArea|Place_Chest|g1|1.5|2.5|0\nTown|a|b|notanumber|1|0\n");
        Assert.Single(list.All);
    }

    [Fact]
    public void Find_MatchesNearPosition()
    {
        var list = new BuildList();
        list.Add(Chest(x: 1f, y: 2f));
        Assert.NotNull(list.Find("TutorialArea", "Place_Chest", 1.005f, 2f));
        Assert.Null(list.Find("TutorialArea", "Place_Chest", 1.5f, 2f));
        Assert.Null(list.Find("Town", "Place_Chest", 1f, 2f));
        Assert.Null(list.Find("TutorialArea", "Place_Bed", 1f, 2f));
    }

    [Fact]
    public void Remove_TakesItOut()
    {
        var list = new BuildList();
        var c = Chest();
        list.Add(c);
        Assert.True(list.Remove(c));
        Assert.Empty(list.All);
        Assert.False(list.Remove(c));
    }

    [Fact]
    public void InScene_FiltersByScene()
    {
        var list = new BuildList();
        list.Add(Chest());
        list.Add(Chest(scene: "Town", save: "g2"));
        Assert.Equal(new[] { "g1" }, list.InScene("TutorialArea").Select(b => b.SaveId));
    }

    [Fact]
    public void Load_InvariantCulture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            var list = new BuildList();
            list.Add(Chest(x: 1.5f, y: -2.25f));
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string text = list.Save();
            Assert.Contains("1.5", text);
            Assert.Equal(1.5f, BuildList.Load(text).All[0].X);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }
}

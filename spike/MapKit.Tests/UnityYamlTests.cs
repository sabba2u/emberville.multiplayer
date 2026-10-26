using System.Linq;
using EmbervilleMapKit.Core;
using Xunit;

public class UnityYamlTests
{
    static readonly UnityYaml Y = UnityYaml.Parse(Fixtures.Scene);

    [Fact]
    public void Names_Parents_Children()
    {
        Assert.Equal("Vessel1_Breakable_01", Y.NameOf(100));
        Assert.Equal(10, Y.ParentOf(100));
        Assert.Equal(0, Y.ParentOf(10));
        Assert.Equal(new long[] { 200, 300 }, Y.ChildrenOf(100).ToArray());
        Assert.Equal(new long[] { 100, 500, 1500, 2500 }, Y.ChildrenOf(10).ToArray());
    }

    [Fact]
    public void Components_AndTheirScripts()
    {
        Assert.Equal(new long[] { 101, 102, 103 }, Y.ComponentsOf(100).ToArray());
        Assert.Equal(100, Y.GameObjectOf(102));
        Assert.Equal(Fixtures.Damageable, Y.ScriptGuidOf(102));
        Assert.Null(Y.ScriptGuidOf(101));
        Assert.Equal(212, Y.Docs.First(d => d.FileId == 402).ClassId);
    }

    [Theory]
    [InlineData("GrassDark2_13", "GrassDark2")]
    [InlineData("Papers2 Variant_02", "Papers2")]
    [InlineData("Light 2D (3)", "Light 2D")]
    [InlineData("Door_Iron_A_Single_R", "Door_Iron_A_Single_R")]
    [InlineData("Vessel1_Breakable_01", "Vessel1_Breakable")]
    public void BaseName_StripsCopyNumbers(string name, string want) => Assert.Equal(want, UnityYaml.BaseName(name));
}

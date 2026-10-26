using System.Linq;
using System.Text.RegularExpressions;
using EmbervilleMapKit.Core;
using Xunit;

public class PropsTests
{
    static readonly UnityYaml Y = UnityYaml.Parse(Fixtures.Scene);

    [Fact]
    public void Find_PropRootsOnly()
    {
        // the vase and the three grass tufts; not the group, not their Art/Colliders/Sprite parts, not the camera
        Assert.Equal(new long[] { 100, 500, 1500, 2500 }, Props.Find(Y, Fixtures.ClassOf).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Kind_FromTheGameComponents()
    {
        Assert.Equal("Breakable", Props.Kind(Y, 100, Fixtures.ClassOf));
        Assert.Equal("Decoration", Props.Kind(Y, 500, Fixtures.ClassOf));
    }

    [Fact]
    public void ArtKey_TheSpritesInIt()
    {
        Assert.Equal("21300000:" + Fixtures.VaseSprite, Props.ArtKey(Y, 100));
        Assert.Equal(Props.ArtKey(Y, 500), Props.ArtKey(Y, 1500));
        Assert.NotEqual(Props.ArtKey(Y, 500), Props.ArtKey(Y, 2500));
    }

    [Fact]
    public void Cut_TheVaseAlone_AtTheOrigin_WithoutTheGamesScripts()
    {
        var text = Props.Cut(Y, 100, Fixtures.ClassOf);
        Assert.StartsWith("%YAML 1.1", text);
        var cut = UnityYaml.Parse(text);
        // its 4 GameObjects (vase, Art, Colliders, Sprite) and their Transforms and the sprite renderer; no MonoBehaviours
        Assert.Equal(new long[] { 100, 200, 300, 400 }, cut.GameObjects.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(cut.Docs, d => d.ClassId == 114);
        Assert.Equal(new long[] { 101 }, cut.ComponentsOf(100).ToArray());   // (the scripts' entries gone)
        var root = cut.Doc(101).Text;
        Assert.Contains("m_Father: {fileID: 0}", root);
        Assert.Contains("m_LocalPosition: {x: 0, y: 0, z: 0}", root);
        Assert.DoesNotContain("guid: " + Fixtures.GameMaterial, text);
        Assert.Contains("guid: a97c105638bdf8b4a8650670310a4cd3", text);   // URP's Sprite-Lit-Default
        Assert.Contains("guid: " + Fixtures.VaseSprite, text);
        // nothing points outside it (the "== Props" group was 10 / 11)
        Assert.DoesNotMatch(new Regex(@"\{fileID: 1[01]\}"), text);
    }
}

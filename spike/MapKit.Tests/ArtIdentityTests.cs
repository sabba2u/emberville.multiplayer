using System.Collections.Generic;
using System.Linq;
using EmbervilleMapKit.Core;
using Xunit;

public class ArtIdentityTests
{
    const string Tex = "11111111111111111111111111111111", Spr1 = "22222222222222222222222222222222", Spr2 = "33333333333333333333333333333333",
        Tile = "44444444444444444444444444444444", Other = "55555555555555555555555555555555";

    static string Sprite(string name, int x) =>
        $"Sprite:\n  m_Name: {name}\n  m_Rect:\n    serializedVersion: 2\n    x: {x}\n    y: 0\n    width: 32\n    height: 32\n  m_RD:\n    texture: {{fileID: 2800000, guid: {Tex}, type: 3}}\n";

    // AssetRipper's output: two sprites both called "Wall_0" (cut from the same texture), the second filed as Wall_0_0
    static readonly Dictionary<string, string> Files = new Dictionary<string, string>
    {
        ["Texture2D/Wall.png"] = "",
        ["Sprite/Wall_0.asset"] = Sprite("Wall_0", 0),
        ["Sprite/Wall_0_0.asset"] = Sprite("Wall_0", 32),
        ["MonoBehaviour/Wall_rules_0.asset"] = $"MonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: 0fb507873f962509567a8ace1719bac8, type: 3}}\n  m_Name: Wall_rules_0\n  m_DefaultSprite: {{fileID: 21300000, guid: {Spr1}, type: 2}}\n",
    };

    static readonly ExportIndex Idx = new ExportIndex(new Dictionary<string, string>
    {
        [Tex] = "Texture2D/Wall.png", [Spr1] = "Sprite/Wall_0.asset", [Spr2] = "Sprite/Wall_0_0.asset", [Tile] = "MonoBehaviour/Wall_rules_0.asset",
    });

    static List<ArtItem> Run() => ArtIdentity.Identify(Files.Keys.Reverse(), Idx, p => Files[p], p => (64, 32), new HashSet<string>());

    [Fact]
    public void Guid_Stable32Hex()
    {
        Assert.Matches("^[0-9a-f]{32}$", ArtIdentity.Guid("a"));
        Assert.Equal(ArtIdentity.Guid("a"), ArtIdentity.Guid("a"));
        Assert.NotEqual(ArtIdentity.Guid("a"), ArtIdentity.Guid("b"));
    }

    [Fact]
    public void PngSize_FromTheHeader()
    {
        var head = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 64, 0, 0, 0, 32 };
        Assert.Equal((64, 32), ArtIdentity.PngSize(head));
    }

    [Fact]
    public void Identify_FixedPlacesAndDistinctIds()
    {
        var items = Run().ToDictionary(i => i.OldGuid);
        Assert.Equal(4, items.Count);
        Assert.Equal(4, items.Values.Select(i => i.NewGuid).Distinct().Count());
        Assert.Equal("Texture2D/Wall.png", items[Tex].Dest);
        Assert.Equal("Sprite/Wall/Wall_0.asset", items[Spr1].Dest);
        Assert.Equal("Sprite/Wall/Wall_0__" + items[Spr2].NewGuid.Substring(0, 6) + ".asset", items[Spr2].Dest);
        Assert.Equal("Tiles/Wall/Wall_rules_0.asset", items[Tile].Dest);
        Assert.Equal("tex|Wall|64x32", items[Tex].Key);
        Assert.Equal("sprite|tex|Wall|64x32|Wall_0|32,0,32,32", items[Spr2].Key);
    }

    [Fact]
    public void Identify_SameEveryTime()
    {
        var a = Run().ToDictionary(i => i.OldGuid, i => (i.NewGuid, i.Dest));
        var b = Run().ToDictionary(i => i.OldGuid, i => (i.NewGuid, i.Dest));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Rewrite_SwapsOnlyMappedGuids()
    {
        var map = new Dictionary<string, string> { [Spr1] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        var text = $"a: {{fileID: 1, guid: {Spr1}, type: 2}}\nb: {{fileID: 1, guid: {Other}, type: 2}}\n";
        Assert.Equal($"a: {{fileID: 1, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 2}}\nb: {{fileID: 1, guid: {Other}, type: 2}}\n", ArtIdentity.Rewrite(text, map));
    }
}

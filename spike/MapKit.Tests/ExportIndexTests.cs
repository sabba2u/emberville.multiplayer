using System.Collections.Generic;
using System.IO;
using EmbervilleMapKit.Core;
using Xunit;

public class ExportIndexTests
{
    [Fact]
    public void GuidOfMeta_ReadsGuid() =>
        Assert.Equal("0fb507873f962509567a8ace1719bac8", ExportIndex.GuidOfMeta("fileFormatVersion: 2\nguid: 0fb507873f962509567a8ace1719bac8\nNativeFormatImporter:\n"));

    [Fact]
    public void ReferencedGuids_DistinctInOrder_SkipsBuiltins()
    {
        var yaml = "m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}\n" +
                   "m_Sprite: {fileID: 21300000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 2}\n" +
                   "x: {fileID: 13312, guid: 0000000000000000e000000000000000, type: 0}\n" +
                   "y: {fileID: 21300000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 2}\n";
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, ExportIndex.ReferencedGuids(yaml));
    }

    [Fact]
    public void Closure_FollowsTextAssets_SkipsScriptsShadersAndUnknown()
    {
        var idx = new ExportIndex(new Dictionary<string, string>
        {
            ["t1"] = "MonoBehaviour/Wall_rules_0.asset", ["s1"] = "Sprite/Wall_0.asset", ["x1"] = "Texture2D/Wall.png",
            ["sc"] = "Scripts/Unity.2D.Tilemap.Extras/RuleTile.cs", ["sh"] = "Shader/Sprite-Lit.shader",
        });
        var files = new Dictionary<string, string>
        {
            ["MonoBehaviour/Wall_rules_0.asset"] = "guid: sc\nguid: s1\nguid: s1\nguid: zz",
            ["Sprite/Wall_0.asset"] = "guid: x1\nguid: sh",
            ["Texture2D/Wall.png"] = "guid: t1",
        };
        var got = idx.Closure("t1", p => files[p]);
        Assert.Equal(new[] { "MonoBehaviour/Wall_rules_0.asset", "Sprite/Wall_0.asset", "Texture2D/Wall.png" }, got);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var idx = new ExportIndex(new Dictionary<string, string> { ["g1"] = "Sprite/A b_0.asset", ["g2"] = "Texture2D/A b.png" });
        var file = Path.GetTempFileName();
        try
        {
            idx.Save(file);
            var back = ExportIndex.Load(file);
            Assert.Equal("Sprite/A b_0.asset", back.PathOf("g1"));
            Assert.Equal("Texture2D/A b.png", back.PathOf("g2"));
            Assert.Null(back.PathOf("nope"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Stamp_ChangesWhenTheExportChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapkit-stamp-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "MonoBehaviour"));
        try
        {
            File.WriteAllText(Path.Combine(root, "MonoBehaviour", "a.asset"), "x");
            var before = ExportIndex.Stamp(root);
            Assert.Equal(before, ExportIndex.Stamp(root));
            File.WriteAllText(Path.Combine(root, "MonoBehaviour", "b.asset"), "y");   // (a re-extraction: new or changed files)
            Assert.NotEqual(before, ExportIndex.Stamp(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OnlyFor_KeepsWhatAnotherSetStillNeeds()
    {
        var walls = new[] { "MonoBehaviour/Walls_rules_0.asset", "Sprite/Walls_0.asset", "Texture2D/Shared.png" };
        var floor = new[] { "MonoBehaviour/Floor_rules_0.asset", "Sprite/Floor_0.asset", "Texture2D/Shared.png" };
        Assert.Equal(new[] { "MonoBehaviour/Walls_rules_0.asset", "Sprite/Walls_0.asset" }, ExportIndex.OnlyFor(walls, floor));
    }

    [Fact]
    public void Build_IndexesMetaFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapkit-idx-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "Sprite"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Sprite", "Wall_0.asset"), "x");
            File.WriteAllText(Path.Combine(root, "Sprite", "Wall_0.asset.meta"), "fileFormatVersion: 2\nguid: 1234567890abcdef1234567890abcdef\n");
            Assert.Equal("Sprite/Wall_0.asset", ExportIndex.Build(root).PathOf("1234567890abcdef1234567890abcdef"));
        }
        finally { Directory.Delete(root, true); }
    }
}

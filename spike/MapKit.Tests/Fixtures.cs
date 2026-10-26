using System.Collections.Generic;

/// <summary>A small scene in Unity's text format, shaped like the game's: a "== Props" group holding a breakable vase
/// (DamageableObject + ItemDropper; Art > Sprite, Colliders) and a grass tuft (SimpleEvents; Art > Sprite, HeightMap),
/// a second grass with the same art, a third with other art, and a camera that isn't a prop.</summary>
static class Fixtures
{
    public const string Damageable = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ItemDropper = "ffffffffffffffffffffffffffffffff",
        SimpleEvents = "dddddddddddddddddddddddddddddddd", GameMaterial = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        VaseSprite = "cccccccccccccccccccccccccccccccc", GrassSprite = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", OtherGrass = "9999999999999999999999999999999a";

    public static readonly Dictionary<string, string> Classes = new Dictionary<string, string>
    {
        [Damageable] = "DamageableObject", [ItemDropper] = "ItemDropper", [SimpleEvents] = "SimpleEvents",
    };

    public static string ClassOf(string guid) => guid != null && Classes.TryGetValue(guid, out var c) ? c : null;

    static string Go(long id, string name, params long[] comps)
    {
        var s = $"--- !u!1 &{id}\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n";
        foreach (var c in comps) s += $"  - component: {{fileID: {c}}}\n";
        return s + $"  m_Layer: 0\n  m_Name: {name}\n  m_IsActive: 1\n";
    }

    static string Tr(long id, long go, long father, float x, float y, params long[] kids)
    {
        var s = $"--- !u!4 &{id}\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {{fileID: {go}}}\n  m_LocalPosition: {{x: {x}, y: {y}, z: 0}}\n  m_LocalScale: {{x: 1, y: 1, z: 1}}\n  m_Children:\n";
        if (kids.Length == 0) s = s.Replace("  m_Children:\n", "  m_Children: []\n");
        foreach (var k in kids) s += $"  - {{fileID: {k}}}\n";
        return s + $"  m_Father: {{fileID: {father}}}\n";
    }

    static string Mb(long id, long go, string script) =>
        $"--- !u!114 &{id}\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_GameObject: {{fileID: {go}}}\n  m_Enabled: 1\n  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}\n  owner: {{fileID: {go}}}\n";

    static string Sr(long id, long go, string sprite) =>
        $"--- !u!212 &{id}\nSpriteRenderer:\n  m_ObjectHideFlags: 0\n  m_GameObject: {{fileID: {go}}}\n  m_Enabled: 1\n  m_Materials:\n  - {{fileID: 2100000, guid: {GameMaterial}, type: 2}}\n  m_SortingLayerID: -1968635437\n  m_SortingOrder: 0\n  m_Sprite: {{fileID: 21300000, guid: {sprite}, type: 3}}\n";

    static string Grass(long b, string name, string sprite, float x) =>
        Go(b, name, b + 1, b + 2) + Tr(b + 1, b, 11, x, 0, b + 11, b + 21) + Mb(b + 2, b, SimpleEvents) +
        Go(b + 10, "Art", b + 11) + Tr(b + 11, b + 10, b + 1, 0, 0, b + 31) +
        Go(b + 20, "HeightMap", b + 21) + Tr(b + 21, b + 20, b + 1, 0, 0) +
        Go(b + 30, "Sprite", b + 31, b + 32) + Tr(b + 31, b + 30, b + 11, 0, 0) + Sr(b + 32, b + 30, sprite);

    public static readonly string Scene =
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
        Go(10, "== Props", 11) + Tr(11, 10, 0, 0, 0, 101, 501, 1501, 2501) +
        // the vase: DamageableObject + ItemDropper; Art > Sprite; Colliders
        Go(100, "Vessel1_Breakable_01", 101, 102, 103) + Tr(101, 100, 11, 5, 3, 201, 301) +
        Mb(102, 100, Damageable) + Mb(103, 100, ItemDropper) +
        Go(200, "Art", 201) + Tr(201, 200, 101, 0, 0, 401) +
        Go(300, "Colliders", 301) + Tr(301, 300, 101, 0, 0) +
        Go(400, "Sprite", 401, 402) + Tr(401, 400, 201, 0, 0.5f) + Sr(402, 400, VaseSprite) +
        Grass(500, "GrassDark2_13", GrassSprite, 1) + Grass(1500, "GrassDark2_14", GrassSprite, 2) + Grass(2500, "GrassDark2_15", OtherGrass, 3) +
        Go(900, "Main Camera", 901) + Tr(901, 900, 0, 0, 0);
}

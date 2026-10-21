using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EmbervilleGhost.Core;

/// <summary>One thing built outside the estate (build mode on B): which placeable, its save id (a chest's contents
/// are kept under it), where, which way, and in which scene.</summary>
public sealed class Built
{
    public string Scene;
    public string PlaceableId;
    public string SaveId;
    public float X, Y;
    public int Dir;
}

/// <summary>
/// Everything built outside the estate in one world, saved in our own file (&lt;world&gt;.build), never in the
/// estate's own list. One line per thing: scene|placeableId|saveId|x|y|dir (numbers in invariant culture).
/// </summary>
public sealed class BuildList
{
    const float Near = 0.01f;
    readonly List<Built> items = new List<Built>();

    public IReadOnlyList<Built> All => items;

    public void Add(Built b) => items.Add(b);

    public bool Remove(Built b) => items.Remove(b);

    public void Clear() => items.Clear();

    public Built Find(string scene, string placeableId, float x, float y) =>
        items.FirstOrDefault(b => b.Scene == scene && b.PlaceableId == placeableId && Math.Abs(b.X - x) <= Near && Math.Abs(b.Y - y) <= Near);

    public IEnumerable<Built> InScene(string scene) => items.Where(b => b.Scene == scene);

    public string Save()
    {
        var sb = new StringBuilder();
        foreach (var b in items)
            sb.Append(b.Scene).Append('|').Append(b.PlaceableId).Append('|').Append(b.SaveId ?? "").Append('|')
              .Append(b.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(b.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(b.Dir.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    public static BuildList Load(string text)
    {
        var list = new BuildList();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var p = raw.Trim().Split('|');
            if (p.Length != 6 || p[0].Length == 0 || p[1].Length == 0) continue;
            if (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
            if (!int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dir)) continue;
            list.Add(new Built { Scene = p[0], PlaceableId = p[1], SaveId = p[2], X = x, Y = y, Dir = dir });
        }
        return list;
    }
}

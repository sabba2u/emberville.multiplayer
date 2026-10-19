using System.Collections.Generic;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Matching ids for things placed in a scene: both copies loaded the same scene file, so the same object has the same
/// path ("TutorialArea:002_Cavern/==Fauna/Chicken", "#n" for same-named siblings). Hashed to 4 bytes (FNV-1a).
/// Things spawned at runtime don't match this way (they need host-assigned ids, like DropSync).
/// </summary>
internal static class SceneIds
{
    static readonly Dictionary<Component, uint> ids = new Dictionary<Component, uint>();
    static readonly Dictionary<uint, Component> byId = new Dictionary<uint, Component>();
    static readonly Dictionary<System.Type, float> lastRescan = new Dictionary<System.Type, float>();

    public static uint Of(Component c)
    {
        if (ids.TryGetValue(c, out var id)) return id;
        id = Fnv(PathOf(c.transform));
        ids[c] = id;
        byId[id] = c;
        return id;
    }

    /// <summary>The object with this id, or null. Rescans the scene for unknown ids at most once a second per type.</summary>
    public static T Find<T>(uint id) where T : Component
    {
        if (byId.TryGetValue(id, out var c) && c is T found && found) return found;
        lastRescan.TryGetValue(typeof(T), out float last);
        if (Time.unscaledTime - last < 1f) return null;
        lastRescan[typeof(T)] = Time.unscaledTime;
        foreach (var o in Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Of(o);
        return byId.TryGetValue(id, out c) ? c as T : null;
    }

    public static void Clear()
    {
        ids.Clear();
        byId.Clear();
        lastRescan.Clear();
    }

    public static string PathOf(Transform t)
    {
        string p = "";
        for (var x = t; x; x = x.parent)
        {
            int n = 0;
            if (x.parent)
                for (int i = 0; i < x.GetSiblingIndex(); i++)
                    if (x.parent.GetChild(i).name == x.name) n++;
            p = "/" + x.name + (n > 0 ? "#" + n : "") + p;
        }
        return t.gameObject.scene.name + ":" + p;
    }

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}

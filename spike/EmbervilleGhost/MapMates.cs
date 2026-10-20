using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// Teammates on the big world map (M). The map is the generated wilds (WorldMap: regions, portals, fog), drawn by its
/// own camera (WorldMapContainer.canvasCamera); our own spot on it is WorldMapContainer.playerPreview, placed at our
/// world position / 10 * 6 and shown only while we're in the wilds (WorldMapState.IsInDungeon). Each teammate who is
/// in the wilds too (PlayerState carries that flag) gets a copy of that same icon, tinted in their tag colour (host
/// gold, clients blue), at their position, plus their name next to it while the map is open.
/// </summary>
internal static class MapMates
{
    public static Plugin Plugin;
    static readonly Dictionary<int, GameObject> icons = new Dictionary<int, GameObject>();

    /// <summary>Are we in the generated wilds (where the world map's positions apply)?</summary>
    public static bool InWilds => Singleton<WorldMap>.HasMain && Singleton<WorldMap>.main.WorldMapState.IsInDungeon.Value;

    static bool MapOpen(out WorldMapContainer container)
    {
        container = null;
        if (!Singleton<WorldMap>.HasMain) return false;
        var map = Singleton<WorldMap>.main;
        container = map.worldMapContainer;
        return map.uiObject && map.uiObject.activeInHierarchy && container && container.playerPreview;
    }

    /// <summary>Every frame (from Hud.Draw, even when the game covers the screen: the map is a menu).</summary>
    public static void Draw()
    {
        if (Plugin == null || !Plugin.Connected || !MapOpen(out var container)) { HideAll(); return; }
        var preview = container.playerPreview;
        var seen = new HashSet<int>();
        foreach (var p in Plugin.Players)
        {
            if (!p.HaveState || !p.InWilds) continue;
            seen.Add(p.Id);
            if (!icons.TryGetValue(p.Id, out var icon) || !icon)
            {
                icon = Object.Instantiate(preview, preview.transform.parent);
                icon.name = $"EmbervilleGhost_MapMate_P{p.Id + 1}";
                Tint(icon, p.Id == 0 ? new Color(1f, 0.8f, 0.3f, 1f) : new Color(0.45f, 0.8f, 1f, 1f));
                icons[p.Id] = icon;
            }
            icon.SetActive(true);
            icon.transform.localPosition = (Vector3)(UnityEngine.Vector2)(p.LastPosition / 10f * 6f);
            icon.transform.SetSiblingIndex(icon.transform.parent.childCount - 1);
            Label(container, icon.transform.position, string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName, p.Id == 0);
        }
        foreach (var pair in icons) if (pair.Value && !seen.Contains(pair.Key)) pair.Value.SetActive(false);
    }

    /// <summary>Their name beside the icon, when the map camera draws straight to the screen.</summary>
    static void Label(WorldMapContainer container, Vector3 world, string name, bool host)
    {
        var cam = container.canvasCamera;
        if (!cam || cam.targetTexture) return;   // drawn into a texture: we can't tell where on screen
        var s = cam.WorldToScreenPoint(world);
        if (s.z < 0) return;
        var at = new Vector2(s.x + 10, Screen.height - s.y - 8);
        var dl = ImGui.GetForegroundDrawList();
        var size = ImGui.CalcTextSize(name);
        dl.AddRectFilled(new Vector2(at.x - 3, at.y - 1), new Vector2(at.x + size.x + 3, at.y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.6f)), 3f);
        dl.AddText(at, ImGui.ColorConvertFloat4ToU32(host ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f)), name);
    }

    static void Tint(GameObject icon, Color colour)
    {
        foreach (var r in icon.GetComponentsInChildren<SpriteRenderer>(true)) r.color = colour;
        foreach (var g in icon.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)) g.color = colour;
    }

    static void HideAll()
    {
        foreach (var icon in icons.Values) if (icon && icon.activeSelf) icon.SetActive(false);
        if (Plugin != null && !Plugin.Connected && icons.Count > 0)
        {
            foreach (var icon in icons.Values) if (icon) Object.Destroy(icon);
            icons.Clear();
        }
    }
}

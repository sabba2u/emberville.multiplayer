using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// An item's icon by its address key (what we send over the wire for items): from the game's loaded items when it has
/// it, else loaded once through Addressables (like the drops / chests / trades do), cached. Until it's there: null.
/// </summary>
internal static class ItemIcons
{
    static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
    static readonly HashSet<string> loading = new HashSet<string>();

    public static Sprite Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (icons.TryGetValue(key, out var s)) return s;
        if (loading.Add(key))
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                icons[key] = h.Status == AsyncOperationStatus.Succeeded && h.Result ? h.Result.itemIconUI : null;
            };
        return null;
    }

    /// <summary>Draw a sprite (atlas part) into a box, keeping its shape, centred.</summary>
    public static void Draw(ImGuiNET.ImDrawListPtr dl, Sprite sprite, ImGuiNET.Vector2 min, ImGuiNET.Vector2 max, uint tint)
    {
        if (!sprite || !sprite.texture) return;
        Rect r;
        try { r = sprite.textureRect; } catch { r = sprite.rect; }
        float w = sprite.texture.width, h = sprite.texture.height;
        float bw = max.x - min.x, bh = max.y - min.y;
        float scale = Mathf.Min(bw / r.width, bh / r.height);
        float dw = r.width * scale, dh = r.height * scale;
        var a = new ImGuiNET.Vector2(min.x + (bw - dw) / 2, min.y + (bh - dh) / 2);
        var b = new ImGuiNET.Vector2(a.x + dw, a.y + dh);
        dl.AddImage(ImGuiUnity.TextureId(sprite.texture), a, b,
            new ImGuiNET.Vector2(r.xMin / w, r.yMax / h), new ImGuiNET.Vector2(r.xMax / w, r.yMin / h), tint);   // Unity's v is bottom-up
    }
}

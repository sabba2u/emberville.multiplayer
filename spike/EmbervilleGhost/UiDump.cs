using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// UI dump (/uidump, or the Insert menu): writes the game's UI that's on screen right now (every active Canvas that
/// isn't ours) to BepInEx/UIDumps/uidump_&lt;time&gt;/: the hierarchy with each element's size and anchors, Images (sprite,
/// 9-slice or not, colour), text (font, size, colour), Buttons / Toggles / Sliders (transition, colours, sprite swaps),
/// layout groups, size fitters, canvases and the game's own scripts on each; a summary of every sprite used (with its
/// 9-slice border); and each of those sprites as a PNG, so we can see which frame is which. Open a game window
/// (inventory, options, a shop) and dump it: that's how our own windows can be made of the same parts.
/// </summary>
internal static class UiDump
{
    public static void Dump()
    {
        string dir = Path.Combine(Paths.BepInExRootPath, "UIDumps", $"uidump_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        var spritesDir = Path.Combine(dir, "sprites");
        Directory.CreateDirectory(spritesDir);

        var sb = new StringBuilder();
        var used = new Dictionary<Sprite, (int count, HashSet<string> types)>();
        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
            .Where(c => c && c.isActiveAndEnabled && c.isRootCanvas && !c.name.StartsWith("EmbervilleGhost") && !c.name.StartsWith("ImGui"))
            .OrderBy(c => c.sortingOrder).ToList();
        sb.AppendLine($"Emberville UI dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}, screen {Screen.width}x{Screen.height}, scene {LocalPlayer.Scene}");
        sb.AppendLine($"{canvases.Count} active root canvases. Only active objects are listed. Sizes are RectTransform sizes (canvas units).");
        sb.AppendLine();
        foreach (var c in canvases) Walk(c.transform, 0, sb, used);

        // Summary: every sprite in use, with its 9-slice border, most used first; and the PNGs.
        var summary = new StringBuilder();
        summary.AppendLine("SPRITES IN USE (count, image types, size, 9-slice border L,B,R,T, texture):");
        int saved = 0;
        foreach (var pair in used.OrderByDescending(p => p.Value.count))
        {
            var s = pair.Key;
            var b = s.border;
            summary.AppendLine($"  {pair.Value.count,4}x  {s.name,-40} [{string.Join(",", pair.Value.types)}] {s.rect.width}x{s.rect.height}" +
                               (b != Vector4.zero ? $"  9-slice {b.x},{b.y},{b.z},{b.w}" : "") + $"  ({(s.texture ? s.texture.name : "?")})");
            if (SavePng(s, Path.Combine(spritesDir, Safe(s.name) + ".png"))) saved++;
        }
        summary.AppendLine();
        File.WriteAllText(Path.Combine(dir, "ui.txt"), summary.ToString() + sb);
        NetLog.Success($"UI dump: {canvases.Count} canvases, {used.Count} sprites ({saved} saved as PNG) in {dir}", toast: true);
    }

    static void Walk(Transform t, int depth, StringBuilder sb, Dictionary<Sprite, (int, HashSet<string>)> used)
    {
        if (!t.gameObject.activeInHierarchy) return;
        string pad = new string(' ', depth * 2);
        var rt = t as RectTransform;
        sb.Append(pad).Append(t.name);
        if (rt) sb.Append($"  [{rt.rect.width:0}x{rt.rect.height:0} anchors {V(rt.anchorMin)}-{V(rt.anchorMax)} pivot {V(rt.pivot)}]");
        sb.AppendLine();
        foreach (var c in t.GetComponents<Component>())
        {
            if (!c || c is Transform) continue;
            string line = Describe(c, used);
            if (line != null) sb.Append(pad).Append("  - ").AppendLine(line);
        }
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb, used);
    }

    static string Describe(Component c, Dictionary<Sprite, (int, HashSet<string>)> used)
    {
        switch (c)
        {
            case Image img:
                if (img.sprite)
                {
                    used.TryGetValue(img.sprite, out var u);
                    (u.Item2 ??= new HashSet<string>()).Add(img.type.ToString());
                    used[img.sprite] = (u.Item1 + 1, u.Item2);
                }
                return $"Image sprite={(img.sprite ? img.sprite.name : "none")} type={img.type}" +
                       (img.type == Image.Type.Sliced || img.type == Image.Type.Tiled ? $" ppuMul={img.pixelsPerUnitMultiplier}" + (img.fillCenter ? "" : " noCenter") : "") +
                       (img.type == Image.Type.Filled ? $" fill={img.fillMethod} {img.fillAmount:0.##}" : "") +
                       $" color={C(img.color)}" + (img.material && img.material.name != "Default UI Material" ? $" material={img.material.name}" : "") +
                       (img.raycastTarget ? "" : " noRaycast");
            case RawImage raw:
                return $"RawImage texture={(raw.texture ? raw.texture.name : "none")} color={C(raw.color)}";
            case TextMeshProUGUI tmp:
                return $"Text \"{Short(tmp.text)}\" font={(tmp.font ? tmp.font.name : "?")} size={tmp.fontSize:0.#}{(tmp.enableAutoSizing ? " auto" : "")} " +
                       $"style={tmp.fontStyle} align={tmp.alignment} color={C(tmp.color)}" + (tmp.fontSharedMaterial ? $" material={tmp.fontSharedMaterial.name}" : "");
            case Text legacy:
                return $"LegacyText \"{Short(legacy.text)}\" size={legacy.fontSize}";
            case Button btn: return "Button " + Sel(btn);
            case Toggle tog: return $"Toggle isOn={tog.isOn} graphic={(tog.graphic ? tog.graphic.name : "none")} group={(tog.group ? tog.group.name : "none")} " + Sel(tog);
            case Slider sl: return $"Slider {sl.minValue}-{sl.maxValue} value={sl.value:0.##} dir={sl.direction} fill={(sl.fillRect ? sl.fillRect.name : "none")} handle={(sl.handleRect ? sl.handleRect.name : "none")} " + Sel(sl);
            case Scrollbar sb: return $"Scrollbar dir={sb.direction} " + Sel(sb);
            case Selectable sel: return $"{sel.GetType().Name} " + Sel(sel);
            case ScrollRect sr: return $"ScrollRect h={sr.horizontal} v={sr.vertical} movement={sr.movementType} content={(sr.content ? sr.content.name : "none")}";
            case HorizontalOrVerticalLayoutGroup lg:
                return $"{lg.GetType().Name} spacing={lg.spacing} padding={lg.padding.left},{lg.padding.right},{lg.padding.top},{lg.padding.bottom} align={lg.childAlignment} " +
                       $"controlSize={lg.childControlWidth}/{lg.childControlHeight} expand={lg.childForceExpandWidth}/{lg.childForceExpandHeight}";
            case GridLayoutGroup g: return $"GridLayoutGroup cell={V(g.cellSize)} spacing={V(g.spacing)} constraint={g.constraint} {g.constraintCount}";
            case ContentSizeFitter f: return $"ContentSizeFitter h={f.horizontalFit} v={f.verticalFit}";
            case LayoutElement le: return $"LayoutElement min={le.minWidth}x{le.minHeight} pref={le.preferredWidth}x{le.preferredHeight} flex={le.flexibleWidth}x{le.flexibleHeight}{(le.ignoreLayout ? " ignore" : "")}";
            case Canvas cv: return $"Canvas mode={cv.renderMode} order={cv.sortingOrder} layer={cv.sortingLayerName} pixelPerfect={cv.pixelPerfect}";
            case CanvasScaler cs: return $"CanvasScaler mode={cs.uiScaleMode} ref={V(cs.referenceResolution)} match={cs.matchWidthOrHeight} ppu={cs.referencePixelsPerUnit}";
            case CanvasGroup cg: return $"CanvasGroup alpha={cg.alpha:0.##} interactable={cg.interactable} blocks={cg.blocksRaycasts}";
            case Mask m: return $"Mask showGraphic={m.showMaskGraphic}";
            case RectMask2D: return "RectMask2D";
            case Outline o: return $"Outline color={C(o.effectColor)} dist={V(o.effectDistance)}";
            case Shadow s: return $"Shadow color={C(s.effectColor)} dist={V(s.effectDistance)}";
            case Animator a: return $"Animator controller={(a.runtimeAnimatorController ? a.runtimeAnimatorController.name : "none")}";
            case CanvasRenderer: case GraphicRaycaster: return null;
            case MonoBehaviour mb: return $"[script] {mb.GetType().FullName}{(mb.enabled ? "" : " (off)")}";
            default: return c.GetType().Name;
        }
    }

    static string Sel(Selectable s)
    {
        string t = $"interactable={s.interactable} transition={s.transition}";
        if (s.transition == Selectable.Transition.ColorTint)
        {
            var cb = s.colors;
            t += $" normal={C(cb.normalColor)} hover={C(cb.highlightedColor)} pressed={C(cb.pressedColor)} selected={C(cb.selectedColor)} disabled={C(cb.disabledColor)} x{cb.colorMultiplier} {cb.fadeDuration}s";
        }
        else if (s.transition == Selectable.Transition.SpriteSwap)
        {
            var ss = s.spriteState;
            t += $" hover={N(ss.highlightedSprite)} pressed={N(ss.pressedSprite)} selected={N(ss.selectedSprite)} disabled={N(ss.disabledSprite)}";
        }
        else if (s.transition == Selectable.Transition.Animation)
        {
            var at = s.animationTriggers;
            t += $" triggers={at.normalTrigger}/{at.highlightedTrigger}/{at.pressedTrigger}/{at.selectedTrigger}/{at.disabledTrigger}";
        }
        t += $" target={(s.targetGraphic ? s.targetGraphic.name : "none")} nav={s.navigation.mode}";
        return t;
    }

    /// <summary>The sprite's pixels as a PNG (its texture is usually not readable: copy it through a RenderTexture).</summary>
    static bool SavePng(Sprite s, string path)
    {
        if (!s || !s.texture || File.Exists(path)) return false;
        RenderTexture rt = null;
        var previous = RenderTexture.active;
        try
        {
            var tex = s.texture;
            rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            Rect r;
            try { r = s.textureRect; } catch { r = s.rect; }
            var outTex = new Texture2D(Mathf.Max(1, (int)r.width), Mathf.Max(1, (int)r.height), TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(r.x, tex.height - r.y - r.height, r.width, r.height), 0, 0);   // ReadPixels counts from the top
            outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            UnityEngine.Object.Destroy(outTex);
            return true;
        }
        catch (Exception e) { NetLog.Warn($"UI dump: sprite {s.name}: {e.Message}", toast: false); return false; }
        finally
        {
            RenderTexture.active = previous;
            if (rt) RenderTexture.ReleaseTemporary(rt);
        }
    }

    static string V(Vector2 v) => $"{v.x:0.##},{v.y:0.##}";
    static string C(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);
    static string N(UnityEngine.Object o) => o ? o.name : "none";
    static string Short(string s) { s = (s ?? "").Replace("\n", "\\n"); return s.Length > 50 ? s.Substring(0, 47) + "..." : s; }
    static string Safe(string s) { foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_'); return s; }
}

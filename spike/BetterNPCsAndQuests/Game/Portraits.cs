using System;
using System.Collections.Generic;
using BetterNPCsAndQuests.Core;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// The dialogue window's portrait of our NPCs: their portrait.png, or a picture taken of their body (a camera that sees
/// only them, for a moment). Never null: without a sprite the game shows the line as the narrator's (no name).
/// </summary>
internal static class Portraits
{
    const int Size = 128;
    // (by reflection: Unity 6 also has a span overload, which this build can't resolve)
    static readonly System.Reflection.MethodInfo LoadImage =
        typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
    static readonly Dictionary<string, Sprite> cache = new(StringComparer.OrdinalIgnoreCase);
    static Sprite blank;

    public static void Forget() => cache.Clear();

    public static Sprite For(NpcDef def)
    {
        if (cache.TryGetValue(def.Id, out var s) && s) return s;
        s = null;
        try
        {
            if (State.Lib.Portraits.TryGetValue(def.Id, out var png)) s = FromPng(png, def.Id);
            if (!s) s = Snapshot(def);
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: portrait of {def.Name}: {e.Message}"); }
        if (!s) s = Blank();
        cache[def.Id] = s;
        return s;
    }

    static Sprite FromPng(byte[] png, string id)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "BNQ_portrait_" + id };
        if (!(bool)LoadImage.Invoke(null, new object[] { tex, png, false })) { Log.Warn($"NPCs and quests: {id}'s portrait.png isn't a picture"); return null; }
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0f), 32f);
    }

    /// <summary>Their body, head and shoulders, from a camera that sees only them.</summary>
    static Sprite Snapshot(NpcDef def)
    {
        var npc = Npcs.Find(def.Id);
        if (npc == null || !npc.Body.Root) return null;
        int layer = FreeLayer();
        if (layer < 0) return null;
        var root = npc.Body.Root;
        var old = new List<(GameObject go, int layer)>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "BNQ_Name") continue;
            old.Add((t.gameObject, t.gameObject.layer));
            t.gameObject.layer = layer;
        }
        var rt = RenderTexture.GetTemporary(Size, Size, 16, RenderTextureFormat.ARGB32);
        var camGo = new GameObject("BNQ_PortraitCamera");
        var prev = RenderTexture.active;
        try
        {
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 0.9f;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.targetTexture = rt;
            cam.transform.position = root.transform.position + new Vector3(0f, 1.5f, -10f);
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "BNQ_portrait_" + def.Id };
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            bool any = false;
            foreach (var px in tex.GetPixels32()) if (px.a > 0) { any = true; break; }
            if (!any) { UnityEngine.Object.Destroy(tex); Log.Info($"NPCs and quests: the camera saw nothing of {def.Name} (no portrait)"); return null; }
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0f), 32f);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(camGo);
            foreach (var (go, l) in old) if (go) go.layer = l;
        }
    }

    static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return -1;
    }

    static Sprite Blank()
    {
        if (blank) return blank;
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        tex.SetPixels32(new Color32[16]);
        tex.Apply();
        return blank = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f), 32f);
    }
}

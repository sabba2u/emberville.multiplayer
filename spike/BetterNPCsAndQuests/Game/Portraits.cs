using System;
using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace BetterNPCsAndQuests;

/// <summary>
/// The dialogue window's portrait of our NPCs: their portrait.png, or the NPC themselves, live, as the inventory shows
/// your character (InventoryUI.playerPortraitCamera: a camera put on the player while the panel is open). Ours is a
/// camera on the NPC we're talking to, framed like the inventory's, rendering into a texture the portrait sprite is
/// copied from every frame. It sees only them: around its own render, their body is moved onto a layer nothing else
/// uses. Never null: without a sprite the game shows the line as the narrator's (no name).
/// </summary>
internal static class Portraits
{
    const int Height = 256;
    const float Zoom = 0.5f;   // of the inventory camera's view: she fills the box
    // (by reflection: Unity 6 also has a span overload, which this build can't resolve)
    static readonly System.Reflection.MethodInfo LoadImage =
        typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
    static readonly Dictionary<string, Sprite> cache = new(StringComparer.OrdinalIgnoreCase);
    static Sprite blank;

    // the live one
    static Camera cam;
    static RenderTexture rt;
    static Texture2D tex;
    static Sprite live;
    static Npc liveFor;
    static Vector3 offset;
    static bool framed, hooked, logged, rendered;
    static int layer = -1, body = -1;
    static readonly List<(GameObject go, int layer)> moved = new();

    public static void Forget() => cache.Clear();

    public static Sprite For(NpcDef def)
    {
        if (cache.TryGetValue(def.Id, out var s) && s) return s;
        s = null;
        try
        {
            if (State.Lib.Portraits.TryGetValue(def.Id, out var png)) { s = FromPng(png, def.Id); if (s) cache[def.Id] = s; }
            if (!s) s = Live(def);
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: portrait of {def.Name}: {e.Message}"); }
        return s ? s : Blank();
    }

    static Sprite FromPng(byte[] png, string id)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "BNQ_portrait_" + id };
        if (!(bool)LoadImage.Invoke(null, new object[] { t, png, false })) { Log.Warn($"NPCs and quests: {id}'s portrait.png isn't a picture"); return null; }
        return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0f), 32f);
    }

    // ---- live ----

    static Sprite Live(NpcDef def)
    {
        var npc = Npcs.Find(def.Id);
        if (npc == null || npc.Body == null || !npc.Body.Root) { Log.Info($"NPCs and quests: {def.Name} isn't here (no portrait)"); return null; }
        if (layer < 0) layer = FreeLayer();
        if (layer < 0) { Log.Warn("NPCs and quests: no free layer for the portrait camera"); return null; }
        if (!live) Make();
        if (liveFor != npc)
        {
            // a new conversation: no picture until the camera has filmed them where they stand now (else the last
            // conversation's last frame shows for a moment: them somewhere else, turned another way)
            Clear();
            rendered = false;
        }
        liveFor = npc;
        framed = false;
        Hook();
        return live;
    }

    /// <summary>The texture and sprite, shaped and sized like the game's own portraits (so the window lays it out the same).</summary>
    static void Make()
    {
        var game = GamePortrait();
        float aspect = game ? game.rect.width / game.rect.height : 1f;
        int w = Mathf.Max(16, Mathf.RoundToInt(Height * aspect));
        float ppu = game ? Height * game.pixelsPerUnit / game.rect.height : 64f;
        rt = new RenderTexture(w, Height, 16, RenderTextureFormat.ARGB32) { name = "BNQ_portrait_rt", filterMode = FilterMode.Point };
        rt.Create();
        tex = new Texture2D(w, Height, TextureFormat.ARGB32, false) { filterMode = FilterMode.Point, name = "BNQ_portrait_live" };
        tex.SetPixels32(new Color32[w * Height]);
        tex.Apply();
        live = Sprite.Create(tex, new Rect(0, 0, w, Height), new Vector2(0.5f, 0f), ppu);
        Log.Info($"NPCs and quests: live portrait {w}x{Height}" + (game ? $" (shaped like {game.name})" : ""));
    }

    static Sprite GamePortrait()
    {
        var dm = Singleton<DialogueManager>.HasMain ? Singleton<DialogueManager>.main : null;
        var entries = dm && dm.npcs ? dm.npcs.entries : null;
        return entries?.Select(e => e.Sprite).FirstOrDefault(s => s);
    }

    /// <summary>Every frame (Plugin.Update): the camera follows the NPC while we talk to them, and is off otherwise.</summary>
    public static void Tick()
    {
        if (liveFor == null) return;
        if (!Talk.IsOpen || Talk.Speaking != liveFor || liveFor.Body == null || !liveFor.Body.Root)
        {
            if (cam) cam.enabled = false;
            if (!Talk.IsOpen) liveFor = null;
            return;
        }
        if (!cam) cam = NewCamera();
        if (!framed) Frame();
        cam.transform.position = liveFor.Body.Root.transform.position + offset;
        cam.enabled = true;
        if (!rendered) return;
        // last frame's picture into the sprite (the GPU copies it; nothing comes back to the CPU)
        if (SystemInfo.copyTextureSupport != CopyTextureSupport.None) Graphics.CopyTexture(rt, tex);
        else if (Time.frameCount % 3 == 0)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
        }
    }

    static Camera NewCamera()
    {
        var go = new GameObject("BNQ_PortraitCamera");
        var c = go.AddComponent<Camera>();
        c.enabled = false;
        var like = InventoryCamera() ?? Camera.main;
        if (like) c.CopyFrom(like);
        body = LayerMask.NameToLayer("Player");
        c.orthographic = true;
        c.targetTexture = rt;
        c.cullingMask = 1 << layer;
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = new Color(0f, 0f, 0f, 0f);
        c.depth = -100f;
        if (like) SameRenderer(like, c);
        Light(c);
        return c;
    }

    /// <summary>
    /// The URP renderer the inventory's camera draws with (its UniversalAdditionalCameraData, which CopyFrom leaves
    /// behind; by name: this mod doesn't build against URP). Without it a camera gets the default one.
    /// </summary>
    static void SameRenderer(Camera from, Camera to)
    {
        try
        {
            var data = from.GetComponents<Component>().FirstOrDefault(x => x && x.GetType().Name == "UniversalAdditionalCameraData");
            if (!data) { Log.Info("NPCs and quests: the portrait's model camera has no URP camera data"); return; }
            var type = data.GetType();
            var field = type.GetField("m_RendererIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            int index = field != null ? (int)field.GetValue(data) : -1;
            var ours = to.GetComponent(type) ?? to.gameObject.AddComponent(type);
            if (index >= 0) type.GetMethod("SetRenderer")?.Invoke(ours, new object[] { index });
            // (post-processing and the rest as theirs)
            foreach (var name in new[] { "renderPostProcessing", "antialiasing", "renderShadows" })
            {
                var p = type.GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(ours, p.GetValue(data));
            }
            Log.Info($"NPCs and quests: portrait camera draws with renderer {index}");
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: portrait camera's renderer: {e.Message}"); }
    }

    /// <summary>
    /// The portrait's own light. URP's 2D renderer only uses the lights on a layer the camera sees (Light2DCullResult
    /// skips the rest, global ones too), and ours sees only our spare layer: without a light there, every lit sprite
    /// draws black. So: one global white light on that layer, always on (the main camera never sees the layer, so
    /// it never lights the world). As the inventory does it: its PreviewCamera and its light share a layer.
    /// </summary>
    static void Light(Camera c)
    {
        try
        {
            var go = new GameObject("BNQ_PortraitLight") { layer = layer };
            go.SetActive(false);
            go.transform.SetParent(c.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 1f);
            var l = go.AddComponent<UnityEngine.Rendering.Universal.Light2D>();
            // every sorting layer first (the body's parts may sit on several; the light's checks read it)
            typeof(UnityEngine.Rendering.Universal.Light2D).GetField("m_ApplyToSortingLayers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(l, SortingLayer.layers.Select(s => s.id).ToArray());
            // a wide point light, not a global one (a second global light trips URP's one-per-layer check)
            l.lightType = UnityEngine.Rendering.Universal.Light2D.LightType.Point;
            l.pointLightInnerRadius = 20f;
            l.pointLightOuterRadius = 25f;
            l.color = Color.white;
            l.intensity = 1f;
            go.SetActive(true);
            Log.Info($"NPCs and quests: portrait light on layer {go.layer}, lighting {SortingLayer.layers.Length} sorting layers");
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: portrait light: {e.Message}"); }
    }

    /// <summary>The camera the inventory shows your character with (child of InventoryUI.playerPortraitCamera).</summary>
    static Camera InventoryCamera() => InventoryRig() is GameObject rig ? rig.GetComponentInChildren<Camera>(true) : null;

    static GameObject InventoryRig()
    {
        var ui = UnityEngine.Object.FindFirstObjectByType<InventoryUI>(FindObjectsInactive.Include);
        return ui ? ui.playerPortraitCamera : null;
    }

    /// <summary>Where the camera sits from the NPC's feet, and how much it sees: as the inventory's sees you; else their outline.</summary>
    static void Frame()
    {
        framed = true;
        var rig = InventoryRig();
        var game = rig ? rig.GetComponentInChildren<Camera>(true) : null;
        string how;
        if (game)
        {
            // the rig is put on the player's position: its camera's place from the rig is its place from your feet
            offset = game.transform.position - rig.transform.position;
            cam.orthographicSize = game.orthographicSize * Zoom;   // (closer than the inventory: the dialogue box is small)
            how = $"like the inventory's (offset {offset}, size {game.orthographicSize:0.##})";
        }
        else
        {
            var root = liveFor.Body.Root.transform;
            var r = root.GetComponentsInChildren<SpriteRenderer>().Where(x => x.enabled && x.sprite && x.name != "BNQ_Name").ToList();
            var b = r.Count > 0 ? r[0].bounds : new Bounds(root.position + Vector3.up, Vector3.one * 2f);
            foreach (var x in r) b.Encapsulate(x.bounds);
            offset = new Vector3(b.center.x - root.position.x, b.center.y - root.position.y, -10f);
            cam.orthographicSize = b.extents.y * 1.1f;
            how = $"from their outline ({r.Count} sprites, size {cam.orthographicSize:0.##})";
        }
        // well back, as the main camera is: from 1 unit away (the inventory's) the parts of the body that sit a little in
        // front or behind (and swing toward the camera when they turn to the left) cross the near plane and get cut
        offset.z = -10f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 50f;
        if (!logged) { logged = true; Log.Info($"NPCs and quests: portrait camera on {liveFor.Def.Name}, framed {how}, layer {layer}"); }
    }

    // ---- only them: their body on our layer around our camera's render ----

    static void Hook()
    {
        if (hooked) return;
        hooked = true;
        RenderPipelineManager.beginCameraRendering += (_, c) => Begin(c);
        RenderPipelineManager.endCameraRendering += (_, c) => End(c);
        Camera.onPreCull += Begin;       // (the built-in pipeline: no render pipeline events there)
        Camera.onPostRender += End;
    }

    static void Begin(Camera c)
    {
        if (c != cam || !cam || liveFor?.Body?.Root == null || moved.Count > 0) return;
        foreach (var t in liveFor.Body.Root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "BNQ_Name") continue;
            // only the body you see in the world (layer Player), lit as it is there. Not the character's second body
            // for the inventory picture (AddonsHandler.playerPortrait, layer PlayerPortrait): the game's scripts dress
            // and light that one for the player, and on a copy it comes out a black shape in front of the real one.
            if (t.gameObject.layer != body) { NoteSkipped(t); continue; }
            moved.Add((t.gameObject, t.gameObject.layer));
            t.gameObject.layer = layer;
        }
    }

    static readonly HashSet<string> skipped = new();

    static void NoteSkipped(Transform t)
    {
        var r = t.GetComponent<Renderer>();
        if (r && skipped.Add(t.name)) Log.Info($"NPCs and quests: portrait leaves out {t.name} (layer {LayerMask.LayerToName(t.gameObject.layer)}, {r.GetType().Name})");
    }

    static void End(Camera c)
    {
        if (c != cam) return;
        if (moved.Count > 0) rendered = true;
        foreach (var (go, l) in moved) if (go) go.layer = l;
        moved.Clear();
    }

    static void Clear()
    {
        if (!tex) return;
        tex.SetPixels32(new Color32[tex.width * tex.height]);
        tex.Apply();
    }

    static int FreeLayer()
    {
        for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return -1;
    }

    static Sprite Blank()
    {
        if (blank) return blank;
        var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        t.SetPixels32(new Color32[16]);
        t.Apply();
        return blank = Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f), 32f);
    }
}

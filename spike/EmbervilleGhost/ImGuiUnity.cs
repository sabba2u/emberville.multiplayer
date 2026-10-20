using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Vec2 = ImGuiNET.Vector2;

namespace EmbervilleGhost;

/// <summary>
/// Dear ImGui drawn by Unity itself, inside the game's own frame (no overlay window).
/// ImGui.NET + cimgui.dll build the UI; we turn its draw lists into Unity meshes and draw them
/// from OnGUI's repaint, which runs after the game's cameras, so it lands on top.
/// Main thread only: Update() builds a frame, Render() draws it.
/// </summary>
internal static class ImGuiUnity
{
    public static event Action OnRender;
    public static bool IsOpen { get; private set; }
    public static ConfigEntry<Key> MenuKey;

    static bool ready;
    static Texture2D fontTexture;
    static Material material;
    static CommandBuffer commands;
    static readonly List<Mesh> meshes = new List<Mesh>();
    static IntPtr iniPath;
    static readonly List<char> typed = new List<char>();
    static CursorLockMode savedLock;
    static bool savedVisible;

    // ImDrawVert in memory: pos (2 floats), uv (2 floats), col (RGBA8).
    [StructLayout(LayoutKind.Sequential)]
    struct ImVert { public float X, Y, U, V; public uint Col; }

    // Unity wants attributes in Position, Color, TexCoord order, so we reorder while copying.
    [StructLayout(LayoutKind.Sequential)]
    struct Vert { public float X, Y; public uint Col; public float U, V; }

    static readonly VertexAttributeDescriptor[] layout =
    {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
    };

    public static unsafe void Init(ConfigFile config, string folder)
    {
        MenuKey = config.Bind("Overlay", "MenuKey", Key.Insert, "Opens and closes the EmbervilleGhost menu");

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        iniPath = Marshal.StringToHGlobalAnsi(Path.Combine(folder, "imgui.ini"));
        io.NativePtr->IniFilename = (byte*)iniPath;
        ImGui.StyleColorsDark();

        var fontPath = Path.Combine(folder, "Assets", "Fonts", "Bombardier-Regular.ttf");
        if (File.Exists(fontPath)) io.NativePtr->FontDefault = io.Fonts.AddFontFromFileTTF(fontPath, 16).NativePtr;

        // Font atlas -> Unity texture. Uploaded as-is: row 0 lands at v=0, which is what ImGui's UVs expect.
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int w, out int h, out int _);
        fontTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Bilinear };
        fontTexture.LoadRawTextureData(pixels, w * h * 4);
        fontTexture.Apply(false, true);
        UnityEngine.Object.DontDestroyOnLoad(fontTexture);
        io.Fonts.SetTexID(FontId);

        // UI/Default: texture * vertex colour, alpha blended, no culling or depth. Shipped with every uGUI game.
        var shader = Shader.Find("UI/Default");
        if (shader == null) throw new Exception("shader UI/Default not found");
        material = new Material(shader) { mainTexture = fontTexture, hideFlags = HideFlags.HideAndDontSave };
        commands = new CommandBuffer { name = "EmbervilleGhost ImGui" };

        if (Keyboard.current != null) Keyboard.current.onTextInput += c => typed.Add(c);
        ready = true;
    }

    /// <summary>Build this frame's UI (call from Update).</summary>
    public static void Update()
    {
        if (!ready) return;
        var kb = Keyboard.current;
        if (kb != null && kb[MenuKey.Value].wasPressedThisFrame && Application.isFocused) SetOpen(!IsOpen);

        var io = ImGui.GetIO();
        io.DisplaySize = new Vec2(Screen.width, Screen.height);
        io.DeltaTime = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
        FeedInput(io, kb);

        ImGui.NewFrame();
        if (OnRender != null)
            foreach (Action handler in OnRender.GetInvocationList())
            {
                try { handler(); }
                catch (Exception e) { Plugin.Log.LogError($"imgui: {e}"); }
            }
        ImGui.Render();
    }

    static void SetOpen(bool open)
    {
        if (open == IsOpen) return;
        IsOpen = open;
        // The game hides/locks the cursor while playing; give it back while the menu is up.
        if (open) { savedLock = Cursor.lockState; savedVisible = Cursor.visible; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        else { Cursor.lockState = savedLock; Cursor.visible = savedVisible; }
    }

    static void FeedInput(ImGuiIOPtr io, Keyboard kb)
    {
        var mouse = Mouse.current;
        if (!(IsOpen || JoinWindow.Visible || HostWindow.Visible || OnlinePlayers.Visible || Trade.Visible) || mouse == null)   // those windows are clickable without the menu
        {
            // HUD only: no hover, no clicks.
            io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
            typed.Clear();
            return;
        }

        var p = mouse.position.ReadValue();
        io.AddMousePosEvent(p.x, Screen.height - p.y);   // Unity is bottom-left, ImGui top-left
        io.AddMouseButtonEvent(0, mouse.leftButton.isPressed);
        io.AddMouseButtonEvent(1, mouse.rightButton.isPressed);
        io.AddMouseButtonEvent(2, mouse.middleButton.isPressed);
        float wheel = mouse.scroll.ReadValue().y;
        if (wheel != 0) io.AddMouseWheelEvent(0, wheel / 120f);

        if (kb != null)
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, kb.ctrlKey.isPressed);
            io.AddKeyEvent(ImGuiKey.ModShift, kb.shiftKey.isPressed);
            io.AddKeyEvent(ImGuiKey.ModAlt, kb.altKey.isPressed);
            foreach (var (unity, imgui) in keyMap) io.AddKeyEvent(imgui, kb[unity].isPressed);
        }
        foreach (char c in typed) if (c >= 32) io.AddInputCharacter(c);
        typed.Clear();
    }

    static readonly (Key, ImGuiKey)[] keyMap =
    {
        (Key.Tab, ImGuiKey.Tab), (Key.LeftArrow, ImGuiKey.LeftArrow), (Key.RightArrow, ImGuiKey.RightArrow),
        (Key.UpArrow, ImGuiKey.UpArrow), (Key.DownArrow, ImGuiKey.DownArrow), (Key.Home, ImGuiKey.Home),
        (Key.End, ImGuiKey.End), (Key.Delete, ImGuiKey.Delete), (Key.Backspace, ImGuiKey.Backspace),
        (Key.Enter, ImGuiKey.Enter), (Key.NumpadEnter, ImGuiKey.KeypadEnter), (Key.Escape, ImGuiKey.Escape),
        (Key.Space, ImGuiKey.Space), (Key.A, ImGuiKey.A), (Key.C, ImGuiKey.C), (Key.V, ImGuiKey.V),
        (Key.X, ImGuiKey.X), (Key.Z, ImGuiKey.Z),
    };

    // ---- other textures in ImGui (ImGui.Image / AddImage): id 1 is the font, ours start at 2 ----

    static readonly IntPtr FontId = (IntPtr)1;
    static readonly Dictionary<IntPtr, Texture> textures = new Dictionary<IntPtr, Texture>();
    static readonly Dictionary<Texture, IntPtr> textureIds = new Dictionary<Texture, IntPtr>();
    static MaterialPropertyBlock textureProps;

    /// <summary>The id to draw this texture with in ImGui (a RenderTexture is upside down: flip the V of its UVs).</summary>
    public static IntPtr TextureId(Texture texture)
    {
        if (textureIds.TryGetValue(texture, out var id)) return id;
        id = (IntPtr)(textures.Count + 2);
        textures[id] = texture;
        textureIds[texture] = id;
        return id;
    }

    public static void ForgetTexture(Texture texture)
    {
        if (!textureIds.TryGetValue(texture, out var id)) return;
        textureIds.Remove(texture);
        textures[id] = null;   // ids aren't reused
    }

    /// <summary>Draw the last built frame (call from OnGUI; only acts on Repaint).</summary>
    public static unsafe void Render()
    {
        if (!ready || Event.current.type != EventType.Repaint) return;
        var data = ImGui.GetDrawData();
        if (data.NativePtr == null || data.CmdListsCount == 0) return;

        float sw = Screen.width, sh = Screen.height;
        commands.Clear();
        commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, sw, sh, 0, -1, 1));

        const MeshUpdateFlags fast = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            if (meshes.Count <= n) meshes.Add(new Mesh { hideFlags = HideFlags.HideAndDontSave });
            var mesh = meshes[n];
            mesh.MarkDynamic();

            int vtxCount = list.VtxBuffer.Size, idxCount = list.IdxBuffer.Size;
            mesh.SetVertexBufferParams(vtxCount, layout);
            mesh.SetVertexBufferData(CopyVerts((ImVert*)list.VtxBuffer.Data, vtxCount), 0, 0, vtxCount, 0, fast);
            mesh.SetIndexBufferParams(idxCount, IndexFormat.UInt16);
            mesh.SetIndexBufferData(CopyIdx((ushort*)list.IdxBuffer.Data, idxCount), 0, 0, idxCount, fast);

            int cmdCount = list.CmdBuffer.Size;
            mesh.subMeshCount = cmdCount;
            for (int i = 0; i < cmdCount; i++)
            {
                var cmd = list.CmdBuffer[i];
                mesh.SetSubMesh(i, new SubMeshDescriptor((int)cmd.IdxOffset, (int)cmd.ElemCount)
                    { baseVertex = (int)cmd.VtxOffset, bounds = new Bounds(UnityEngine.Vector3.zero, UnityEngine.Vector3.one * 1e6f) }, fast);
            }

            for (int i = 0; i < cmdCount; i++)
            {
                var cmd = list.CmdBuffer[i];
                if (cmd.ElemCount == 0) continue;
                var c = cmd.ClipRect;   // x1, y1, x2, y2, top-left origin
                // Scissor rect is bottom-left origin.
                commands.EnableScissorRect(new Rect(c.x, sh - c.w, c.z - c.x, c.w - c.y));
                if (cmd.TextureId != FontId && textures.TryGetValue(cmd.TextureId, out var tex))
                {
                    if (!tex) continue;
                    if (textureProps == null) textureProps = new MaterialPropertyBlock();
                    textureProps.SetTexture("_MainTex", tex);
                    commands.DrawMesh(mesh, Matrix4x4.identity, material, i, 0, textureProps);
                }
                else commands.DrawMesh(mesh, Matrix4x4.identity, material, i);
            }
        }
        commands.DisableScissorRect();
        Graphics.ExecuteCommandBuffer(commands);
    }

    static Vert[] vertScratch = new Vert[0];
    static ushort[] idxScratch = new ushort[0];

    static unsafe Vert[] CopyVerts(ImVert* src, int count)
    {
        if (vertScratch.Length < count) vertScratch = new Vert[count * 2];
        for (int i = 0; i < count; i++)
        {
            var v = src[i];
            vertScratch[i] = new Vert { X = v.X, Y = v.Y, Col = v.Col, U = v.U, V = v.V };
        }
        return vertScratch;
    }

    static unsafe ushort[] CopyIdx(ushort* src, int count)
    {
        if (idxScratch.Length < count) idxScratch = new ushort[count * 2];
        for (int i = 0; i < count; i++) idxScratch[i] = src[i];
        return idxScratch;
    }
}

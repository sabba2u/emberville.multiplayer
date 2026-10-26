using System.Collections.Generic;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine.InputSystem;
using Camera = UnityEngine.Camera;
using Mathf = UnityEngine.Mathf;
using Screen = UnityEngine.Screen;
using Time = UnityEngine.Time;
using V3 = UnityEngine.Vector3;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;

namespace EmbervilleGhost;

/// <summary>
/// Pings and drawing on the ground, from our Duckov mod (RTS-style, Age of Empires 4): middle-click the ground and
/// everyone in the area sees an animated marker there (rings + a bouncing arrow with your name, in your colour,
/// fading over 3 s; off screen it shows as an edge arrow). Hold middle mouse and drag to draw instead: the line shows
/// while you draw, goes to everyone when you let go, stays 10 s with an arrowhead at the end.
/// Emberville is a flat 2D world (X/Y), so the cursor's world point is just the camera's ScreenToWorldPoint; rings
/// are squashed a little so they read as lying on the ground. Drawn with ImGui (like the name tags), sent to the area
/// (Ping / PathDraw).
/// </summary>
internal static class Pings
{
    public static Plugin Plugin;

    const float Lifetime = 3f;
    const float OrderLifetime = 1f, OrderFade = 0.4f;   // an order to the risen / the flock: a quick mark
    const float PathLifetime = 4f, PathFade = 1f;
    const float WalkedLifetime = 30f, BehindFade = 0.5f, Reached = 0.6f;   // a path your companion walks: kept while it does, fading behind it
    const float Stuck = 6f;   // ... unless it gets no further along it for this long (a fight, a wall): then the rest fades
    const float PointSpacing = 0.3f;   // world units between recorded points
    const float DragStart = 0.5f;      // move this far while holding = drawing, not a ping
    const int MaxPoints = 256;
    const float Squash = 0.55f;        // rings: flatter than tall, lying on the ground

    struct Ping { public V3 Position; public float Born; public int PlayerId; public string Name; public bool Order; }

    class DrawnPath
    {
        public readonly List<V3> Points = new List<V3>();
        public float Born;
        public int PlayerId;
        /// <summary>Your companion is walking it: how far it's got (points before this are behind it), and when it passed each.</summary>
        public bool Walked;
        public int Next;
        public float[] PassedAt;
        public float LastStep;
    }

    static readonly List<Ping> active = new List<Ping>();
    static readonly List<DrawnPath> paths = new List<DrawnPath>();
    static DrawnPath drawing;
    static V3 pressPoint;
    static bool pressed;

    static readonly Vector4[] colours =
    {
        new Vector4(1f, 0.8f, 0.3f, 1f),    // host: gold (like its [host] tag)
        new Vector4(0.45f, 0.8f, 1f, 1f),   // P2: blue
        new Vector4(0.4f, 1f, 0.45f, 1f),   // P3: green
        new Vector4(1f, 0.45f, 0.85f, 1f),  // P4: pink
    };

    static Vector4 ColourOf(int playerId) => colours[Mathf.Abs(playerId) % colours.Length];
    static int MyId => Plugin != null && Plugin.MyId >= 0 ? Plugin.MyId : 0;

    public static void Clear()
    {
        active.Clear();
        paths.Clear();
        drawing = null;
        pressed = false;
    }

    /// <summary>The world point under the mouse (z = 0).</summary>
    static bool CursorWorld(Mouse mouse, out V3 point)
    {
        point = default;
        var cam = Camera.main;
        if (!cam || !LocalPlayer.Get()) return false;
        var screen = mouse.position.ReadValue();
        point = cam.ScreenToWorldPoint(new V3(screen.x, screen.y, -cam.transform.position.z));
        point.z = 0f;
        return true;
    }

    static bool Shift => Keyboard.current != null && Keyboard.current.shiftKey.isPressed;

    /// <summary>Middle mouse: click = ping, hold + drag = draw (every frame).</summary>
    public static void Tick()
    {
        var mouse = Mouse.current;
        if (mouse == null || !UnityEngine.Application.isFocused) return;
        var button = mouse.middleButton;

        if (button.wasPressedThisFrame && !ImGuiUnity.IsOpen && !Chat.IsOpen && !PlayerStatusUtil.LocalBusy)
        {
            pressed = CursorWorld(mouse, out pressPoint);
            drawing = null;
        }
        if (!pressed) return;

        if (button.isPressed)
        {
            if (!CursorWorld(mouse, out var point)) return;
            if (drawing == null)
            {
                if ((point - pressPoint).magnitude < DragStart) return;   // still might be a click
                foreach (var old in paths) if (old.PlayerId == MyId) End(old, Time.unscaledTime);   // (a new line: your old ones fade away)
                drawing = new DrawnPath { PlayerId = MyId, Born = float.MaxValue };   // doesn't age while drawing
                drawing.Points.Add(pressPoint);
                paths.Add(drawing);
            }
            var last = drawing.Points[drawing.Points.Count - 1];
            if ((point - last).magnitude >= PointSpacing && drawing.Points.Count < MaxPoints) drawing.Points.Add(point);
            return;
        }

        pressed = false;   // released
        if (drawing != null)
        {
            drawing.Born = Time.unscaledTime;
            SendPath(drawing);
            if (Integrations.CompanionOut)   // (your companion walks it: the line stays while it does, and goes behind it)
            {
                Integrations.RallyPath(drawing.Points, Shift);   // (Shift: it holds the end of the line)
                Add(drawing.Points[drawing.Points.Count - 1], MyId, "", order: true);
                drawing.Walked = true;
                drawing.PassedAt = new float[drawing.Points.Count];
                drawing.LastStep = Time.unscaledTime;
            }
            drawing = null;
        }
        else if (Integrations.HoldingStaff) { Add(pressPoint, MyId, "", order: true); Integrations.Rally(pressPoint); }   // an order to the risen / the flock: a red mark, just for us
        else if (Integrations.CompanionOut && Integrations.CompanionAt(out var mate) && new V3(mate.x - pressPoint.x, mate.y - pressPoint.y, 0f).magnitude <= 0.8f)
            Integrations.CompanionFollow();   // a click on your companion: follow me (when it's holding a spot)
        else if (Integrations.CompanionOut)   // an order to your companion: the red mark for you; your teammates still see your ping
        {
            Add(pressPoint, MyId, "", order: true);
            SendPing(pressPoint, showOurs: false);
            if (Integrations.HasPets) Integrations.Rally(pressPoint, companion: false);
            Integrations.CompanionTo(pressPoint, Shift);
        }
        else { SendPing(pressPoint); Integrations.Rally(pressPoint); }   // (and the Staff of Clucking's flock runs there)
    }

    /// <summary>A ping where we stand (arriving in a generated place: where you are), the same as a middle click there.</summary>
    public static void PingHere()
    {
        var me = LocalPlayer.Get();
        if (me) SendPing(me.transform.position);
    }

    static void SendPing(V3 point, bool showOurs = true)
    {
        if (showOurs) Add(point, MyId, "You");
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Ping);
        w.Put(point.x); w.Put(point.y);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableUnordered);
    }

    static void SendPath(DrawnPath path)
    {
        if (Plugin == null || !Plugin.Connected || Plugin.MyId < 0 || path.Points.Count < 2) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.PathDraw);
        w.Put((ushort)path.Points.Count);
        foreach (var p in path.Points) { w.Put(p.x); w.Put(p.y); }
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static void OnPing(NetDataReader r, int from)
    {
        var pos = new V3(r.GetFloat(), r.GetFloat(), 0f);
        Add(pos, from, Plugin.NameOf(from));
    }

    public static void OnPath(NetDataReader r, int from)
    {
        int count = r.GetUShort();
        var path = new DrawnPath { PlayerId = from, Born = Time.unscaledTime };
        for (int i = 0; i < count; i++) path.Points.Add(new V3(r.GetFloat(), r.GetFloat(), 0f));
        if (count < 2) return;
        paths.Add(path);
        if (paths.Count > 20) paths.RemoveAt(0);
    }

    static void Add(V3 pos, int playerId, string name, bool order = false)
    {
        active.Add(new Ping { Position = pos, Born = Time.unscaledTime, PlayerId = playerId, Name = name, Order = order });
        if (active.Count > 20) active.RemoveAt(0);
    }

    /// <summary>Every live ping and path (from Hud.Draw).</summary>
    public static void Draw()
    {
        if (active.Count == 0 && paths.Count == 0) return;
        float now = Time.unscaledTime;
        active.RemoveAll(p => now - p.Born > (p.Order ? OrderLifetime : Lifetime));
        foreach (var p in paths) if (p.Walked && p != drawing) Follow(p, now);
        paths.RemoveAll(p => p != drawing && now - p.Born > (p.Walked ? WalkedLifetime : PathLifetime));
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGui.GetBackgroundDrawList();

        foreach (var path in paths) DrawPath(dl, cam, path, now);

        foreach (var p in active)
        {
            float age = now - p.Born;
            float fade = p.Order
                ? 1f - Mathf.Clamp01((age - (OrderLifetime - OrderFade)) / OrderFade)   // an order: 1 s in all
                : 1f - Mathf.Clamp01((age - (Lifetime - 1f)) / 1f);   // full for 2 s, then fades over 1 s
            var colour = p.Order ? new Vector4(1f, 0.25f, 0.2f, 1f) : ColourOf(p.PlayerId);   // an order to the risen: red

            for (int ring = 0; ring < 3; ring++)   // three waves rippling out of the spot
            {
                float t = (age * 1.2f + ring / 3f) % 1f;
                var c = colour; c.w = (1f - t) * fade;
                DrawGroundCircle(dl, cam, p.Position, Mathf.Lerp(0.15f, 1.8f, t), ImGui.ColorConvertFloat4ToU32(c), 3f);
            }
            var solid = colour; solid.w = fade;
            uint solidU = ImGui.ColorConvertFloat4ToU32(solid);
            DrawGroundCircle(dl, cam, p.Position, 0.3f, solidU, 2.5f);

            if (p.Order) continue;   // just the rings: no arrow, no name
            var top = Project(cam, p.Position + V3.up * 1.8f);
            float bounce = Mathf.Abs(Mathf.Sin(age * 6f)) * 10f;
            var tip = new Vector2(top.x, top.y + 26f - bounce);
            dl.AddTriangleFilled(new Vector2(tip.x - 11f, tip.y - 18f), new Vector2(tip.x + 11f, tip.y - 18f), tip, solidU);
            dl.AddRectFilled(new Vector2(tip.x - 4f, tip.y - 34f), new Vector2(tip.x + 4f, tip.y - 17f), solidU);
            var size = ImGui.CalcTextSize(p.Name);
            dl.AddText(new Vector2(tip.x - size.x / 2 + 1, tip.y - 52f + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.7f * fade)), p.Name);
            dl.AddText(new Vector2(tip.x - size.x / 2, tip.y - 52f), solidU, p.Name);

            if (p.PlayerId != MyId && !OnScreen(top)) Hud.EdgeArrowTo(p.Position, solid, "ping");
        }
    }

    /// <summary>A path your companion walks: the points it has reached are marked passed; at the end (or with no companion) the rest fades.</summary>
    static void Follow(DrawnPath path, float now)
    {
        if (!Integrations.CompanionAt(out var at)) { End(path, now); return; }
        // the furthest point within reach a little way ahead (it cuts corners, so not only the next one)
        for (int i = path.Next; i < path.Points.Count && i < path.Next + 8; i++)
            if (new V3(at.x - path.Points[i].x, at.y - path.Points[i].y, 0f).magnitude <= Reached)
                for (; path.Next <= i; path.Next++) { path.PassedAt[path.Next] = now; path.LastStep = now; }
        if (path.Next >= path.Points.Count || now - path.LastStep > Stuck) End(path, now);
    }

    static void End(DrawnPath path, float now)
    {
        path.Walked = false;
        path.Born = Mathf.Min(path.Born, now - (PathLifetime - PathFade));   // (straight into its fade)
    }

    static void DrawPath(ImDrawListPtr dl, Camera cam, DrawnPath path, float now)
    {
        float age = path == drawing ? 0f : now - path.Born;
        float fade = path.Walked ? 1f : 1f - Mathf.Clamp01((age - (PathLifetime - PathFade)) / PathFade);
        var colour = ColourOf(path.PlayerId); colour.w = fade;
        uint line = ImGui.ColorConvertFloat4ToU32(colour);
        uint outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.5f * fade));

        Vector2 prev = default, beforeLast = default;
        for (int i = 0; i < path.Points.Count; i++)
        {
            var pt = Project(cam, path.Points[i]);
            if (i > 0)
            {
                uint edge = outline, middle = line;
                if (path.PassedAt != null && path.PassedAt[i] > 0f)   // behind your companion: fading out
                {
                    float left = fade * (1f - Mathf.Clamp01((now - path.PassedAt[i]) / BehindFade));
                    edge = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.5f * left));
                    middle = ImGui.ColorConvertFloat4ToU32(new Vector4(colour.x, colour.y, colour.z, left));
                }
                dl.AddLine(prev, pt, edge, 7f);   // dark edge so it reads on any ground
                dl.AddLine(prev, pt, middle, 4f);
                beforeLast = prev;
            }
            prev = pt;
        }

        var dir = new Vector2(prev.x - beforeLast.x, prev.y - beforeLast.y);   // arrowhead the way it was drawn
        float len = Mathf.Sqrt(dir.x * dir.x + dir.y * dir.y);
        if (path.Points.Count < 2 || len < 0.01f) return;
        dir = new Vector2(dir.x / len, dir.y / len);
        var side = new Vector2(-dir.y, dir.x);
        var a = new Vector2(prev.x - dir.x * 16f + side.x * 9f, prev.y - dir.y * 16f + side.y * 9f);
        var b = new Vector2(prev.x - dir.x * 16f - side.x * 9f, prev.y - dir.y * 16f - side.y * 9f);
        dl.AddTriangleFilled(new Vector2(prev.x + dir.x * 4f, prev.y + dir.y * 4f), a, b, line);
    }

    static void DrawGroundCircle(ImDrawListPtr dl, Camera cam, V3 centre, float radius, uint colour, float thickness)
    {
        const int segments = 24;
        Vector2 prev = default;
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            var pt = Project(cam, centre + new V3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius * Squash, 0f));
            if (i > 0) dl.AddLine(prev, pt, colour, thickness);
            prev = pt;
        }
    }

    /// <summary>World -> ImGui screen coordinates (top-left origin).</summary>
    static Vector2 Project(Camera cam, V3 world)
    {
        var s = cam.WorldToScreenPoint(world);
        return new Vector2(s.x, Screen.height - s.y);
    }

    static bool OnScreen(Vector2 p) => p.x >= 0 && p.y >= 0 && p.x <= Screen.width && p.y <= Screen.height;
}

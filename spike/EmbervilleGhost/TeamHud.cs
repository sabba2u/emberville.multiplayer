using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The party strip along the top of the screen while connected: one card per player, in join order (host P1 first,
/// the same order on every screen), four to a row, a second row from the fifth player. Each card: a live full-body
/// portrait of their character, their name (◆ if they run the area we're in), health on top and Kymia (only if they have
/// any) underneath, and a small line: P-number, host, what they're doing, status effects, dead.
///
/// Portraits: a tiny camera per player looks at their whole character (framed like the inventory screen's preview) (ours: our player; theirs: their stand-in)
/// and renders only the character's layer into a 64x64 RenderTexture, which ImGui draws (ImGuiUnity.TextureId).
/// Someone not in our area has no stand-in: their last picture stays, dimmed. Hidden with the other HUD bits when the
/// game covers the screen (menus, dialogue, cutscenes).
/// </summary>
internal static class TeamHud
{
    public static Plugin Plugin;
    static ConfigEntry<bool> show, portraitsOn;
    static ConfigEntry<float> headHeight, headSize;

    const float CardW = 210f, CardH = 58f, Gap = 8f, Top = 34f;
    const int PerRow = 4;

    class Portrait
    {
        public Camera Cam;
        public RenderTexture Tex;
        public IntPtr Id;
        public bool Seen;   // has a picture (else draw a plain box)
        public Transform Target;   // whose character it films this frame
    }

    static readonly Dictionary<int, Portrait> portraits = new Dictionary<int, Portrait>();

    // ---- right-click a card: teleport / trade / mute ----
    // The cards drawn this frame (screen rects, ImGui's top-left coordinates), which one the mouse is over, the menu.
    static readonly List<(int id, float x0, float y0, float x1, float y1)> cards = new List<(int, float, float, float, float)>();
    static int hovered = -1, menuFor = -1;
    static Vector2 menuAt;

    /// <summary>ImGui needs the mouse: over the strip, or our menu is up.</summary>
    public static bool WantsMouse => hovered >= 0 || menuFor >= 0;

    /// <summary>
    /// Every frame (Update): is the mouse over a card? While it is, the game's controls are paused (right-click is the
    /// game's secondary attack: no swing when you right-click a teammate). A right-click on a teammate opens the menu.
    /// </summary>
    public static void Tick()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        hovered = -1;
        if (mouse != null && Application.isFocused && Plugin != null && Plugin.Connected)
        {
            var p = mouse.position.ReadValue();
            float mx = p.x, my = Screen.height - p.y;
            foreach (var c in cards)
                if (mx >= c.x0 && mx <= c.x1 && my >= c.y0 && my <= c.y1) { hovered = c.id; break; }
            bool rightDown = mouse.rightButton.wasPressedThisFrame;
            MenuClick(new Vector2(mx, my), mouse.leftButton.wasReleasedThisFrame, rightDown);
            if (hovered >= 0 && hovered != Plugin.MyId && rightDown)
            {
                menuFor = hovered;
                menuAt = new Vector2(mx, my);
                NetLog.Info($"Party menu opened for P{hovered}");
            }
            var kbd = UnityEngine.InputSystem.Keyboard.current;
            if (menuFor >= 0 && kbd != null && kbd.escapeKey.wasPressedThisFrame) menuFor = -1;
        }
        if (menuFor >= 0 && (Plugin == null || !Plugin.Connected)) menuFor = -1;
        if (hovered >= 0 || menuFor >= 0) GameInput.Block("strip"); else GameInput.Unblock("strip");
    }

    static void Menu()
    {
        if (menuFor < 0) return;
        RemotePlayer p = null;
        foreach (var rp in Plugin.Players) if (rp.Id == menuFor) { p = rp; break; }
        if (p == null) { menuFor = -1; return; }

        ImGui.SetNextWindowPos(menuAt, ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                       ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("##stripmenu", flags)) { ImGui.End(); return; }
        string name = string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName;
        ImGui.TextDisabled(p.Name);
        ImGui.Separator();
        bool here = p.HaveState && p.Scene == LocalPlayer.Scene;
        // The options are picked in Tick from the mouse itself (ImGui's own clicks didn't reach this window):
        // here they're drawn and their boxes remembered.
        bool muted = OnlinePlayers.IsMuted(p.Name);
        items.Clear();
        Item(here ? $"Teleport to {name}" : $"Teleport to {name}  (another scene)", here ? 1 : 0);
        Item($"Trade with {name}", 2);
        Item(muted ? $"Unmute {name}" : $"Mute {name}", 3);
        var wp = ImGui.GetWindowPos(); var ws = ImGui.GetWindowSize();
        menuBox = new Rect(wp.x, wp.y, ws.x, ws.y);
        ImGui.End();
    }

    struct MenuItem { public Rect Box; public int Action; }
    static readonly List<MenuItem> items = new List<MenuItem>();
    static Rect menuBox;

    static void Item(string label, int action)
    {
        if (action == 0) ImGui.BeginDisabled();
        ImGui.Selectable(label);
        if (action == 0) ImGui.EndDisabled();
        var a = ImGui.GetItemRectMin(); var b = ImGui.GetItemRectMax();
        items.Add(new MenuItem { Box = Rect.MinMaxRect(a.x, a.y, b.x, b.y), Action = action });
    }

    /// <summary>A left click (on release) in the open menu: do that option; anywhere else: close it.</summary>
    static void MenuClick(Vector2 at, bool left, bool right)
    {
        if (menuFor < 0 || (!left && !right)) return;
        if (!menuBox.Contains(new UnityEngine.Vector2(at.x, at.y))) { if (hovered != menuFor) menuFor = -1; return; }
        if (!left) return;
        RemotePlayer p = null;
        foreach (var rp in Plugin.Players) if (rp.Id == menuFor) { p = rp; break; }
        if (p == null) { menuFor = -1; return; }
        foreach (var it in items)
        {
            if (!it.Box.Contains(new UnityEngine.Vector2(at.x, at.y)) || it.Action == 0) continue;
            NetLog.Info($"Party menu: {(it.Action == 1 ? "teleport to" : it.Action == 2 ? "trade with" : "mute / unmute")} {p.Name}");
            if (it.Action == 1) DebugPanel.TeleportTo(p);
            else if (it.Action == 2) Trade.Invite(p.Id);
            else OnlinePlayers.SetMuted(p.Name, !OnlinePlayers.IsMuted(p.Name));
            menuFor = -1;
            return;
        }
    }

    public static void Bind(ConfigFile config)
    {
        show = config.Bind("PartyStrip", "Show", true, "The party strip at the top of the screen while connected");
        portraitsOn = config.Bind("PartyStrip", "Portraits", false, "Live character portraits instead of the weapon icons (a small camera per player)");
        // Full body, like the character in the inventory screen (the keys are new so old head-only values don't stick).
        headHeight = config.Bind("PartyStrip", "PortraitCenter", 0.95f, "Where the middle of the portrait is above a character's feet (world units)");
        headSize = config.Bind("PartyStrip", "PortraitSize", 1.1f, "How much of the character the portrait shows (half height, world units): ~1.1 = the whole body");
    }

    /// <summary>From Hud.Draw (only when the game isn't covering the screen).</summary>
    public static void Draw()
    {
        if (Plugin == null || !show.Value || !Plugin.Connected || Plugin.MyId < 0) { Sleep(); return; }
        var me = LocalPlayer.Get();
        if (!me) { Sleep(); return; }

        // Everyone, in join order (player id): the same on every screen.
        var ids = new List<int> { Plugin.MyId };
        foreach (var p in Plugin.Players) ids.Add(p.Id);
        ids.Sort();

        cards.Clear();
        float total = Mathf.Min(ids.Count, PerRow) * (CardW + Gap) - Gap;
        float left = (Screen.width - total) / 2f;
        float top = Mathf.Max(Top, BossBarBottom() + 6f);   // a boss fight: below the boss's health bar, not over it
        var dl = ImGui.GetBackgroundDrawList();
        var alive = new HashSet<int>();
        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            alive.Add(id);
            float x = left + (i % PerRow) * (CardW + Gap), y = top + (i / PerRow) * (CardH + Gap);
            cards.Add((id, x, y, x + CardW, y + CardH));
            if (id == Plugin.MyId) Card(dl, x, y, id, Me(me), me.transform);
            else
            {
                RemotePlayer rp = null;
                foreach (var p in Plugin.Players) if (p.Id == id) { rp = p; break; }
                if (rp != null) Card(dl, x, y, id, Them(rp), rp.Avatar != null && rp.Avatar.Visible ? rp.Avatar.Root.transform : null);
            }
        }
        // Players who left: their portrait camera goes.
        var gone = new List<int>();
        foreach (int id in portraits.Keys) if (!alive.Contains(id)) gone.Add(id);
        foreach (int id in gone) Drop(id);
        Menu();
    }

    static UIManager ui;
    static float uiLookedAt = -10f;
    static readonly UnityEngine.Vector3[] corners = new UnityEngine.Vector3[4];

    /// <summary>The bottom (screen pixels from the top) of the game's boss / mini-boss health bar when one shows; else 0.</summary>
    static float BossBarBottom()
    {
        if (!ui && Time.unscaledTime - uiLookedAt > 1f) { uiLookedAt = Time.unscaledTime; ui = UnityEngine.Object.FindAnyObjectByType<UIManager>(); }
        if (!ui) return 0f;
        return Mathf.Max(Bottom(ui.panelBossHud), Bottom(ui.panelMiniBossHud));
    }

    static float Bottom(GameObject panel)
    {
        if (!panel || !panel.activeInHierarchy || !(panel.transform is RectTransform rt)) return 0f;
        var canvas = panel.GetComponentInParent<Canvas>();
        var cam = canvas && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
        rt.GetWorldCorners(corners);
        float lowest = float.MaxValue;
        foreach (var c in corners) lowest = Mathf.Min(lowest, RectTransformUtility.WorldToScreenPoint(cam, c).y);
        float bottom = Screen.height - lowest;   // ImGui counts from the top
        return bottom > 0f && bottom < Screen.height * 0.33f ? bottom : 0f;   // a full-screen panel: not a bar, ignore
    }

    struct Info
    {
        public string Name, Line;
        public float Health, MaxHealth, Kymia, MaxKymia;
        public bool Dead, Runs;
        public bool Loading;   // connected, but not in the game yet (title screen, character pick, loading the world)
        public string Weapon;  // their weapon's item key (its icon on the card)
    }

    static Info Me(PlayerMaster me)
    {
        string name = GameData.playerData != null && !string.IsNullOrEmpty(GameData.playerData.playerName) ? GameData.playerData.playerName : "You";
        bool stats = me.characterStats;
        var status = PlayerStatusUtil.Local();
        return new Info
        {
            Name = name,
            Line = Tag(Plugin.MyId) + Extra(PlayerStatusUtil.Label(status), null),
            Health = me.CurrentHealthPoints, MaxHealth = stats ? me.MaxHealthPoints : 0,
            Kymia = Mathf.Max(me.CurrentKymiaPoints, 0f), MaxKymia = stats ? me.MaxKymiaPoints : 0,
            Weapon = me.currentWeaponItem?.data != null ? me.currentWeaponItem.data.addressKey : "",
            Dead = me.IsDead, Runs = Plugin.IOwnHere,
        };
    }

    static Info Them(RemotePlayer p) => !p.HaveState || p.MaxHealth <= 0 || p.Status == PlayerStatus.Loading
        ? new Info
        {
            Name = string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName,
            Line = Tag(p.Id) + " - loading into the game...",
            Loading = true,
        }
        : new Info
    {
        Name = string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName,
        Line = Tag(p.Id) + Extra(PlayerStatusUtil.Label(p.Status), p.Effects),
        Health = p.Health, MaxHealth = p.MaxHealth, Kymia = p.Kymia, MaxKymia = p.MaxKymia, Weapon = p.Weapon,
        Dead = p.Status == PlayerStatus.Dead || (p.MaxHealth > 0 && p.Health <= 0),
        Runs = Plugin.OwnerHere == p.Id && p.SamePlace(LocalPlayer.Scene, LocalPlayer.Area),
    };

    static string Tag(int id) => $"P{id + 1}" + (id == 0 ? " - host" : "");

    static string Extra(string status, List<string> effects)
    {
        string s = status.Length > 0 ? " - " + status : "";
        if (effects != null && effects.Count > 0) s += " - " + string.Join(", ", effects);
        return s;
    }

    static void Card(ImDrawListPtr dl, float x, float y, int id, Info info, Transform target)
    {
        bool lit = id == hovered && id != Plugin.MyId;   // hover: a lighter card says "right-click me"
        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(lit ? 0.15f : 0f, lit ? 0.15f : 0f, lit ? 0.2f : 0f, lit ? 0.75f : 0.55f));
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + CardW, y + CardH), bg, 6f);

        // Portrait
        const float ps = 48f;
        var p0 = new Vector2(x + 5, y + 5);
        var p1 = new Vector2(p0.x + ps, p0.y + ps);
        dl.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(new Vector4(0.17f, 0.13f, 0.2f, 1f)), 4f);
        if (portraitsOn.Value)
        {
            var portrait = Picture(id, target);
            if (portrait != null && portrait.Seen)
            {
                float dim = target ? 1f : 0.45f;   // not in our area: their last picture, dimmed
                if (info.Dead) dim *= 0.5f;
                dl.AddImage(portrait.Id, p0, p1, new Vector2(0, 1), new Vector2(1, 0), ImGui.ColorConvertFloat4ToU32(new Vector4(dim, dim, dim, 1f)));
            }
        }
        else
        {
            // The weapon they're holding (sword, staff, bow, knuckles...); nothing in hand: empty.
            var icon = ItemIcons.Get(info.Weapon);
            float dim = info.Dead ? 0.45f : 1f;
            if (icon) ItemIcons.Draw(dl, icon, new Vector2(p0.x + 5, p0.y + 5), new Vector2(p1.x - 5, p1.y - 5), ImGui.ColorConvertFloat4ToU32(new Vector4(dim, dim, dim, 1f)));
        }
        uint role = ImGui.ColorConvertFloat4ToU32(id == 0 ? new Vector4(1f, 0.8f, 0.3f, 1f) : new Vector4(0.45f, 0.8f, 1f, 1f));
        dl.AddRect(p0, p1, role, 4f, ImDrawFlags.None, 2f);

        // Name (+ diamond when they run our area)
        float tx = p1.x + 7, right = x + CardW - 6;
        string name = info.Name + (info.Dead ? "  (dead)" : "");
        dl.AddText(new Vector2(tx, y + 3), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), name);
        if (info.Runs)   // the diamond, drawn (the font may not have the character)
        {
            var size = ImGui.CalcTextSize(name);
            float d = size.y * 0.35f;
            var m = new Vector2(tx + size.x + 6 + d, y + 3 + size.y / 2);
            dl.AddQuadFilled(new Vector2(m.x, m.y - d), new Vector2(m.x + d, m.y), new Vector2(m.x, m.y + d), new Vector2(m.x - d, m.y), role);
        }

        // Health on top, Kymia under it (still loading in: no bars yet, just that they're on their way)
        float barW = right - tx, hy = y + 22;
        if (info.Loading)
        {
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.5f));
            dl.AddText(new Vector2(tx, hy - 2), ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.85f, 1f, pulse)), "Connected");
        }
        else
        {
            Bar(dl, tx, hy, barW, 8f, info.MaxHealth > 0 ? info.Health / info.MaxHealth : 0f, true);
            if (info.MaxKymia > 0f) Bar(dl, tx, hy + 11, barW, 5f, info.Kymia / info.MaxKymia, false);
        }

        // P-number, host, what they're doing, effects
        string line = info.Line;
        if (ImGui.CalcTextSize(line).x > barW) line = line.Substring(0, Math.Max(0, Math.Min(line.Length, 32))) + "...";
        dl.AddText(new Vector2(tx, y + 38), ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.75f, 0.8f, 1f)), line);
    }

    static void Bar(ImDrawListPtr dl, float x, float y, float w, float h, float frac, bool health)
    {
        frac = Mathf.Clamp01(frac);
        dl.AddRectFilled(new Vector2(x - 1, y - 1), new Vector2(x + w + 1, y + h + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.7f)));
        var colour = health
            ? (frac < 0.3f ? new Vector4(0.9f, 0.25f, 0.2f, 1f) : new Vector4(0.25f, 0.85f, 0.35f, 1f))   // low health turns red
            : new Vector4(0.35f, 0.6f, 1f, 1f);
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w * frac, y + h), ImGui.ColorConvertFloat4ToU32(colour));
    }

    // ---- portraits ----

    /// <summary>This player's portrait; its camera follows their character while we can see it.</summary>
    static Portrait Picture(int id, Transform target)
    {
        if (!portraits.TryGetValue(id, out var p))
        {
            if (!target) return null;
            portraits[id] = p = Make(id);
        }
        if (!p.Cam) return p;
        p.Cam.enabled = false;   // never drawn by the pipeline on its own: RenderPortraits draws it, isolated
        if (!target) { p.Target = null; return p; }
        p.Seen = true;
        var head = target.position + Vector3.up * headHeight.Value;
        var main = Camera.main;
        p.Cam.transform.position = new Vector3(head.x, head.y, main ? main.transform.position.z : -10f);
        p.Cam.orthographicSize = headSize.Value;
        p.Target = target;
        p.Cam.cullingMask = 1 << PortraitLayer;   // only the character, moved onto that layer while we draw it
        return p;
    }

    static Portrait Make(int id)
    {
        var tex = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGB32) { name = $"EmbervilleGhost_Portrait_P{id + 1}" };
        tex.Create();
        var go = new GameObject($"EmbervilleGhost_PortraitCam_P{id + 1}") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(go);
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.17f, 0.13f, 0.2f, 1f);   // the inventory preview's dark purple
        cam.targetTexture = tex;
        cam.depth = -100;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;
        return new Portrait { Cam = cam, Tex = tex, Id = ImGuiUnity.TextureId(tex) };
    }

    /// <summary>The layers the character's sprites are on (not the minimap marker): the portrait sees only those.</summary>
    // ---- just the character in the picture ----
    //
    // Layers can't tell a character from the scenery (some of a character's parts share layers with the world, so a
    // camera filming "the character's layers" films the ground too). So the portrait cameras are never drawn by the
    // pipeline on its own (disabled); we draw each one ourselves: its character's renderers move onto a spare layer
    // nothing else uses, the camera (filming only that layer) renders into its texture right then
    // (RenderPipeline.SubmitRenderRequest), and they move straight back. The main view never sees the change.

    static int portraitLayer = -1;
    static readonly List<(GameObject go, int layer)> moved = new List<(GameObject, int)>();
    static int frame;

    /// <summary>The highest layer with no name (unused by the game).</summary>
    static int PortraitLayer
    {
        get
        {
            if (portraitLayer >= 0) return portraitLayer;
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { portraitLayer = i; break; }
            if (portraitLayer < 0) portraitLayer = 31;
            return portraitLayer;
        }
    }

    /// <summary>Every other frame (Update): draw each visible player's portrait, isolated on our layer.</summary>
    public static void RenderPortraits()
    {
        if (portraits.Count == 0 || (++frame & 1) != 0) return;
        foreach (var p in portraits.Values)
        {
            if (!p.Cam || !p.Tex || !p.Target) continue;
            Isolate(p.Target);
            try
            {
                var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = p.Tex };
                if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(p.Cam, request))
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(p.Cam, request);
                else { p.Cam.targetTexture = p.Tex; p.Cam.Render(); }
            }
            catch (Exception e) { NetLog.Warn($"Portrait: {e.Message}"); }
            finally { Restore(); }
        }
    }

    static void Isolate(Transform target)
    {
        int minimap = LayerMask.NameToLayer("Minimap");
        foreach (var r in target.GetComponentsInChildren<Renderer>())
        {
            var go = r.gameObject;
            if (go.layer == minimap || go.layer == PortraitLayer) continue;
            moved.Add((go, go.layer));
            go.layer = PortraitLayer;
        }
    }

    static void Restore()
    {
        foreach (var (go, layer) in moved) if (go) go.layer = layer;
        moved.Clear();
    }

    static void Drop(int id)
    {
        if (!portraits.TryGetValue(id, out var p)) return;
        portraits.Remove(id);
        if (p.Tex) { ImGuiUnity.ForgetTexture(p.Tex); p.Tex.Release(); UnityEngine.Object.Destroy(p.Tex); }
        if (p.Cam) UnityEngine.Object.Destroy(p.Cam.gameObject);
    }

    /// <summary>Not showing: cameras off (their pictures stay for later).</summary>
    static void Sleep()
    {
        cards.Clear();
        menuFor = -1;
        foreach (var p in portraits.Values) if (p.Cam) p.Cam.enabled = false;
        if (Plugin != null && !Plugin.Connected && portraits.Count > 0)
            foreach (int id in new List<int>(portraits.Keys)) Drop(id);
    }
}

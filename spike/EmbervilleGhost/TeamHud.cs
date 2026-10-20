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
/// the same order on every screen), four to a row, a second row from the fifth player. Each card: a live portrait of
/// their character's head, their name (◆ if they run the area we're in), health on top and Kymia (only if they have
/// any) underneath, and a small line: P-number, host, what they're doing, status effects, dead.
///
/// Portraits: a tiny camera per player looks at their character's head (ours: our player; theirs: their stand-in)
/// and renders only the character's layer into a 64x64 RenderTexture, which ImGui draws (ImGuiUnity.TextureId).
/// Someone not in our area has no stand-in: their last picture stays, dimmed. Hidden with the other HUD bits when the
/// game covers the screen (menus, dialogue, cutscenes).
/// </summary>
internal static class TeamHud
{
    public static Plugin Plugin;
    static ConfigEntry<bool> show;
    static ConfigEntry<float> headHeight, headSize;

    const float CardW = 210f, CardH = 58f, Gap = 8f, Top = 34f;
    const int PerRow = 4;

    class Portrait
    {
        public Camera Cam;
        public RenderTexture Tex;
        public IntPtr Id;
        public bool Seen;   // has a picture (else draw a plain box)
    }

    static readonly Dictionary<int, Portrait> portraits = new Dictionary<int, Portrait>();

    public static void Bind(ConfigFile config)
    {
        show = config.Bind("PartyStrip", "Show", true, "The party strip at the top of the screen while connected");
        headHeight = config.Bind("PartyStrip", "HeadHeight", 1.55f, "Where the head is above a character's feet (world units), for the portraits");
        headSize = config.Bind("PartyStrip", "HeadSize", 0.5f, "How much around the head the portrait shows (half height, world units)");
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

        float total = Mathf.Min(ids.Count, PerRow) * (CardW + Gap) - Gap;
        float left = (Screen.width - total) / 2f;
        var dl = ImGui.GetBackgroundDrawList();
        var alive = new HashSet<int>();
        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            alive.Add(id);
            float x = left + (i % PerRow) * (CardW + Gap), y = Top + (i / PerRow) * (CardH + Gap);
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
    }

    struct Info
    {
        public string Name, Line;
        public float Health, MaxHealth, Kymia, MaxKymia;
        public bool Dead, Runs;
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
            Dead = me.IsDead, Runs = Plugin.IOwnHere,
        };
    }

    static Info Them(RemotePlayer p) => new Info
    {
        Name = string.IsNullOrEmpty(p.CharacterName) ? p.Tag : p.CharacterName,
        Line = Tag(p.Id) + Extra(PlayerStatusUtil.Label(p.Status), p.Effects),
        Health = p.Health, MaxHealth = p.MaxHealth, Kymia = p.Kymia, MaxKymia = p.MaxKymia,
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
        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f));
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + CardW, y + CardH), bg, 6f);

        // Portrait
        const float ps = 48f;
        var p0 = new Vector2(x + 5, y + 5);
        var p1 = new Vector2(p0.x + ps, p0.y + ps);
        dl.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.18f, 1f)), 4f);
        var portrait = Picture(id, target);
        if (portrait != null && portrait.Seen)
        {
            float dim = target ? 1f : 0.45f;   // not in our area: their last picture, dimmed
            if (info.Dead) dim *= 0.5f;
            dl.AddImage(portrait.Id, p0, p1, new Vector2(0, 1), new Vector2(1, 0), ImGui.ColorConvertFloat4ToU32(new Vector4(dim, dim, dim, 1f)));
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

        // Health on top, Kymia under it
        float barW = right - tx, hy = y + 22;
        Bar(dl, tx, hy, barW, 8f, info.MaxHealth > 0 ? info.Health / info.MaxHealth : 0f, true);
        if (info.MaxKymia > 0f) Bar(dl, tx, hy + 11, barW, 5f, info.Kymia / info.MaxKymia, false);

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

    /// <summary>This player's portrait; its camera follows their head while we can see their character.</summary>
    static Portrait Picture(int id, Transform target)
    {
        if (!portraits.TryGetValue(id, out var p))
        {
            if (!target) return null;
            portraits[id] = p = Make(id);
        }
        if (!p.Cam) return p;
        if (!target) { p.Cam.enabled = false; return p; }
        p.Cam.enabled = true;
        p.Seen = true;
        var head = target.position + Vector3.up * headHeight.Value;
        var main = Camera.main;
        p.Cam.transform.position = new Vector3(head.x, head.y, main ? main.transform.position.z : -10f);
        p.Cam.orthographicSize = headSize.Value;
        p.Cam.cullingMask = LayersOf(target);
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
        cam.backgroundColor = new Color(0.15f, 0.15f, 0.18f, 1f);
        cam.targetTexture = tex;
        cam.depth = -100;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 100f;
        return new Portrait { Cam = cam, Tex = tex, Id = ImGuiUnity.TextureId(tex) };
    }

    /// <summary>The layers the character's sprites are on (not the minimap marker): the portrait sees only those.</summary>
    static int LayersOf(Transform target)
    {
        int minimap = LayerMask.NameToLayer("Minimap");
        int mask = 0;
        foreach (var r in target.GetComponentsInChildren<SpriteRenderer>())
            if (r.gameObject.layer != minimap) mask |= 1 << r.gameObject.layer;
        return mask == 0 ? ~0 : mask;
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
        foreach (var p in portraits.Values) if (p.Cam) p.Cam.enabled = false;
        if (Plugin != null && !Plugin.Connected && portraits.Count > 0)
            foreach (int id in new List<int>(portraits.Keys)) Drop(id);
    }
}

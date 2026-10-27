using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Chatters as enemies: the monsters wear the names of people chatting (anyone who's said something in the last few
/// minutes, never the streamer or a chat bot), floating over their heads, so you're fighting your chat. "RIP name"
/// when one falls; the one standing nearest when you go down gets the kill on the leaderboard. Every monster, or only
/// Twitch raids' ([Chatters] Which). A raid party's monsters wear their raiders' names.
/// </summary>
internal static class ChatterEnemies
{
    const int MaxTags = 40;

    static ConfigEntry<bool> on;
    static ConfigEntry<int> which;
    static ConfigEntry<float> keepMinutes;
    static ChatterPool pool;
    static readonly Queue<string> reserved = new();
    static float reservedAt;
    static readonly Dictionary<Enemy, (string name, TextMeshProUGUI text, float height)> tags = new();
    static Canvas canvas;
    static float nextScan;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Chatters", "Enabled", true, "Monsters wear the names of people chatting");
        which = config.Bind("Chatters", "Which", 0, "0: every monster, 1: only Twitch raids' monsters");
        keepMinutes = config.Bind("Chatters", "KeepMinutes", 10f, "A chatter's name is used for this long after they last said something");
        pool = new ChatterPool(keepMinutes.Value * 60.0, "");
    }

    public static bool On { get => on.Value; set { on.Value = value; if (!value) Clear(); } }
    public static int Which { get => which.Value; set { which.Value = Mathf.Clamp(value, 0, 1); Clear(); } }
    static double Now => Time.realtimeSinceStartupAsDouble;

    /// <summary>Someone said something in chat.</summary>
    public static void Seen(string user)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Equals(TwitchAuth.Login, System.StringComparison.OrdinalIgnoreCase)) return;
        pool.Seen(Streamer.Filter.Clean(user, 25), Now);
    }

    public static int Chatters => pool.Count(Now);

    /// <summary>The next Twitch creatures to appear wear these names (a raid party's raiders).</summary>
    public static void Reserve(IEnumerable<string> names)
    {
        foreach (var n in names) reserved.Enqueue(n);
        reservedAt = Time.unscaledTime;
    }

    /// <summary>The name on the monster nearest there (within reach), for the kill.</summary>
    public static string NearestName(Vector3 at, float reach)
    {
        string best = null;
        float bestD = reach * reach;
        foreach (var kv in tags)
        {
            if (!kv.Key) continue;
            float d = ((Vector2)(kv.Key.transform.position - at)).sqrMagnitude;
            if (d <= bestD) { bestD = d; best = kv.Value.name; }
        }
        return best;
    }

    static RectTransform Root
    {
        get
        {
            if (!canvas)
            {
                var go = new GameObject("BS_ChatterNames");
                Object.DontDestroyOnLoad(go);
                canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 1;   // (under the game's menus)
                var scaler = go.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
            }
            return (RectTransform)canvas.transform;
        }
    }

    static bool Counts(Enemy e)
    {
        if (!e || e.IsDead || !e.gameObject.activeInHierarchy || e.initHealth <= 0f || e.name.StartsWith("NPC_") || Hooks.IsFriendly(e)) return false;
        bool twitch = Hooks.IsTwitchCreature(e);
        if (which.Value == 1) return twitch;
        return twitch || e.enemyType != EnemyType.None || Hooks.IsHostileFauna(e);
    }

    public static void Tick()
    {
        if (!on.Value || !Singleton<GameManager>.HasMain) { if (tags.Count > 0) Clear(); return; }
        // the fallen: RIP, and their tags gone
        foreach (var e in tags.Keys.ToList())
        {
            if (e && !e.IsDead && e.gameObject.activeInHierarchy) continue;
            var (name, text, _) = tags[e];
            if (e && e.IsDead)
                try { Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, $"RIP {name}", e.transform.position, DamageResult.None)); } catch { }
            if (text) Object.Destroy(text.gameObject);
            tags.Remove(e);
        }
        if (reserved.Count > 0 && Time.unscaledTime - reservedAt > 5f) reserved.Clear();   // (names for monsters that never came)
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.5f;
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (tags.Count >= MaxTags) break;
                if (tags.ContainsKey(e) || !Counts(e)) continue;
                string name = reserved.Count > 0 && Hooks.IsTwitchCreature(e) ? reserved.Dequeue()
                            : pool.Pick(Now, tags.Values.Select(t => t.name).ToList(), k => Random.Range(0, k));
                if (name == null) continue;
                tags[e] = (name, MakeTag(name), Height(e));
            }
        }
        Place();
    }

    static TextMeshProUGUI MakeTag(string name)
    {
        var t = GambleUi.Text("Name", Root, name, 22f, new Color(0.85f, 0.7f, 1f), new Vector2(300f, 30f));
        t.outlineWidth = 0.3f;
        return t;
    }

    /// <summary>How far above its feet a monster's head is (its sprites' top), plus a little.</summary>
    static float Height(Enemy e)
    {
        float top = 0f;
        foreach (var r in e.GetComponentsInChildren<SpriteRenderer>())
            if (r.enabled && r.sprite) top = Mathf.Max(top, r.bounds.max.y - e.transform.position.y);
        return Mathf.Clamp(top > 0f ? top + 0.25f : 1.6f, 0.8f, 5f);
    }

    static void Place()
    {
        var cam = Camera.main;
        bool show = cam && !UIPanels.HasOpenedPanel;
        var root = Root;
        foreach (var kv in tags)
        {
            var text = kv.Value.text;
            if (!text) continue;
            Vector3 screen = show ? cam.WorldToScreenPoint(kv.Key.transform.position + Vector3.up * kv.Value.height) : Vector3.zero;
            bool visible = show && screen.z > 0f && screen.x > -50f && screen.x < Screen.width + 50f && screen.y > -50f && screen.y < Screen.height + 50f;
            if (text.gameObject.activeSelf != visible) text.gameObject.SetActive(visible);
            if (!visible) continue;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out var local);
            text.rectTransform.anchoredPosition = local;
        }
    }

    static void Clear()
    {
        foreach (var kv in tags) if (kv.Value.text) Object.Destroy(kv.Value.text.gameObject);
        tags.Clear();
    }
}

/// <summary>
/// A raid party: "!party" in chat (or the tab's button, or every few minutes) opens one; for a while chatters join
/// ("!join", or "!raid"), once each, and then a raid comes as big as the party, every monster wearing a raider's name
/// (Core.RaidParty: a monster a raider, at least 2, up to the cap; past it they get tougher). Chat can start one again
/// after the cooldown.
/// </summary>
internal static class Party
{
    static ConfigEntry<bool> on;
    static ConfigEntry<int> seconds, cap, everyMinutes;
    static ConfigEntry<float> cooldownMinutes;
    static RaidParty forming;
    static float endsAt, nextChatStart, nextAuto = -1f, owedSince = -1f;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("RaidParty", "Enabled", true, "Chat can start a raid party with !party, and join with !join");
        seconds = config.Bind("RaidParty", "Seconds", 45, "How long chat has to join (15 to 300)");
        cap = config.Bind("RaidParty", "Cap", 20, "The most monsters a party brings (more raiders than that: tougher monsters)");
        cooldownMinutes = config.Bind("RaidParty", "CooldownMinutes", 10f, "After a party, how long before chat can start another");
        everyMinutes = config.Bind("RaidParty", "EveryMinutes", 0, "Start one by itself every this many minutes (0: only !party or the button)");
    }

    public static bool On { get => on.Value; set => on.Value = value; }
    public static int Seconds { get => seconds.Value; set => seconds.Value = Mathf.Clamp(value, 15, 300); }
    public static int EveryMinutes { get => everyMinutes.Value; set => everyMinutes.Value = Mathf.Clamp(value, 0, 120); }
    public static bool Forming => forming != null && owedSince < 0f;
    public static string Status = "";

    /// <summary>Open one (false: one's on, or needs the mod pack).</summary>
    public static bool Start(string by)
    {
        if (forming != null) return false;
        if (Hooks.SpawnRaid == null) { Status = "A raid party needs the Emberville mod pack"; return false; }
        forming = new RaidParty(Mathf.Clamp(cap.Value, 2, 40));
        owedSince = -1f;
        endsAt = Time.unscaledTime + Mathf.Clamp(seconds.Value, 15, 300);
        Status = $"Raid party forming (started by {by})";
        Overlay.Banner("Raid party forming!", "type !join in chat: every raider is a monster with your name");
        if (TwitchAuth.SignedIn) _ = TwitchHelix.Chat($"A raid party is forming! Type !join in the next {Mathf.Clamp(seconds.Value, 15, 300)} s: every raider comes as a monster with your name on it.");
        Log.Info($"Streaming: a raid party, started by {by}");
        return true;
    }

    /// <summary>A chat line: "!party", or "!join" / "!raid" while one forms (true: it was one of those).</summary>
    public static bool OnChat(string user, string text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (t == "!party")
        {
            if (!on.Value) return false;
            if (forming == null && Time.unscaledTime >= nextChatStart) Start(Streamer.Filter.Clean(user, 25));
            if (Forming) forming.Join(Streamer.Filter.Clean(user, 25));
            return true;
        }
        if (Forming && (t == "!join" || t == "!raid"))
        {
            forming.Join(Streamer.Filter.Clean(user, 25));
            return true;
        }
        return false;
    }

    /// <summary>The overlay's line while one forms.</summary>
    public static string OverlayLine()
    {
        if (!Forming) return "";
        int left = Mathf.Max(0, Mathf.CeilToInt(endsAt - Time.unscaledTime));
        return $"Raid party: {forming.Count} {(forming.Count == 1 ? "raider" : "raiders")}, type !join ({left} s)";
    }

    public static void Tick()
    {
        if (everyMinutes.Value > 0 && on.Value)
        {
            if (nextAuto < 0f) nextAuto = Time.unscaledTime + everyMinutes.Value * 60f;
            if (Time.unscaledTime >= nextAuto && forming == null && Streamer.CanFight()) { nextAuto = Time.unscaledTime + everyMinutes.Value * 60f; Start("the timer"); }
        }
        else nextAuto = -1f;
        if (forming == null) return;
        if (owedSince < 0f && Time.unscaledTime < endsAt) return;
        if (owedSince < 0f) owedSince = Time.unscaledTime;
        if (forming.Count == 0) { Done("Nobody joined the raid party", ""); return; }
        // (the raid waits for a moment it can land: not in a menu, a cutscene or loading; a minute at most)
        if (!Streamer.CanFight() || !Effects.Player)
        {
            if (Time.unscaledTime - owedSince > 60f) Done("The raid party couldn't find you", "");
            return;
        }
        int n = forming.Monsters;
        var names = forming.Raiders.OrderBy(_ => Random.value).ToList();
        var wearing = Enumerable.Range(0, n).Select(i => names[i % names.Count]).ToList();
        ChatterEnemies.Reserve(wearing);
        bool came = Hooks.SpawnRaid(Effects.Player.transform.position, n, forming.Toughness, "the raid party");
        if (!came) { Done("The raid party couldn't find a way in here", ""); return; }
        Effects.Glow(Effects.Player, Effects.ColourOf("raid"));
        Done($"Raid party! {forming.Count} {(forming.Count == 1 ? "raider" : "raiders")} incoming", string.Join(", ", names.Take(8)) + (names.Count > 8 ? $" and {names.Count - 8} more" : ""));
    }

    static void Done(string banner, string sub)
    {
        Overlay.Banner(banner, sub, "raid");
        Log.Info($"Streaming: {banner}");
        Status = banner;
        forming = null;
        owedSince = -1f;
        nextChatStart = Time.unscaledTime + Mathf.Max(0f, cooldownMinutes.Value) * 60f;
    }
}

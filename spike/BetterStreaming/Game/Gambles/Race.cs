using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterStreaming;

/// <summary>
/// The monster race, a game chat plays together: four of the game's creatures line up across the top of the screen,
/// each carrying an effect to the finish (two nice, two naughty). Chat cheers them on, "!1" to "!4": each cheer is a
/// burst of speed (Core.RaceModel), so the one chat backs usually wins, but not always. The winner's effect happens.
/// Bought with a cheer ("race"), started from the tab, or every few minutes by itself ([Race] EveryMinutes).
/// </summary>
internal sealed class Race : IEffect
{
    public string Id => "race";

    static ConfigEntry<string> nice, naughty;
    static ConfigEntry<int> everyMinutes;
    static float nextAuto = -1f;
    static Run running;

    public static void Bind(ConfigFile config)
    {
        nice = config.Bind("Race", "Nice", "heal,haste,shield,chainlightning,loot", "The nice prizes racers carry (two of these at random)");
        naughty = config.Bind("Race", "Naughty", "raid,chickens,drunk,freeze,poison", "The naughty prizes racers carry (two at random)");
        everyMinutes = config.Bind("Race", "EveryMinutes", 0, "Start a race by itself every this many minutes (0: only bought, or from the button)");
    }

    public static string NiceText { get => nice.Value; set => nice.Value = value; }
    public static string NaughtyText { get => naughty.Value; set => naughty.Value = value; }
    public static int EveryMinutes { get => everyMinutes.Value; set => everyMinutes.Value = Mathf.Clamp(value, 0, 120); }
    public static bool Running => running != null;

    public bool Available(out string why) { why = ""; return true; }
    public bool Ready(out string why) => Effects.HavePlayer(out why);

    public bool Fire(Pending p, float size) { Start(p.Viewer); return true; }

    public void Stop() { }

    /// <summary>A race, when the stage is free.</summary>
    public static void Start(string by) => Stage.Add("the race", () =>
    {
        var good = Payout.List(nice.Value, "heal,haste,shield", 1).OrderBy(_ => Random.value).ToList();
        var bad = Payout.List(naughty.Value, "raid,chickens,drunk", 1).OrderBy(_ => Random.value).ToList();
        var prizes = new List<string>();
        for (int i = 0; i < 2; i++) { if (good.Count > 0) prizes.Add(good[i % good.Count]); if (bad.Count > 0) prizes.Add(bad[i % bad.Count]); }
        if (prizes.Count < 2) return null;
        return running = new Run(by, prizes.OrderBy(_ => Random.value).ToList());
    });

    /// <summary>Every frame: a race by itself when it's time.</summary>
    public static void Tick()
    {
        if (everyMinutes.Value <= 0) { nextAuto = -1f; return; }
        if (nextAuto < 0f) nextAuto = Time.unscaledTime + everyMinutes.Value * 60f;
        if (Time.unscaledTime < nextAuto || !Streamer.CanFight() || Running) return;
        nextAuto = Time.unscaledTime + everyMinutes.Value * 60f;
        Start("chat");
    }

    /// <summary>A chat line: "!1" to "!4" cheers a racer on while one's running (true: it was one).</summary>
    public static bool OnChat(string user, string text) => running != null && running.Cheer(user, text);

    /// <summary>The overlay's line while one's on.</summary>
    public static string OverlayLine() => running == null ? "" : running.Racing ? "Monster race: type !1 !2 !3 !4 to cheer them on!" : "Monster race starting: pick a racer, !1 to !4";

    // ---- the creatures' pictures ----

    /// <summary>The Bestiary's creatures (the multiplayer mod's): a name and a picture of parts each. None: blobs.</summary>
    static List<(string Name, IReadOnlyList<(Sprite Sprite, Rect Box, bool Flip, Color Colour)> Parts)> Looks()
    {
        try { return Hooks.Creatures().Where(c => c.Parts != null && c.Parts.Count > 0 && c.Parts.All(p => p.Sprite)).ToList(); }
        catch (System.Exception ex) { Log.Warn($"Streaming: the race's creatures: {ex.Message}"); return new(); }
    }

    /// <summary>A creature's picture into a box that size, its parts where they sit (as the Bestiary draws it).</summary>
    static void Picture(RectTransform box, IReadOnlyList<(Sprite Sprite, Rect Box, bool Flip, Color Colour)> parts, float size)
    {
        var all = parts[0].Box;
        foreach (var p in parts) all = Rect.MinMaxRect(Mathf.Min(all.xMin, p.Box.xMin), Mathf.Min(all.yMin, p.Box.yMin), Mathf.Max(all.xMax, p.Box.xMax), Mathf.Max(all.yMax, p.Box.yMax));
        float scale = size / Mathf.Max(0.01f, Mathf.Max(all.width, all.height));
        foreach (var p in parts)
        {
            var rt = GambleUi.Box("Part", box, p.Box.size * scale);
            rt.anchoredPosition = (p.Box.center - all.center) * scale;
            if (p.Flip) rt.localScale = new Vector3(-1f, 1f, 1f);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = p.Sprite;
            img.color = p.Colour;
            img.raycastTarget = false;
        }
    }

    sealed class Run : IShow
    {
        const float Lineup = 6f, LaneH = 92f, Start0 = -560f, Finish = 470f;
        static readonly Color[] Tints = { new(1f, 0.55f, 0.3f), new(0.45f, 0.75f, 1f), new(0.6f, 1f, 0.5f), new(1f, 0.6f, 0.9f) };

        readonly string by;
        readonly List<string> prizes;
        readonly RaceModel model;
        readonly RectTransform panel;
        readonly List<RectTransform> racers = new();
        readonly List<TextMeshProUGUI> counts = new();
        readonly int[] cheers;
        readonly string[] names;
        readonly Dictionary<string, float> lastCheer = new(System.StringComparer.OrdinalIgnoreCase);
        readonly TextMeshProUGUI title, line;
        readonly float started;
        float wonAt = -1f, lastSecond = -1f;

        public bool Racing { get; private set; }

        public Run(string by, List<string> prizes)
        {
            this.by = by;
            this.prizes = prizes;
            int n = prizes.Count;
            model = new RaceModel(n, new System.Random(Random.Range(0, int.MaxValue)));
            cheers = new int[n];
            names = new string[n];
            started = Time.unscaledTime;
            var pool = Looks().OrderBy(_ => Random.value).Take(n).ToList();

            float h = n * LaneH + 150f;
            panel = GambleUi.Box("BS_Race", GambleUi.Root, new Vector2(1300f, h));
            panel.anchoredPosition = new Vector2(0f, 540f - h / 2f - 20f);
            GambleUi.Panel("Rim", panel, new Vector2(1260f, h - 20f), new Color(0.95f, 0.75f, 0.3f));
            GambleUi.Panel("Back", panel, new Vector2(1244f, h - 36f), new Color(0.13f, 0.1f, 0.18f, 0.94f));
            title = GambleUi.Text("Title", panel, by == "chat" ? "Monster race!" : $"Monster race: from {by}", 36f, new Color(1f, 0.93f, 0.82f), new Vector2(1200f, 50f));
            title.rectTransform.anchoredPosition = new Vector2(0f, h / 2f - 50f);
            line = GambleUi.Text("Line", panel, "", 32f, Color.white, new Vector2(1200f, 50f));
            line.rectTransform.anchoredPosition = new Vector2(0f, -h / 2f + 46f);
            var finish = GambleUi.Box("Finish", panel, new Vector2(8f, n * LaneH));
            finish.anchoredPosition = new Vector2(Finish + 30f, LaneY(n, (n - 1) / 2f));
            finish.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.85f);

            for (int i = 0; i < n; i++)
            {
                float y = LaneY(n, i);
                var lane = GambleUi.Box("Lane" + i, panel, new Vector2(Finish - Start0 + 140f, 3f));
                lane.anchoredPosition = new Vector2((Start0 + Finish) / 2f, y - 34f);
                lane.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
                var badge = GambleUi.Text("Key", panel, $"!{i + 1}", 32f, Tints[i % Tints.Length], new Vector2(70f, 50f));
                badge.rectTransform.anchoredPosition = new Vector2(Start0 - 60f, y);
                var prize = GambleUi.Panel("Prize", panel, new Vector2(150f, 52f), Payout.Colour(prizes[i]), 0.3f);
                prize.rectTransform.anchoredPosition = new Vector2(Finish + 120f, y);
                var pt = GambleUi.Text("Text", prize.transform, Payout.Name(prizes[i]), 22f, Color.white, new Vector2(140f, 46f));
                pt.enableAutoSizing = true; pt.fontSizeMin = 10f; pt.fontSizeMax = 22f;
                var c = GambleUi.Text("Cheers", panel, "", 22f, new Color(1f, 0.9f, 0.6f), new Vector2(120f, 30f));
                counts.Add(c);

                var racer = GambleUi.Box("Racer" + i, panel, new Vector2(80f, 80f));
                if (i < pool.Count)
                {
                    Picture(racer, pool[i].Parts, 80f);
                    names[i] = pool[i].Name;
                }
                else
                {
                    GambleUi.Shape("Blob", racer, "disc", new Vector2(64f, 64f), Tints[i % Tints.Length]);
                    names[i] = $"Racer {i + 1}";
                }
                var tag = GambleUi.Text("Name", racer, names[i], 18f, Tints[i % Tints.Length], new Vector2(220f, 26f));
                tag.rectTransform.anchoredPosition = new Vector2(0f, 50f);
                racers.Add(racer);
            }
            panel.localScale = Vector3.zero;
            Place();
            GambleUi.Sound("Play_UI_Tutorial_Panel_Popup");
            if (TwitchAuth.SignedIn) _ = TwitchHelix.Chat($"Monster race! Cheer your racer on with !1 to !{n}: " + string.Join(" | ", Enumerable.Range(0, n).Select(i => $"!{i + 1} {names[i]} carries {Payout.Name(prizes[i])}")));
        }

        static float LaneY(int n, float i) => ((n - 1) / 2f - i) * LaneH - 10f;

        public bool Cheer(string user, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length != 2 || text[0] != '!' || text[1] < '1' || text[1] > '0' + racers.Count) return false;
            int i = text[1] - '1';
            float now = Time.unscaledTime;
            if (lastCheer.TryGetValue(user ?? "", out var at) && now - at < 1f) return true;   // (one cheer a second each: a spammer is one voice)
            lastCheer[user ?? ""] = now;
            cheers[i]++;
            if (Racing) model.Boost(i);
            return true;
        }

        void Place()
        {
            float t = Time.unscaledTime;
            for (int i = 0; i < racers.Count; i++)
            {
                float hop = Racing && model.Winner < 0 ? Mathf.Abs(Mathf.Sin(t * (9f + model.Speed(i) * 40f) + i)) * 14f : 0f;
                float x = Mathf.Lerp(Start0, Finish, Mathf.Min(1f, model.Pos[i]));
                racers[i].anchoredPosition = new Vector2(x, LaneY(racers.Count, i) + hop - 4f);
                racers[i].localScale = Vector3.one * (1f + model.Burst[i] * 40f);   // (puffed up while chat cheers it)
                counts[i].text = cheers[i] > 0 ? $"x{cheers[i]}" : "";
                counts[i].rectTransform.anchoredPosition = new Vector2(Start0 - 60f, LaneY(racers.Count, i) - 30f);
            }
        }

        public bool Tick()
        {
            float age = Time.unscaledTime - started;
            panel.localScale = Vector3.one * GambleUi.Pop(age / 0.4f);
            if (!Racing)
            {
                int left = Mathf.CeilToInt(Lineup - age);
                line.text = left > 3 ? $"Pick a racer: type !1 to !{racers.Count} in chat to cheer it on" : left > 0 ? $"{left}..." : "GO!";
                if (left != (int)lastSecond && left <= 3 && left >= 0) { lastSecond = left; GambleUi.Sound(left == 0 ? "Play_UI_General_Select_Important" : "Play_UI_General_ChangeValueA"); }
                if (age >= Lineup) { Racing = true; }
                Place();
                return true;
            }
            if (model.Winner < 0)
            {
                model.Step(Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                int lead = Enumerable.Range(0, racers.Count).OrderByDescending(i => model.Pos[i]).First();
                line.text = $"{names[lead]} is in the lead!   (!1 to !{racers.Count} to cheer)";
                Place();
                if (model.Winner >= 0) Won();
                return true;
            }
            Place();
            float since = Time.unscaledTime - wonAt;
            racers[model.Winner].localEulerAngles = new Vector3(0f, 0f, Mathf.Sin(since * 10f) * 12f);
            if (since < 3.2f) return true;
            return Payout.PopOut(panel, since, 3.2f) > 0f;
        }

        void Won()
        {
            wonAt = Time.unscaledTime;
            int w = model.Winner;
            string id = prizes[w];
            int backers = cheers[w];
            line.text = $"{names[w]} wins! {Payout.Name(id)}";
            line.color = Payout.Good(id) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
            GambleUi.Sound(Payout.Good(id) ? "Play_Player_LevelUp" : "Play_UI_General_Select_Important");
            Log.Info($"Streaming: the race from {by}: {names[w]} won ({backers} cheers) -> {id}");
            Payout.Fire(id, by == "chat" ? "the monster race" : by, $"{names[w]} won the race", 1f, "race");
        }

        public void Destroy()
        {
            if (panel) Object.Destroy(panel.gameObject);
            if (running == this) running = null;
        }
    }
}

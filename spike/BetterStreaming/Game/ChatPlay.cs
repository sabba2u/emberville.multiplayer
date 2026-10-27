using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using BepInEx.Configuration;
using BetterStreaming.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Chat votes, for viewers who don't spend anything: "!raid" (any effect's word with a "!") from enough different
/// chatters within the window sets it off ("10 votes in 60 s" by default), then that effect's votes rest for the
/// cooldown. Votes show on stream as they come in ("!raid 4/10").
/// </summary>
internal static class ChatVotes
{
    static ConfigEntry<bool> on;
    static ConfigEntry<int> needed;
    static ConfigEntry<float> window, cooldown;
    static readonly Dictionary<string, (VoteCounter votes, int needed, float rest)> counters = new();
    static readonly Dictionary<string, double> lastVote = new();

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Votes", "Enabled", true, "Chat can set off an effect by voting: \"!raid\" from enough different chatters");
        needed = config.Bind("Votes", "Needed", 10, "How many different chatters it takes");
        window = config.Bind("Votes", "WindowSeconds", 60f, "Within how long");
        cooldown = config.Bind("Votes", "CooldownSeconds", 60f, "After an effect passes, how long before its votes count again");
        if (Mathf.Approximately(cooldown.Value, 300f)) cooldown.Value = 60f;   // (the old default, never changed: the new one)
    }

    public static bool On { get => on.Value; set => on.Value = value; }
    public static int Needed { get => needed.Value; set => needed.Value = Mathf.Clamp(value, 1, 500); }
    public static float Window { get => window.Value; set => window.Value = Mathf.Clamp(value, 10f, 600f); }
    public static float Rest { get => cooldown.Value; set => cooldown.Value = Mathf.Clamp(value, 0f, 3600f); }

    static double Now => Time.realtimeSinceStartupAsDouble;

    /// <summary>A chat line: a vote? (true: it was one, counted or not)</summary>
    public static bool OnChat(string user, string text)
    {
        text = (text ?? "").Trim();
        if (!on.Value || text.Length < 2 || text[0] != '!') return false;
        string word = text.Substring(1).Split(' ')[0].ToLowerInvariant();
        var d = Streamer.Buyable().FirstOrDefault(e => e.Id == word || e.Keywords.Any(k => k == word));
        if (d == null) return false;
        if (!counters.TryGetValue(d.Id, out var c) || c.needed != needed.Value || c.rest != cooldown.Value)
            counters[d.Id] = c = (new VoteCounter(Math.Max(1, needed.Value), window.Value, cooldown.Value), needed.Value, cooldown.Value);
        double rest = c.votes.RestLeft(Now);
        if (rest > 0) { Log.Info($"Streaming: vote !{word} from {user}: {d.Name} is resting, {rest:0} s left (it went off recently)"); return true; }
        lastVote[d.Id] = Now;
        if (!c.votes.Vote(user, Now)) { Log.Info($"Streaming: vote !{word} from {user}: {c.votes.Count}/{c.needed}"); return true; }
        Log.Info($"Streaming: chat voted {d.Name} in ({c.needed} votes)");
        Streamer.Submit(new Pending { EffectId = d.Id, Viewer = $"chat ({c.needed} votes)", Message = "", Size = 1f, Source = "vote", Mood = d.Mood });
        return true;
    }

    /// <summary>The votes going on, for the overlay: "!raid 4/10".</summary>
    public static IEnumerable<string> Progress()
    {
        foreach (var kv in counters)
        {
            if (kv.Value.votes.Count == 0 || !lastVote.TryGetValue(kv.Key, out var at) || Now - at > window.Value) continue;
            var d = Streamer.Def(kv.Key);
            yield return $"!{d?.Keywords.FirstOrDefault() ?? kv.Key} {kv.Value.votes.Count}/{kv.Value.needed}";
        }
    }
}

/// <summary>
/// Twitch polls: "What happens next?" with a few effects to pick from (the streamer starts one from the tab, or one
/// starts every few minutes); the winner happens, from "chat poll". Needs an Affiliate channel. With the local mock
/// (or not signed in), the poll runs here only and a random choice wins, for testing.
/// </summary>
internal static class Polls
{
    static ConfigEntry<int> everyMinutes, choices;
    static ConfigEntry<int> seconds;
    static string[] offered = new string[0];
    static string pollId = "";
    static bool running, local;
    static float endsAt;
    static float nextAuto = -1f;

    public static void Bind(ConfigFile config)
    {
        everyMinutes = config.Bind("Polls", "EveryMinutes", 0, "Start a poll by itself every this many minutes (0: only from the button)");
        seconds = config.Bind("Polls", "Seconds", 60, "How long a poll runs (15 to 1800)");
        choices = config.Bind("Polls", "Choices", 3, "How many effects to choose from (2 to 5)");
    }

    public static int EveryMinutes { get => everyMinutes.Value; set => everyMinutes.Value = Mathf.Clamp(value, 0, 120); }
    public static bool Running => running;
    public static string Status = "";

    /// <summary>A poll now (false: one's going, or nothing to choose from).</summary>
    public static bool Start()
    {
        if (running) return false;
        var pool = Streamer.Buyable().Where(d => d.Name.Length <= 25).OrderBy(_ => UnityEngine.Random.value).Take(Mathf.Clamp(choices.Value, 2, 5)).ToList();
        if (pool.Count < 2) { Status = "Not enough effects switched on for a poll"; return false; }
        offered = pool.Select(d => d.Name).ToArray();
        int secs = Mathf.Clamp(seconds.Value, 15, 1800);
        running = true;
        endsAt = Time.unscaledTime + secs;
        local = TwitchConfig.Mock || !TwitchAuth.SignedIn;
        pollId = "";
        Overlay.Banner("Chat poll: what happens next?", string.Join(" / ", offered));
        if (local) { Status = $"Test poll (here only): {string.Join(", ", offered)}"; return true; }
        Status = "Starting the poll on Twitch...";
        _ = Create(offered, secs);
        return true;
    }

    static async System.Threading.Tasks.Task Create(string[] names, int secs)
    {
        try
        {
            var (code, body) = await TwitchHelix.Api(HttpMethod.Post, "/polls", new
            {
                broadcaster_id = TwitchAuth.UserId,
                title = "What happens next?",
                choices = names.Select(n => new { title = n }).ToArray(),
                duration = secs,
            });
            if (code >= 200 && code < 300) { pollId = (string)JObject.Parse(body)["data"]?[0]?["id"] ?? ""; Status = $"Poll running: {string.Join(", ", names)}"; return; }
            running = false;
            Status = code == 403 ? "Twitch won't run polls here (it needs an Affiliate or Partner channel)" : code == 401 ? "Sign out and connect Twitch again to allow polls" : $"The poll didn't start ({code})";
            Log.Warn($"Streaming: poll: {code} {TwitchHelix.Short(body)}");
        }
        catch (Exception e) { running = false; Status = "The poll didn't start: " + e.Message; Log.Warn($"Streaming: poll: {e.Message}"); }
    }

    /// <summary>Twitch's result: the winner happens (if it was ours and anyone voted).</summary>
    public static void OnEnd(TwitchEvent e)
    {
        if (!running || local || (pollId != "" && e.PollId != pollId)) return;
        running = false;
        if (e.PollStatus == "archived" || e.PollVotes <= 0) { Status = "The poll ended with no votes"; return; }
        Won(e.PollWinner, $"{e.PollVotes} votes");
    }

    static void Won(string name, string how)
    {
        var d = Streamer.Defs.FirstOrDefault(x => x.Name == name);
        Status = $"Chat voted: {name}";
        if (d == null) return;
        Streamer.Submit(new Pending { EffectId = d.Id, Viewer = "chat poll", Message = how, Size = 1f, Source = "poll", Mood = d.Mood });
    }

    public static void Tick()
    {
        if (running && Time.unscaledTime >= endsAt)
        {
            if (local) { running = false; Won(offered[UnityEngine.Random.Range(0, offered.Length)], "test poll"); }
            else if (Time.unscaledTime >= endsAt + 120f) { running = false; Status = "No result came back from Twitch"; }
        }
        if (everyMinutes.Value <= 0) { nextAuto = -1f; return; }
        if (nextAuto < 0f) nextAuto = Time.unscaledTime + everyMinutes.Value * 60f;
        if (Time.unscaledTime >= nextAuto && Streamer.CanFight())
        {
            nextAuto = Time.unscaledTime + everyMinutes.Value * 60f;
            Start();
        }
    }
}

/// <summary>
/// "Will they survive?": when a big raid lands (a raid, chicken raid or storm bought at twice its price or more), a
/// Twitch prediction starts by itself: Survives / Falls, 30 s to bet channel points. It settles itself: you die (or go
/// down) while it's on: Falls; the raid's creatures all gone (the storm over): Survives. Needs an Affiliate channel,
/// and a sign-in from this version (it asks Twitch for the predictions permission).
/// </summary>
internal static class Prediction
{
    static ConfigEntry<bool> on;
    static ConfigEntry<float> minSize;
    static string id = "", survives = "", falls = "", about = "";
    static bool active, settling;
    static float startedAt;
    public static string Status = "";
    public static bool NeedsSignIn;

    public static void Bind(ConfigFile config)
    {
        on = config.Bind("Predictions", "Enabled", true, "A Twitch prediction (\"will they survive?\") when a big raid lands");
        minSize = config.Bind("Predictions", "MinSize", 2f, "How big a raid starts one (2 = bought for twice its price or more)");
    }

    public static bool On { get => on.Value; set => on.Value = value; }

    /// <summary>An effect landed (Streamer.Fire): a big raid starts a prediction.</summary>
    public static void Landed(string effectId, float size)
    {
        if (!on.Value || active || settling || size < minSize.Value) return;
        if (effectId != "raid" && effectId != "chickens" && effectId != "storm") return;
        if (TwitchConfig.Mock || !TwitchAuth.SignedIn) { Status = "(a big raid: a prediction would start here, signed in to Twitch)"; return; }
        active = true;
        about = effectId;
        startedAt = Time.unscaledTime;
        id = survives = falls = "";
        string who = TwitchAuth.Login.Length > 0 ? TwitchAuth.Login : "the streamer";
        string what = Streamer.Def(effectId)?.Name.ToLowerInvariant() ?? "raid";
        string title = $"Will {who} survive the {what}?";
        if (title.Length > 45) title = $"Will they survive the {what}?";
        _ = Create(title);
    }

    static async System.Threading.Tasks.Task Create(string title)
    {
        try
        {
            var (code, body) = await TwitchHelix.Api(HttpMethod.Post, "/predictions", new
            {
                broadcaster_id = TwitchAuth.UserId,
                title,
                outcomes = new[] { new { title = "Survives" }, new { title = "Falls" } },
                prediction_window = 30,
            });
            if (code >= 200 && code < 300)
            {
                var p = JObject.Parse(body)["data"]?[0];
                id = (string)p?["id"] ?? "";
                survives = (string)p?["outcomes"]?[0]?["id"] ?? "";
                falls = (string)p?["outcomes"]?[1]?["id"] ?? "";
                Status = $"Prediction running: {title}";
                return;
            }
            active = false;
            NeedsSignIn = code == 401;
            Status = code == 401 ? "Sign out and connect Twitch again to allow predictions" : code == 403 ? "Twitch won't run predictions here (it needs an Affiliate or Partner channel)" : $"The prediction didn't start ({code})";
            Log.Warn($"Streaming: prediction: {code} {TwitchHelix.Short(body)}");
        }
        catch (Exception e) { active = false; Status = "The prediction didn't start: " + e.Message; }
    }

    static async System.Threading.Tasks.Task Settle(string winner, string result)
    {
        settling = true;
        try
        {
            var (code, body) = await TwitchHelix.Api(new HttpMethod("PATCH"), "/predictions", new { broadcaster_id = TwitchAuth.UserId, id, status = "RESOLVED", winning_outcome_id = winner });
            Status = code >= 200 && code < 300 ? $"Prediction settled: {result}" : $"The prediction couldn't be settled ({code}): settle it on Twitch";
            if (code < 200 || code >= 300) Log.Warn($"Streaming: prediction settle: {code} {TwitchHelix.Short(body)}");
        }
        catch (Exception e) { Status = "The prediction couldn't be settled: " + e.Message; }
        finally { settling = false; }
    }

    public static void Tick()
    {
        if (!active || id == "") return;
        var me = Effects.Player;
        float age = Time.unscaledTime - startedAt;
        bool fell = me && (me.IsDead || TwitchDeath.Falling);
        bool over = age > 20f && !Streamer.Timers.Active(about) && !EnemiesManager.AllEnemies.Any(e => e && !e.IsDead && Hooks.IsTwitchCreature(e));
        if (!fell && !over && age < 240f) return;
        active = false;
        string result = fell ? "Falls" : "Survives";
        Overlay.Banner(fell ? "Chat called it: they fell" : "They survived!", "");
        _ = Settle(fell ? falls : survives, result);
    }
}

/// <summary>
/// The leaderboard: Bits spent on naughty effects (top raiders), on nice ones (top healers), and deaths caused (a
/// death within 45 s of someone's naughty effect is theirs). Kept across streams in its own file; "!top" in chat, a
/// line on the overlay, and a Clear in the tab.
/// </summary>
internal static class Board
{
    static Leaderboard board = new();
    static ConfigEntry<bool> show;
    static string lastMean = "";
    static float lastMeanAt = -100f;
    static bool wasDown;
    static double nextTopLine;
    static string File => Path.Combine(BepInEx.Paths.ConfigPath, "spike.emberville.betterstreaming.leaderboard.json");

    public static void Bind(ConfigFile config)
    {
        show = config.Bind("Overlay", "ShowLeaderboard", true, "The leaderboard on the overlay (top raiders, healers, deaths caused)");
        try { if (System.IO.File.Exists(File)) board = Leaderboard.Load(System.IO.File.ReadAllText(File)); }
        catch (Exception e) { Log.Warn($"Streaming: the leaderboard couldn't be read ({e.Message})"); }
    }

    public static bool Show { get => show.Value; set => show.Value = value; }

    /// <summary>An effect landed: a paid one counts; a naughty one could get the kill.</summary>
    public static void Landed(Pending p, EffectDef d)
    {
        if (p.Source == "test") return;
        if (p.Bits > 0) { board.Add(p.Viewer, d.Mood == Mood.Nice ? Leaderboard.Healer : Leaderboard.Raider, p.Bits); Save(); }
        if (d.Mood == Mood.Mean && !p.Viewer.StartsWith("chat")) { lastMean = p.Viewer; lastMeanAt = Time.time; }
    }

    public static void Tick()
    {
        var me = Effects.Player;
        bool down = me && (me.IsDead || TwitchDeath.Falling);
        if (down && !wasDown)
        {
            // the kill: whoever's naughty effect landed last (in the last 45 s), else the chatter whose name the
            // nearest monster wears
            string killer = lastMean != "" && Time.time - lastMeanAt < 45f ? lastMean : ChatterEnemies.NearestName(me.transform.position, 5f);
            if (!string.IsNullOrEmpty(killer))
            {
                board.Add(killer, Leaderboard.Killer, 1);
                Save();
                Overlay.Banner($"{killer} gets the kill", "");
            }
            lastMean = "";
        }
        wasDown = down;
    }

    public static void Clear() { board.Clear(); Save(); }

    static string Top(string which, string unit) =>
        string.Join(", ", board.Top(which, 3).Select(t => $"{t.Viewer} {t.Amount}{unit}"));

    /// <summary>The overlay's line, one board at a time.</summary>
    public static string OverlayLine()
    {
        if (!show.Value) return "";
        var lines = new List<string>();
        string r = Top(Leaderboard.Raider, ""), h = Top(Leaderboard.Healer, ""), k = Top(Leaderboard.Killer, "");
        if (r != "") lines.Add("Top raiders: " + r);
        if (h != "") lines.Add("Top healers: " + h);
        if (k != "") lines.Add("Most deaths caused: " + k);
        return lines.Count == 0 ? "" : lines[(int)(Time.unscaledTime / 6f) % lines.Count];
    }

    /// <summary>"!top": the boards, in chat (once per 30 s).</summary>
    public static void TopInChat()
    {
        if (Time.realtimeSinceStartupAsDouble < nextTopLine) return;
        nextTopLine = Time.realtimeSinceStartupAsDouble + 30;
        string r = Top(Leaderboard.Raider, " Bits"), h = Top(Leaderboard.Healer, " Bits"), k = Top(Leaderboard.Killer, "");
        _ = TwitchHelix.Chat(r == "" && h == "" && k == "" ? "Nobody's on the leaderboard yet." :
            $"Top raiders: {(r == "" ? "-" : r)} | Top healers: {(h == "" ? "-" : h)} | Most deaths caused: {(k == "" ? "-" : k)}");
    }

    static void Save()
    {
        try { System.IO.File.WriteAllText(File, board.Save()); }
        catch (Exception e) { Log.Warn($"Streaming: couldn't save the leaderboard ({e.Message})"); }
    }
}

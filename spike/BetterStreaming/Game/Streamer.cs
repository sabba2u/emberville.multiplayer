using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using BetterStreaming.Core;
using UnityEngine;

namespace BetterStreaming;

/// <summary>An effect in the game: can it ever happen (no: why), can it happen now, set it off (false: it couldn't),
/// and end it (timed ones).</summary>
internal interface IEffect
{
    string Id { get; }
    bool Available(out string why);
    bool Ready(out string why);
    bool Fire(Pending p, float size);
    void Stop();
}

/// <summary>
/// The streamer's side, every frame: what Twitch sent (cheers routed to an effect; "!effects" answered in chat), the
/// queue (Core.ChaosQueue: nothing while the player can't fight; nice at once; mean ones on the chaos dial), each
/// effect fired with its banner, timed ones ended. The effects' settings (on, Bits, cap, seconds) and the dial are in
/// the config; the queue is saved to its own file on every change, so a closed game owes them next time.
/// </summary>
internal static class Streamer
{
    public static readonly List<EffectDef> Defs = new List<EffectDef>();
    public static readonly Dictionary<string, IEffect> Effects = new Dictionary<string, IEffect>();
    public static ChaosQueue Queue;
    public static Timers Timers;
    public static TextFilter Filter;
    static ConfigEntry<int> dial;
    static ConfigEntry<bool> paused;

    /// <summary>"Twitch off: I'm not streaming": chat, cheers, polls and rewards all ignored (just logged).</summary>
    public static bool Paused { get => paused?.Value ?? false; set { if (paused != null) paused.Value = value; } }
    static ConfigEntry<string> extraWords;
    static readonly Dictionary<string, (ConfigEntry<bool> on, ConfigEntry<int> bits, ConfigEntry<int> cap, ConfigEntry<float> secs)> settings = new();
    static string QueueFile => Path.Combine(BepInEx.Paths.ConfigPath, "spike.emberville.betterstreaming.queue.json");
    static double Now() => Time.realtimeSinceStartupAsDouble;
    const int Tries = 3;
    static double holdUntil, nextEffectsLine;

    public static void Init(ConfigFile config)
    {
        paused = config.Bind("Twitch", "Paused", false, "Twitch off (not streaming): chat commands, votes, cheers, polls and rewards are ignored");
        dial = config.Bind("Chaos", "Dial", 2, "How often a mean effect may land: 0 Chill (one per 2 min), 1, 2 (middle), 3, 4 Mayhem (one per 15 s)");
        extraWords = config.Bind("Text", "ExtraWords", "", "Your own words to hide in viewers' messages (comma separated)");
        Filter = new TextFilter(extraWords.Value.Split(','));
        foreach (var d in Catalogue.Defaults())
        {
            string s = "Effect." + d.Id;
            var on = config.Bind(s, "Enabled", d.Enabled, $"{d.Name}: chat can buy it");
            var bits = config.Bind(s, "Bits", d.Bits, $"{d.Name}: its price in Bits");
            var cap = config.Bind(s, "CapBits", d.CapBits, $"{d.Name}: the most Bits it grows with (scaling effects)");
            var secs = config.Bind(s, "Seconds", d.Seconds, $"{d.Name}: how long it lasts (timed effects)");
            if (d.Id == "roulette" && Mathf.Approximately(secs.Value, 120f)) secs.Value = 90f;   // (its old length, never changed: the new one)
            if ((d.Id == "kymia" || d.Id == "gold" || d.Id == "shield") && secs.Value <= 0f) secs.Value = 30f;   // (it was once instant: now it lasts, and must end)
            settings[d.Id] = (on, bits, cap, secs);
            Defs.Add(d);
        }
        Apply();
        string saved = null;
        try { if (File.Exists(QueueFile)) saved = File.ReadAllText(QueueFile); } catch (Exception e) { Log.Warn($"Streaming: the queue file couldn't be read ({e.Message})"); }
        Queue = saved != null ? ChaosQueue.Load(saved, Now) : new ChaosQueue(Now);
        Queue.Dial = dial.Value;
        Timers = new Timers(() => Time.timeAsDouble);   // (game time: a pause doesn't eat a timed effect)
        if (Queue.Waiting.Count > 0) Overlay.Banner($"Welcome back: {Queue.Waiting.Count} {(Queue.Waiting.Count == 1 ? "curse" : "curses")} still owed", "");
    }

    /// <summary>The config's numbers onto the effects (after the panel changes one, too).</summary>
    public static void Apply()
    {
        foreach (var d in Defs)
        {
            var s = settings[d.Id];
            d.Enabled = s.on.Value; d.Bits = Math.Max(1, s.bits.Value); d.CapBits = Math.Max(d.Bits, s.cap.Value); d.Seconds = Math.Max(0f, s.secs.Value);
        }
    }

    public static (ConfigEntry<bool> on, ConfigEntry<int> bits, ConfigEntry<int> cap, ConfigEntry<float> secs) Settings(string id) => settings[id];

    public static int Dial { get => Queue.Dial; set { Queue.Dial = value; dial.Value = Queue.Dial; Save(); } }

    public static EffectDef Def(string id) => Defs.FirstOrDefault(d => d.Id == id);

    /// <summary>The effects chat can buy: switched on, and able to happen at all (a raid needs the pack). Whether it can
    /// happen right now (a player there, not loading) is the queue's business: a cheer bought now waits.</summary>
    public static List<EffectDef> Buyable() => Defs.Where(d => d.Enabled && Effects.TryGetValue(d.Id, out var fx) && fx.Available(out _)).ToList();

    /// <summary>An effect over before its time (second wind used, a weapon lock ended by a scene change): its timer and
    /// its buff icon gone.</summary>
    public static void EndEarly(string id) { Timers.Stop(id); BuffIcons.Hide(id); }

    /// <summary>Into the queue (a paid one, a test, a simulated cheer).</summary>
    public static void Submit(Pending p)
    {
        var d = Def(p.EffectId);
        if (d == null) { Log.Warn($"Streaming: no effect called {p.EffectId}"); return; }
        Queue.Add(p, d.Mood);
        Save();
        if (d.Mood == Mood.Mean && Queue.NextMeanIn() > 0.5) Overlay.Banner($"{d.Name} from {p.Viewer}: queued", p.Message, d.Id);
    }

    /// <summary>A cheer (real or simulated): routed by keyword or amount, then queued; too small: a thank-you.</summary>
    public static void Cheer(string viewer, int bits, string message, string source = "bits")
    {
        var buy = CheerRouter.Route(Buyable(), bits, message);
        string words = Filter.Clean(message);
        if (buy.Effect == null) { Overlay.Banner($"{viewer} cheered {bits} Bits: thank you!", words); return; }
        Submit(new Pending { EffectId = buy.Effect.Id, Viewer = viewer, Message = words, Size = buy.Size, Source = source, Bits = bits });
    }

    /// <summary>Can the player take a hit now? (Nothing lands in a cutscene, dialogue, a menu, loading, or dead.)</summary>
    public static bool CanFight()
    {
        if (!Singleton<GameManager>.HasMain) return false;
        var gm = Singleton<GameManager>.main;
        var p = gm.player;
        return p && !p.IsDead && gm.gameState == GameState.Playing && !UIPanels.HasOpenedPanel && MainInput.Actions.Player.enabled;
    }

    public static void Tick()
    {
        while (TwitchSocket.Events.TryDequeue(out var e)) OnTwitch(e);
        foreach (var id in Timers.Expired())
        {
            BuffIcons.Hide(id);
            if (Effects.TryGetValue(id, out var fx)) { try { fx.Stop(); } catch (Exception ex) { Log.Warn($"Streaming: ending {id}: {ex.Message}"); } }
        }
        if (Now() < holdUntil) return;
        var next = Queue.Next(CanFight());
        if (next == null) return;
        Fire(next);
    }

    /// <summary>One off now (its banner, ring, word and timer), past the queue (the random packs' picks).</summary>
    internal static void Fire(Pending p)
    {
        var d = Def(p.EffectId);
        if (d == null || !Effects.TryGetValue(p.EffectId, out var fx)) { Log.Warn($"Streaming: {p.EffectId} isn't there"); return; }
        float size = Math.Max(1f, p.Size);
        bool ok = false, wasOn = Timers.Active(d.Id);
        string why = "";
        if (fx.Ready(out why))
        {
            // (timed: started first, so a second one extends it and the effect reads its whole time)
            if (d.Seconds > 0f) Timers.Start(d.Id, d.Seconds * (d.Scales && d.LongerWithBits ? size : 1f));
            try { ok = fx.Fire(p, size); if (!ok) why = "nothing could come here"; }
            catch (Exception e) { why = e.Message; Log.Warn($"Streaming: {d.Name} went wrong: {e}"); }
            if (!ok && !wasOn) Timers.Stop(d.Id);
        }
        if (!ok)
        {
            // (it couldn't happen: it goes first again a little later; paid effects never just vanish)
            if (p.Tries + 1 < Tries) { Queue.Retry(p); holdUntil = Now() + 5; Save(); Log.Info($"Streaming: {d.Name} waits ({why})"); }
            else { Save(); Overlay.Banner($"{d.Name} from {p.Viewer} couldn't happen here", "skipped after three tries"); Log.Warn($"Streaming: {d.Name} from {p.Viewer} skipped ({why})"); }
            return;
        }
        Save();
        if (d.Seconds > 0f) BuffIcons.Show(d.Id, d.Name, Timers.Left(d.Id));   // (the game's buff list on the left: name, icon, time)
        if (BetterStreaming.Effects.Player)
        {
            BetterStreaming.Effects.Glow(BetterStreaming.Effects.Player, BetterStreaming.Effects.ColourOf(d.Id));   // (the ring under you, in the effect's colour)
            BetterStreaming.Effects.Say(BetterStreaming.Effects.Player, BetterStreaming.Effects.WordOf(d.Id));      // (and its word, floating up)
        }
        Overlay.Banner(p.EffectId == "raid" ? $"Twitch raid incoming by {p.Viewer}" : $"{d.Name} from {p.Viewer}", p.Message, d.Id);
        Board.Landed(p, d);
        Prediction.Landed(d.Id, size);
        Log.Info($"Streaming: {d.Name} from {p.Viewer} (x{size:0.##}, {p.Source})");
    }

    static void OnTwitch(TwitchEvent e)
    {
        // not streaming: nothing chat does reaches the game (still logged, to see what came)
        if (Paused && (e.Kind == TwitchKind.Cheer || e.Kind == TwitchKind.Chat || e.Kind == TwitchKind.PollEnd || e.Kind == TwitchKind.Redemption))
        {
            Log.Info($"Twitch (off: not streaming) ignored {e.Kind}{(e.User != null ? " from " + e.User : "")}{(e.Kind == TwitchKind.Cheer ? $" ({e.Bits} bits)" : "")}{(e.Kind == TwitchKind.Chat ? ": " + e.Message : "")}");
            return;
        }
        switch (e.Kind)
        {
            case TwitchKind.Cheer:
                Log.Info($"Twitch: cheer {e.Bits} from {(e.Anonymous ? "an anonymous cheerer" : e.User)}");
                Cheer(TextFilter.Viewer(e.User, e.Anonymous), e.Bits, e.Message);
                break;
            case TwitchKind.Chat:
                Log.Info($"Twitch chat < {e.User}: {e.Message}");
                Chat(e.User, e.Message);
                break;
            case TwitchKind.PollEnd:
                Log.Info($"Twitch: poll ended ({e.PollStatus})");
                Polls.OnEnd(e);
                break;
            case TwitchKind.Redemption:
                Log.Info($"Twitch: {e.User} redeemed {e.RewardTitle} (channel-point rewards come in part 3)");
                break;
        }
    }

    /// <summary>A chat line (real, or the tab's pretend one): its name for the monsters; a raid party ("!party", "!join"),
    /// a race cheer ("!1"), "!effects", "!top", or a vote ("!raid").</summary>
    public static void Chat(string user, string message)
    {
        string text = (message ?? "").Trim();
        ChatterEnemies.Seen(user);
        if (Party.OnChat(user, text) || Race.OnChat(user, text)) return;
        if (text.Equals("!effects", StringComparison.OrdinalIgnoreCase))
        {
            if (Now() < nextEffectsLine) return;
            nextEffectsLine = Now() + 60;   // (once a minute: chat can't make the streamer's account spam)
            _ = SayAll(EffectsLines());
            return;
        }
        if (text.Equals("!top", StringComparison.OrdinalIgnoreCase)) { Board.TopInChat(); return; }
        ChatVotes.OnChat(user, text);
    }

    /// <summary>The "!effects" answer, as lines Twitch takes: how it works, then the gambles, the nice ones and the
    /// naughty ones, each as its cheer word and price ("raid 500+": more Bits, a bigger one).</summary>
    public static List<string> EffectsLines()
    {
        var on = Buyable();
        if (on.Count == 0) return new List<string> { "No effects are switched on right now." };
        var lines = new List<string>
        {
            "Mess with my game: put an effect's word in a cheer (\"cheer525 wheel\"), or cheer its exact amount." +
            (ChatVotes.On ? $" Free: type !word (\"!wheel\"){(ChatVotes.Needed > 1 ? $" and {ChatVotes.Needed} of you set it off" : " to set it off")}." : "") +
            (Party.On ? " !party starts a raid party (then !join)." : "") + " !top: the leaderboard.",
        };
        string Item(EffectDef d) => $"{d.Keywords.FirstOrDefault() ?? d.Id} {d.Bits}{(d.Scales ? "+" : "")}";
        lines.AddRange(ChatLines.Pack("Gambles:", on.Where(d => d.Mood == Mood.Gamble).Select(Item)));
        lines.AddRange(ChatLines.Pack("Nice:", on.Where(d => d.Mood == Mood.Nice).Select(Item)));
        lines.AddRange(ChatLines.Pack("Naughty:", on.Where(d => d.Mood == Mood.Mean).Select(Item)));
        return lines;
    }

    /// <summary>Lines into chat one after another, in order (a moment apart).</summary>
    static async System.Threading.Tasks.Task SayAll(List<string> lines)
    {
        foreach (var line in lines)
        {
            await TwitchHelix.Chat(line);
            await System.Threading.Tasks.Task.Delay(400);
        }
    }

    static void Save()
    {
        try { File.WriteAllText(QueueFile, Queue.Save()); }
        catch (Exception e) { Log.Warn($"Streaming: couldn't save the queue ({e.Message})"); }
    }
}

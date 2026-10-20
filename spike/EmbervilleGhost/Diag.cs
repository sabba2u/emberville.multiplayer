using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Diagnostics, so problems can be read from one place:
///  - Spam squashing: BepInEx's log file (LogOutput.log) gets each repeating line a few times, then a summary every
///    minute ("repeated 12400 more times in 60 s") instead of the same error every frame.
///  - One session log on the host's PC (BepInEx/SessionLogs/session_*.log): every player's mod lines (NetLog) and the
///    game's warnings / errors, in time order and labelled by player. Clients send theirs to the host in batches.
/// The mismatch checker (DesyncCheck) writes its findings through NetLog, so they land here too.
/// </summary>
internal static class Diag
{
    public static Plugin Plugin;
    static ConfigEntry<bool> sessionLogOn;

    const int MaxBatch = 80, MaxQueue = 2000, MaxLine = 600;
    const float SendEvery = 0.5f;

    struct Line { public DateTime Time; public LogKind Kind; public string Text; }

    static readonly object gate = new object();
    static readonly List<Line> pending = new List<Line>();   // ours, waiting to be written (host) or sent (client)
    static int droppedLines;
    static float nextSend;

    static StreamWriter file;
    static string filePath;

    public static void Bind(ConfigFile config)
    {
        sessionLogOn = config.Bind("Diagnostics", "SessionLog", true,
            "Host: write everyone's mod log lines and game errors to BepInEx/SessionLogs (clients send theirs to the host)");
        InstallSquasher();
    }

    // ---- our lines (NetLog) and the game's warnings / errors (via the squasher) ----

    public static void Capture(LogKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxLine) text = text.Substring(0, MaxLine) + "...";
        lock (gate)
        {
            if (pending.Count >= MaxQueue) { droppedLines++; return; }
            pending.Add(new Line { Time = DateTime.Now, Kind = kind, Text = text });
        }
    }

    /// <summary>Every frame: host writes everything queued; a client sends a batch twice a second.</summary>
    public static void Tick()
    {
        bool connected = Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
        if (!connected || !sessionLogOn.Value)
        {
            CloseFile();
            lock (gate) { pending.Clear(); droppedLines = 0; }
            return;
        }
        if (Plugin.IsHost)
        {
            List<Line> lines;
            int dropped;
            lock (gate) { lines = new List<Line>(pending); pending.Clear(); dropped = droppedLines; droppedLines = 0; }
            foreach (var l in lines) Write(l.Time, Plugin.MyId, l.Kind, l.Text);
            if (dropped > 0) Write(DateTime.Now, Plugin.MyId, LogKind.Warning, $"({dropped} lines dropped: too many at once)");
            return;
        }
        CloseFile();
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + SendEvery;
        List<Line> batch;
        int lost;
        lock (gate)
        {
            if (pending.Count == 0 && droppedLines == 0) return;
            batch = pending.Take(MaxBatch).ToList();
            pending.RemoveRange(0, batch.Count);
            lost = droppedLines; droppedLines = 0;
        }
        var w = new NetDataWriter();
        w.Put((byte)MsgType.LogBatch);
        w.Put(lost);
        w.Put(batch.Count);
        foreach (var l in batch) { w.Put((byte)l.Kind); w.Put(l.Text); }
        Plugin.SendTo(0, w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Host: a client's lines.</summary>
    public static void OnBatch(NetDataReader r, int from)
    {
        int lost = r.GetInt();
        int n = r.GetInt();
        var now = DateTime.Now;
        if (lost > 0) Write(now, from, LogKind.Warning, $"({lost} lines dropped on their side: too many at once)");
        for (int i = 0; i < n; i++)
        {
            var kind = (LogKind)r.GetByte();
            Write(now, from, kind, r.GetString());
        }
    }

    static void Write(DateTime time, int player, LogKind kind, string text)
    {
        if (!sessionLogOn.Value || Plugin == null) return;
        try
        {
            if (file == null)
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "SessionLogs");
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                file = new StreamWriter(filePath, append: false) { AutoFlush = true };
                file.WriteLine($"Emberville multiplayer session log, mod v{Plugin.Version}, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                foreach (var old in new DirectoryInfo(dir).GetFiles("session_*.log").OrderByDescending(f => f.CreationTime).Skip(20)) old.Delete();
                NetLog.Info($"Session log: {filePath}");
            }
            string who = player == 0 ? "P1 host" : $"P{player + 1}";
            string name = player == Plugin.MyId ? "" : " " + Plugin.NameOf(player);
            string tag = kind switch { LogKind.Error => "ERR ", LogKind.Warning => "WARN", _ => "    " };
            file.WriteLine($"{time:HH:mm:ss.fff} [{who}{name}] {tag} {text.Replace("\n", "\n                   ")}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Session log: {e.Message}"); CloseFile(); }
    }

    static void CloseFile()
    {
        if (file == null) return;
        try { file.Dispose(); } catch { }
        file = null;
    }

    public static string FilePath => file != null ? filePath : null;

    // ---- spam squashing ----

    /// <summary>Put our squasher in front of BepInEx's log file (LogOutput.log).</summary>
    static void InstallSquasher()
    {
        try
        {
            var disk = BepInEx.Logging.Logger.Listeners.OfType<DiskLogListener>().FirstOrDefault();
            if (disk == null) return;
            BepInEx.Logging.Logger.Listeners.Remove(disk);
            BepInEx.Logging.Logger.Listeners.Add(new Squasher(disk));
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"Log squasher: {e.Message}"); }
    }

    /// <summary>
    /// Passes each distinct line on, but a line seen more than a few times in the last minute is held back and counted;
    /// once a minute the counts go out as one summary line each. Game warnings / errors also go to the session log
    /// (after squashing). Thread-safe: Unity's log can come from other threads.
    /// </summary>
    sealed class Squasher : ILogListener
    {
        const int FreeRepeats = 3;
        const double Window = 60;

        readonly ILogListener inner;
        readonly object sync = new object();
        readonly Dictionary<string, (int seen, int held, DateTime since, string first)> counts = new Dictionary<string, (int, int, DateTime, string)>();
        DateTime nextFlush = DateTime.Now.AddSeconds(Window);

        public Squasher(ILogListener inner) { this.inner = inner; }

        public void LogEvent(object sender, LogEventArgs e)
        {
            string text = e.Data?.ToString() ?? "";
            string key = e.Level + "|" + e.Source?.SourceName + "|" + FirstLine(text);
            bool pass;
            lock (sync)
            {
                var now = DateTime.Now;
                if (now >= nextFlush) Flush(sender, now);
                counts.TryGetValue(key, out var c);
                if (c.seen == 0) c.since = now;
                c.seen++;
                pass = c.seen <= FreeRepeats;
                if (!pass) { c.held++; if (c.first == null) c.first = FirstLine(text); }
                counts[key] = c;
            }
            if (!pass) return;
            inner.LogEvent(sender, e);
            bool fromGame = e.Source?.SourceName == "Unity Log";
            if (fromGame && (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0) Capture(LogKind.Error, text);
            else if (fromGame && (e.Level & LogLevel.Warning) != 0) Capture(LogKind.Warning, text);
        }

        void Flush(object sender, DateTime now)
        {
            foreach (var pair in counts.Where(p => p.Value.held > 0).ToList())
            {
                string summary = $"(repeated {pair.Value.held} more times in the last {(now - pair.Value.since).TotalSeconds:0} s) {pair.Value.first}";
                inner.LogEvent(sender, new LogEventArgs(summary, LogLevel.Warning, Squashed));
                Capture(LogKind.Warning, summary);
            }
            counts.Clear();
            nextFlush = now.AddSeconds(Window);
        }

        static readonly ManualLogSource Squashed = new ManualLogSource("Repeats");

        static string FirstLine(string text)
        {
            int n = text.IndexOf('\n');
            string first = n >= 0 ? text.Substring(0, n) : text;
            return first.Length > 200 ? first.Substring(0, 200) : first;
        }

        public void Dispose() => inner.Dispose();
    }
}

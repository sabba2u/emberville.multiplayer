using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterStreaming.Core;

/// <summary>A pick from weighted entries ("heal:3, death:1", as WheelSlices.Parse reads them). roll is 0..1.</summary>
public static class Weighted
{
    public static string Pick(IReadOnlyList<WheelSlice> list, double roll)
    {
        float total = list.Sum(e => e.Weight);
        if (list.Count == 0 || total <= 0f) return "";
        double at = Math.Max(0.0, Math.Min(0.999999, roll)) * total;
        foreach (var e in list) { if (at < e.Weight) return e.Id; at -= e.Weight; }
        return list[list.Count - 1].Id;
    }

    internal static HashSet<string> Words(string message)
    {
        var set = new HashSet<string>();
        foreach (var w in Regex.Split((message ?? "").ToLowerInvariant(), "[^a-z]+")) if (w.Length > 0) set.Add(w);
        return set;
    }
}

/// <summary>
/// The slot machine's reels and what they pay: three of a kind is that effect doubled, three 7s the jackpot, three
/// skulls instant death; two of a kind is that effect once (two 7s or two skulls are nothing); no match is the
/// streamer's NoMatch ("" by default: nothing).
/// </summary>
public static class SlotMachine
{
    public const string Default = "heal:3,raid:3,chickens:3,chainlightning:2,haste:3,freeze:2,shield:3,loot:2,drunk:3,death:1,jackpot:1";

    public static (string Id, float Size) Pays(string a, string b, string c, string noMatch = "")
    {
        if (a == b && b == c) return (a, a == DiceTable.Jackpot || a == "death" ? 1f : 2f);
        string pair = a == b || a == c ? a : b == c ? b : null;
        if (pair != null && pair != DiceTable.Jackpot && pair != "death") return (pair, 1f);
        return (noMatch ?? "", 1f);
    }
}

/// <summary>
/// Plinko: a ball dropped onto rows of pegs (a triangle, a peg wider each row), bouncing down into one of the bins
/// under the last row. Units are the peg spacing; y is up, the top peg at 0. Nothing is picked in advance: the bounces
/// decide, so the middle bins come up often and the edges rarely.
/// </summary>
public sealed class PlinkoBoard
{
    public const float BallR = 0.2f, PegR = 0.1f, RowH = 0.87f;
    const float Gravity = 14f, Bounce = 0.3f, SubStep = 0.002f, MaxTime = 20f;

    readonly Random rng;
    readonly List<(float x, float y)> pegs = new();
    float time;

    public readonly int Bins, Rows;
    public float X, Y, VX, VY;
    public int Hits;
    public bool Done { get; private set; } = true;
    public IReadOnlyList<(float x, float y)> Pegs => pegs;
    public float Bottom => -(Rows - 1) * RowH;
    public float HalfWidth => Bins / 2f;
    /// <summary>How far out the side is at height y (they slant with the pegs).</summary>
    public float WallAt(float y) => Math.Min(HalfWidth, (2f - Math.Min(0f, y) / RowH) / 2f + 0.45f);

    public PlinkoBoard(int bins, Random rng)
    {
        this.rng = rng;
        Bins = Math.Max(3, bins);
        Rows = Bins - 1;
        for (int r = 0; r < Rows; r++)
            for (int i = 0; i < r + 3; i++)
                pegs.Add((i - (r + 2) / 2f, -r * RowH));
    }

    /// <summary>The middle of bin i (x).</summary>
    public float BinX(int i) => i - Bins / 2f + 0.5f;

    /// <summary>Where the ball is falling: the bin under it.</summary>
    public int Bin => Math.Max(0, Math.Min(Bins - 1, (int)Math.Floor(X + Bins / 2f)));

    public void Drop(float x)
    {
        X = x; Y = 0.9f; VX = 0f; VY = 0f;
        Hits = 0; time = 0f; Done = false;
    }

    public void Step(float dt)
    {
        if (Done) return;
        for (float t = 0f; t < dt && !Done; t += SubStep) Tick(Math.Min(SubStep, dt - t));
    }

    void Tick(float dt)
    {
        time += dt;
        VY -= Gravity * dt;
        X += VX * dt; Y += VY * dt;
        foreach (var (px, py) in pegs)
        {
            float dx = X - px, dy = Y - py, d2 = dx * dx + dy * dy, r = BallR + PegR;
            if (d2 >= r * r || d2 < 1e-8f) continue;
            float d = (float)Math.Sqrt(d2), nx = dx / d, ny = dy / d;
            X = px + nx * r; Y = py + ny * r;
            float vn = VX * nx + VY * ny;
            if (vn >= 0f) continue;
            VX -= (1f + Bounce) * vn * nx;
            VY -= (1f + Bounce) * vn * ny;
            // (a little luck each bounce, and never balanced on top of a peg)
            float turn = (float)(rng.NextDouble() - 0.5) * 0.3f, c = (float)Math.Cos(turn), s = (float)Math.Sin(turn);
            (VX, VY) = (VX * c - VY * s, VX * s + VY * c);
            if (Math.Abs(VX) < 0.25f) VX += (rng.NextDouble() < 0.5 ? -1f : 1f) * (0.25f + (float)rng.NextDouble() * 0.25f);
            Hits++;
        }
        // (the sides slant out with the triangle of pegs: a ball can't slip down outside them)
        float wall = WallAt(Y) - BallR;
        if (X < -wall) { X = -wall; VX = Math.Abs(VX) * Bounce; }
        if (X > wall) { X = wall; VX = -Math.Abs(VX) * Bounce; }
        if (Y < Bottom - 0.6f || time > MaxTime) Done = true;
    }
}

/// <summary>The roulette wheel (the European one, a single 0): its numbers in order round the wheel and their colours,
/// and the colour a cheer's message bets on.</summary>
public static class RouletteTable
{
    public const string Red = "red", Black = "black", Green = "green";

    public static readonly int[] Order = { 0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26 };
    static readonly HashSet<int> Reds = new() { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

    public static string Colour(int n) => n == 0 ? Green : Reds.Contains(n) ? Red : Black;

    public static string Bet(string message)
    {
        var words = Weighted.Words(message);
        return words.Contains(Red) ? Red : words.Contains(Black) ? Black : words.Contains(Green) ? Green : "";
    }
}

/// <summary>The side a coin-flip cheer calls ("heads"/"tails" in its message), or "".</summary>
public static class CoinCall
{
    public static string Of(string message)
    {
        var words = Weighted.Words(message);
        return words.Contains("heads") ? "heads" : words.Contains("tails") ? "tails" : "";
    }
}

/// <summary>
/// The monster race: racers running a track from 0 to 1 (about 16 seconds), each finding and losing its stride at
/// random, and chat cheering them on: every cheer is a little burst of speed that fades, up to a cap, so the one chat
/// backs usually wins, but not always.
/// </summary>
public sealed class RaceModel
{
    const float BaseSpeed = 1f / 16f, BoostEach = 0.0025f, BoostCap = 0.1f * BaseSpeed, BoostFade = 0.8f;

    readonly Random rng;
    readonly float[] stride, target, retarget;
    public readonly float[] Pos, Burst;
    public int Winner { get; private set; } = -1;

    public RaceModel(int racers, Random rng)
    {
        this.rng = rng;
        Pos = new float[racers]; Burst = new float[racers];
        stride = new float[racers]; target = new float[racers]; retarget = new float[racers];
        for (int i = 0; i < racers; i++) stride[i] = target[i] = 1f;
    }

    public void Boost(int i)
    {
        if (i < 0 || i >= Pos.Length || Winner >= 0) return;
        Burst[i] = Math.Min(BoostCap, Burst[i] + BoostEach);
    }

    /// <summary>How fast racer i is going now (track lengths a second).</summary>
    public float Speed(int i) => BaseSpeed * stride[i] + Burst[i];

    public void Step(float dt)
    {
        if (Winner >= 0) return;
        for (int i = 0; i < Pos.Length; i++)
        {
            retarget[i] -= dt;
            if (retarget[i] <= 0f) { target[i] = 0.65f + (float)rng.NextDouble() * 0.7f; retarget[i] = 0.4f + (float)rng.NextDouble() * 0.9f; }
            stride[i] += (target[i] - stride[i]) * Math.Min(1f, dt * 3f);
            Burst[i] *= (float)Math.Exp(-dt / BoostFade);
            Pos[i] += Speed(i) * dt;
        }
        int best = -1;
        for (int i = 0; i < Pos.Length; i++) if (Pos[i] >= 1f && (best < 0 || Pos[i] > Pos[best])) best = i;
        Winner = best;
    }
}

/// <summary>
/// Who's been chatting lately (for monsters' names): seen within the last few minutes, never the streamer or a chat
/// bot. A pick shares the names out: one nobody's wearing yet if there is one, else any.
/// </summary>
public sealed class ChatterPool
{
    static readonly HashSet<string> Bots = new(StringComparer.OrdinalIgnoreCase)
    {
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "soundalerts", "sery_bot",
        "pokemoncommunitygame", "kofistreambot", "commanderroot", "botrixoficial", "streamstickers", "blerp",
    };

    readonly double keep;
    readonly string streamer;
    readonly List<string> order = new();
    readonly Dictionary<string, (string name, double at)> seen = new(StringComparer.OrdinalIgnoreCase);

    public ChatterPool(double keepSeconds, string streamer) { keep = keepSeconds; this.streamer = streamer ?? ""; }

    public void Seen(string name, double now)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || Bots.Contains(name) || name.Equals(streamer, StringComparison.OrdinalIgnoreCase)) return;
        if (!seen.ContainsKey(name)) order.Add(name);
        seen[name] = (name, now);
    }

    IEnumerable<string> Active(double now) => order.Where(n => now - seen[n].at <= keep).Select(n => seen[n].name);

    public int Count(double now) => Active(now).Count();

    public string Pick(double now, ICollection<string> inUse, Func<int, int> random)
    {
        var active = Active(now).ToList();
        if (active.Count == 0) return null;
        var free = active.Where(n => !inUse.Any(u => u.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var from = free.Count > 0 ? free : active;
        return from[Math.Max(0, Math.Min(from.Count - 1, random(from.Count)))];
    }
}

/// <summary>
/// A raid party: chatters join while it forms ("!join"), once each, and the raid that comes is as big as the party:
/// a monster a raider (at least 2), up to the cap; past the cap the monsters get tougher instead.
/// </summary>
public sealed class RaidParty
{
    readonly int cap;
    readonly List<string> raiders = new();

    public RaidParty(int cap) { this.cap = Math.Max(2, cap); }

    public IReadOnlyList<string> Raiders => raiders;
    public int Count => raiders.Count;
    public int Monsters => Count == 0 ? 0 : Math.Min(cap, Math.Max(2, Count));
    public float Toughness => Count > cap ? Math.Min(3f, 1f + 0.05f * (Count - cap)) : 1f;

    public bool Join(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || raiders.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        raiders.Add(name);
        return true;
    }
}

/// <summary>The weapon roulette's next weapon type: any other type there is, never the one in hand ("": none).</summary>
public static class WeaponTypes
{
    public static string Next(string now, IReadOnlyList<string> types, Func<int, int> random)
    {
        var others = types.Distinct().Where(t => t != now).ToList();
        return others.Count == 0 ? "" : others[Math.Max(0, Math.Min(others.Count - 1, random(others.Count)))];
    }
}

/// <summary>A long list for Twitch chat as lines it takes (it cuts a message off at 500 characters): "Head: a, b, c",
/// as many lines as it needs, never an item split across two.</summary>
public static class ChatLines
{
    public static List<string> Pack(string head, IEnumerable<string> items, int max = 480)
    {
        var lines = new List<string>();
        string line = "";
        foreach (var item in items)
        {
            string next = line == "" ? $"{head} {item}" : $"{line}, {item}";
            if (next.Length > max && line != "") { lines.Add(line); next = $"{head} {item}"; }
            line = next;
        }
        if (line != "") lines.Add(line);
        return lines;
    }
}

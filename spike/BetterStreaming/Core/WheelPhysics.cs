using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BetterStreaming.Core;

/// <summary>How the wheel feels (angles in turns, speeds in turns a second).</summary>
public sealed class WheelSettings
{
    /// <summary>The wheel's own slowing (turns/s²), pegs or not.</summary>
    public float Friction = 0.2f;
    /// <summary>How far the flapper reaches over the pegs: a peg within this of it bends it.</summary>
    public float Reach = 0.012f;
    /// <summary>How stiff the flapper is: the harder, the more each peg costs and the sooner one throws the wheel back.</summary>
    public float Stiffness = 70f;
    /// <summary>How much of the wheel's speed a bending flapper soaks up (so a roll-back rocks, then settles).</summary>
    public float Damping = 4f;

    /// <summary>The slowest the wheel can be going and still bend the flapper all the way over (friction aside).</summary>
    public float Barrier => (float)Math.Sqrt(Stiffness) * Reach;
}

/// <summary>
/// The wheel, as a little physics toy (Wheel of Fortune's): slices as wide as their weights, a peg on each line between
/// them, and a rubber flapper at the pointer. The wheel slows by itself, and every peg has to bend the flapper over to
/// get past: that costs speed (the flapper's spring soaks it up and lets go), so the clicks come further and further
/// apart. A wheel that reaches a peg too slowly to bend the flapper all the way is pushed back by it, and settles in the
/// slice it was leaving. Nothing is picked in advance: where it lands comes from how hard it was spun, so a slice's
/// chance is about its width.
/// The pointer is at angle 0; Angle is the wheel's turn (a slice's place on screen is its start plus Angle).
/// </summary>
public sealed class WheelPhysics
{
    const float SubStep = 0.001f, RestSpeed = 0.002f, RestFor = 0.3f, MaxTime = 40f;

    readonly WheelSettings s;
    readonly bool[] slipped;   // (a peg the flapper has let go over: no push from it until it's clear of the flapper)
    float rested, time;

    /// <summary>Where each slice starts (turns, from the wheel's 0); the pegs are there too.</summary>
    public readonly float[] Starts;
    public float Angle;
    public float Omega;
    /// <summary>How many pegs the flapper has let go over (a click each).</summary>
    public int Clicks;
    public bool Done { get; private set; } = true;
    /// <summary>A peg it couldn't clear threw it back (the wheel turned round) this spin.</summary>
    public bool RolledBack { get; private set; }
    /// <summary>The flapper's bend for drawing: -1..1 (0: straight).</summary>
    public float Bend { get; private set; }

    public WheelPhysics(IReadOnlyList<float> weights, WheelSettings settings)
    {
        s = settings;
        float total = weights.Sum(w => Math.Max(0f, w));
        Starts = new float[weights.Count];
        float at = 0f;
        for (int i = 0; i < weights.Count; i++)
        {
            Starts[i] = at;
            at += total > 0f ? Math.Max(0f, weights[i]) / total : 1f / weights.Count;
        }
        slipped = new bool[weights.Count];
    }

    public float Width(int i) => (i + 1 < Starts.Length ? Starts[i + 1] : 1f) - Starts[i];

    /// <summary>The slice under the pointer.</summary>
    public int PointerSlice
    {
        get
        {
            float rel = Wrap01(-Angle);
            for (int i = Starts.Length - 1; i >= 0; i--) if (rel >= Starts[i]) return i;
            return 0;
        }
    }

    /// <summary>A spin that hard (turns a second).</summary>
    public void Spin(float omega)
    {
        Omega = omega;
        Done = false;
        RolledBack = false;
        rested = time = 0f;
    }

    /// <summary>A random spin's speed: about 4 to 6 seconds of spinning, a bit harder for a bigger cheer (size 1 = its
    /// price). roll is 0..1.</summary>
    public static float RandomSpeed(double roll, float size)
    {
        float boost = 1f + 0.12f * (float)Math.Log(Math.Max(1f, Math.Min(size, 20f)), 2);
        return (0.8f + 0.4f * (float)roll) * boost;
    }

    public void Step(float dt)
    {
        if (Done) return;
        for (float t = 0f; t < dt && !Done; t += SubStep) Tick(Math.Min(SubStep, dt - t));
    }

    void Tick(float dt)
    {
        time += dt;
        float force = 0f, bend = 0f;
        bool touching = false;
        for (int i = 0; i < Starts.Length; i++)
        {
            float rel = WrapHalf(Starts[i] + Angle);   // (the peg, from the pointer: negative coming up to it while turning forwards)
            float far = Math.Abs(rel);
            if (far > s.Reach) { slipped[i] = false; continue; }
            if (slipped[i]) continue;
            float dent = s.Reach - far;                          // (how far it has bent the flapper)
            float side = rel < 0f ? -1f : 1f;
            force += side * s.Stiffness * dent;                  // (the flapper pushes it back the way it came)
            bend = -side * dent / s.Reach;
            touching = true;
        }
        // the flapper only soaks up speed springing back (pushing the wheel the way the spring goes), so a roll-back
        // rocks and settles; bending it over costs only what the spring stores and keeps when it lets go
        if (touching && Math.Sign(force) == Math.Sign(Omega)) force -= s.Damping * Omega;
        float before = Omega;
        Omega += force * dt;
        if (before > 0f && Omega < 0f) RolledBack = true;
        // the wheel's own slowing: it never turns it round, only stops it
        float slow = s.Friction * dt;
        if (Math.Abs(Omega) <= slow && !touching) Omega = 0f;
        else if (Omega != 0f) Omega -= Math.Sign(Omega) * Math.Min(slow, Math.Abs(Omega));

        float oldAngle = Angle;
        Angle = Wrap01(Angle + Omega * dt);
        // a peg the flapper let go over: it crossed the pointer
        for (int i = 0; i < Starts.Length; i++)
        {
            float a = WrapHalf(Starts[i] + oldAngle), b = WrapHalf(Starts[i] + Angle);
            if (!slipped[i] && Math.Abs(a) <= s.Reach && Math.Abs(b) <= s.Reach && Math.Sign(a) != Math.Sign(b) && a != 0f)
            {
                slipped[i] = true;
                Clicks++;
            }
        }
        Bend = bend;
        // at rest: barely turning, and whatever the flapper still pushes the wheel's own friction holds
        rested = Math.Abs(Omega) < RestSpeed && Math.Abs(force) <= s.Friction ? rested + dt : 0f;
        if (rested >= RestFor || time >= MaxTime) { Done = true; Omega = 0f; Bend = 0f; }
    }

    static float Wrap01(float a) { a %= 1f; return a < 0f ? a + 1f : a; }
    static float WrapHalf(float a) { a = Wrap01(a); return a > 0.5f ? a - 1f : a; }
}

/// <summary>One slice: which effect, and how wide (its weight against the others').</summary>
public readonly struct WheelSlice
{
    public readonly string Id;
    public readonly float Weight;
    public WheelSlice(string id, float weight) { Id = id; Weight = weight; }
}

/// <summary>The streamer's slices, as the config has them: "heal:1, raid:1, death:0.35" (no weight: 1). Unknown
/// effects and broken weights are left out.</summary>
public static class WheelSlices
{
    /// <summary>"again" is a slice that spins once more.</summary>
    public const string Again = "again";

    public const string Default = "heal:1,raid:1,chainlightning:1,poison:1,loot:1,chickens:1,haste:1,freeze:1,shield:1,drunk:1,again:1,death:0.35";

    public static List<WheelSlice> Parse(string text, IEnumerable<string> known)
    {
        var ok = known == null ? null : new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var list = new List<WheelSlice>();
        foreach (var part in (text ?? "").Split(','))
        {
            var bits = part.Split(':');
            string id = bits[0].Trim().ToLowerInvariant();
            if (id.Length == 0 || (ok != null && !ok.Contains(id))) continue;
            float weight = 1f;
            if (bits.Length > 1 && !(float.TryParse(bits[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out weight) && weight > 0f)) continue;
            list.Add(new WheelSlice(id, weight));
        }
        return list;
    }
}

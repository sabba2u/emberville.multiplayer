using System.Collections.Generic;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Smooths a remote player's movement. Each packet carries the sender's clock; we play the remote
/// player back <see cref="Delay"/> seconds in the past and blend between the two real samples around
/// that moment, instead of chasing the newest packet (which jitters when packets bunch up or drop).
/// </summary>
internal class SnapshotBuffer
{
    public struct Snapshot
    {
        public double Time;     // sender's clock
        public Vector3 Position;
        public float Yaw;
    }

    public float Delay = 0.1f;            // ~2 packets at 20 Hz
    const float MaxExtrapolate = 0.15f;   // when packets stop, coast this long then hold still
    const int MaxSnapshots = 64;

    readonly List<Snapshot> snapshots = new List<Snapshot>();
    double clockOffset;   // our clock - sender clock, tracked at the fastest packet seen
    bool haveOffset;

    public int Count => snapshots.Count;
    public bool HasData => snapshots.Count > 0;

    public void Clear()
    {
        snapshots.Clear();
        haveOffset = false;
    }

    public void Add(double senderTime, Vector3 position, float yaw, double localNow)
    {
        // Unreliable packets can arrive out of order: drop anything older than what we have.
        if (snapshots.Count > 0 && senderTime <= snapshots[snapshots.Count - 1].Time) return;

        // The fastest packet has the least network delay, so track the smallest offset; drift up slowly
        // so a one-off lucky packet doesn't pin it forever.
        double offset = localNow - senderTime;
        if (!haveOffset || offset < clockOffset) { clockOffset = offset; haveOffset = true; }
        else clockOffset += (offset - clockOffset) * 0.01;

        snapshots.Add(new Snapshot { Time = senderTime, Position = position, Yaw = yaw });
        if (snapshots.Count > MaxSnapshots) snapshots.RemoveAt(0);
    }

    /// <summary>Where the remote player was <see cref="Delay"/> seconds ago, plus its speed (units/s).</summary>
    public bool Sample(double localNow, out Vector3 position, out float yaw, out Vector3 velocity)
    {
        position = default; yaw = 0; velocity = default;
        if (snapshots.Count == 0) return false;

        double renderTime = localNow - clockOffset - Delay;

        // Drop samples we'll never need again (keep the one just before renderTime).
        while (snapshots.Count > 2 && snapshots[1].Time <= renderTime) snapshots.RemoveAt(0);

        var a = snapshots[0];
        if (snapshots.Count == 1 || renderTime <= a.Time)
        {
            position = a.Position; yaw = a.Yaw;
            return true;
        }

        var b = snapshots[1];
        float span = (float)(b.Time - a.Time);
        velocity = span > 1e-4f ? (b.Position - a.Position) / span : Vector3.zero;   // 2D top-down: x and y both count

        if (renderTime <= b.Time)
        {
            float t = (float)((renderTime - a.Time) / span);
            position = Vector3.Lerp(a.Position, b.Position, t);
            yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
        }
        else
        {
            // Past the newest sample: coast briefly along the last velocity, then stop.
            float over = Mathf.Min((float)(renderTime - b.Time), MaxExtrapolate);
            position = b.Position + velocity * over;
            yaw = b.Yaw;
            if (renderTime - b.Time > MaxExtrapolate) velocity = Vector3.zero;
        }
        return true;
    }
}

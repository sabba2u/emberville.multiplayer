using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Everyone's Better Companion for everyone in the area. The companion's brain and body run on its owner's copy
/// only; ~10x a second that copy sends CompanionState (position, facing, health, downed, animators) and, when it
/// changes, CompanionLook (LookSync: hair, gear, colours, name). Every other copy keeps a stand-in for it (a stripped
/// copy of our character, as for teammates: RemoteAvatar), gliding to where the real one is, with its name and health
/// over it; it goes when its owner stops sending (dismissed, or left the area).
/// Creatures fight a companion on its owner's copy: the enemy handover counts a companion as its owner (EnemySync),
/// so a creature next to someone's companion runs on their game, whose Aggro points it at the companion. A teammate's
/// downed companion: hold F at it for 2 s and its owner's game gets it up (CompanionRevive).
/// Only called through Integrations (HasCompanion).
/// </summary>
internal static class CompanionSync
{
    public static Plugin Plugin;
    const float SendEvery = 0.1f, LookEvery = 2f, StaleAfter = 1.5f, ReviveReach = 1.5f, ReviveHold = 2f;

    static bool Online => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static float nextSend, nextLook;
    static bool sentOut;
    static byte[] lastLook;

    // ---- ours, out ----

    static void Send()
    {
        if (!Online || Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + SendEvery;
        var root = BetterCompanion.Plugin.CompanionRoot;
        if (!root)
        {
            if (sentOut) { var gone = new NetDataWriter(); gone.Put((byte)MsgType.CompanionState); gone.Put(false); Plugin.SendToPlace(gone, DeliveryMethod.ReliableOrdered); }
            sentOut = false;
            lastLook = null;
            return;
        }
        if (!Plugin.AnyoneElseHere) { sentOut = false; lastLook = null; return; }
        if (Time.unscaledTime >= nextLook || !sentOut)
        {
            nextLook = Time.unscaledTime + LookEvery;
            var look = LookSync.Capture(root, BetterCompanion.Plugin.CompanionName);
            if (lastLook == null || !look.SequenceEqual(lastLook))
            {
                lastLook = look;
                var lw = new NetDataWriter();
                lw.Put((byte)MsgType.CompanionLook);
                lw.PutBytesWithLength(look);
                Plugin.SendToPlace(lw, DeliveryMethod.ReliableOrdered);
            }
        }
        sentOut = true;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.CompanionState);
        w.Put(true);
        var p = root.transform.position;
        w.Put(p.x); w.Put(p.y);
        w.Put(BetterCompanion.Plugin.CompanionFlipped);
        w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(BetterCompanion.Plugin.CompanionHealth) * 255f));
        w.Put(BetterCompanion.Plugin.CompanionDown);
        AnimSync.Write(w, BetterCompanion.Plugin.CompanionAnimators ?? new Animator[0]);
        Plugin.SendToPlace(w, DeliveryMethod.Unreliable);
    }

    // ---- theirs, in ----

    sealed class Shown
    {
        public RemoteAvatar Avatar;
        public Vector3 To;
        public bool Flip, Down;
        public float Health = 1f, HeardAt, Held;
        public AnimSync.Frame[] Anim;
        public byte[] Look;
        public string Name = "";
    }

    static readonly Dictionary<int, Shown> theirs = new();

    public static void OnState(NetDataReader r, int from)
    {
        if (!r.GetBool()) { Drop(from); return; }
        if (!theirs.TryGetValue(from, out var s)) theirs[from] = s = new Shown();
        s.To = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        s.Flip = r.GetBool();
        s.Health = r.GetByte() / 255f;
        s.Down = r.GetBool();
        s.Anim = AnimSync.Read(r);
        s.HeardAt = Time.unscaledTime;
    }

    public static void OnLook(NetDataReader r, int from)
    {
        if (!theirs.TryGetValue(from, out var s)) theirs[from] = s = new Shown { HeardAt = Time.unscaledTime };
        s.Look = r.GetBytesWithLength();
        s.Name = LookSync.ReadName(s.Look) ?? "";
        s.Avatar?.ApplyLook(s.Look);
    }

    /// <summary>Owner: a teammate got our companion up.</summary>
    public static void OnRevive(int from)
    {
        BetterCompanion.Plugin.Revived();
        NetLog.Info($"Companion: revived by {Plugin.NameOf(from)}");
    }

    static void Drop(int from)
    {
        if (theirs.TryGetValue(from, out var s)) s.Avatar?.Destroy();
        theirs.Remove(from);
    }

    /// <summary>Every frame: send ours; move theirs; F held at a downed one of theirs revives it.</summary>
    public static void Tick()
    {
        Send();
        if (theirs.Count == 0) return;
        var me = LocalPlayer.Get();
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
        foreach (var id in theirs.Keys.ToList())
        {
            var s = theirs[id];
            if (now - s.HeardAt > StaleAfter || s.Anim == null) { if (now - s.HeardAt > StaleAfter) Drop(id); continue; }
            if (s.Avatar == null || s.Avatar.Lost)
            {
                if (!me) continue;
                s.Avatar = RemoteAvatar.Create(me, id);
                s.Avatar.Root.name = $"EmbervilleGhost_Companion_P{id + 1}";
                foreach (var t in s.Avatar.Root.GetComponentsInChildren<StandInTarget>(true)) Object.Destroy(t);   // (hits on it aren't its owner's)
                foreach (var c in s.Avatar.Root.GetComponentsInChildren<Collider2D>(true)) Object.Destroy(c);
                BetterCompanion.CompanionMarks.Ring(s.Avatar.Root.transform);   // (told apart from the creatures, as ours)
                if (s.Look != null) s.Avatar.ApplyLook(s.Look);
                s.Avatar.Apply(s.To, s.Flip, s.Anim);
            }
            var at = s.Avatar.Root.transform.position;
            var next = Vector2.Distance(at, s.To) > 4f ? s.To : Vector3.MoveTowards(at, s.To, Mathf.Max(3f, Vector2.Distance(at, s.To) * 10f) * dt);
            s.Avatar.Apply(next, s.Flip, s.Anim);
            Revive(s, id, me, dt);
        }
    }

    static void Revive(Shown s, int owner, PlayerMaster me, float dt)
    {
        if (!s.Down || !me || me.IsDead) { s.Held = 0f; return; }
        bool holding = Vector2.Distance(me.transform.position, s.To) <= ReviveReach && MainInput.Actions.Shared.Interaction.IsPressed()
                       && !ImGuiUnity.Typing && !Chat.IsOpen;
        s.Held = holding ? s.Held + dt : 0f;
        if (s.Held < ReviveHold) return;
        s.Held = 0f;
        s.Down = false;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.CompanionRevive);
        Plugin.SendTo(owner, w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>A teammate's downed companion within reach of there.</summary>
    public static bool DownedNear(Vector3 at, float reach)
    {
        foreach (var s in theirs.Values)
            if (s.Down && Time.unscaledTime - s.HeardAt <= StaleAfter && Vector2.Distance(at, s.To) <= reach) return true;
        return false;
    }

    /// <summary>Where each player's companion is (ours too), for the enemy handover.</summary>
    public static IEnumerable<(int id, Vector3 pos)> Positions()
    {
        var root = BetterCompanion.Plugin.CompanionRoot;
        if (root && Plugin != null && !BetterCompanion.Plugin.CompanionDown) yield return (Plugin.MyId, root.transform.position);
        foreach (var kv in theirs)
            if (!kv.Value.Down && Time.unscaledTime - kv.Value.HeardAt <= StaleAfter) yield return (kv.Key, kv.Value.To);
    }

    /// <summary>Their companions' name tags and health bars, as ours (downed: "hold F" and how far the revive has got).</summary>
    public static void Draw()
    {
        if (theirs.Count == 0) return;
        var cam = Camera.main;
        if (!cam) return;
        foreach (var s in theirs.Values)
            if (s.Avatar != null && !s.Avatar.Lost)
                BetterCompanion.CompanionMarks.Tag(cam, s.Avatar.Root.transform.position, s.Name, s.Health, s.Down, s.Held / ReviveHold);
    }

    public static void Clear()
    {
        foreach (var s in theirs.Values) s.Avatar?.Destroy();
        theirs.Clear();
        sentOut = false;
        lastLook = null;
    }
}

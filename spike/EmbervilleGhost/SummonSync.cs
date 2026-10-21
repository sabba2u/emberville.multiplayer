using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using BetterPetsAndSummons;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The staffs' summons for everyone in the area: the Staff of Clucking's chickens and the Staff of the Risen's risen.
/// Their brains run on the summoner's copy only (they pick targets, peck, get swatted); ~10x a second it sends the
/// list (Summons: id, chicken or risen, creature, position, size, health, attack count), and every other copy keeps a
/// look-alike for each (our pet stand-in, PetBody): it glides to where the real one is, plays its attack when the count
/// goes up, shows its health bar, and goes when it's gone from the list. Their hits need nothing here: they're the
/// summoner's own hits, synced already.
/// </summary>
internal static class SummonSync
{
    public static Plugin Plugin;
    const float SendEvery = 0.1f, StaleAfter = 1.5f;

    static bool Online => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;
    static float nextSend;
    static int lastSent;

    static readonly Color Ghost = new Color(0.55f, 1f, 0.6f, 0.85f);

    // ---- ours, out ----

    static void Send()
    {
        if (!Online || Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + SendEvery;
        var list = new List<Summoned.Summon>(Summoned.All());
        if (list.Count == 0 && lastSent == 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.Summons);
        w.Put((byte)Mathf.Min(list.Count, 40));
        for (int i = 0; i < list.Count && i < 40; i++)
        {
            var s = list[i];
            w.Put(s.Id);
            w.Put(s.Risen);
            w.Put(s.Prefab ?? "");
            w.Put(s.At.x); w.Put(s.At.y);
            w.Put(s.Size);
            w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(s.Health) * 255f));
            w.Put(s.Attacks);
        }
        // the last one (an empty list: they're all gone) must arrive; the rest can drop now and then
        Plugin.SendToPlace(w, list.Count == 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        lastSent = list.Count;
    }

    // ---- theirs, in ----

    sealed class Shown
    {
        public PetBody Body;
        public bool Risen;
        public string Prefab;
        public Vector3 To;
        public float Health = 1f, HeardAt;
        public byte Attacks;
    }

    static readonly Dictionary<int, Dictionary<ushort, Shown>> theirs = new Dictionary<int, Dictionary<ushort, Shown>>();

    public static void OnSummons(NetDataReader r, int from)
    {
        if (!theirs.TryGetValue(from, out var mine)) theirs[from] = mine = new Dictionary<ushort, Shown>();
        int n = r.GetByte();
        var seen = new HashSet<ushort>();
        float now = Time.unscaledTime;
        for (int i = 0; i < n; i++)
        {
            ushort id = r.GetUShort();
            bool risen = r.GetBool();
            string prefab = r.GetString();
            var at = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            float size = r.GetFloat();
            float health = r.GetByte() / 255f;
            byte attacks = r.GetByte();
            seen.Add(id);
            if (!mine.TryGetValue(id, out var s) || s.Body == null || !s.Body.Alive || s.Prefab != prefab)
            {
                s?.Body?.Destroy();
                var kind = risen ? new PetKind("risen_" + prefab, prefab, prefab, prefab) : Pet.Kinds[0];
                if (!PetBody.Available(kind) && !PetBody.Learn(kind)) continue;   // that creature isn't loaded here
                var body = PetBody.Create(kind, at);
                if (body == null) continue;
                body.Scale(size);
                if (risen) body.SetTint(Ghost);
                mine[id] = s = new Shown { Body = body, Risen = risen, Prefab = prefab, To = at, Attacks = attacks };
            }
            s.To = at;
            s.Health = health;
            s.HeardAt = now;
            if (attacks != s.Attacks) { s.Attacks = attacks; s.Body.Attack(); }
        }
        var gone = new List<ushort>();
        foreach (var kv in mine) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone) { Poof(mine[id]); mine.Remove(id); }
    }

    static void Poof(Shown s)
    {
        if (s.Body == null || !s.Body.Alive) return;
        Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, s.Risen ? "crumbles" : "poof", s.Body.Position, DamageResult.Parry));
        s.Body.Destroy();
    }

    /// <summary>Every frame: send ours; move theirs toward where the real ones are.</summary>
    public static void Tick()
    {
        Send();
        if (theirs.Count == 0) return;
        float now = Time.unscaledTime, dt = Time.deltaTime;
        foreach (var kv in theirs)
        {
            List<ushort> stale = null;
            foreach (var s in kv.Value)
            {
                var v = s.Value;
                if (v.Body == null || !v.Body.Alive || now - v.HeardAt > StaleAfter) { (stale ??= new List<ushort>()).Add(s.Key); continue; }
                float far = ((Vector2)(v.To - v.Body.Position)).magnitude;
                if (far > 4f) v.Body.Teleport(v.To);
                else if (far > 0.05f) v.Body.MoveToward(v.To, Mathf.Max(3f, far * 10f), dt, 0.02f);   // updates come 10x a second: glide between them
                else v.Body.Stop(v.To);   // standing (keeps facing the way it last moved)
                v.Body.Hover();
            }
            if (stale != null) foreach (var id in stale) { kv.Value[id].Body?.Destroy(); kv.Value.Remove(id); }
        }
    }

    /// <summary>Their summons' health bars (yellow chickens, green risen; like ours).</summary>
    public static void Draw()
    {
        if (theirs.Count == 0) return;
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        foreach (var kv in theirs)
            foreach (var s in kv.Value.Values)
            {
                if (s.Body == null || !s.Body.Alive) continue;
                var p = cam.WorldToScreenPoint(s.Body.Position + Vector3.up * (s.Risen ? 1.4f : 1f));
                if (p.z < 0) continue;
                float x = p.x, y = Screen.height - p.y, w = s.Risen ? 50f : 28f, h = s.Risen ? 6f : 4f;
                var fill = s.Risen ? new ImGuiNET.Vector4(0.25f, 0.85f, 0.35f, 1f) : new ImGuiNET.Vector4(1f, 0.85f, 0.3f, 1f);
                dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2 - 1, y - 1), new ImGuiNET.Vector2(x + w / 2 + 1, y + h + 1), ImGuiNET.ImGui.ColorConvertFloat4ToU32(new ImGuiNET.Vector4(0f, 0f, 0f, 0.7f)));
                dl.AddRectFilled(new ImGuiNET.Vector2(x - w / 2, y), new ImGuiNET.Vector2(x - w / 2 + w * s.Health, y + h), ImGuiNET.ImGui.ColorConvertFloat4ToU32(fill));
            }
    }

    public static void Clear()
    {
        foreach (var mine in theirs.Values) foreach (var s in mine.Values) s.Body?.Destroy();
        theirs.Clear();
    }
}

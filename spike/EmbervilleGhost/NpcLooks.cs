using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Everyone sees the host's NPCs (Better NPCs and Quests' Ellie and the rest, and Rory) the same. Each game builds its
/// NPCs as copies of its own player's character, and a character's colours (hair, skin, clothes) live on materials
/// every copy shares, while body type, beard and face come from whoever is copied: so on a guest's game the NPCs
/// wore the guest's looks. The host captures each NPC's look (LookSync: which parts are on, the materials' colours)
/// and sends it to everyone (on a change, and to each player who joins); a guest gives that NPC its own material
/// copies and puts the host's look on it.
/// </summary>
internal static class NpcLooks
{
    public static Plugin Plugin;
    const string RoryId = "~rory";
    const float Every = 2f;

    static readonly Dictionary<string, byte[]> captured = new();   // host: last look sent, by NPC id
    static readonly Dictionary<string, byte[]> received = new();   // guest: the host's looks, by NPC id
    static readonly Dictionary<int, (byte[] look, Dictionary<string, Material> mats)> applied = new();   // guest: by body (instance id)
    static readonly HashSet<int> sentTo = new();
    static float next;

    public static void Tick()
    {
        if (Plugin == null || !Plugin.Connected) { Forget(); return; }
        if (Plugin.IsHost) HostTick();
        else GuestTick();
    }

    static void Forget()
    {
        captured.Clear(); received.Clear(); sentTo.Clear();
        applied.Clear();   // (bodies keep their material copies: they're the guest's NPCs as last seen)
    }

    // ---- host ----

    static void HostTick()
    {
        received.Clear();
        if (Time.unscaledTime >= next)
        {
            next = Time.unscaledTime + Every;
            bool changed = false;
            foreach (var (id, root) in Bodies())
            {
                var look = LookSync.Capture(root, id);
                if (captured.TryGetValue(id, out var old) && old.SequenceEqual(look)) continue;
                captured[id] = look;
                changed = true;
            }
            if (changed && captured.Count > 0) Plugin.SendToAll(Message(), DeliveryMethod.ReliableOrdered);
        }
        var ids = Plugin.Players.Select(p => p.Id).ToList();
        sentTo.RemoveWhere(id => !ids.Contains(id));
        if (captured.Count == 0) return;
        foreach (var id in ids) if (sentTo.Add(id)) Plugin.SendTo(id, Message());
    }

    static NetDataWriter Message()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.NpcLook);
        w.Put(captured.Count);
        foreach (var kv in captured) { w.Put(kv.Key); w.PutBytesWithLength(kv.Value); }
        return w;
    }

    // ---- guest ----

    public static void OnLooks(NetDataReader r)
    {
        int count = r.GetInt();
        for (int i = 0; i < count; i++) received[r.GetString()] = r.GetBytesWithLength();
    }

    static void GuestTick()
    {
        if (received.Count == 0) return;
        foreach (var (id, root) in Bodies())
        {
            if (!received.TryGetValue(id, out var look)) continue;
            int key = root.GetInstanceID();
            applied.TryGetValue(key, out var was);
            if (was.look == look) continue;
            var mats = was.mats ?? LookSync.OwnMaterials(root);
            // (the name label is shown and hidden by the NPC's own mod, near or far: not the host's)
            var labels = root.GetComponentsInChildren<Transform>(true).Where(t => t.name == "BNQ_Name").Select(t => (t.gameObject, t.gameObject.activeSelf)).ToList();
            LookSync.Apply(root, mats, look);
            foreach (var (go, on) in labels) if (go) go.SetActive(on);
            applied[key] = (look, mats);
        }
        if (applied.Count > 32) applied.Clear();   // (old bodies from earlier scenes)
    }

    // ---- the NPCs here ----

    static IEnumerable<(string id, GameObject root)> Bodies()
    {
        if (Rory.Root) yield return (RoryId, Rory.Root);
        if (Integrations.HasNpcs) foreach (var b in NpcBodies()) yield return b;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<(string, GameObject)> NpcBodies() => BetterNPCsAndQuests.Plugin.Bodies().ToList();
}

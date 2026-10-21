using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Better NPCs and Quests in co-op: the host's files (NPCs, dialogue, quests, rules, portraits) go to each player who
/// joins (they use them while connected, nothing is written to their folder); the host's story (flags, quest progress)
/// goes to everyone after each change; guests' actions, turn-ins and events go to the host; the host's NPCs' walks go
/// to the players in that place. Only touched when the mod is installed (Integrations.HasNpcs); every method that names
/// the mod's types is NoInlining.
/// </summary>
internal static class NpcSync
{
    public static Plugin Plugin;
    static readonly HashSet<int> sentTo = new();
    static bool wasClient;

    static bool Up => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Tick()
    {
        if (Plugin == null) return;
        bool client = Plugin.IsClient;
        if (wasClient && !client) BetterNPCsAndQuests.Plugin.Disconnected();
        wasClient = client;
        if (!Plugin.IsHost) { sentTo.Clear(); return; }
        // the host: everything to each player who just joined
        var ids = Plugin.Players.Select(p => p.Id).ToList();
        sentTo.RemoveWhere(id => !ids.Contains(id));
        foreach (var id in ids)
        {
            if (!sentTo.Add(id)) continue;
            Plugin.SendTo(id, FilesMessage());
            Plugin.SendTo(id, BookMessage(BetterNPCsAndQuests.Plugin.BookText()));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static NetDataWriter FilesMessage()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.NpcFiles);
        w.PutBytesWithLength(BetterNPCsAndQuests.Plugin.FilesPacked());
        return w;
    }

    static NetDataWriter BookMessage(string text)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.NpcBook);
        w.PutBytesWithLength(Encoding.UTF8.GetBytes(text ?? ""));
        return w;
    }

    /// <summary>The host read its files again: everyone gets the new set.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendFilesToAll()
    {
        if (!Up || !Plugin.IsHost) return;
        Plugin.SendToAll(FilesMessage(), DeliveryMethod.ReliableOrdered);
    }

    // ---- hooks the mod calls ----

    public static void SendBook(string text)
    {
        if (!Up || !Plugin.IsHost) return;
        Plugin.SendToAll(BookMessage(text), DeliveryMethod.ReliableOrdered);
    }

    public static void Ask(string text)
    {
        if (!Up || Plugin.IsHost) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.NpcAsk);
        w.Put(text ?? "");
        Plugin.SendTo(0, w);
    }

    public static void SendWander(string npc, Vector3 to)
    {
        if (!Up || !Plugin.IsHost) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.NpcWander);
        w.Put(npc ?? "");
        w.Put(to.x);
        w.Put(to.y);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    public static bool OthersNear(Vector3 at, float distance)
    {
        if (Plugin == null || !Plugin.Connected) return false;
        string scene = LocalPlayer.Scene;
        return Plugin.Players.Any(p => p.Scene == scene && ((Vector2)(p.LastPosition - at)).sqrMagnitude < distance * distance);
    }

    // ---- messages in ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnFiles(NetDataReader r) => BetterNPCsAndQuests.Plugin.OnFiles(r.GetBytesWithLength());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnBook(NetDataReader r) => BetterNPCsAndQuests.Plugin.OnBook(Encoding.UTF8.GetString(r.GetBytesWithLength()));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnAsk(NetDataReader r) => BetterNPCsAndQuests.Plugin.OnAsk(r.GetString());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OnWander(NetDataReader r)
    {
        string id = r.GetString();
        float x = r.GetFloat(), y = r.GetFloat();
        BetterNPCsAndQuests.Plugin.OnWander(id, new Vector3(x, y, 0f));
    }
}

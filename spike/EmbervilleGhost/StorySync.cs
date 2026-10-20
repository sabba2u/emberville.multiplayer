using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleGhost;

/// <summary>
/// Story progress is shared ("unlock for everyone", todo #5): the world's story flags (GameData.gameBools: BoolID
/// cutscenes seen, bosses defeated, people met, panels / stores / building unlocked...) are one set for the whole
/// session. Whenever one changes on a player's copy (the game sets them from cutscenes, dialogue, quests, boss deaths)
/// it goes to everyone, who set it too, through the same dictionary, so whatever listens to it reacts. The host's
/// world save keeps what guests achieved, and a door or gate one player opened stays open for all.
///
/// The game swaps the whole dictionary when a world loads (GameData.LoadData), so we re-attach to the current one.
/// Newcomers get the flags with the host's world (GuestWorld).
/// </summary>
internal static class StorySync
{
    public static Plugin Plugin;

    static HookedDictionary<BoolID, bool> watched;
    static bool applying;

    /// <summary>Every frame: listen to the flags of the world that's loaded now.</summary>
    public static void Tick()
    {
        var flags = GameData.gameBools;
        if (ReferenceEquals(flags, watched)) return;
        if (watched != null) changedEvent.RemoveEventHandler(watched, handler);
        watched = flags;
        if (watched != null) changedEvent.AddEventHandler(watched, handler);
    }

    // Through reflection: the publicizer exposes the event's backing field under the same name (ambiguous in C#).
    static readonly System.Reflection.EventInfo changedEvent = typeof(HookedDictionary<BoolID, bool>).GetEvent("OnValueChanged");
    static readonly System.Action<BoolID, bool> handler = Changed;

    static void Changed(BoolID id, bool value)
    {
        if (applying || Plugin == null || !Plugin.Connected || Plugin.MyId < 0 || !LocalPlayer.Get()) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.StoryFlag);
        w.Put((int)id);
        w.Put(value);
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
        NetLog.Info($"Story: {id} = {value} (for everyone)");
    }

    public static void OnFlag(NetDataReader r, int from)
    {
        var id = (BoolID)r.GetInt();
        bool value = r.GetBool();
        if (!GameData.HasWorldData) return;
        if (GameData.gameBools.TryGetValue(id, out bool have) && have == value) return;
        applying = true;
        try { GameData.gameBools[id] = value; }
        finally { applying = false; }
        NetLog.Info($"Story: {id} = {value} ({Plugin.NameOf(from)})");
    }
}

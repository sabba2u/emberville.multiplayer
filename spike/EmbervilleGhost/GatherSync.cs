using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Rocks, ore, trees, herbs (GatheringObject), run by the area's owner.
///
/// How the game gathers: each swing of the player's gather animation calls OnHitCallback (via
/// PlayerMaster.OnGatherActionHit): health - 1, shake, a few drops; at 0 the rock breaks (FinishObject: art off,
/// rubble on, final drops, life-skill XP), a tree falls first (async animation) and becomes a stump (Residual) that
/// takes more hits.
///
/// Puppet: our swing still plays, but the hit is a request (GatherHit) and nothing changes locally until the owner
/// answers. Owner: applies it with the game's own OnHitCallback (its drops become synced drops, DropSync) and
/// broadcasts GatherDamaged; puppets replay it through OnHitCallback (shake, break, tree fall) with drops blocked.
/// Life-skill XP only goes to whoever swung. Everyone remembers which ones were hit, so whoever runs the area next can
/// tell newcomers (GatherState: health + type, applied quietly).
/// </summary>
[HarmonyPatch]
internal static class GatherSync
{
    public static Plugin Plugin;

    static bool replaying;                // puppet: replaying the owner's result
    static int applyingFor = -1;          // owner: applying this player's swing
    static int replayGatherer = -1;       // puppet: whose swing we're replaying
    static readonly Dictionary<GatheringObject, int> xpFor = new Dictionary<GatheringObject, int>();   // who gets the XP when it finishes
    static readonly HashSet<uint> touched = new HashSet<uint>();   // hit in this scene (for arrival snapshots / handover)
    static string touchedScene = "";
    static bool noXp;

    static bool Puppet => EnemySync.PuppetMode;

    public static void Clear()
    {
        xpFor.Clear(); touched.Clear();
        touchedScene = "";
        replaying = noXp = false;
        applyingFor = replayGatherer = -1;
    }

    static void Touch(uint id)
    {
        if (LocalPlayer.Scene != touchedScene) { touched.Clear(); touchedScene = LocalPlayer.Scene; }
        touched.Add(id);
    }

    /// <summary>The game only sets this when the local player walks up to it; replays and remote swings need one.</summary>
    static void EnsurePlayer(GatheringObject g)
    {
        if (!g.player) g.player = LocalPlayer.Get();
    }

    // ---- owner ----

    /// <summary>Owner: another player swung at one of our rocks/trees.</summary>
    public static void OnHit(NetDataReader r, int fromId)
    {
        var g = SceneIds.Find<GatheringObject>(r.GetUInt());
        if (!g || !g.isActive || g.currentHealth <= 0 || !g.player && !LocalPlayer.Get()) return;
        EnsurePlayer(g);
        applyingFor = fromId;
        try { g.OnHitCallback(); }
        finally { applyingFor = -1; }
    }

    /// <summary>Owner: rocks and trees already hit here, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (touched.Count == 0 || touchedScene != LocalPlayer.Scene) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.GatherState);
        var list = new List<GatheringObject>();
        foreach (uint id in touched) { var g = SceneIds.Find<GatheringObject>(id); if (g) list.Add(g); }
        w.Put(list.Count);
        foreach (var g in list)
        {
            w.Put(SceneIds.Of(g));
            w.Put(g.currentHealth);
            w.Put((byte)g.gatherType);
        }
        Plugin.SendTo(playerId, w);
    }

    // ---- puppet ----

    public static void OnDamaged(NetDataReader r)
    {
        uint id = r.GetUInt();
        int healthBefore = r.GetInt();
        var type = (GatheringObject.GatherType)r.GetByte();
        int gatherer = r.GetInt();
        var g = SceneIds.Find<GatheringObject>(id);
        if (!g || !LocalPlayer.Get()) return;
        EnsurePlayer(g);
        g.currentHealth = healthBefore;
        g.gatherType = type;
        g.isActive = healthBefore > 0;
        replaying = true;
        replayGatherer = gatherer;
        try { g.OnHitCallback(); }
        finally { replaying = false; replayGatherer = -1; }
    }

    /// <summary>Puppet: these were hit before we came; set them to how they are now, without effects or drops.</summary>
    public static void OnState(NetDataReader r)
    {
        int n = r.GetInt();
        for (int i = 0; i < n; i++)
        {
            uint id = r.GetUInt();
            int health = r.GetInt();
            var type = (GatheringObject.GatherType)r.GetByte();
            Touch(id);
            var g = SceneIds.Find<GatheringObject>(id);
            if (!g) continue;
            g.currentHealth = health;
            g.gatherType = type;
            g.isActive = health > 0;
            bool residual = type == GatheringObject.GatherType.Residual;
            if (health <= 0 || residual)
                foreach (var art in g.artToHide) if (art) art.SetActive(false);
            if (g.artResidual) g.artResidual.SetActive(health <= 0 ? !residual : residual);
            if (g.isTree && g.artRemovable && (health <= 0 || residual)) g.artRemovable.transform.localRotation = Quaternion.identity;
        }
    }

    // ---- patches ----

    /// <summary>
    /// Puppet: our swing is a request, nothing changes until the owner answers.
    /// Everyone: remember who gets the XP when it finishes (the tree's finish comes later, after its fall).
    /// </summary>
    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.OnHitCallback)), HarmonyPrefix]
    static bool BeforeHit(GatheringObject __instance, out int __state)
    {
        __state = __instance.currentHealth;
        if (Puppet && !replaying)
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.GatherHit);
            w.Put(SceneIds.Of(__instance));
            Plugin.SendToOwner(w, DeliveryMethod.ReliableOrdered);
            return false;
        }
        xpFor[__instance] = replaying ? replayGatherer : applyingFor >= 0 ? applyingFor : Plugin == null ? 0 : Plugin.MyId;
        return true;
    }

    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.OnHitCallback)), HarmonyPostfix]
    static void AfterHit(GatheringObject __instance, int __state, bool __runOriginal)
    {
        if (!__runOriginal || Plugin == null || !Plugin.Connected) return;
        uint id = SceneIds.Of(__instance);
        Touch(id);
        if (Puppet || !Plugin.AnyoneElseHere) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.GatherDamaged);
        w.Put(id);
        w.Put(__state);
        w.Put((byte)__instance.gatherType);
        w.Put(applyingFor >= 0 ? applyingFor : Plugin.MyId);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Puppet: drops only happen on the owner (they arrive as synced drops).</summary>
    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.DropItems)), HarmonyPrefix]
    static bool BeforeDrops() => !Puppet;

    /// <summary>Life-skill XP (mining, woodcutting, herbalism) only for whoever swung.</summary>
    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.FinishObject)), HarmonyPrefix]
    static void BeforeFinish(GatheringObject __instance)
    {
        if (Plugin == null || !Plugin.Connected) return;
        noXp = xpFor.TryGetValue(__instance, out int who) && who != Plugin.MyId;
        xpFor.Remove(__instance);
    }

    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.FinishObject)), HarmonyFinalizer]
    static void AfterFinish() => noXp = false;

    [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.AddXP)), HarmonyPrefix]
    static bool BeforeAddXp() => !noXp;
}

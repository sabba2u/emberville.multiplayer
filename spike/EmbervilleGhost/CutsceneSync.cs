using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Story cutscenes play for everyone in the area. The game starts each one from its own entry point (Play, StartIntro,
/// ReleaseTinetia...), called by a trigger only the local player can set off, and the cutscene works on the local
/// player (freezes them, turns them, moves the camera) and changes the world on that copy only (the spider door opens,
/// the HummingSphere gate, Tinetia's cage...). So when ours starts one, the others in the area start the same one
/// (CutsceneStart): each watches it in their own game, and the world ends up the same.
/// If both players set it off together, it plays once: a cutscene that just came from a teammate isn't started again
/// by our own trigger for a minute (after that it can replay, e.g. a boss intro after a wipe).
/// The game's own intro (IntroCutscene) and the boss arenas (their own triggers per player) aren't in the list.
///
/// Arriving later: the area owner sends which ones already played here (CutsceneSnapshot), and the newcomer's game
/// skips straight to how each one leaves the world (Ending: the spider door open, Tinetia out of her cage, the
/// HummingSphere's finish events...), the way the game itself skips a cutscene that's done when you load in; no
/// camera, no freeze. Its trigger then stays quiet for this visit.
/// </summary>
[HarmonyPatch]
internal static class CutsceneSync
{
    public static Plugin Plugin;
    const float SkipOwnFor = 60f;

    /// <summary>The entry points we play for everyone (index = what goes over the wire).</summary>
    static readonly (Type type, string method)[] Entries =
    {
        (typeof(SpiderDoorCutscene), nameof(SpiderDoorCutscene.Play)),
        (typeof(KaldhorsIntro), nameof(KaldhorsIntro.StartIntro)),
        (typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.ReleaseTinetia)),
        (typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.StartGuideCutscene)),
        (typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.StartActivePortalCutscene)),
        (typeof(AdeosCutscene), nameof(AdeosCutscene.Play)),
        (typeof(HummingSphereCutscene), nameof(HummingSphereCutscene.FinishCutscene)),
    };

    static int mirroring;
    static readonly Dictionary<(int, uint), float> fromTeammate = new Dictionary<(int, uint), float>();
    static readonly List<(int index, uint id)> played = new List<(int, uint)>();   // in this scene, in order (for arrivals)
    static readonly HashSet<(int, uint)> ended = new HashSet<(int, uint)>();      // skipped to the end here: our trigger stays quiet
    static bool Connected => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var (type, method) in Entries)
        {
            var m = AccessTools.Method(type, method, Type.EmptyTypes);
            if (m != null) yield return m;
        }
    }

    static int IndexOf(MethodBase m)
    {
        for (int i = 0; i < Entries.Length; i++)
            if (Entries[i].type == m.DeclaringType && Entries[i].method == m.Name) return i;
        return -1;
    }

    static bool Prefix(Component __instance, MethodBase __originalMethod)
    {
        if (mirroring > 0 || !Connected) return true;
        int index = IndexOf(__originalMethod);
        if (index < 0) return true;
        uint id = SceneIds.Of(__instance);
        if (ended.Contains((index, id))) return false;   // it already played before we came: we skipped to its end
        if (fromTeammate.TryGetValue((index, id), out float at) && Time.unscaledTime - at < SkipOwnFor)
        {
            NetLog.Info($"Cutscene {__instance.name}.{__originalMethod.Name}: already playing (a teammate started it)");
            return false;
        }
        Played(index, id);
        DoorSync.CutsceneDoors(__instance, ours: true);
        if (!Plugin.AnyoneElseHere) return true;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.CutsceneStart);
        w.Put((byte)index);
        w.Put(id);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        NetLog.Info($"Cutscene {__instance.name}.{__originalMethod.Name}: playing it for everyone here");
        return true;
    }

    public static void OnStart(NetDataReader r, int from)
    {
        int index = r.GetByte();
        uint id = r.GetUInt();
        if (index >= Entries.Length) return;
        var (type, method) = Entries[index];
        var target = Find(index, id);
        var me = LocalPlayer.Get();
        if (!target || !me || me.IsDead) return;
        fromTeammate[(index, id)] = Time.unscaledTime;
        Played(index, id);
        DoorSync.CutsceneDoors(target, ours: false, from);
        if (!target.gameObject.activeInHierarchy) target.gameObject.SetActive(true);   // its trigger never switched it on here
        mirroring++;
        try { AccessTools.Method(type, method, Type.EmptyTypes).Invoke(target, null); }
        catch (Exception e) { NetLog.Warn($"Cutscene {target.name}.{method} from {Plugin.NameOf(from)}: {e.InnerException?.Message ?? e.Message}"); }
        finally { mirroring--; }
        NetLog.Info($"Cutscene {target.name}.{method}: started by {Plugin.NameOf(from)}");
    }

    static void Played(int index, uint id)
    {
        if (!played.Contains((index, id))) played.Add((index, id));
    }

    static Component Find(int index, uint id)
    {
        var find = typeof(SceneIds).GetMethod(nameof(SceneIds.Find)).MakeGenericMethod(Entries[index].type);
        return find.Invoke(null, new object[] { id }) as Component;
    }

    // ---- arriving later ----

    /// <summary>Owner: the cutscenes that already played here, for a player who just arrived.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (played.Count == 0) return;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.CutsceneSnapshot);
        w.Put(played.Count);
        foreach (var (index, id) in played) { w.Put((byte)index); w.Put(id); }
        Plugin.SendTo(playerId, w);
    }

    public static void OnSnapshot(NetDataReader r)
    {
        int n = r.GetInt();
        for (int i = 0; i < n; i++)
        {
            int index = r.GetByte();
            uint id = r.GetUInt();
            if (index >= Entries.Length || played.Contains((index, id))) continue;   // we saw it ourselves
            var target = Find(index, id);
            if (!target) continue;
            Played(index, id);
            ended.Add((index, id));
            DoorSync.CutsceneDoors(target, ours: false, Plugin.OwnerHere);   // (who started it isn't sent: the area owner stands in)
            mirroring++;
            try { Ending(index, target); }
            catch (Exception e) { NetLog.Warn($"Cutscene {target.name}: skipping to its end failed: {e.Message}"); }
            finally { mirroring--; }
            NetLog.Info($"Cutscene {target.name}.{Entries[index].method}: played before we came, skipped to its end");
        }
    }

    /// <summary>How each cutscene leaves the world, without playing it (no camera, no freeze, no fades).</summary>
    static void Ending(int index, Component target)
    {
        switch (target)
        {
            case SpiderDoorCutscene door:
                door.doorStatic.SetActive(false);
                door.doorAnimator.SetActive(true);
                break;
            case KaldhorsIntro intro:
                intro.bossAnimator.SetTrigger("Start");
                DoorSync.Mirror(() => intro.onStartEvents.Invoke());
                break;
            case CutsceneTinetiaCage cage when Entries[index].method == nameof(CutsceneTinetiaCage.ReleaseTinetia):
                cage.tinetiaNpc.transform.position = cage.tinetiaReleasePosition.position;
                foreach (var c in cage.tinetiaNpc.GetComponents<Behaviour>())
                    if (c && c.GetType().Name == "NavMeshAgent") c.enabled = true;
                if (Singleton<QuestManager>.HasMain) Singleton<QuestManager>.main.IncrementObjective(cage.objectiveToIncrement, 1);   // our quest moves on too
                break;
            case HummingSphereCutscene sphere:
                DoorSync.Mirror(() => sphere.OnFinishEvents.Invoke());
                if (Singleton<GameManager>.HasMain) Singleton<GameManager>.main.RemoveResettable(sphere);
                break;
            default:
                // Tinetia's guide / portal routines (her AI walking her there) and Adeos (its Play skips to the end
                // by itself once its story flag is set): start them as they are.
                AccessTools.Method(Entries[index].type, Entries[index].method, Type.EmptyTypes).Invoke(target, null);
                break;
        }
    }

    public static void Clear() { fromTeammate.Clear(); played.Clear(); ended.Clear(); }
}

using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The Adeos room (AdeosCutscene, early on): the first player in trips its trigger and the entrance gate shuts behind
/// them for the fight (the door sync shuts it for everyone). A teammate who comes later was left outside. Now: stand at
/// the shut gate (outside) for a moment while a teammate is in there and the fight isn't won (BoolID.CutsceneAdeosSpank
/// not set yet), and you're brought in beside them.
/// </summary>
internal static class AdeosGate
{
    public static Plugin Plugin;
    const float Reach = 3.5f, Wait = 1f;

    static AdeosCutscene room;
    static float nextFind, waited, nextLook;

    public static void Tick()
    {
        if (Time.unscaledTime < nextLook) return;
        nextLook = Time.unscaledTime + 0.25f;
        var me = LocalPlayer.Get();
        if (!me || me.IsDead || Plugin == null || !Plugin.Connected || Dungeon.Active) { waited = 0f; return; }
        if (!room && Time.unscaledTime >= nextFind)
        {
            nextFind = Time.unscaledTime + 3f;
            room = Object.FindAnyObjectByType<AdeosCutscene>();
        }
        var gate = room ? room.entranceGate : null;
        if (!gate || !gate.isActiveAndEnabled || gate.state == DoorState.Opened || GameData.gameBools.IsTrue(room.cutsceneDone)) { waited = 0f; return; }

        // which side is in: towards the trigger past the gate (or Adeos himself)
        Vector2 at = gate.transform.position;
        Vector2 inward = room.triggerForward ? (Vector2)room.triggerForward.transform.position - at : Vector2.zero;
        if (inward.magnitude < 0.5f && room.npcAdeos) inward = (Vector2)room.npcAdeos.transform.position - at;
        if (inward.magnitude < 0.5f) { waited = 0f; return; }
        inward.Normalize();
        bool Inside(Vector3 p) => Vector2.Dot((Vector2)p - at, inward) > 0.3f;

        Vector2 mine = me.transform.position;
        if ((mine - at).magnitude > Reach || Inside(me.transform.position)) { waited = 0f; return; }
        var mate = Plugin.Players.FirstOrDefault(p => p.HaveState && p.Scene == LocalPlayer.Scene && p.Area == LocalPlayer.Area && Inside(p.LastPosition));
        if (mate == null) { waited = 0f; return; }

        waited += 0.25f;
        if (waited < Wait) return;
        waited = 0f;
        NetLog.Info($"The gate's shut: in you go, beside {mate.Name}", toast: true);
        Api.Teleport(mate.LastPosition + new Vector3(0.9f, 0f, 0f));
    }
}

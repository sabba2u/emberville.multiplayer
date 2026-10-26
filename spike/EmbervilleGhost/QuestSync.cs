using System.Collections.Generic;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// One quest log for the party. The game's quests live in the world (QuestManager.model: each quest's state and step,
/// each step's objectives with their state and count); joining copies the host's, but after that each copy went its own
/// way. Now every copy sends its whole quest state whenever it changes (and every 10 s, for whoever joined since), and
/// everyone merges what they hear, forward only:
///  - a quest someone has started starts for you (the game's own StartQuest: "New quest");
///  - kill and count objectives take the highest count anyone has;
///  - a step someone is past (they finished it, or handed in what it asked for) is finished on your copy too, through
///    its objectives, so the quest moves on the game's own way ("New objective", and at the end "Quest complete" with
///    its rewards, on everyone's copy);
///  - failed or switched-off quests follow.
/// Collect-item objectives count each player's own bag, so their counts aren't shared; their step still moves on when
/// a teammate is past it. Better NPCs and Quests' own quests (bnq_...) sync through that mod and are left alone.
/// </summary>
internal static class QuestSync
{
    public static Plugin Plugin;

    struct QuestNote { public QuestState State; public int Step; }
    struct ObjNote { public bool Done; public int Progress; }

    // the best anyone else has told us
    static readonly Dictionary<string, QuestNote> theirQuests = new();
    static readonly Dictionary<string, ObjNote> theirObjs = new();
    static string lastSent = "";
    static float nextLook, nextResend;

    static QuestManager Manager => Singleton<QuestManager>.HasMain ? Singleton<QuestManager>.main : null;
    static bool Ready => Manager && Manager.isInitialized && QuestManager.model != null && GameData.HasWorldData;

    static int Rank(QuestState s) => s == QuestState.Inactive ? 0 : s == QuestState.Active ? 1 : 2;

    static IEnumerable<Quest> Ours()
    {
        foreach (var q in Manager.AllQuests)
            if (q?.data != null && !string.IsNullOrEmpty(q.data.questID) && !q.data.questID.StartsWith("bnq_")) yield return q;
    }

    public static void Tick()
    {
        if (Plugin == null || !Plugin.Connected) { if (theirQuests.Count > 0 || theirObjs.Count > 0) { theirQuests.Clear(); theirObjs.Clear(); lastSent = ""; } return; }
        if (Time.unscaledTime < nextLook || !Ready) return;
        nextLook = Time.unscaledTime + 0.5f;
        try { Apply(); }
        catch (System.Exception e) { NetLog.Warn($"Quest sync: {e.Message}", toast: false); }
        Send();
    }

    // ---- ours, out ----

    static void Send()
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.QuestState);
        var sig = new StringBuilder();
        var quests = new List<Quest>(Ours());
        w.Put(quests.Count);
        foreach (var q in quests)
        {
            w.Put(q.data.questID);
            w.Put((byte)q.mainState.Value);
            w.Put(q.stepIndex);
            sig.Append(q.data.questID).Append(':').Append((int)q.mainState.Value).Append('/').Append(q.stepIndex).Append(';');
        }
        var objs = new List<QuestObjective>();
        foreach (var q in quests)
            foreach (var step in q.data.steps)
                foreach (var o in step.questObjectives)
                    if (o != null && !string.IsNullOrEmpty(o.ID) && !(o is QuestObjective.CollectItem)) objs.Add(o);
        w.Put(objs.Count);
        foreach (var o in objs)
        {
            bool done = o.state.Value == QuestState.Completed;
            w.Put(o.ID);
            w.Put(done);
            w.Put(o.Progress);
            sig.Append(o.ID).Append(done ? '+' : '-').Append(o.Progress).Append(';');
        }
        string s = sig.ToString();
        if (s == lastSent && Time.unscaledTime < nextResend) return;
        lastSent = s;
        nextResend = Time.unscaledTime + 10f;
        Plugin.SendToAll(w, DeliveryMethod.ReliableOrdered);
    }

    // ---- theirs, in ----

    public static void OnState(NetDataReader r, int from)
    {
        int n = r.GetInt();
        for (int i = 0; i < n; i++)
        {
            string id = r.GetString();
            var state = (QuestState)r.GetByte();
            int step = r.GetInt();
            theirQuests.TryGetValue(id, out var had);
            if (!theirQuests.ContainsKey(id) || Rank(state) > Rank(had.State)) had.State = state;
            had.Step = Mathf.Max(had.Step, step);
            theirQuests[id] = had;
        }
        int m = r.GetInt();
        for (int i = 0; i < m; i++)
        {
            string id = r.GetString();
            bool done = r.GetBool();
            int progress = r.GetInt();
            theirObjs.TryGetValue(id, out var had);
            had.Done |= done;
            had.Progress = Mathf.Max(had.Progress, progress);
            theirObjs[id] = had;
        }
    }

    // ---- merged into ours ----

    static void Apply()
    {
        if (theirQuests.Count == 0 && theirObjs.Count == 0) return;
        var manager = Manager;
        foreach (var q in Ours())
        {
            string id = q.data.questID;
            var mine = q.mainState.Value;
            if (theirQuests.TryGetValue(id, out var them))
            {
                if (mine == QuestState.Inactive && (them.State == QuestState.Active || them.State == QuestState.Completed))
                {
                    NetLog.Info($"Quests: {id} started (a teammate has it)");
                    manager.SetQuestState(id, QuestState.Active);
                    continue;
                }
                if ((them.State == QuestState.Failed || them.State == QuestState.Disabled) && Rank(mine) < 2)
                {
                    manager.SetQuestState(id, them.State);
                    continue;
                }
            }
            if (mine != QuestState.Active || q.stepIndex >= q.data.steps.Count) continue;
            var step = q.data.steps[q.stepIndex];
            // a teammate is past this step (or done with the quest): finish it here too
            bool past = theirQuests.TryGetValue(id, out var t) && (t.Step > q.stepIndex || t.State == QuestState.Completed);
            foreach (var o in step.questObjectives)
            {
                if (o == null || o.state.Value == QuestState.Completed) continue;
                bool collect = o is QuestObjective.CollectItem;
                theirObjs.TryGetValue(o.ID ?? "", out var their);
                if (past || (!collect && their.Done)) { Finish(o); continue; }
                if (!collect && their.Progress > o.Progress)
                {
                    o.Progress = Mathf.Min(their.Progress, o.Count);
                    if (o.Progress >= o.Count) Finish(o);
                    else o.CallValueChanged();
                }
            }
        }
    }

    /// <summary>An objective done, as the game does it (its count full, its listeners off, then Completed: the step checks).</summary>
    static void Finish(QuestObjective o)
    {
        o.Progress = Mathf.Max(o.Progress, o.Count);
        if (o is QuestObjective.KillEnemies || o is QuestObjective.CollectItem) o.OnComplete();
        o.state.Value = QuestState.Completed;
        o.CallValueChanged();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterNPCsAndQuests.Core;
using HarmonyLib;
using R3;
using UnityEngine.Localization;

namespace BetterNPCsAndQuests;

/// <summary>
/// Our quests in the game's own journal and right-side quest list: each quest file becomes a game Quest ("bnq_&lt;id&gt;")
/// in QuestManager.model, with our own objectives (progress read from the Book and the bag) and rewards. The Book is the
/// truth; the game's quest is driven to match it through the game's own flow (so "New quest", "New objective", "Quest
/// complete" and the rewards happen as for its own quests), or quietly when a world loads or a guest joins.
/// Our quests are never written into the game's save (WorldData.SaveQuests gets a copy without them).
/// </summary>
[HarmonyPatch]   // (without it PatchAll skips the patches in this class)
internal static class QuestBridge
{
    const string Prefix = "bnq_";
    static readonly Dictionary<LocalizedString, string> Texts = new(new SameObject());
    static readonly Dictionary<string, IDisposable> tracking = new();
    static readonly List<Quest> stashed = new();
    static bool hooked;
    static float labelsAt;

    static QuestManager Manager => Singleton<QuestManager>.HasMain ? Singleton<QuestManager>.main : null;

    public static bool IsOurs(string questId) => questId != null && questId.StartsWith(Prefix);

    static void Hook()
    {
        if (hooked) return;
        hooked = true;
        State.Reloaded += () => { Inject(); ApplyAll(quiet: true); };
        State.Book.QuestChanged += id => { if (!Hooks.IsClient()) Apply(id, quiet: false); };
    }

    public static void Tick()
    {
        Hook();
        if (UnityEngine.Time.unscaledTime < labelsAt || QuestManager.model == null) return;
        labelsAt = UnityEngine.Time.unscaledTime + 0.5f;
        // live progress on the labels (bag counts change without the Book knowing)
        foreach (var q in Ours())
        {
            if (q.mainState.Value != QuestState.Active || q.stepIndex >= q.data.steps.Count) continue;
            foreach (var o in q.data.steps[q.stepIndex].questObjectives.OfType<NpcObjective>()) o.Refresh();
        }
        if (Hooks.IsClient()) ClientCollect(); else State.Book.Refresh(Items.MyBag);
    }

    /// <summary>A world's story was just loaded: the game's quests in the same state, quietly.</summary>
    public static void AfterLoad() { Inject(); ApplyAll(quiet: true); }

    static IEnumerable<Quest> Ours() =>
        QuestManager.model == null ? Enumerable.Empty<Quest>() : QuestManager.model.Quests.Where(kv => IsOurs(kv.Key)).Select(kv => kv.Value).ToList();

    // ---- building the game's quests ----

    /// <summary>Our quests into the game's model (again: replaces the old ones), each with our own tracking subscription.</summary>
    static void Inject()
    {
        var m = Manager;
        if (m == null || QuestManager.model == null || !State.Loaded) return;
        foreach (var old in Ours()) Remove(old);
        int index = 10000;
        foreach (var def in State.Lib.Quests)
        {
            try
            {
                var q = new Quest(Build(def), QuestState.Inactive) { index = index++ };
                QuestManager.model.Quests[q.data.questID] = q;
                foreach (var step in q.data.steps)
                {
                    m.steps[step.identifier] = step;
                    foreach (var obj in step.questObjectives) m.objectives[obj.ID] = new QuestObjectiveContainer { objective = obj, quest = q, step = step };
                }
                Track(q);
            }
            catch (Exception e) { Log.Warn($"NPCs and quests: quest {def.Id}: {e.Message}"); }
        }
        m.questUI.UpdateQuestList();
    }

    static void Remove(Quest q)
    {
        var m = Manager;
        if (tracking.TryGetValue(q.data.questID, out var d)) { d.Dispose(); tracking.Remove(q.data.questID); }
        if (m != null) m.RemoveQuestUIReminder(q);
        QuestManager.model.RemoveTrackedQuest(q);
        QuestManager.model.Quests.Remove(q.data.questID);
        q.stepDispose?.Dispose();
    }

    /// <summary>What the game's LoadQuestsState does for each quest: tracked → on the right-side list.</summary>
    static void Track(Quest q)
    {
        tracking[q.data.questID] = q.isTracked.Subscribe(on =>
        {
            var m = Manager;
            if (m == null) return;
            if (on) { m.AddQuestUIReminder(q); QuestManager.model.AddTrackedQuest(q); }
            else { m.RemoveQuestUIReminder(q); QuestManager.model.RemoveTrackedQuest(q); }
        });
    }

    static QuestData Build(QuestDef def)
    {
        var data = UnityEngine.ScriptableObject.CreateInstance<QuestData>();
        data.name = Prefix + def.Id;
        data.questID = Prefix + def.Id;
        data.Title = Text(def.Title);
        data.Description = Text(string.IsNullOrEmpty(def.Text) ? def.Title : def.Text);
        for (int s = 0; s < def.Steps.Count; s++)
        {
            var step = new QuestStep { identifier = $"{Prefix}{def.Id}_{s}" };
            for (int i = 0; i < def.Steps[s].Objectives.Count; i++)
                step.questObjectives.Add(new NpcObjective { ID = $"{Prefix}{def.Id}_{s}_{i}", Quest = def.Id, Step = s, Index = i, Def = def.Steps[s].Objectives[i], StepDef = def.Steps[s] });
            data.steps.Add(step);
        }
        if (def.Rewards.Any(r => r.Kind != "set")) data.rewards.Add(new NpcReward { Quest = def.Id, Rewards = def.Rewards.Where(r => r.Kind != "set").ToList() });
        return data;
    }

    static LocalizedString Text(string text)
    {
        var s = new LocalizedString();
        Texts[s] = text ?? "";
        return s;
    }

    // ---- matching the Book ----

    public static void ApplyAll(bool quiet)
    {
        if (QuestManager.model == null) return;
        foreach (var q in Ours()) Apply(q.data.questID.Substring(Prefix.Length), quiet);
    }

    /// <summary>The game's quest to the Book's state: through its own flow (live), or set directly (quiet).</summary>
    public static void Apply(string id, bool quiet)
    {
        var m = Manager;
        if (m == null || QuestManager.model == null || !QuestManager.model.Quests.TryGetValue(Prefix + id, out var q)) return;
        var p = State.Book.Progress(id);
        try
        {
            if (quiet) { SetQuiet(m, q, p); return; }
            bool behind = q.mainState.Value == QuestState.Completed && p.State != QState.Completed
                || p.State == QState.Inactive && q.mainState.Value != QuestState.Inactive
                || p.State == QState.Active && q.mainState.Value == QuestState.Active && q.stepIndex > p.Step;
            if (behind) { SetQuiet(m, q, p); return; }
            if (p.State != QState.Inactive && q.mainState.Value == QuestState.Inactive) m.SetQuestState(q.data.questID, QuestState.Active);
            for (int guard = 0; guard < 64 && q.mainState.Value == QuestState.Active && q.stepIndex < q.data.steps.Count
                 && (q.stepIndex < p.Step || p.State == QState.Completed); guard++)
                foreach (var obj in q.data.steps[q.stepIndex].questObjectives) obj.state.Value = QuestState.Completed;   // → the game moves on
            m.questUI.UpdateQuestList();
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: showing quest {id}: {e.Message}"); }
    }

    /// <summary>Straight to the state: no popups, no rewards (a world loading, a guest arriving, a reset).</summary>
    static void SetQuiet(QuestManager m, Quest q, QProgress p)
    {
        bool init = m.isInitialized;
        m.isInitialized = false;   // (the game's popups wait for this)
        try
        {
            q.stepDispose?.Dispose();
            foreach (var s in q.data.steps)
            {
                s.toDispose?.Dispose();
                s.state.SetValueSilently(QuestState.Inactive);
                foreach (var o in s.questObjectives) o.state.SetValueSilently(QuestState.Inactive);
            }
            switch (p.State)
            {
                case QState.Inactive:
                    q.stepIndex = 0;
                    q.isTracked.Value = false;
                    q.mainState.Value = QuestState.Inactive;
                    break;
                case QState.Completed:
                    foreach (var s in q.data.steps) Done(s);
                    q.stepIndex = q.data.steps.Count;
                    q.isTracked.Value = false;
                    q.mainState.Value = QuestState.Completed;
                    break;
                default:
                    for (int i = 0; i < p.Step && i < q.data.steps.Count; i++) Done(q.data.steps[i]);
                    q.stepIndex = Math.Min(p.Step, q.data.steps.Count - 1);
                    bool fresh = q.mainState.Value == QuestState.Inactive;
                    q.mainState.Value = QuestState.Active;
                    q.CheckStep();   // listens to the current step again (its popup waits for isInitialized)
                    if (fresh && QuestManager.model.CanTrackQuest) q.isTracked.Value = true;
                    break;
            }
        }
        finally { m.isInitialized = init; }
        m.questUI.UpdateQuestList();

        static void Done(QuestStep s)
        {
            s.state.SetValueSilently(QuestState.Completed);
            foreach (var o in s.questObjectives) o.state.SetValueSilently(QuestState.Completed);
        }
    }

    /// <summary>A guest: collect objectives that keep the items, done with the guest's own bag → tell the host.</summary>
    static readonly HashSet<string> told = new();
    static void ClientCollect()
    {
        foreach (var def in State.Lib.Quests)
        {
            var p = State.Book.Progress(def.Id);
            if (p.State != QState.Active) continue;
            var step = def.Steps[p.Step];
            var keep = step.Objectives.Where(o => o.Kind == "collect").ToList();
            if (keep.Count == 0 || keep.Any(o => o.Item.Take) || keep.Any(o => Items.MyBag.Count(o.Item) < o.Count)) continue;
            if (told.Add($"{def.Id}/{p.Step}")) Hooks.Ask($"handed {def.Id} {p.Step}");
        }
    }

    // ---- patches ----

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.QuestsSetup)), HarmonyPostfix]
    static void AfterSetup() { Hook(); Inject(); }

    /// <summary>The game's load subscribes every quest in the model: ours sit it out (we track them ourselves).</summary>
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.LoadQuestsState)), HarmonyPrefix]
    static void BeforeLoad()
    {
        stashed.Clear();
        if (QuestManager.model == null) return;
        foreach (var q in Ours()) { stashed.Add(q); QuestManager.model.Quests.Remove(q.data.questID); }
    }

    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.LoadQuestsState)), HarmonyPostfix]
    static void AfterLoadState()
    {
        foreach (var q in stashed) QuestManager.model.Quests[q.data.questID] = q;
        stashed.Clear();
        ApplyAll(quiet: true);
    }

    [HarmonyPatch(typeof(WorldData), nameof(WorldData.SaveQuests)), HarmonyPrefix]
    static void KeepOutOfSave(ref Dictionary<string, Quest> quests)
    {
        if (quests == null || !quests.Keys.Any(IsOurs)) return;
        quests = quests.Where(kv => !IsOurs(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Our titles and descriptions (the game reads them as localized strings).</summary>
    [HarmonyPatch]
    static class Titles
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(LocalizedString).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(mi => mi.Name == "GetLocalizedString" && mi.ReturnType == typeof(string));

        static bool Prefix(LocalizedString __instance, ref string __result)
        {
            if (__instance == null || Texts.Count == 0 || !Texts.TryGetValue(__instance, out var text)) return true;
            __result = text;
            return false;
        }
    }
}

/// <summary>Keys by the object itself (a LocalizedString may count empty ones as equal).</summary>
internal sealed class SameObject : IEqualityComparer<LocalizedString>
{
    public bool Equals(LocalizedString a, LocalizedString b) => ReferenceEquals(a, b);
    public int GetHashCode(LocalizedString o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
}

/// <summary>One objective of our quest step: its progress from the Book (kills, flags, handed in) or the bag.</summary>
[Serializable]
internal sealed class NpcObjective : QuestObjective
{
    public string Quest;
    public int Step, Index;
    public ObjectiveDef Def;
    public StepDef StepDef;
    int shown = -1;

    public override int Count { get => Def.Kind == "flag" ? 1 : Def.Count; set { } }

    public override int Progress
    {
        get
        {
            var p = State.Book.Progress(Quest);
            if (p.State == QState.Completed || p.Step > Step) return Count;
            if (p.State != QState.Active || p.Step < Step || Index >= p.Counts.Length) return 0;
            int done = p.Counts[Index];
            if (Def.Kind == "collect" && done < Count) return Math.Min(Count, Items.MyBag.Count(Def.Item));
            return Math.Min(done, Count);
        }
        set { }
    }

    public override string GetTitle()
    {
        if (StepDef.Objectives.Count == 1) return StepDef.Title;
        return Def.Kind switch
        {
            "collect" => (Def.Item.Take ? "Bring " : "Find ") + Items.Label(Def.Item),
            "kill" => "Defeat " + (Def.Target.Equals("any", StringComparison.OrdinalIgnoreCase) ? "creatures" : Def.Target),
            _ => StepDef.Title,
        };
    }

    /// <summary>Redraw the label if the number moved.</summary>
    public void Refresh()
    {
        int now = Progress;
        if (now == shown) return;
        shown = now;
        CallValueChanged();
    }
}

/// <summary>Our quest's rewards: run on each player's own game when the quest completes there.</summary>
[Serializable]
internal sealed class NpcReward : QuestReward
{
    public string Quest;
    public List<RewardDef> Rewards;

    public override string GetDescription() => string.Join(", ", Rewards.Select(r => r.Kind switch
    {
        "gold" => $"{r.Amount} gold",
        "xp" => $"{r.Amount} XP",
        "item" => $"{Items.Label(r.Item)} x{r.Item.Count}",
        "random" => r.QMin == r.QMax ? $"A random {Parse.Qualities[r.QMin]} item" : $"A random item ({Parse.Qualities[r.QMin]} to {Parse.Qualities[r.QMax]})",
        _ => r.Kind,
    }));

    public override void Reward()
    {
        foreach (var r in Rewards) Items.Give(r);
    }
}

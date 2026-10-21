using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BetterNPCsAndQuests.Core;

/// <summary>The player's bag, for collect objectives (the game's: the main bag only, not what's worn).</summary>
public interface IBag
{
    int Count(ItemSpec spec);
    bool Take(ItemSpec spec, int n);
}

public enum QState { Inactive, Active, Completed }

/// <summary>Where one quest is: its state, its current step, and per objective of that step how far (kills, flag 0/1, collect done).</summary>
public sealed class QProgress
{
    public QState State;
    public int Step;
    public int[] Counts = new int[0];
}

/// <summary>
/// A world's story: our flags and every quest's progress, and the rules that react to what happens. All changes go
/// through here (dialogue, quests, rules, the Insert tab, co-op), and it saves as text (our own per-world file).
/// Actions: "set $x to ...", "start &lt;quest&gt;", "advance &lt;quest&gt;" (finish the current step), "turnin &lt;quest&gt;".
/// Events: kill &lt;creature&gt;, boss_kill &lt;floor&gt;, descent_floor &lt;n&gt;, enter &lt;scene&gt;, pickup &lt;item&gt;,
/// and (raised here) quest_done &lt;id&gt;, step_done &lt;id&gt; &lt;n&gt;, flag &lt;name&gt;.
/// </summary>
public sealed class Book
{
    const int MaxDepth = 16;

    Library lib;
    readonly Dictionary<string, QProgress> progress = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> passthrough = new();   // saved lines of quests we don't have right now (kept, re-saved)
    int depth;
    bool loading;

    public FlagStore Flags { get; private set; }
    /// <summary>Flags the game works out (has_wood_10, has_any_sword): null = not one of those.</summary>
    public Func<string, int?> Computed;
    public List<string> Problems = new();
    /// <summary>"command &lt;text&gt;": another mod's chat command (e.g. "pet chicken", "clucking"), run by the game layer.</summary>
    public Action<string> Command;

    /// <summary>A quest's state or step changed (its id).</summary>
    public event Action<string> QuestChanged;
    /// <summary>A quest was just finished (not when loading).</summary>
    public event Action<string> QuestCompleted;
    /// <summary>Anything changed (flags or quests): save, sync, redraw.</summary>
    public event Action Changed;

    public Library Lib => lib;

    public Book(Library lib)
    {
        this.lib = lib;
        UseFlags(new FlagStore());
    }

    void UseFlags(FlagStore f)
    {
        Flags = f;
        f.Changed += (name, _) =>
        {
            if (loading) return;
            Changed?.Invoke();
            CheckAll();
            Raise("flag", name);
        };
    }

    // ---- reading ----

    public int Read(string name)
    {
        string k = FlagStore.Key(name);
        if (k.StartsWith("quest_"))
        {
            foreach (var (suffix, what) in new[] { ("_active", 0), ("_done", 1), ("_step", 2) })
            {
                if (!k.EndsWith(suffix)) continue;
                var q = lib.Quest(k.Substring(6, k.Length - 6 - suffix.Length));
                if (q == null) return 0;
                var p = Progress(q.Id);
                return what switch
                {
                    0 => p.State == QState.Active ? 1 : 0,
                    1 => p.State == QState.Completed ? 1 : 0,
                    _ => p.State == QState.Active ? p.Step + 1 : 0,
                };
            }
        }
        return Computed?.Invoke(k) ?? Flags.Get(k);
    }

    public QProgress Progress(string questId)
    {
        if (progress.TryGetValue(questId, out var p)) return p;
        var q = lib.Quest(questId);
        p = new QProgress();
        if (q != null) { Size(q, p); progress[q.Id] = p; }
        return p;
    }

    static void Size(QuestDef q, QProgress p)
    {
        int n = p.State == QState.Active && p.Step < q.Steps.Count ? q.Steps[p.Step].Objectives.Count : 0;
        if (p.Counts.Length != n) Array.Resize(ref p.Counts, n);
    }

    // ---- actions ----

    public void Run(string action, IBag bag = null)
    {
        action = (action ?? "").Trim();
        if (++depth > MaxDepth) { depth--; Problems.Add($"stopped a loop of rules at \"{action}\""); return; }
        try
        {
            if (action.StartsWith("command ", StringComparison.OrdinalIgnoreCase))
            {
                if (Command != null) Command(action.Substring(8).Trim());
                else Problems.Add($"nothing can run \"{action}\"");
                return;
            }
            if (action.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            {
                if (Expr.TryAssign(action.Substring(4), Read, out var name, out var value)) Flags.Set(name, value);
                else Problems.Add($"can't do \"{action}\"");
                return;
            }
            var parts = action.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var q = parts.Length == 2 ? lib.Quest(parts[1]) : null;
            if (q == null) { Problems.Add($"can't do \"{action}\""); return; }
            switch (parts[0])
            {
                case "start": Start(q); break;
                case "advance": Advance(q); break;
                case "turnin": if (bag != null) TurnIn(q.Id, bag); break;
                default: Problems.Add($"can't do \"{action}\""); break;
            }
        }
        finally { depth--; }
    }

    void Start(QuestDef q)
    {
        var p = Progress(q.Id);
        if (p.State != QState.Inactive) return;
        p.State = QState.Active;
        p.Step = 0;
        Size(q, p);
        Notify(q.Id);
        foreach (var a in q.OnStart) Run(a);
        Check(q);
    }

    /// <summary>Hand over what the current step asks for, from this bag: all of it or nothing.</summary>
    public bool TurnIn(string questId, IBag bag)
    {
        var q = lib.Quest(questId);
        if (q == null) return false;
        var p = Progress(q.Id);
        if (p.State != QState.Active) return false;
        var step = q.Steps[p.Step];
        var wanted = step.Objectives.Select((o, i) => (o, i)).Where(x => x.o.Kind == "collect" && p.Counts[x.i] < x.o.Count).ToList();
        if (wanted.Count == 0 || wanted.Any(x => bag.Count(x.o.Item) < x.o.Count)) return false;
        foreach (var (o, i) in wanted)
        {
            if (o.Item.Take && !bag.Take(o.Item, o.Count)) return false;
            p.Counts[i] = o.Count;
        }
        Notify(q.Id);
        Check(q);
        return true;
    }

    /// <summary>A bag that has everything (the items were checked and taken from someone else's bag).</summary>
    sealed class Everything : IBag
    {
        public int Count(ItemSpec spec) => int.MaxValue;
        public bool Take(ItemSpec spec, int n) => true;
    }

    /// <summary>A guest handed in what step &lt;step&gt; (0-based) asked for, from their own bag. Ignored if the quest has
    /// moved on since (someone else was quicker).</summary>
    public bool HandedIn(string questId, int step)
    {
        var q = lib.Quest(questId);
        if (q == null) return false;
        var p = Progress(q.Id);
        return p.State == QState.Active && p.Step == step && TurnIn(q.Id, new Everything());
    }

    /// <summary>Collect objectives that keep the items: done once the bag has them.</summary>
    public void Refresh(IBag bag)
    {
        foreach (var q in lib.Quests)
        {
            var p = Progress(q.Id);
            if (p.State != QState.Active) continue;
            bool any = false;
            var step = q.Steps[p.Step];
            for (int i = 0; i < step.Objectives.Count; i++)
            {
                var o = step.Objectives[i];
                if (o.Kind == "collect" && !o.Item.Take && p.Counts[i] < o.Count && bag.Count(o.Item) >= o.Count) { p.Counts[i] = o.Count; any = true; }
            }
            if (any) { Notify(q.Id); Check(q); }
        }
    }

    void Advance(QuestDef q)
    {
        var p = Progress(q.Id);
        if (p.State != QState.Active) return;
        var step = q.Steps[p.Step];
        for (int i = 0; i < step.Objectives.Count; i++) p.Counts[i] = Math.Max(p.Counts[i], step.Objectives[i].Kind == "flag" ? 1 : step.Objectives[i].Count);
        Finish(q, p);
    }

    public void Reset(string questId)
    {
        var q = lib.Quest(questId);
        if (q == null) return;
        progress[q.Id] = new QProgress();
        Notify(q.Id);
    }

    // ---- events ----

    public void Raise(string ev, string arg)
    {
        if (loading) return;
        if (++depth > MaxDepth) { depth--; Problems.Add($"stopped a loop of rules at {ev} {arg}"); return; }
        try
        {
            if (ev == "kill")
                foreach (var q in lib.Quests)
                {
                    var p = Progress(q.Id);
                    if (p.State != QState.Active) continue;
                    var step = q.Steps[p.Step];
                    bool any = false;
                    for (int i = 0; i < step.Objectives.Count; i++)
                    {
                        var o = step.Objectives[i];
                        if (o.Kind == "kill" && p.Counts[i] < o.Count && (o.Target.Equals("any", StringComparison.OrdinalIgnoreCase) || o.Target.Equals(arg, StringComparison.OrdinalIgnoreCase)))
                        { p.Counts[i]++; any = true; }
                    }
                    if (any) { Notify(q.Id); Check(q); }
                }
            foreach (var r in lib.Rules.ToList())
                if (r.Event == ev && Matches(r, arg) && Expr.Test(r.Cond, Read))
                    foreach (var a in r.Actions) Run(a);
        }
        finally { depth--; }
    }

    static bool Matches(RuleDef r, string arg)
    {
        if (string.IsNullOrEmpty(r.Arg) || r.Arg.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
        string want = r.Event == "flag" ? FlagStore.Key(r.Arg) : r.Arg;
        return want.Equals((arg ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- steps ----

    void CheckAll() { foreach (var q in lib.Quests.ToList()) if (Progress(q.Id).State == QState.Active) Check(q); }

    /// <summary>Flag objectives catch up; a finished step moves on (maybe several in a row).</summary>
    void Check(QuestDef q)
    {
        var p = Progress(q.Id);
        while (p.State == QState.Active)
        {
            var step = q.Steps[p.Step];
            for (int i = 0; i < step.Objectives.Count; i++)
                if (step.Objectives[i].Kind == "flag" && p.Counts[i] == 0 && Read(step.Objectives[i].Target) != 0) p.Counts[i] = 1;
            for (int i = 0; i < step.Objectives.Count; i++)
                if (p.Counts[i] < (step.Objectives[i].Kind == "flag" ? 1 : step.Objectives[i].Count)) return;
            Finish(q, p);
        }
    }

    void Finish(QuestDef q, QProgress p)
    {
        int n = p.Step + 1;
        var step = q.Steps[p.Step];
        p.Step++;
        if (p.Step >= q.Steps.Count) { p.State = QState.Completed; p.Step = q.Steps.Count; }
        p.Counts = new int[0];   // a fresh step: nothing done yet
        Size(q, p);
        Notify(q.Id);
        foreach (var a in step.OnDone) Run(a);
        Raise("step_done", q.Id + " " + n);
        if (p.State == QState.Completed)
        {
            foreach (var a in q.OnComplete) Run(a);
            foreach (var r in q.Rewards.Where(r => r.Kind == "set")) Run(r.Arg);
            Raise("quest_done", q.Id);
            QuestCompleted?.Invoke(q.Id);
        }
    }

    void Notify(string id)
    {
        if (loading) return;
        QuestChanged?.Invoke(id);
        Changed?.Invoke();
    }

    // ---- saving ----

    /// <summary>"f\tname\tvalue" and "q\tid\tstate\tstep\tcounts" lines.</summary>
    public string Save()
    {
        var sb = new StringBuilder();
        foreach (var kv in Flags.All.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append("f\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
        foreach (var kv in progress.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (kv.Value.State == QState.Inactive) continue;
            sb.Append("q\t").Append(kv.Key).Append('\t').Append(kv.Value.State).Append('\t').Append(kv.Value.Step).Append('\t')
              .Append(string.Join(",", kv.Value.Counts)).Append('\n');
        }
        foreach (var line in passthrough) sb.Append(line).Append('\n');
        return sb.ToString();
    }

    /// <summary>Replace everything with a saved state (quietly: no rewards, no rules).</summary>
    public void Load(string text)
    {
        loading = true;
        try
        {
            var flags = new FlagStore();
            progress.Clear();
            passthrough.Clear();
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var f = raw.Split('\t');
                if (f.Length == 3 && f[0] == "f" && int.TryParse(f[2], out var v)) { flags.Set(f[1], v); continue; }
                if (f.Length < 4 || f[0] != "q") continue;
                var q = lib.Quest(f[1]);
                if (q == null) { passthrough.Add(raw); continue; }
                if (!Enum.TryParse(f[2], out QState state) || !int.TryParse(f[3], out var step)) continue;
                var p = new QProgress { State = state, Step = Math.Max(0, step) };
                if (p.State == QState.Active && p.Step >= q.Steps.Count) { p.State = QState.Completed; p.Step = q.Steps.Count; }
                if (p.State == QState.Inactive) p.Step = 0;
                if (f.Length > 4 && f[4].Length > 0) p.Counts = f[4].Split(',').Select(c => int.TryParse(c, out var n) ? n : 0).ToArray();
                Size(q, p);
                progress[q.Id] = p;
            }
            UseFlags(flags);
        }
        finally { loading = false; }
        Changed?.Invoke();
    }

    /// <summary>The files were read again: keep flags and progress (by quest id and step).</summary>
    public void Reattach(Library newLib)
    {
        string state = Save();
        lib = newLib;
        Load(state);
    }
}

using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using Xunit;

public class BookTests
{
    /// <summary>A bag of named items; "sword" items also count as "any sword".</summary>
    sealed class FakeBag : IBag
    {
        public Dictionary<string, int> Items = new();
        public int Count(ItemSpec s) => s.Name != null ? Items.GetValueOrDefault(s.Name.ToLowerInvariant()) : Items.Where(kv => kv.Key.Contains(s.Category)).Sum(kv => kv.Value);
        public bool Take(ItemSpec s, int n)
        {
            if (Count(s) < n) return false;
            string key = s.Name != null ? s.Name.ToLowerInvariant() : Items.Keys.First(k => k.Contains(s.Category) && Items[k] > 0);
            Items[key] -= n;
            return true;
        }
    }

    static Library Lib(string rules = null, params (string path, string text)[] more)
    {
        var files = new List<(string, string)> { ("quests/ellie_errands.txt", ParseTests.EllieQuest),
            ("quests/hunt.txt", "id = hunt\ntitle = Hunt\nstep = Kill\nkill = Aco_Tracker x2\nstep = Report\nflag = $reported\ncomplete = set $hunt_over to true\n") };
        if (rules != null) files.Add(("rules/r.txt", rules));
        files.AddRange(more);
        var lib = Library.Load(ParseTests.Files(files.ToArray()));
        Assert.Empty(lib.Problems);
        return lib;
    }

    [Fact]
    public void Start_SetsActive_RunsOnStart()
    {
        var lib = Lib(null, ("quests/s.txt", "id = s\ntitle = S\nstart = set $s_started to true\nstep = A\nflag = $never\n"));
        var b = new Book(lib);
        b.Run("start s");
        Assert.Equal(QState.Active, b.Progress("s").State);
        Assert.Equal(1, b.Read("s_started"));
        Assert.Equal(1, b.Read("quest_s_active"));
    }

    [Fact]
    public void TurnIn_NotEnough_TakesNothing()
    {
        var b = new Book(Lib());
        var bag = new FakeBag { Items = { ["wood"] = 9 } };
        b.Run("start ellie_errands");
        Assert.False(b.TurnIn("ellie_errands", bag));
        Assert.Equal(9, bag.Items["wood"]);
        Assert.Equal(0, b.Progress("ellie_errands").Step);
    }

    [Fact]
    public void TurnIn_Enough_TakesAndAdvances()
    {
        var b = new Book(Lib());
        var bag = new FakeBag { Items = { ["wood"] = 12 } };
        b.Run("start ellie_errands");
        Assert.True(b.TurnIn("ellie_errands", bag));
        Assert.Equal(2, bag.Items["wood"]);
        Assert.Equal(1, b.Progress("ellie_errands").Step);
        Assert.Equal(1, b.Read("ellie_wood_given"));
        Assert.Equal(2, b.Read("quest_ellie_errands_step"));
    }

    [Fact]
    public void AnySword_Category_CompletesOnce()
    {
        var b = new Book(Lib());
        var done = new List<string>();
        b.QuestCompleted += done.Add;
        var bag = new FakeBag { Items = { ["wood"] = 10, ["iron sword"] = 1 } };
        b.Run("start ellie_errands");
        b.Run("turnin ellie_errands", bag);
        b.Run("turnin ellie_errands", bag);
        b.Run("turnin ellie_errands", bag);
        Assert.Equal(QState.Completed, b.Progress("ellie_errands").State);
        Assert.Equal(0, bag.Items["iron sword"]);
        Assert.Equal(new[] { "ellie_errands" }, done);
        Assert.Equal(1, b.Read("ellie_trusts_you"));
        Assert.Equal(1, b.Read("quest_ellie_errands_done"));
    }

    [Fact]
    public void Kill_Counts_AndCompletes()
    {
        var b = new Book(Lib());
        b.Run("start hunt");
        b.Raise("kill", "Aco_Tracker");
        Assert.Equal(1, b.Progress("hunt").Counts[0]);
        b.Raise("kill", "Glowbat");
        b.Raise("kill", "aco_tracker");
        Assert.Equal(1, b.Progress("hunt").Step);
    }

    [Fact]
    public void Kill_BeforeStart_DoesNotCount()
    {
        var b = new Book(Lib());
        b.Raise("kill", "Aco_Tracker");
        b.Run("start hunt");
        Assert.Equal(0, b.Progress("hunt").Counts[0]);
    }

    [Fact]
    public void FlagObjective_CompletesWhenFlagSet()
    {
        var b = new Book(Lib());
        b.Run("start hunt");
        b.Raise("kill", "Aco_Tracker"); b.Raise("kill", "Aco_Tracker");
        b.Run("set $reported to true");
        Assert.Equal(QState.Completed, b.Progress("hunt").State);
        Assert.Equal(1, b.Read("hunt_over"));
    }

    [Fact]
    public void FlagObjective_AlreadySet_CompletesOnArrival()
    {
        var b = new Book(Lib());
        b.Run("set $reported to true");
        b.Run("start hunt");
        b.Raise("kill", "Aco_Tracker"); b.Raise("kill", "Aco_Tracker");
        Assert.Equal(QState.Completed, b.Progress("hunt").State);
    }

    [Fact]
    public void Rule_BossKill_SetsFlag()
    {
        var b = new Book(Lib("when boss_kill 5 -> set $saved_rory_d5 to true; start hunt\n"));
        b.Raise("boss_kill", "4");
        Assert.Equal(0, b.Read("saved_rory_d5"));
        b.Raise("boss_kill", "5");
        Assert.Equal(1, b.Read("saved_rory_d5"));
        Assert.Equal(QState.Active, b.Progress("hunt").State);
    }

    [Fact]
    public void Rule_IfCondition_AndCounting()
    {
        var b = new Book(Lib("when kill any if $hunting -> set $kills to $kills + 1\n"));
        b.Raise("kill", "Glowbat");
        b.Run("set $hunting to true");
        b.Raise("kill", "Glowbat");
        b.Raise("kill", "Aco_Tracker");
        Assert.Equal(2, b.Read("kills"));
    }

    [Fact]
    public void Rule_QuestDone_Chains()
    {
        var b = new Book(Lib("when quest_done hunt -> set $chain to 1\nwhen flag chain -> set $chain2 to 1\n"));
        b.Run("set $reported to true");
        b.Run("start hunt");
        b.Raise("kill", "x"); // not counted
        b.Raise("kill", "Aco_Tracker"); b.Raise("kill", "Aco_Tracker");
        Assert.Equal(1, b.Read("chain"));
        Assert.Equal(1, b.Read("chain2"));
    }

    [Fact]
    public void Rule_Loop_DoesNotHang()
    {
        var b = new Book(Lib("when flag a -> set $a to $a + 1\n"));
        b.Run("set $a to 1");
        Assert.True(b.Read("a") > 1);
        Assert.NotEmpty(b.Problems);
    }

    [Fact]
    public void Read_QuestComputed_AndGameComputed()
    {
        var b = new Book(Lib()) { Computed = n => n == "has_wood_10" ? 1 : null };
        Assert.Equal(0, b.Read("quest_ellie_errands_step"));
        b.Run("start ellie_errands");
        Assert.Equal(1, b.Read("quest_ellie_errands_step"));
        Assert.Equal(1, b.Read("$has_wood_10"));
        Assert.Equal(0, b.Read("quest_nope_active"));
    }

    [Fact]
    public void Changed_Fires()
    {
        var b = new Book(Lib());
        int n = 0;
        b.Changed += () => n++;
        b.Run("start ellie_errands");
        b.Run("set $x to 3");
        Assert.True(n >= 2);
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var b = new Book(Lib());
        b.Run("start hunt");
        b.Raise("kill", "Aco_Tracker");
        b.Run("set $x to 5");
        var c = new Book(Lib());
        c.Load(b.Save());
        Assert.Equal(5, c.Read("x"));
        Assert.Equal(QState.Active, c.Progress("hunt").State);
        Assert.Equal(1, c.Progress("hunt").Counts[0]);
    }

    [Fact]
    public void Load_DoesNotFireCompleted()
    {
        var b = new Book(Lib());
        b.Run("set $reported to true"); b.Run("start hunt"); b.Raise("kill", "Aco_Tracker"); b.Raise("kill", "Aco_Tracker");
        var c = new Book(Lib());
        int done = 0;
        c.QuestCompleted += _ => done++;
        c.Load(b.Save());
        Assert.Equal(QState.Completed, c.Progress("hunt").State);
        Assert.Equal(0, done);
    }

    [Fact]
    public void Load_UnknownQuest_KeptAndResaved()
    {
        var b = new Book(Lib(null, ("quests/gone.txt", "id = gone\ntitle = G\nstep = A\nflag = $never\n")));
        b.Run("start gone");
        var c = new Book(Lib());
        c.Load(b.Save());
        Assert.Contains("gone", c.Save());
        var d = new Book(Lib(null, ("quests/gone.txt", "id = gone\ntitle = G\nstep = A\nflag = $never\n")));
        d.Load(c.Save());
        Assert.Equal(QState.Active, d.Progress("gone").State);
    }

    [Fact]
    public void Load_StepPastEnd_Clamped()
    {
        var c = new Book(Lib());
        c.Load("q\tellie_errands\tActive\t7\t\n");
        Assert.Equal(QState.Completed, c.Progress("ellie_errands").State);
        c.Load("garbage line\nq\tellie_errands\tNope\tx\t\n");
        Assert.Equal(QState.Inactive, c.Progress("ellie_errands").State);
    }

    [Fact]
    public void Reload_KeepsProgress()
    {
        var b = new Book(Lib());
        var bag = new FakeBag { Items = { ["wood"] = 10 } };
        b.Run("start ellie_errands");
        b.TurnIn("ellie_errands", bag);
        b.Reattach(Lib());
        Assert.Equal(1, b.Progress("ellie_errands").Step);
        Assert.Equal(1, b.Read("ellie_wood_given"));
    }

    [Fact]
    public void Reset_PutsQuestBack()
    {
        var b = new Book(Lib());
        b.Run("start ellie_errands");
        b.Reset("ellie_errands");
        Assert.Equal(QState.Inactive, b.Progress("ellie_errands").State);
    }

    [Fact]
    public void HandedIn_OnlyForTheStepItWasFor()
    {
        var b = new Book(Lib());
        var bag = new FakeBag { Items = { ["wood"] = 10 } };
        b.Run("start ellie_errands");
        b.TurnIn("ellie_errands", bag);                       // the host handed in the wood: step 2 now
        Assert.False(b.HandedIn("ellie_errands", 0));         // a guest's wood, for step 1: stale, ignored
        Assert.Equal(1, b.Progress("ellie_errands").Step);
        Assert.True(b.HandedIn("ellie_errands", 1));          // a guest's sword, for step 2
        Assert.Equal(QState.Completed, b.Progress("ellie_errands").State);
    }

    [Fact]
    public void Run_Command_GoesToTheHook()
    {
        var b = new Book(Lib());
        var ran = new List<string>();
        b.Command = ran.Add;
        b.Run("command pet chicken");
        Assert.Equal(new[] { "pet chicken" }, ran);
        Assert.True(Parse.IsAction("command risen"));
        Assert.False(Parse.IsAction("command"));
    }

    [Fact]
    public void Run_UnknownAction_IsProblemNotThrow()
    {
        var b = new Book(Lib());
        b.Run("dance wildly");
        b.Run("start no_such_quest");
        Assert.Equal(2, b.Problems.Count);
    }

    [Fact]
    public void Refresh_CollectWithoutTake()
    {
        var b = new Book(Lib(null, ("quests/look.txt", "id = look\ntitle = L\nstep = Find ore\ncollect = iron ore x3\n")));
        var bag = new FakeBag { Items = { ["iron ore"] = 3 } };
        b.Run("start look");
        b.Refresh(bag);
        Assert.Equal(QState.Completed, b.Progress("look").State);
        Assert.Equal(3, bag.Items["iron ore"]);
    }
}

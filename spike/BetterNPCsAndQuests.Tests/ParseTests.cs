using System.Collections.Generic;
using System.Linq;
using System.Text;
using BetterNPCsAndQuests.Core;
using Xunit;

public class ParseTests
{
    internal const string EllieNpc = @"# Ellie, the quest giver
name     = Ellie
scene    = TutorialArea
spot     = -49.5, 64.8
facing   = down
hair     = 2
outfit   = light
holding  = none
color    = #e8a0c0
dialogue = ellie.twee
wander   = 3
pause    = 3-8
";

    internal const string EllieQuest = @"id       = ellie_errands
title    = Ellie's Errands
text     = Ellie needs a few things.
step     = Bring Ellie 10 wood
collect  = wood x10 take
done     = set $ellie_wood_given to true
step     = Bring Ellie any sword
collect  = any sword x1 take
done     = set $ellie_sword_given to true
complete = set $ellie_trusts_you to true
reward   = gold 200
reward   = random good-legendary
";

    internal const string EllieTwee = ":: StoryTitle\nEllie\n\n:: StoryData\n\n:: DialogStart [NPC:Ellie]\nHi\n(if: $ellie_trusts_you)[[Thanks|DialogStart]]\n(if: $has_wood_10)[[Wood|DialogStart]]\n";

    internal static Dictionary<string, byte[]> Files(params (string path, string text)[] files) =>
        files.ToDictionary(f => f.path, f => Encoding.UTF8.GetBytes(f.text));

    [Fact]
    public void Kv_CommentsAndRepeats()
    {
        var kv = KvFile.Read("# hi\nA = 1\n\nb=2 # not a comment here? \nb = 3\n");
        Assert.Equal(3, kv.Count);
        Assert.Equal(("a", "1"), (kv[0].key, kv[0].value));
        Assert.Equal(2, kv[0].line);
        Assert.Equal("3", kv[2].value);
    }

    [Fact]
    public void ItemSpec_Variants()
    {
        var a = Parse.ItemSpec("wood x10 take");
        Assert.Equal(("wood", null, 10, true), (a.Name, a.Category, a.Count, a.Take));
        var b = Parse.ItemSpec("any sword x1 take");
        Assert.Equal((null, "sword", 1, true), (b.Name, b.Category, b.Count, b.Take));
        var c = Parse.ItemSpec("Iron Ore");
        Assert.Equal(("Iron Ore", null, 1, false), (c.Name, c.Category, c.Count, c.Take));
        Assert.Null(Parse.ItemSpec("any spaceship x2"));
        Assert.Null(Parse.ItemSpec(""));
    }

    [Fact]
    public void Npc_Ellie_Parses()
    {
        var probs = new List<Problem>();
        var n = Parse.Npc("npcs/Ellie/npc.txt", "Ellie", EllieNpc, probs);
        Assert.Empty(probs);
        Assert.Equal(("Ellie", "Ellie", "TutorialArea"), (n.Id, n.Name, n.Scene));
        Assert.Equal((-49.5f, 64.8f), (n.X, n.Y));
        Assert.Equal(("down", 2, "light", "none", "#e8a0c0", "ellie.twee"), (n.Facing, n.Hair, n.Outfit, n.Holding, n.Color, n.Dialogue));
        Assert.Equal((3f, 3f, 8f), (n.Wander, n.PauseMin, n.PauseMax));
    }

    [Fact]
    public void Npc_GeneratedScene_IsProblem()
    {
        var probs = new List<Problem>();
        Assert.Null(Parse.Npc("npcs/X/npc.txt", "X", "name = X\nscene = DungeonScene\nspot = 1, 2\ndialogue = x.twee\n", probs));
        Assert.Contains(probs, p => p.Message.Contains("fixed scenes"));
    }

    [Fact]
    public void Npc_MissingSpot_IsProblem()
    {
        var probs = new List<Problem>();
        Assert.Null(Parse.Npc("npcs/X/npc.txt", "X", "name = X\nscene = TutorialArea\ndialogue = x.twee\n", probs));
        Assert.Contains(probs, p => p.Message.Contains("spot"));
    }

    [Fact]
    public void Quest_Ellie_Parses()
    {
        var probs = new List<Problem>();
        var q = Parse.Quest("quests/ellie_errands.txt", EllieQuest, probs);
        Assert.Empty(probs);
        Assert.Equal(("ellie_errands", "Ellie's Errands"), (q.Id, q.Title));
        Assert.Equal(2, q.Steps.Count);
        Assert.Equal("collect", q.Steps[0].Objectives[0].Kind);
        Assert.Equal(10, q.Steps[0].Objectives[0].Item.Count);
        Assert.Equal("sword", q.Steps[1].Objectives[0].Item.Category);
        Assert.Equal("set $ellie_wood_given to true", q.Steps[0].OnDone.Single());
        Assert.Equal("set $ellie_trusts_you to true", q.OnComplete.Single());
        Assert.Equal(("gold", 200), (q.Rewards[0].Kind, q.Rewards[0].Amount));
        Assert.Equal(("random", 3, 6), (q.Rewards[1].Kind, q.Rewards[1].QMin, q.Rewards[1].QMax));
    }

    [Fact]
    public void Quest_ObjectiveBeforeStep_IsProblem()
    {
        var probs = new List<Problem>();
        Parse.Quest("quests/q.txt", "id = q\ntitle = Q\ncollect = wood x1\nstep = S\nflag = $x\n", probs);
        Assert.Contains(probs, p => p.Line == 3);
    }

    [Fact]
    public void Quest_KillAndFlag()
    {
        var probs = new List<Problem>();
        var q = Parse.Quest("quests/q.txt", "id = q\ntitle = Q\nstep = S\nkill = Aco_Tracker x5\nflag = $talked_to_rory\nreward = item wood x5\nreward = xp 50\n", probs);
        Assert.Empty(probs);
        Assert.Equal(("kill", "Aco_Tracker", 5), (q.Steps[0].Objectives[0].Kind, q.Steps[0].Objectives[0].Target, q.Steps[0].Objectives[0].Count));
        Assert.Equal(("flag", "talked_to_rory"), (q.Steps[0].Objectives[1].Kind, q.Steps[0].Objectives[1].Target));
        Assert.Equal(("item", 5), (q.Rewards[0].Kind, q.Rewards[0].Item.Count));
        Assert.Equal(("xp", 50), (q.Rewards[1].Kind, q.Rewards[1].Amount));
    }

    [Fact]
    public void Rule_Parses()
    {
        var probs = new List<Problem>();
        var rules = Parse.Rules("rules/r.txt", "# rules\nwhen boss_kill 5 -> set $saved_rory_d5 to true; start rory_thanks\nwhen kill any if $hunting -> set $kills to $kills + 1\nnonsense\n", probs);
        Assert.Equal(2, rules.Count);
        Assert.Equal(("boss_kill", "5", null), (rules[0].Event, rules[0].Arg, rules[0].Cond));
        Assert.Equal(new[] { "set $saved_rory_d5 to true", "start rory_thanks" }, rules[0].Actions);
        Assert.Equal(("kill", "any", "$hunting"), (rules[1].Event, rules[1].Arg, rules[1].Cond));
        Assert.Single(probs);
        Assert.Equal(4, probs[0].Line);
    }

    [Fact]
    public void Library_LoadsEllie()
    {
        var lib = Library.Load(Files(("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", EllieTwee), ("quests/ellie_errands.txt", EllieQuest)));
        Assert.Empty(lib.Problems);
        Assert.Single(lib.Npcs);
        Assert.Single(lib.Quests);
        Assert.StartsWith(":: StoryTitle", lib.Dialogue["Ellie"]);
    }

    [Fact]
    public void Library_SkipsBrokenNpc_KeepsOthers()
    {
        var lib = Library.Load(Files(
            ("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", EllieTwee),
            ("npcs/Bad/npc.txt", "name = Bad\n"),
            ("npcs/NoTwee/npc.txt", "name = N\nscene = TutorialArea\nspot = 0, 0\ndialogue = missing.twee\n")));
        Assert.Equal(new[] { "Ellie" }, lib.Npcs.Select(n => n.Id));
        Assert.Contains(lib.Problems, p => p.File == "npcs/Bad/npc.txt");
        Assert.Contains(lib.Problems, p => p.File == "npcs/NoTwee/npc.txt" && p.Message.Contains("missing.twee"));
    }

    [Fact]
    public void Library_FlagWarning_ForTypo()
    {
        var twee = ":: StoryTitle\n:: StoryData\n:: DialogStart\nHi\n(if: $elie_x)[[Go|DialogStart]]\n";
        var lib = Library.Load(Files(("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", twee),
            ("quests/q.txt", "id = q\ntitle = Q\nstart = set $ellie_x to true\nstep = S\nflag = $ellie_x\n")));
        Assert.Single(lib.FlagWarnings);
        Assert.Contains("elie_x", lib.FlagWarnings[0].Message);
        Assert.Equal("npcs/Ellie/ellie.twee", lib.FlagWarnings[0].File);
    }

    [Fact]
    public void Library_ComputedFlags_NoWarning()
    {
        var twee = ":: StoryTitle\n:: StoryData\n:: DialogStart\nHi\n(if: $has_wood_10 and $quest_q_done)[[Go|DialogStart]]\n";
        var lib = Library.Load(Files(("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", twee)));
        Assert.Empty(lib.FlagWarnings);
    }

    /// <summary>The shipped Content folder (Ellie, her quest, the example rules) loads clean.</summary>
    [Fact]
    public void EllieFiles_LoadWithoutProblems()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "BetterNPCsAndQuests", "Content"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string root = System.IO.Path.Combine(dir.FullName, "BetterNPCsAndQuests", "Content");
        var files = System.IO.Directory.GetFiles(root, "*", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("README.txt") && !f.EndsWith("TEMPLATE.twee"))
            .ToDictionary(f => f.Substring(root.Length + 1).Replace('\\', '/'), System.IO.File.ReadAllBytes);
        var lib = Library.Load(files);
        Assert.Empty(lib.Problems.Select(p => p.ToString()));
        Assert.Empty(lib.FlagWarnings.Select(p => p.ToString()));
        Assert.Single(lib.Npcs);
        Assert.Single(lib.Quests);
        var r = Twee.Inspect(lib.Dialogue["Ellie"]);
        Assert.Contains("start ellie_errands", r.Triggers);
        Assert.Contains("give ellie_errands", r.Triggers);   // (handed over through her box)
    }

    /// <summary>Ellie's conversation, walked through the Book: start, wood, sword, done.</summary>
    [Fact]
    public void EllieQuest_PlaysThrough()
    {
        var lib = Library.Load(Files(("quests/ellie_errands.txt", EllieQuest.Replace("wood x10", "softwood x10"))));
        var book = new Book(lib);
        int completed = 0;
        book.QuestCompleted += _ => completed++;
        var bag = new Dictionary<string, int> { ["softwood"] = 10, ["sword"] = 1 };
        var b = new DictBag(bag);
        Assert.True(Expr.Test("not $quest_ellie_errands_active", book.Read));
        book.Run("start ellie_errands");
        Assert.True(Expr.Test("$quest_ellie_errands_step is 1", book.Read));
        book.Run("turnin ellie_errands", b);
        Assert.True(Expr.Test("$quest_ellie_errands_step is 2", book.Read));
        book.Run("turnin ellie_errands", b);
        Assert.True(Expr.Test("$quest_ellie_errands_done and $ellie_trusts_you", book.Read));
        Assert.Equal(1, completed);
        Assert.Equal(0, bag["softwood"] + bag["sword"]);
    }

    sealed class DictBag : IBag
    {
        readonly Dictionary<string, int> items;
        public DictBag(Dictionary<string, int> items) => this.items = items;
        public int Count(ItemSpec s) => items.GetValueOrDefault(s.Name ?? s.Category);
        public bool Take(ItemSpec s, int n) { items[s.Name ?? s.Category] -= n; return true; }
    }

    [Fact]
    public void Library_EmptyDialogue_NpcSkipped()
    {
        var lib = Library.Load(Files(("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", ":: StoryTitle\nx\n:: StoryData\ny\n")));
        Assert.Empty(lib.Npcs);
        Assert.Contains(lib.Problems, p => p.File == "npcs/Ellie/ellie.twee");
        var none = Library.Load(Files(("npcs/Ellie/npc.txt", EllieNpc), ("npcs/Ellie/ellie.twee", "just some text, no passages")));
        Assert.Empty(none.Npcs);
    }

    [Fact]
    public void Library_DuplicateQuestId_SecondSkipped()
    {
        var lib = Library.Load(Files(("quests/a.txt", "id = q\ntitle = A\nstep = S\nflag = $x\n"), ("quests/b.txt", "id = q\ntitle = B\nstep = S\nflag = $x\n")));
        Assert.Single(lib.Quests);
        Assert.Contains(lib.Problems, p => p.Message.Contains("same id"));
    }
}

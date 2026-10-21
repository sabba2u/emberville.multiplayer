using System.Collections.Generic;

namespace BetterNPCsAndQuests.Core;

/// <summary>A file's problem, for the log and the Insert tab.</summary>
public sealed class Problem
{
    public string File;
    public int Line;
    public string Message;
    public Problem(string file, int line, string message) { File = file; Line = line; Message = message; }
    public override string ToString() => Line > 0 ? $"{File}:{Line}: {Message}" : $"{File}: {Message}";
}

/// <summary>Items by name ("wood", "Iron Ore") or by kind ("any sword"), how many, and whether they're handed over.</summary>
public sealed class ItemSpec
{
    public string Name;       // null for "any <category>"
    public string Category;   // sword, axe, mace, dagger, bow, crossbow, staff, wand, weapon, armor, material, consumable, gear
    public int Count = 1;
    public bool Take;
    public override string ToString() => (Name ?? "any " + Category) + (Count != 1 ? " x" + Count : "");
}

public sealed class NpcDef
{
    public string Id, Name, Scene, Facing = "down", Outfit = "light", Holding = "none", Color = "#ffffff", Dialogue, Portrait, Folder;
    public float X, Y, Wander, PauseMin = 3f, PauseMax = 8f;
    public int Hair = -1;   // -1: keep the copied character's hair
}

public sealed class ObjectiveDef
{
    public string Kind;     // collect, kill, flag
    public ItemSpec Item;   // collect
    public string Target;   // kill: the creature's key (or "any"); flag: the flag's name
    public int Count = 1;
}

public sealed class StepDef
{
    public string Title;
    public List<ObjectiveDef> Objectives = new();
    public List<string> OnDone = new();
}

public sealed class RewardDef
{
    public string Kind;     // gold, item, random, xp, set
    public string Arg;      // set: the action
    public ItemSpec Item;   // item
    public int Amount, QMin = 3, QMax = 6;
}

public sealed class QuestDef
{
    public string Id, Title, Text, File;
    public List<string> OnStart = new();
    public List<StepDef> Steps = new();
    public List<string> OnComplete = new();
    public List<RewardDef> Rewards = new();
}

public sealed class RuleDef
{
    public string Event, Arg, Cond, File;
    public int Line;
    public List<string> Actions = new();
}

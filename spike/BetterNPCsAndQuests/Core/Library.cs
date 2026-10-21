using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BetterNPCsAndQuests.Core;

/// <summary>
/// Everything in the mod's folder, read: NPCs (npcs/&lt;Folder&gt;/npc.txt + their .twee and portrait), quests (quests/*.txt)
/// and rules (rules/*.txt). Paths are relative to the mod folder with "/". A broken file is skipped with its Problems;
/// the others still load. FlagWarnings: flags something checks that nothing ever sets (a typo, usually).
/// </summary>
public sealed class Library
{
    public List<NpcDef> Npcs = new();
    public List<QuestDef> Quests = new();
    public List<RuleDef> Rules = new();
    public Dictionary<string, string> Dialogue = new(StringComparer.OrdinalIgnoreCase);   // npc id → normalized .twee
    public Dictionary<string, byte[]> Portraits = new(StringComparer.OrdinalIgnoreCase);  // npc id → png
    public List<Problem> Problems = new();
    public List<Problem> FlagWarnings = new();

    /// <summary>Flags worked out on the spot (inventory, quests), never set by anyone.</summary>
    public static bool Computed(string flag) => flag.StartsWith("has_") || flag.StartsWith("quest_");

    public QuestDef Quest(string id) => Quests.FirstOrDefault(q => q.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public NpcDef Npc(string id) => Npcs.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static Library Load(IReadOnlyDictionary<string, byte[]> raw)
    {
        var lib = new Library();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw) files[kv.Key.Replace('\\', '/').TrimStart('/')] = kv.Value;
        string Text(string path) => Encoding.UTF8.GetString(files[path]).TrimStart('﻿');

        var reads = new List<(string file, string flag)>();
        var sets = new HashSet<string>();
        void Sets(IEnumerable<string> actions)
        {
            foreach (var a in actions)
            {
                var names = Expr.Names(a).ToList();
                if (a.TrimStart().StartsWith("set ") && names.Count > 0) sets.Add(names[0]);
            }
        }

        foreach (var path in files.Keys.Where(p => p.StartsWith("npcs/") && p.EndsWith("/npc.txt")).OrderBy(p => p))
        {
            var parts = path.Split('/');
            if (parts.Length != 3) continue;
            string folder = parts[1], dir = "npcs/" + folder + "/";
            var npc = Parse.Npc(path, folder, Text(path), lib.Problems);
            if (npc == null) continue;
            if (lib.Npc(npc.Id) != null) { lib.Problems.Add(new Problem(path, 0, $"another NPC has the same id \"{npc.Id}\": skipped")); continue; }
            string twee = dir + npc.Dialogue;
            if (!files.ContainsKey(twee)) { lib.Problems.Add(new Problem(path, 0, $"dialogue file {npc.Dialogue} not found in {dir}")); continue; }
            string normalized = Twee.Normalize(Text(twee));
            var report = Twee.Inspect(normalized);
            foreach (var p in report.Problems) lib.Problems.Add(new Problem(twee, 0, p));
            if (report.Passages.Count == 0) { lib.Problems.Add(new Problem(path, 0, $"{npc.Name} has nothing to say ({npc.Dialogue} has no passages): skipped")); continue; }
            foreach (var f in report.FlagsRead) reads.Add((twee, f));
            sets.UnionWith(report.FlagsSet);
            Sets(report.Triggers);
            string png = dir + (npc.Portrait ?? "portrait.png");
            if (files.ContainsKey(png)) lib.Portraits[npc.Id] = files[png];
            else if (npc.Portrait != null) lib.Problems.Add(new Problem(path, 0, $"portrait {npc.Portrait} not found (using one made from the NPC)"));
            lib.Dialogue[npc.Id] = normalized;
            lib.Npcs.Add(npc);
        }

        foreach (var path in files.Keys.Where(p => p.StartsWith("quests/") && p.EndsWith(".txt")).OrderBy(p => p))
        {
            var q = Parse.Quest(path, Text(path), lib.Problems);
            if (q == null) continue;
            if (lib.Quest(q.Id) != null) { lib.Problems.Add(new Problem(path, 0, $"another quest has the same id \"{q.Id}\": skipped")); continue; }
            lib.Quests.Add(q);
            Sets(q.OnStart); Sets(q.OnComplete);
            foreach (var s in q.Steps)
            {
                Sets(s.OnDone);
                foreach (var o in s.Objectives) if (o.Kind == "flag") reads.Add((path, o.Target));
            }
            Sets(q.Rewards.Where(r => r.Kind == "set").Select(r => r.Arg));
        }

        foreach (var path in files.Keys.Where(p => p.StartsWith("rules/") && p.EndsWith(".txt")).OrderBy(p => p))
        {
            var rules = Parse.Rules(path, Text(path), lib.Problems);
            lib.Rules.AddRange(rules);
            foreach (var r in rules)
            {
                Sets(r.Actions);
                foreach (var n in Expr.Names(r.Cond)) reads.Add((path, n));
                if (r.Event == "flag" && r.Arg != null) reads.Add((path, r.Arg.TrimStart('$').ToLowerInvariant()));
            }
        }

        foreach (var (file, flag) in reads.Distinct())
            if (!sets.Contains(flag) && !Computed(flag))
                lib.FlagWarnings.Add(new Problem(file, 0, $"${flag} is checked here but nothing ever sets it (a typo?)"));
        return lib;
    }
}

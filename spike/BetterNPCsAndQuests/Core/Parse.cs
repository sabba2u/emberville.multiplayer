using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterNPCsAndQuests.Core;

/// <summary>The npc.txt, quest and rules file formats (see README.txt). A bad file adds Problems and returns null.</summary>
public static class Parse
{
    public static readonly string[] Categories = { "sword", "axe", "mace", "dagger", "bow", "crossbow", "staff", "wand", "weapon", "armor", "material", "consumable", "gear" };
    public static readonly string[] Qualities = { "none", "broken", "ordinary", "good", "superior", "epic", "legendary" };
    public static readonly string[] Events = { "kill", "boss_kill", "descent_floor", "enter", "pickup", "quest_done", "step_done", "flag" };
    /// <summary>Scenes made fresh for each world (or not a place at all): NPCs can't stand there.</summary>
    public static readonly string[] NotFixedScenes = { "DungeonScene", "MainMenu" };

    static readonly Regex Spec = new(@"^(?:(any)\s+)?(.+?)(?:\s+x(\d+))?(\s+take)?$", RegexOptions.IgnoreCase);

    /// <summary>"wood x10 take", "any sword x1 take", "Iron Ore"; null if unreadable.</summary>
    public static ItemSpec ItemSpec(string text)
    {
        var m = Spec.Match((text ?? "").Trim());
        if (!m.Success) return null;
        var s = new ItemSpec { Count = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1, Take = m.Groups[4].Success };
        string what = m.Groups[2].Value.Trim();
        if (m.Groups[1].Success)
        {
            what = what.ToLowerInvariant();
            if (what == "armour") what = "armor";
            if (!Categories.Contains(what)) return null;
            s.Category = what;
        }
        else s.Name = what;
        return s.Count > 0 ? s : null;
    }

    /// <summary>One action: set $x to ..., start / advance / turnin &lt;quest id&gt;, command &lt;another mod's chat command&gt;.</summary>
    public static bool IsAction(string action)
    {
        action = (action ?? "").Trim();
        if (action.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            return Expr.TryAssign(action.Substring(4), _ => 0, out _, out _);
        if (action.StartsWith("command ", StringComparison.OrdinalIgnoreCase)) return action.Substring(8).Trim().Length > 0;
        var parts = action.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && (parts[0] == "start" || parts[0] == "advance" || parts[0] == "turnin" || parts[0] == "give");
    }

    public static NpcDef Npc(string file, string folder, string text, List<Problem> problems)
    {
        var n = new NpcDef { Id = folder, Folder = folder };
        bool spot = false, ok = true;
        void Bad(int line, string msg) { problems.Add(new Problem(file, line, msg)); ok = false; }
        foreach (var (line, key, value) in KvFile.Read(text))
        {
            switch (key)
            {
                case "name": n.Name = value; break;
                case "id": n.Id = value; break;
                case "scene": n.Scene = value; break;
                case "spot":
                    var xy = (value ?? "").Split(',');
                    if (xy.Length == 2 && Num(xy[0], out n.X) && Num(xy[1], out n.Y)) spot = true;
                    else Bad(line, "spot must be two numbers: spot = x, y");
                    break;
                case "facing": n.Facing = (value ?? "down").ToLowerInvariant(); break;
                case "hair": if (!int.TryParse(value, out n.Hair)) Bad(line, "hair must be a number"); break;
                case "outfit": n.Outfit = (value ?? "").ToLowerInvariant(); break;
                case "holding": n.Holding = value; break;
                case "color": case "colour": n.Color = value; break;
                case "dialogue": n.Dialogue = value; break;
                case "portrait": n.Portrait = value; break;
                case "wander": if (!Num(value, out n.Wander)) Bad(line, "wander must be a number (0 = stands still)"); break;
                case "pause":
                    var ab = (value ?? "").Split('-');
                    if (ab.Length == 1 && Num(ab[0], out n.PauseMin)) n.PauseMax = n.PauseMin;
                    else if (!(ab.Length == 2 && Num(ab[0], out n.PauseMin) && Num(ab[1], out n.PauseMax))) Bad(line, "pause must be seconds, or a range: pause = 3-8");
                    break;
                default: Bad(line, $"unknown key \"{key}\""); break;
            }
        }
        if (string.IsNullOrEmpty(n.Name)) Bad(0, "missing name");
        if (string.IsNullOrEmpty(n.Scene)) Bad(0, "missing scene");
        else if (NotFixedScenes.Any(s => s.Equals(n.Scene, StringComparison.OrdinalIgnoreCase)))
            Bad(0, $"scene {n.Scene} is generated or not a place: NPCs can only stand in fixed scenes");
        if (!spot) Bad(0, "missing spot (spot = x, y; the Insert tab copies yours)");
        if (string.IsNullOrEmpty(n.Dialogue)) Bad(0, "missing dialogue (dialogue = name.twee)");
        return ok ? n : null;
    }

    public static QuestDef Quest(string file, string text, List<Problem> problems)
    {
        var q = new QuestDef { File = file, Id = System.IO.Path.GetFileNameWithoutExtension(file) };
        int before = problems.Count;
        StepDef step = null;
        void Bad(int line, string msg) => problems.Add(new Problem(file, line, msg));
        void Act(List<string> to, int line, string value) { if (IsAction(value)) to.Add(value.Trim()); else Bad(line, $"can't read the action \"{value}\""); }
        foreach (var (line, key, value) in KvFile.Read(text))
        {
            if ((key == "collect" || key == "kill" || key == "flag" || key == "done") && step == null) { Bad(line, $"\"{key}\" needs a step = ... line above it"); continue; }
            switch (key)
            {
                case "id": q.Id = value; break;
                case "title": q.Title = value; break;
                case "text": q.Text = value; break;
                case "start": Act(q.OnStart, line, value); break;
                case "step": step = new StepDef { Title = value }; q.Steps.Add(step); break;
                case "collect":
                    var spec = ItemSpec(value);
                    if (spec == null) Bad(line, $"can't read the items \"{value}\" (e.g. wood x10 take, any sword x1)");
                    else step.Objectives.Add(new ObjectiveDef { Kind = "collect", Item = spec, Count = spec.Count });
                    break;
                case "kill":
                    var k = Regex.Match(value ?? "", @"^(\S+)(?:\s+x(\d+))?$");
                    if (!k.Success) Bad(line, "kill = <creature> x<count>");
                    else step.Objectives.Add(new ObjectiveDef { Kind = "kill", Target = k.Groups[1].Value, Count = k.Groups[2].Success ? int.Parse(k.Groups[2].Value) : 1 });
                    break;
                case "flag":
                    var f = Expr.Names(value).FirstOrDefault();
                    if (f == null) Bad(line, "flag = $name");
                    else step.Objectives.Add(new ObjectiveDef { Kind = "flag", Target = f });
                    break;
                case "done": Act(step.OnDone, line, value); break;
                case "complete": Act(q.OnComplete, line, value); break;
                case "reward":
                    var r = Reward(value);
                    if (r == null) Bad(line, $"can't read the reward \"{value}\" (gold 200, item wood x5, random good-legendary, xp 50, set $x to true)");
                    else q.Rewards.Add(r);
                    break;
                default: Bad(line, $"unknown key \"{key}\""); break;
            }
        }
        if (string.IsNullOrEmpty(q.Id)) Bad(0, "missing id");
        if (string.IsNullOrEmpty(q.Title)) q.Title = q.Id;
        if (q.Steps.Count == 0) Bad(0, "a quest needs at least one step");
        foreach (var s in q.Steps) if (s.Objectives.Count == 0) Bad(0, $"step \"{s.Title}\" has nothing to do (collect, kill or flag)");
        return problems.Count == before ? q : null;
    }

    static RewardDef Reward(string text)
    {
        text = (text ?? "").Trim();
        int sp = text.IndexOf(' ');
        string kind = (sp < 0 ? text : text.Substring(0, sp)).ToLowerInvariant(), arg = sp < 0 ? "" : text.Substring(sp + 1).Trim();
        switch (kind)
        {
            case "gold": case "xp": return int.TryParse(arg, out var n) && n > 0 ? new RewardDef { Kind = kind, Amount = n } : null;
            case "item": var spec = ItemSpec(arg); return spec != null && spec.Name != null ? new RewardDef { Kind = "item", Item = spec } : null;
            case "set": return IsAction(text) ? new RewardDef { Kind = "set", Arg = text } : null;
            case "random":
                var r = new RewardDef { Kind = "random" };
                if (arg.Length == 0) return r;
                var ab = arg.ToLowerInvariant().Split('-');
                int a = Array.IndexOf(Qualities, ab[0].Trim()), b = ab.Length > 1 ? Array.IndexOf(Qualities, ab[1].Trim()) : a;
                if (a < 0 || b < 0 || ab.Length > 2) return null;
                r.QMin = Math.Min(a, b); r.QMax = Math.Max(a, b);
                return r;
        }
        return null;
    }

    public static List<RuleDef> Rules(string file, string text, List<Problem> problems)
    {
        var list = new List<RuleDef>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            void Bad(string msg) => problems.Add(new Problem(file, i + 1, msg));
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (!line.StartsWith("when ", StringComparison.OrdinalIgnoreCase) || arrow < 0) { Bad("rules look like: when <event> [what] [if <condition>] -> <action>; <action>"); continue; }
            string left = line.Substring(5, arrow - 5).Trim(), right = line.Substring(arrow + 2).Trim();
            var r = new RuleDef { File = file, Line = i + 1 };
            int iff = Regex.Match(left, @"\s+if\s+").Index;
            if (iff > 0) { r.Cond = left.Substring(iff).Trim().Substring(2).Trim(); left = left.Substring(0, iff).Trim(); }
            int sp = left.IndexOf(' ');
            r.Event = (sp < 0 ? left : left.Substring(0, sp)).ToLowerInvariant();
            r.Arg = sp < 0 ? null : left.Substring(sp + 1).Trim();
            if (!Events.Contains(r.Event)) { Bad($"unknown event \"{r.Event}\" ({string.Join(", ", Events)})"); continue; }
            bool ok = true;
            foreach (var a in right.Split(';').Select(a => a.Trim()).Where(a => a.Length > 0))
                if (IsAction(a)) r.Actions.Add(a); else { Bad($"can't read the action \"{a}\""); ok = false; }
            if (ok && r.Actions.Count > 0) list.Add(r);
            else if (ok) Bad("a rule needs an action after ->");
        }
        return list;
    }

    static bool Num(string s, out float v) => float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}

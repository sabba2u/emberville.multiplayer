using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterNPCsAndQuests.Core;

/// <summary>What one Twine file holds, as the game's dialogue reader will see it.</summary>
public sealed class TweeReport
{
    public HashSet<string> Passages = new();
    public HashSet<string> FlagsRead = new();
    public HashSet<string> FlagsSet = new();
    public List<string> Triggers = new();
    public List<string> Problems = new();
}

/// <summary>
/// Twine (.twee) files for the game's dialogue reader (DialogueManager.GetAllNodes / CreateNode). Its traps:
/// it splits the file at every "::" and skips the first two pieces (Twine's StoryTitle and StoryData), and a passage's
/// tags (NPC:Ellie, VO:..., TRIGGER:...) only count inside the header's [brackets].
/// </summary>
public static class Twee
{
    /// <summary>A choice to "END" ends the conversation (the game ends it on a passage that isn't there).</summary>
    public const string EndTarget = "END";

    static readonly Regex LooseTag = new(@"^\w+:\S*$");
    static readonly Regex Choice = new(@"\[\[([^|\]]+)(?:\|([^\]]+))?\]\]");
    static readonly Regex Command = new(@"(?<=\()(.+?):(.*?)(?=\))");
    static readonly Regex HeaderToken = new(@"(\w+):([^\s\]]*)");
    static readonly Regex TextToken = new(@"{(.*?):(.*?)}");

    /// <summary>
    /// The file as the game must get it: plain \n line ends, nothing before the first passage, StoryTitle and StoryData
    /// first (empty ones added if missing), and loose tags in a header moved into [brackets].
    /// </summary>
    public static string Normalize(string text)
    {
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        int first = text.IndexOf("::", StringComparison.Ordinal);
        text = first < 0 ? "" : text.Substring(first);

        var pieces = text.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        for (int i = 0; i < pieces.Count; i++) pieces[i] = FixHeader(pieces[i]);
        bool title = pieces.Count > 0 && Id(pieces[0]) == "StoryTitle";
        bool data = pieces.Count > 1 && Id(pieces[1]) == "StoryData";
        if (title && !data) pieces.Insert(1, " StoryData\n\n");
        else if (!title)
        {
            if (pieces.Count > 0 && Id(pieces[0]) == "StoryData") pieces.Insert(0, " StoryTitle\n\n");
            else { pieces.Insert(0, " StoryData\n\n"); pieces.Insert(0, " StoryTitle\n\n"); }
        }
        var sb = new StringBuilder();
        foreach (var p in pieces) sb.Append("::").Append(p);
        return sb.ToString();
    }

    /// <summary>"DialogStart NPC:Ellie VO:x" → "DialogStart [NPC:Ellie VO:x]" (only when there are no brackets yet).</summary>
    static string FixHeader(string piece)
    {
        int nl = piece.IndexOf('\n');
        string header = nl < 0 ? piece : piece.Substring(0, nl);
        string rest = nl < 0 ? "" : piece.Substring(nl);
        if (header.Contains('[')) return piece;
        string meta = "";
        int brace = header.IndexOf('{');
        if (brace >= 0) { meta = " " + header.Substring(brace); header = header.Substring(0, brace); }
        var words = header.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var tags = words.Where(w => LooseTag.IsMatch(w)).ToList();
        if (tags.Count == 0) return piece;
        var name = words.Where(w => !LooseTag.IsMatch(w));
        return " " + string.Join(" ", name) + " [" + string.Join(" ", tags) + "]" + meta + rest;
    }

    /// <summary>A passage's id, as the game reads it: the header up to [ , or {.</summary>
    static string Id(string piece)
    {
        string header = piece.Split('\n')[0];
        return Regex.Match(header, @"(^[^\[,{]*)").Groups[1].Value.Trim();
    }

    /// <summary>Passages, flags read and set, triggers, and problems, of a normalized file.</summary>
    public static TweeReport Inspect(string text)
    {
        var r = new TweeReport();
        var pieces = (text ?? "").Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
        var targets = new List<(string passage, string text, string target)>();
        for (int i = 2; i < pieces.Length; i++)
        {
            string id = Id(pieces[i]);
            r.Passages.Add(id);
            var lines = pieces[i].Trim().Split('\n');
            foreach (Match t in HeaderToken.Matches(lines[0])) Token(r, t.Groups[1].Value, t.Groups[2].Value);
            int choices = 0;
            bool hasText = false;   // (a passage without text is a router: the game tries all its choices, no limit)
            foreach (var raw in lines.Skip(1))
            {
                string line = raw.Trim();
                if (!line.StartsWith("(") && !line.StartsWith("["))
                {
                    if (line.Length > 0) hasText = true;
                    foreach (Match t in TextToken.Matches(line)) Token(r, t.Groups[1].Value, t.Groups[2].Value);
                    continue;
                }
                var commands = Command.Matches(line);
                var choice = Choice.Match(line);
                if (choice.Success)
                {
                    choices++;
                    string target = choice.Groups[2].Success ? choice.Groups[2].Value : choice.Groups[1].Value;
                    targets.Add((id, choice.Groups[1].Value, target.Trim()));
                    foreach (Match c in commands)
                        if (c.Groups[1].Value.Trim() == "if") foreach (var n in Expr.Names(c.Groups[2].Value)) r.FlagsRead.Add(n);
                    continue;
                }
                foreach (Match c in commands)
                {
                    string kind = c.Groups[1].Value.Trim(), body = c.Groups[2].Value;
                    if (kind == "set")
                    {
                        var names = Expr.Names(body).ToList();
                        if (names.Count == 0) continue;
                        r.FlagsSet.Add(names[0]);
                        foreach (var n in names.Skip(1)) r.FlagsRead.Add(n);
                    }
                    else if (kind == "trigger" || kind == "trigger_out") r.Triggers.Add(body.Trim().Trim('"'));
                }
            }
            if (hasText && choices > 6) r.Problems.Add($"passage \"{id}\" has more than 6 choices (the game shows only 6)");
        }
        foreach (var (passage, label, target) in targets)
            if (target != EndTarget && !r.Passages.Contains(target)) r.Problems.Add($"choice \"{label}\" in \"{passage}\" goes to missing passage \"{target}\"");
        if (r.Passages.Count > 0 && !r.Passages.Contains("DialogStart"))
            r.Problems.Add("no DialogStart passage (the game then starts at the first passage)");
        if (r.Passages.Count == 0) r.Problems.Add("no passages (keep the two header passages, then write yours below them)");
        return r;
    }

    static void Token(TweeReport r, string key, string value)
    {
        if (key == "TRIGGER" || key == "TRIGGER_OUT") r.Triggers.Add(value);
    }
}

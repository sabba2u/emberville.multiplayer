using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BetterNPCsAndQuests.Core;

/// <summary>
/// Conditions and assignments, a little more than the game's dialogue reader (which knows only true / false, "and",
/// "not"): numbers and comparisons too.
///   condition:  term [and term]...   term = [not ]$a  |  $a OP value   OP = >= &lt;= &gt; &lt; == != is
///   value:      a whole number, true / false, or $b
///   assignment: $x to value  |  $x to $y + n  |  $x to $y - n
/// An empty condition is true; one we can't read is false.
/// </summary>
public static class Expr
{
    static readonly Regex Name = new(@"\$(\w+)");
    static readonly Regex Compare = new(@"^\$(\w+)\s*(>=|<=|==|!=|>|<|\bis\b)\s*(\S+)$");
    static readonly Regex Plain = new(@"^(not\s+)?\$(\w+)$");
    static readonly Regex Assignment = new(@"^\$(\w+)\s+to\s+(\$\w+|-?\d+|true|false)\s*(?:([+-])\s*(\d+))?$", RegexOptions.IgnoreCase);

    public static IEnumerable<string> Names(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Name.Matches(text)) yield return m.Groups[1].Value.ToLowerInvariant();
    }

    public static bool Test(string cond, Func<string, int> read)
    {
        if (string.IsNullOrWhiteSpace(cond)) return true;
        foreach (var raw in Regex.Split(cond.Trim(), @"\s+and\s+"))
        {
            string term = raw.Trim();
            var p = Plain.Match(term);
            if (p.Success)
            {
                bool on = read(p.Groups[2].Value.ToLowerInvariant()) != 0;
                if (p.Groups[1].Success) on = !on;
                if (!on) return false;
                continue;
            }
            var c = Compare.Match(term);
            if (!c.Success || !TryValue(c.Groups[3].Value, read, out int right)) return false;
            int left = read(c.Groups[1].Value.ToLowerInvariant());
            bool ok = c.Groups[2].Value switch
            {
                ">=" => left >= right,
                "<=" => left <= right,
                ">" => left > right,
                "<" => left < right,
                "!=" => left != right,
                _ => left == right,   // == and is
            };
            if (!ok) return false;
        }
        return true;
    }

    public static bool TryAssign(string body, Func<string, int> read, out string name, out int value)
    {
        name = null;
        value = 0;
        var m = Assignment.Match((body ?? "").Trim());
        if (!m.Success || !TryValue(m.Groups[2].Value, read, out value)) return false;
        name = m.Groups[1].Value.ToLowerInvariant();
        if (m.Groups[3].Success)
        {
            int n = int.Parse(m.Groups[4].Value);
            value = m.Groups[3].Value == "+" ? value + n : value - n;
        }
        return true;
    }

    static bool TryValue(string text, Func<string, int> read, out int value)
    {
        text = text.Trim().Trim('"');
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) { value = 1; return true; }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
        if (text.StartsWith("$") && text.Length > 1) { value = read(text.Substring(1).ToLowerInvariant()); return true; }
        return int.TryParse(text, out value);
    }
}

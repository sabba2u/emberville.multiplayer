using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BetterNPCsAndQuests.Core;

/// <summary>
/// Our own story flags: any name, no declaration (a flag exists once something sets it; unset reads 0), shared by every
/// NPC, quest and rule. Names are kept without the leading $ and ignore case. True / false are 1 / 0.
/// </summary>
public sealed class FlagStore
{
    readonly Dictionary<string, int> values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> All => values;

    /// <summary>(name, new value) after a real change.</summary>
    public event Action<string, int> Changed;

    public static string Key(string name) => (name ?? "").Trim().TrimStart('$').ToLowerInvariant();

    public int Get(string name) => values.TryGetValue(Key(name), out var v) ? v : 0;

    public void Set(string name, int value)
    {
        string k = Key(name);
        if (k.Length == 0) return;
        if (values.TryGetValue(k, out var old) && old == value) return;
        values[k] = value;
        Changed?.Invoke(k, value);
    }

    public void Clear(string name)
    {
        string k = Key(name);
        if (values.Remove(k)) Changed?.Invoke(k, 0);
    }

    /// <summary>One "name\tvalue" line per flag, sorted.</summary>
    public string Save()
    {
        var sb = new StringBuilder();
        foreach (var kv in values.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
        return sb.ToString();
    }

    public static FlagStore Load(string text)
    {
        var f = new FlagStore();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 2 && int.TryParse(parts[1], out var v)) f.values[Key(parts[0])] = v;
        }
        return f;
    }
}

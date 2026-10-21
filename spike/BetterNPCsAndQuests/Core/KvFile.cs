using System.Collections.Generic;

namespace BetterNPCsAndQuests.Core;

/// <summary>
/// "key = value" files (like the BepInEx .cfg files players already edit): one per line, keys ignore case, order kept,
/// a key may repeat. A line starting with # is a comment (a # later in a line is part of the value: colours).
/// </summary>
public static class KvFile
{
    public static List<(int line, string key, string value)> Read(string text)
    {
        var list = new List<(int, string, string)>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) { list.Add((i + 1, line.ToLowerInvariant(), null)); continue; }
            list.Add((i + 1, line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim()));
        }
        return list;
    }
}

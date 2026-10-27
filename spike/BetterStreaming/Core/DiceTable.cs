using System.Collections.Generic;

namespace BetterStreaming.Core;

/// <summary>
/// Roll the Dice: three dice, and their total (3 to 18) picks the outcome from the streamer's table, as the config has
/// it: "3:death, 4-5:storm, 6-7:raid ...". Low is bad, the middle mild, high good. Three 1s are always the table's 3
/// (instant death by default), three 6s its 18 (the jackpot). Broken entries are left out; a total with no entry: "".
/// </summary>
public sealed class DiceTable
{
    /// <summary>"silly" is a random naughty one of the 30-second kind; "jackpot" three good ones at once.</summary>
    public const string Silly = "silly", Jackpot = "jackpot";

    public const string Default = "3:death,4-5:storm,6-7:raid,8-9:chickens,10-11:silly,12-13:heal,14-15:haste,16-17:chainlightning,18:jackpot";

    readonly string[] byTotal = new string[19];

    public static DiceTable Parse(string text)
    {
        var t = new DiceTable();
        foreach (var part in (text ?? "").Split(','))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) continue;
            string id = kv[1].Trim().ToLowerInvariant();
            if (id.Length == 0) continue;
            var range = kv[0].Trim().Split('-');
            if (!int.TryParse(range[0], out int lo)) continue;
            int hi = lo;
            if (range.Length > 1 && !int.TryParse(range[1], out hi)) continue;
            for (int n = System.Math.Max(3, lo); n <= System.Math.Min(18, hi); n++) t.byTotal[n] = id;
        }
        return t;
    }

    public string Outcome(int a, int b, int c)
    {
        int total = a + b + c;
        if (a == b && b == c && (a == 1 || a == 6)) total = a == 1 ? 3 : 18;
        return total >= 3 && total <= 18 ? byTotal[total] ?? "" : "";
    }

    /// <summary>Every outcome the table can give (to check they exist).</summary>
    public IEnumerable<string> All()
    {
        var seen = new HashSet<string>();
        for (int n = 3; n <= 18; n++) if (byTotal[n] != null && seen.Add(byTotal[n])) yield return byTotal[n];
    }
}

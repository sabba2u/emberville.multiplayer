using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace BetterStreaming.Core;

/// <summary>
/// Who did the most to the streamer: Bits spent on naughty effects (raiders), on nice ones (healers), and deaths caused.
/// Names count together whatever their case. Saved as JSON (kept across streams until the streamer clears it).
/// </summary>
public sealed class Leaderboard
{
    public const string Raider = "raider", Healer = "healer", Killer = "killer";

    [JsonProperty] Dictionary<string, Dictionary<string, int>> boards = new Dictionary<string, Dictionary<string, int>>();

    public void Add(string viewer, string board, int amount)
    {
        if (string.IsNullOrWhiteSpace(viewer) || amount <= 0) return;
        if (!boards.TryGetValue(board, out var b)) boards[board] = b = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        b[viewer] = (b.TryGetValue(viewer, out int had) ? had : 0) + amount;
    }

    public List<(string Viewer, int Amount)> Top(string board, int count) =>
        boards.TryGetValue(board, out var b)
            ? b.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(count).Select(kv => (kv.Key, kv.Value)).ToList()
            : new List<(string, int)>();

    public void Clear() => boards.Clear();

    public string Save() => JsonConvert.SerializeObject(this);

    public static Leaderboard Load(string json)
    {
        try
        {
            var b = JsonConvert.DeserializeObject<Leaderboard>(json) ?? new Leaderboard();
            // (back to case-blind names after loading)
            b.boards = b.boards.ToDictionary(kv => kv.Key, kv => new Dictionary<string, int>(kv.Value, StringComparer.OrdinalIgnoreCase));
            return b;
        }
        catch (JsonException) { return new Leaderboard(); }
    }
}

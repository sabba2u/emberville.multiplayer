using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterStreaming.Core;

/// <summary>What a cheer bought: the effect (null: nothing, only a thank-you) and how big (1 = its base size).</summary>
public readonly struct Purchase
{
    public readonly EffectDef Effect;
    public readonly float Size;
    public Purchase(EffectDef effect, float size) { Effect = effect; Size = size; }
}

/// <summary>
/// A cheer to an effect: a keyword in its message first (one it can afford), then the effect priced at exactly the
/// amount, then the dearest scaling effect it can afford, then the dearest of any. Only a cheer below every price buys
/// nothing (a thank-you). A scaling effect grows with the Bits (up to its cap).
/// Cheermotes ("Cheer100", "Kappa500") aren't words.
/// </summary>
public static class CheerRouter
{
    static readonly Regex Cheermote = new Regex(@"^[a-z]+\d+$", RegexOptions.Compiled);

    public static Purchase Route(IReadOnlyList<EffectDef> effects, int bits, string message)
    {
        var on = effects.Where(e => e != null && e.Enabled && e.Bits > 0).ToList();
        var words = Words(message);
        var picked = on.FirstOrDefault(e => e.Bits <= bits && e.Keywords != null && e.Keywords.Any(k => words.Contains(k.ToLowerInvariant())))
                     ?? on.FirstOrDefault(e => e.Bits == bits)
                     ?? on.Where(e => e.Scales && e.Bits <= bits).OrderByDescending(e => e.Bits).FirstOrDefault()
                     ?? on.Where(e => e.Bits <= bits).OrderByDescending(e => e.Bits).FirstOrDefault();
        if (picked == null) return new Purchase(null, 0f);
        float size = picked.Scales ? (float)System.Math.Min(bits, System.Math.Max(picked.CapBits, picked.Bits)) / picked.Bits : 1f;
        return new Purchase(picked, size);
    }

    static HashSet<string> Words(string message)
    {
        var set = new HashSet<string>();
        foreach (var token in (message ?? "").ToLowerInvariant().Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (Cheermote.IsMatch(token)) continue;
            foreach (var w in Regex.Split(token, "[^a-z]+")) if (w.Length > 0) set.Add(w);
        }
        return set;
    }
}

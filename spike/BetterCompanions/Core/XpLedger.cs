using System.Collections.Generic;

namespace BetterCompanions.Core;

/// <summary>
/// Which enemies the companion hit lately: when one dies, it earns the kill's XP only if it hit it within the last
/// XpWindow seconds. Each kill is credited once.
/// </summary>
public class XpLedger
{
    readonly Dictionary<int, float> lastHit = new();

    public void Hit(int enemyId, float now) => lastHit[enemyId] = now;

    public bool Credit(int enemyId, float now)
    {
        if (!lastHit.TryGetValue(enemyId, out float at)) return false;
        lastHit.Remove(enemyId);
        return now - at <= Rules.XpWindow;
    }

    public void Clear() => lastHit.Clear();
}

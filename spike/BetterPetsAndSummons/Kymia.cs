using UnityEngine;

namespace BetterPetsAndSummons;

/// <summary>
/// What summoning costs in Kymia (the game's mana), like the Hunter's Hook: a share of a full bar (a full bar is `uses`
/// summons), with the player's Kymia-cost modifiers like any attack. Not enough: the game's own "no Kymia" feedback.
/// </summary>
internal static class Kymia
{
    public static float Cost(PlayerMaster me, float uses) => me.MaxKymiaPoints / uses * me.AttackKymiaConsumptionMultiplier.Value;

    /// <summary>Is there enough (else the game's feedback)?</summary>
    public static bool Has(PlayerMaster me, float cost)
    {
        if (cost <= 0f || me.CurrentKymiaPoints + 0.01f >= cost) return true;
        me.NoKymiaFeedback();
        return false;
    }

    public static void Take(PlayerMaster me, float cost)
    {
        if (cost > 0f) me.ConsumeKymia(Mathf.Min(cost, me.CurrentKymiaPoints));
    }
}

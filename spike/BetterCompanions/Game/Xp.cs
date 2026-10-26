using HarmonyLib;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The companion's own XP and levels, on the game's XP tables (its save's PlayerData, keyed by its class): a creature
/// that dies within a few seconds of the companion hitting it (XpLedger) gives it the same XP a kill gives the player
/// (the creature's expRange). Every copy in co-op replays the death, so the owner's copy credits it by itself.
/// </summary>
[HarmonyPatch]
internal static class Xp
{
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetKilled)), HarmonyPostfix]
    static void OnEnemyDied(Enemy __instance)
    {
        var body = Companion.Body;
        if (body == null || body.Lost || !CompanionSave.Has || !__instance) return;
        if (!Companion.Ledger.Credit(__instance.GetInstanceID(), Time.time)) return;
        int xp = __instance.expRange.Random();
        if (xp <= 0) return;
        var data = CompanionSave.Data;
        var key = data.equippedClass;
        int before = data.GetCurrentLevel(key);
        data.GetPlayerExperience(key);   // (first: AddXP on a key it hasn't got goes wrong)
        data.AddXP(key, xp);
        int after = data.GetCurrentLevel(key);
        if (body.Stats) body.Stats.baseClassXP = data.GetPlayerExperience(key);
        CompanionSave.Dirty = true;
        if (after > before)
        {
            body.Unit.RefreshHealth();
            body.Fx("LevelUp");
            Hooks.Say($"{data.playerName} reached level {after}! (attribute points to spend: F, then the character panel)");
            CompanionSave.Write();
            CompanionSave.Dirty = false;
        }
    }
}

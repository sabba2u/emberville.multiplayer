using HarmonyLib;

namespace EmbervilleGhost;

/// <summary>
/// Creatures for a bigger party (the host's Session.PartyHealth / PartyDamage, % per player past the first). Health:
/// set where the game gives a creature its health (Enemy.OnEnable: on appearing, respawning, being reset), so the
/// descent's and the mini-bosses' own scaling go on top of it; a creature already out keeps what it had. Damage: in the
/// same step as the game's difficulty (EnemyDamageDifficultyModifier), creature hits only.
/// </summary>
[HarmonyPatch]
internal static class PartyScale
{
    public static Plugin Plugin;

    /// <summary>Players in the session (you included); 1 on your own.</summary>
    public static int Players => Plugin != null && Plugin.Connected ? System.Math.Max(1, Plugin.PlayerCount) : 1;
    public static float HealthMul => 1f + Session.PartyHealth * 0.01f * (Players - 1);
    public static float DamageMul => 1f + Session.PartyDamage * 0.01f * (Players - 1);

    [HarmonyPatch(typeof(Enemy), "OnEnable"), HarmonyPostfix]
    static void Health(Enemy __instance)
    {
        float mul = HealthMul;
        if (mul == 1f || !__instance || __instance.IsDead || __instance.initHealth <= 0f) return;
        __instance.initHealth *= mul;
        __instance.currentHealth = __instance.initHealth;
    }

    [HarmonyPatch(typeof(EnemyDamageDifficultyModifier), nameof(EnemyDamageDifficultyModifier.Process)), HarmonyPostfix]
    static void Damage(ref DamageData data)
    {
        if (!(data.Aggressor is Enemy)) return;
        float mul = DamageMul;
        if (mul != 1f) data.FAmount *= mul;
    }
}

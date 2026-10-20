using System.Collections.Generic;
using LiteNetLib.Utils;

namespace EmbervilleGhost;

/// <summary>
/// The parts of a hit beyond its damage, on the wire: status effects and the attack that made it.
///
/// A hit (DamageData from CombatSystem.GetDamageData) carries the effects that stuck (Effects: burn, poison, bleed,
/// freeze, shock, stun, trapped, shield, speed..., already rolled) and the special effects its attack creates
/// (CreatedEffects, from Attack.customEffects with the skill's level). The target applies them in its ApplyDamage
/// (Enemy.HandleEffect / EffectsHandler.HandleEffect), so a hit replayed on another copy needs them too.
/// Effects go as values; created effects as the attack's name + level, made again on the receiving side.
/// </summary>
internal static class CombatNet
{
    public static void PutEffects(NetDataWriter w, List<DamageEffect> effects)
    {
        int n = effects == null ? 0 : System.Math.Min(effects.Count, 255);
        w.Put((byte)n);
        for (int i = 0; i < n; i++)
        {
            var e = effects[i];
            w.Put((int)e.Effect);
            w.Put(e.OtherEffect ?? "");
            w.Put(e.Chance);
            w.Put(e.DamagePerSec);
            w.Put(e.Duration);
            w.Put(e.HideUI);
        }
    }

    public static List<DamageEffect> GetEffects(NetDataReader r)
    {
        int n = r.GetByte();
        var list = new List<DamageEffect>(n);
        for (int i = 0; i < n; i++)
            list.Add(new DamageEffect
            {
                Effect = (EffectType)r.GetInt(),
                OtherEffect = r.GetString(),
                Chance = r.GetFloat(),
                DamagePerSec = r.GetInt(),
                Duration = r.GetFloat(),
                HideUI = r.GetBool(),
            });
        return list;
    }

    /// <summary>The attack whose custom effects this hit made ("" if it made none).</summary>
    public static string AttackRef(Attack attack, List<Effect> created) => attack && created != null && created.Count > 0 ? attack.name : "";

    /// <summary>That attack's name and the skill level its effects scale with.</summary>
    public static void PutAttackRef(NetDataWriter w, string name, int[] level)
    {
        w.Put(name ?? "");
        w.PutArray(!string.IsNullOrEmpty(name) && level != null ? level : new int[0]);
    }

    public static void GetAttackRef(NetDataReader r, out string name, out int[] level)
    {
        name = r.GetString();
        level = r.GetIntArray();
        if (level.Length == 0) level = null;
    }

    /// <summary>Make the attack's custom effects again here (as CombatSystem.GetDamageData does).</summary>
    public static List<Effect> CreateEffects(string name, int[] level)
    {
        var list = new List<Effect>();
        if (string.IsNullOrEmpty(name)) return list;
        var attack = PlayerHits.FindAttack(null, name);
        if (!attack || attack.customEffects == null) return list;
        foreach (var creator in attack.customEffects)
            list.Add(creator.CreateEffect(new EffectCreationContext { Level = level }));
        return list;
    }
}

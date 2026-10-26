using System;
using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// What the game talks to on the companion's body: it can be hit (IDamageable: enemies' attacks find it through its
/// collider) and it holds what CharacterStats equips (IEquipable: the weapon shown in its hands). Health lives here;
/// what a hit does is Companion's business (Hurt).
/// </summary>
internal class CompanionUnit : MonoBehaviour, IDamageable, IEquipable
{
    public CharacterStats Stats;
    public AddonsHandler Addons;
    public EffectsHandler Effects;
    public Revive Revive = new();
    public int Health, MaxHealth;
    public Weapon Weapon;   // the one in its hands (null: bare hands)
    public ItemObject WeaponItem;   // (its rolled damage is the hit's base, as for the player)
    public Action<DamageData> Hurt;

    public EffectsHandler EffectsHandler => Effects;
    public bool IsDead => Revive.Down;
    public DamageableType DamageableType => DamageableType.Player;
    public CharacterStats GetCharacterStats() => Stats;
    public Transform GetTransform() => transform;
    public void ApplyDamage(DamageData damageData) => Hurt?.Invoke(damageData);

    public void EquipPrimaryWeapon(ref ItemObject item)
    {
        Weapon = (item?.data as CollectableWeapon)?.weapon;
        WeaponItem = Weapon ? item : null;
        if (Addons) Addons.ShowWeapon(Weapon ? Weapon.weaponIndex : -1, Weapon ? Weapon.vfxIndex : -1);
    }

    public void EquipSecondarySkill(Attack dodge) { }

    /// <summary>Health from its stats (after gear or a level-up), keeping the same fraction.</summary>
    public void RefreshHealth()
    {
        int max = Stats && Stats.secStatDict.TryGetValue(SecondaryStat.HealthPoints, out var hp) ? Mathf.Max(1, (int)hp.Value) : 100;
        float frac = MaxHealth > 0 ? (float)Health / MaxHealth : 1f;
        MaxHealth = max;
        Health = Revive.Down ? 0 : Mathf.Clamp(Mathf.RoundToInt(max * frac), 1, max);
    }
}

/// <summary>The companion's hurtbox when it sits below the body (as the player's does): hits on it are the companion's.</summary>
internal class CompanionHurtbox : MonoBehaviour, IDamageable
{
    public CompanionUnit Unit;
    public EffectsHandler EffectsHandler => Unit ? Unit.EffectsHandler : null;
    public bool IsDead => !Unit || Unit.IsDead;
    public DamageableType DamageableType => DamageableType.Player;
    public CharacterStats GetCharacterStats() => Unit ? Unit.Stats : null;
    public Transform GetTransform() => Unit ? Unit.transform : transform;
    public void ApplyDamage(DamageData damageData) { if (Unit) Unit.ApplyDamage(damageData); }
}

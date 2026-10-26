using System.Linq;
using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The companion's weapons and swings. It attacks the way its weapon does: the weapon's own first attack names the
/// player animation (a sword "SwingBasic", an axe "DHSwing", a bow "BowShot", fists "Punch1"); a ranged weapon uses
/// its attack that shoots (a staff's is its secondary, "CastShot"). The hit is the game's own, with the player as the
/// one hitting (as a pet's peck), so co-op sync and kill credit work as for any hit of ours. A bow's arrow and a staff's
/// orb are the weapon's own projectile, fired at the creature from the game's pool: it flies, stops at walls, and hits
/// what it meets (the player's shots pass through you and the companion, as yours do).
///
/// Its two favourites: one melee, one ranged weapon (CompanionMeta), held in its hand or its bag; it swaps between
/// them (Brain.WantRanged). Unset, they're picked for it: what it holds, and the best of the other kind in its bag.
/// </summary>
internal static class Arms
{
    public static Style StyleNow(CompanionBody b) => Rules.StyleOf(b.Unit.Weapon ? (int)b.Unit.Weapon.weaponCategory : 14);

    /// <summary>The attack it uses with what it holds: for a ranged weapon, one that shoots.</summary>
    public static Attack AttackOf(Weapon w)
    {
        if (!w) return null;
        var all = w.primaryAttack.Concat(w.secondaryAttack).Where(a => a).ToList();
        if (Rules.StyleOf((int)w.weaponCategory) == Style.Ranged) return all.FirstOrDefault(a => a.projectile) ?? all.FirstOrDefault();
        return all.FirstOrDefault();
    }

    public static float Cooldown(CompanionBody b) => 1.1f / Mathf.Max(0.3f, b.Unit.Weapon ? b.Unit.Weapon.attackRate : 1f);

    /// <summary>Swing (or shoot) at it: the animation now, the hit a moment later (when the swing lands).</summary>
    public static void Strike(CompanionBody b, Enemy e, XpLedger ledger)
    {
        var attack = AttackOf(b.Unit.Weapon);
        b.Face((Vector2)(e.transform.position - b.Position));
        b.Play(attack && !string.IsNullOrEmpty(attack.animation) ? attack.animation : "Punch1");
        pending = e;
        landAt = Time.time + (StyleNow(b) == Style.Ranged ? 0.35f : 0.22f);
        Companion.Ledger = ledger;
    }

    static Enemy pending;
    static float landAt;

    /// <summary>Each frame: a swing whose moment has come lands.</summary>
    public static void Tick(CompanionBody b)
    {
        if (!pending || Time.time < landAt) return;
        var e = pending;
        pending = null;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me || !e || e.IsDead || !e.isActiveAndEnabled || b.Unit.Revive.Down) return;
        float reach = Rules.AttackRange(StyleNow(b)) + 1.5f;
        if (Vector2.Distance(b.Position, e.transform.position) > reach) return;   // (it got away)
        Companion.Ledger?.Hit(e.GetInstanceID(), Time.time);
        var attack = AttackOf(b.Unit.Weapon);
        var hit = Hit(b, me, attack);
        if (StyleNow(b) == Style.Ranged && attack && attack.projectile && Shoot(b, attack, e, hit)) return;
        hit.Target = e;
        CombatSystem.Resolve(hit);
    }

    /// <summary>
    /// The hit, worked out by the game as for the player's own (CombatSystem.ProcessAttackDamageData): the weapon's
    /// rolled damage plus the companion's damage stat for that weapon, times its attribute bonus, its crit chance and
    /// effects. Then it's handed in as the player's (kill credit, co-op sync, no friendly fire), the amount kept.
    /// </summary>
    static DamageData Hit(CompanionBody b, PlayerMaster me, Attack attack)
    {
        var d = new DamageData(b.Unit, null, 0, attack ? attack.hitType : DamageHitType.Impact, KnockBack.Low) { AgressorWeapon = b.Unit.WeaponItem, Attack = attack };
        try { CombatSystem.ProcessAttackDamageData(ref d); }
        catch (System.Exception ex) { Log.Warn($"Companion: working out a hit: {ex.Message}"); }
        if (d.Amount <= 0) d.Amount = Mathf.Max(1, Mathf.RoundToInt(b.Stats ? b.Stats.GetSecondaryStatValue(SecondaryStat.AttackDamage) : 5f));
        d.Aggressor = me;
        return d;
    }

    /// <summary>The weapon's projectile, from about chest height, at the creature (as PlayerMaster fires its own).</summary>
    static bool Shoot(CompanionBody b, Attack attack, Enemy e, DamageData hit)
    {
        var prefab = attack.projectile;
        GameObject go = null;
        try { go = PoolManager.Get(prefab); }
        catch (System.Exception) { }   // (a pool whose holder went with the last scene: made again below)
        if (!go)
        {
            PoolManager.ClearPool(prefab);   // (none yet, or one whose objects went with a scene)
            PoolManager.CreatePool(prefab, "Pool_" + prefab.name, 10);
            go = PoolManager.Get(prefab);
        }
        if (!go || !go.TryGetComponent<Projectile>(out var p)) return false;
        var from = b.Position + new Vector3(0f, 0.55f, 0f);
        var aim = e.transform.position + new Vector3(0f, 0.4f, 0f) - from;
        aim.z = 0f;
        p.transform.position = from;
        p.Direction = aim.sqrMagnitude > 0.0001f ? aim.normalized : (b.Flipped ? Vector3.left : Vector3.right);
        p.damageData = hit;
        p.ToIgnoreOnStart.Clear();
        p.ToIgnoreOnStart.AddRange(b.Root.GetComponentsInChildren<Collider2D>(true));   // (not its own hurtbox, leaving)
        go.SetActive(true);
        return true;
    }

    // ---- favourites ----

    static InventoryItem FindItem(CompanionBody b, string key) =>
        string.IsNullOrEmpty(key) || b.Items == null ? null : b.Items.FirstOrDefault(i => i.itemObject?.data is CollectableWeapon && KeyOf(i) == key);

    public static string KeyOf(InventoryItem i) => i?.itemObject?.data ? i.itemObject.data.addressKey : null;

    static Style StyleOfItem(InventoryItem i) => Rules.StyleOf((int)((i.itemObject.data as CollectableWeapon)?.weapon.weaponCategory ?? WeaponCategory.Gauntlets));

    /// <summary>Unset favourites: what it holds for its kind, and the best weapon of the other kind in its bag.</summary>
    public static void PickFavourites(CompanionBody b)
    {
        var meta = CompanionSave.Meta;
        if (b.Items == null) return;
        var weapons = b.Items.Where(i => i.itemObject?.data is CollectableWeapon).ToList();
        string Best(Style s) => weapons.Where(i => StyleOfItem(i) == s)
            .OrderByDescending(i => i.section == InventorySection.Gear).ThenByDescending(i => (int)i.itemObject.data.itemTier)
            .Select(KeyOf).FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(meta.favMelee) || FindItem(b, meta.favMelee) == null) meta.favMelee = Best(Style.Melee);
        if (string.IsNullOrEmpty(meta.favRanged) || FindItem(b, meta.favRanged) == null) meta.favRanged = Best(Style.Ranged);
    }

    public static bool Has(CompanionBody b, Style s) => FindItem(b, s == Style.Ranged ? CompanionSave.Meta.favRanged : CompanionSave.Meta.favMelee) != null;

    /// <summary>Take the favourite of that kind in hand (the other goes to its bag, in its save too).</summary>
    public static bool Swap(CompanionBody b, Style to)
    {
        var want = FindItem(b, to == Style.Ranged ? CompanionSave.Meta.favRanged : CompanionSave.Meta.favMelee);
        if (want == null || want.section == InventorySection.Gear) return false;
        var held = b.Items.FirstOrDefault(i => i.section == InventorySection.Gear && i.itemObject?.data is CollectableWeapon cw && cw.weapon.gearSlot == ((CollectableWeapon)want.itemObject.data).weapon.gearSlot);
        b.Stats.EquipItem(want.itemObject);
        (want.section, want.slotIndex, var wasSection, var wasSlot) = (InventorySection.Gear, held?.slotIndex ?? want.slotIndex, want.section, want.slotIndex);
        if (held != null) { held.section = wasSection; held.slotIndex = wasSlot; }
        CompanionSave.FromItems(b.Items);   // (the save follows what it has; saved gear all has slot -1, so no matching by slot)
        CompanionSave.Dirty = true;
        b.Unit.RefreshHealth();
        return true;
    }
}

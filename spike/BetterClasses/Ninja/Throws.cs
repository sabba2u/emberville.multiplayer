using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// A Ninja's bare hands throw: the game's punches (the same moves, its white punch swipe hidden), each launching a
/// poisoned arrow from the hand (the bow's, green; shuriken later): half the creature's health over five seconds. The kicks stay kicks. It's the bare hands only (no item: nothing new in
/// your bag or your save), so any weapon you hold works as usual, and as another class your hands punch again.
/// </summary>
[HarmonyPatch]
internal static class Throws
{
    static readonly HashSet<Attack> ours = new();   // (the throws: their hits poison)
    const float PoisonShare = 0.5f, BossPoisonShare = 0.1f, PoisonFor = 5f;
    static GameObject greenArrow;   // (the bow's arrow, tinted poison green: what the throws launch)

    static Attack arrow;          // (the bow's shot: its arrow, where it leaves from, how it hits)
    static bool loading;
    static ItemObject ninja, plain;   // (the Ninja's hands, and the game's own)

    /// <summary>Every frame: your bare hands as they should be (a Ninja's or the plain ones).</summary>
    public static void Keep(PlayerMaster me, bool asNinja)
    {
        var hands = me.fallbackWeaponItem;
        if (hands == null) return;
        if (hands != ninja) plain = hands;
        if (asNinja && ninja == null && !Make(me)) return;   // (the bow's arrow still loading)
        var want = asNinja ? ninja : plain;
        if (want == null || hands == want) return;
        me.fallbackWeaponItem = want;
        if (me.currentWeaponItem == hands)   // (bare-handed now: the new hands at once)
        {
            me.characterStats.RemoveItem(hands);
            me.characterStats.SetFallbackWeapon();
        }
    }

    static bool Make(PlayerMaster me)
    {
        if (!arrow) { Load(); return false; }
        var hands = me.fallbackWeapon;
        if (!hands || !hands.weapon) return false;
        var w = Object.Instantiate(hands.weapon);
        w.name = "Weapon_NinjaHands";
        w.hideFlags = HideFlags.DontUnloadUnusedAsset;
        // (its weapon type stays bare hands: the game's thrown-weapons mastery is unfinished and breaks the masteries screen)
        w.vfxIndex = -1;   // (no white punch swipe)
        w.primaryAttack = hands.weapon.primaryAttack.Select(Throw).ToList();
        w.secondaryAttack = hands.weapon.secondaryAttack.Select(Throw).ToList();
        w.combos = new List<ComboMove>();   // (the punch combos are close-up)
        w.currentIndex = 0;
        w.CurrentAttack = null;
        var item = Object.Instantiate(hands);
        item.name = "Item_NinjaHands";
        item.hideFlags = HideFlags.DontUnloadUnusedAsset;
        item.weapon = w;
        ninja = new ItemObject(item);
        Log.Info($"Better Classes: Ninja hands ready ({w.primaryAttack.Count(a => a && a.projectile)} throws)");
        return true;
    }

    /// <summary>A punch thrown: the same move, the bow's arrow leaving the hand. (Kicks stay kicks.)</summary>
    static Attack Throw(Attack a)
    {
        if (!a || a.animation == null || !a.animation.StartsWith("Punch")) return a;
        var t = Object.Instantiate(a);
        t.name = a.name.Replace("Punch", "NinjaThrow");
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        t.attackHit = AttackHit.Projectile;
        t.projectile = GreenArrow();
        t.spawnPointName = arrow.spawnPointName;
        t.hitType = arrow.hitType;
        ours.Add(t);
        return t;
    }

    /// <summary>The bow's arrow, green: a copy kept switched off, for the game to make its arrows from.</summary>
    static GameObject GreenArrow()
    {
        if (greenArrow) return greenArrow;
        var holder = new GameObject("BetterClasses_Templates");
        holder.SetActive(false);
        Object.DontDestroyOnLoad(holder);
        greenArrow = Object.Instantiate(arrow.projectile, holder.transform);
        greenArrow.name = arrow.projectile.name + "_NinjaPoison";
        foreach (var r in greenArrow.GetComponentsInChildren<SpriteRenderer>(true)) r.color = new Color(0.55f, 1f, 0.45f);
        return greenArrow;
    }

    /// <summary>A throw that hit: the creature poisoned (the game's poison), half its whole health over five seconds
    /// (a boss: a tenth). Hit again while it's poisoned: the poison starts over.</summary>
    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out }), HarmonyPostfix]
    static void Poison(DamageData damageData)
    {
        if (!damageData.Attack || !ours.Contains(damageData.Attack) || damageData.Target is not Enemy e || !e || e.IsDead) return;
        float share = Hook.Boss(e) ? BossPoisonShare : PoisonShare;
        int perSecond = Mathf.Max(1, Mathf.RoundToInt(e.MaxHealth * share / PoisonFor));
        e.EffectsHandler?.HandleEffect(new DamageEffect { Effect = EffectType.Poison, Chance = 100f, DamagePerSec = perSecond, Duration = PoisonFor });
    }

    /// <summary>The bow (by its key, as the game loads items), for its arrow.</summary>
    static void Load()
    {
        if (loading) return;
        loading = true;
        try
        {
            UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<ItemData>("wpn_shrtBw").Completed += h =>
            {
                if (h.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded
                    && h.Result is CollectableWeapon bow && bow.weapon && bow.weapon.primaryAttack.Count > 0 && bow.weapon.primaryAttack[0].projectile)
                    arrow = bow.weapon.primaryAttack[0];
                else Log.Warn("Better Classes: the bow (wpn_shrtBw) didn't load: Ninja hands punch for now");
            };
        }
        catch (System.Exception e) { Log.Warn($"Better Classes: the bow: {e.Message}"); }
    }
}

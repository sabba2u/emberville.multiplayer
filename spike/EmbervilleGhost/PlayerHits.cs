using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>On a stand-in's hurtbox: lets the game's attacks find it as an IDamageable. The real work is in PlayerHits.</summary>
internal class StandInTarget : MonoBehaviour, IDamageable
{
    public int PlayerId;
    public EffectsHandler EffectsHandler => null;
    public bool IsDead => false;
    public DamageableType DamageableType => DamageableType.Player;
    public void ApplyDamage(DamageData damageData) { }   // never reached: PlayerHits stops it in CombatSystem.Resolve
    public CharacterStats GetCharacterStats() => null;
    public Transform GetTransform() => transform;
}

/// <summary>
/// Creatures hurting other players. Every hit in the game (melee hitbox, area, projectile, explosion...) ends in
/// CombatSystem.Resolve(damageData, out attackDamage): that's where the TARGET's defence, block, parry, crit and
/// counter-attack are rolled, from the target's CharacterStats. So:
///  - on the area owner's copy (it runs the creatures' attacks) a creature attack that lands on a stand-in's hurtbox
///    (StandInTarget) is stopped there and sent, unresolved, to that player (PlayerHit);
///  - the victim's copy rebuilds it (attacker = its puppet of that creature, same Attack asset, same crit/effect
///    chances) and runs the game's own Resolve against the real player: their own armour, masteries, block, parry,
///    effects and knockback apply; a counter-attack becomes a normal hit request to the owner (EnemySync).
/// Only creature attacks are forwarded (traps and hazards run on every copy and hit each real player there already),
/// plus, with the host's PvP rule on, our own player's attacks (melee, projectiles: only the attacker's real one hurts).
/// </summary>
[HarmonyPatch]
internal static class PlayerHits
{
    public static Plugin Plugin;

    static bool replaying;   // resolving a creature's hit its runner sent us (OnPlayerHit): the aggressor is our copy of it

    static Dictionary<string, Attack> attacksByName;

    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    [HarmonyPrefix]
    static bool BeforeResolve(DamageData damageData, out DamageData attackDamage, ref DamageData __result)
    {
        attackDamage = default;
        // A creature someone else runs (its copy here: a mirrored attack object, AttackFx): its real hits come from
        // its runner as PlayerHit, so here it hurts nobody.
        if (!replaying && damageData.Aggressor is Enemy puppet && Plugin != null && Plugin.Connected && !EnemySync.Runs(puppet))
        {
            __result = default;
            return false;
        }
        if (!(damageData.Target is StandInTarget target)) return true;
        __result = default;   // no counter-damage here; the victim's copy rolls that
        if (Plugin == null || !Plugin.Connected) return false;

        // A creature's attack, or (PvP on) our own player's attack: our weapon, masteries and buffs are already in the amount.
        var enemy = damageData.Aggressor as Enemy;
        var me = LocalPlayer.Get();
        bool pvp = Session.Pvp && me && ReferenceEquals(damageData.Aggressor, me);
        if (!enemy && !pvp) return false;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerHit);
        w.Put(enemy ? EnemySync.NetId(enemy) : 0u);
        w.Put(pvp ? Plugin.MyId : -1);
        var at = enemy ? enemy.transform.position : me.transform.position;
        w.Put(at.x); w.Put(at.y);
        w.Put(damageData.Amount);
        w.Put(damageData.BaseCriticalChance);
        w.Put(damageData.BaseEffectsChance);
        w.Put((byte)damageData.HitType);
        w.Put((byte)damageData.KnockBackForce);
        w.Put(damageData.NoStagger);
        w.Put(damageData.Attack ? damageData.Attack.name : "");
        Plugin.SendTo(target.PlayerId, w);
        return false;
    }

    /// <summary>Victim: a creature (or, PvP, a player) hit our stand-in on their screen; take it on our real player.</summary>
    public static void OnPlayerHit(NetDataReader r)
    {
        uint enemyId = r.GetUInt();
        int attacker = r.GetInt();   // a player (PvP), or -1 for a creature
        var from = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        int amount = r.GetInt();
        float crit = r.GetFloat(), effects = r.GetFloat();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        string attackName = r.GetString();

        var me = LocalPlayer.Get();
        if (!me || me.IsDead) return;
        if (attacker >= 0 && !Session.Pvp) return;   // PvP was switched off meanwhile
        var enemy = EnemySync.FindById(enemyId);
        IDamageable aggressor = attacker >= 0 ? (RemoteHitter.For(Plugin, attacker) ?? RemoteHitter.At(from))
            : enemy && enemy.isActiveAndEnabled ? enemy : RemoteHitter.At(from);
        if (attacker >= 0) Chat.LastHitBy(Plugin.NameOf(attacker));   // "killed by sabba"

        var d = new DamageData(aggressor, me, amount, hitType, knock, noStagger: noStagger)
        {
            Attack = FindAttack(enemy, attackName),
            BaseCriticalChance = crit,
            BaseEffectsChance = effects,
        };
        DamageData counter;
        replaying = true;
        try { counter = CombatSystem.Resolve(d); }   // our defence, block, parry, crit, effects, knockback
        finally { replaying = false; }
        if (attacker < 0 && counter.Result == DamageResult.Success && enemy && enemy.isActiveAndEnabled)
            enemy.ApplyDamage(counter);           // a counter-attack: EnemySync turns it into a hit request to the owner
    }

    /// <summary>The creature's Attack asset by name: from that creature's own attacks/skills, else any loaded Attack.</summary>
    static Attack FindAttack(Enemy enemy, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (enemy)
        {
            if (enemy.attacks != null)
                foreach (var a in enemy.attacks) if (a.Attack && a.Attack.name == name) return a.Attack;
            if (enemy.skills != null)
                foreach (var s in enemy.skills) if (s && s.attack && s.attack.name == name) return s.attack;
        }
        if (attacksByName == null)
        {
            attacksByName = new Dictionary<string, Attack>();
            foreach (var a in Resources.FindObjectsOfTypeAll<Attack>()) attacksByName[a.name] = a;
        }
        return attacksByName.TryGetValue(name, out var found) ? found : null;
    }
}

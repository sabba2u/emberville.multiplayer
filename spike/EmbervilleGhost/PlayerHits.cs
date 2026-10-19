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
/// Only creature attacks are forwarded: traps and hazards run on every copy and hit each real player there already.
/// Our own player's attacks never hurt a stand-in (no PvP yet; see todo: both players opt in).
/// </summary>
[HarmonyPatch]
internal static class PlayerHits
{
    public static Plugin Plugin;

    static Dictionary<string, Attack> attacksByName;

    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    [HarmonyPrefix]
    static bool BeforeResolve(DamageData damageData, out DamageData attackDamage, ref DamageData __result)
    {
        attackDamage = default;
        if (!(damageData.Target is StandInTarget target)) return true;
        __result = default;   // no counter-damage here; the victim's copy rolls that
        if (Plugin == null || !Plugin.Connected || !(damageData.Aggressor is Enemy enemy)) return false;

        var w = new NetDataWriter();
        w.Put((byte)MsgType.PlayerHit);
        w.Put(SceneIds.Of(enemy));
        var at = enemy.transform.position;
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

    /// <summary>Victim: a creature hit our stand-in on the owner's screen; take it on our real player.</summary>
    public static void OnPlayerHit(NetDataReader r)
    {
        uint enemyId = r.GetUInt();
        var from = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        int amount = r.GetInt();
        float crit = r.GetFloat(), effects = r.GetFloat();
        var hitType = (DamageHitType)r.GetByte();
        var knock = (KnockBack)r.GetByte();
        bool noStagger = r.GetBool();
        string attackName = r.GetString();

        var me = LocalPlayer.Get();
        if (!me || me.IsDead) return;
        var enemy = SceneIds.Find<Enemy>(enemyId);
        IDamageable aggressor = enemy && enemy.isActiveAndEnabled ? enemy : RemoteHitter.At(from);

        var d = new DamageData(aggressor, me, amount, hitType, knock, noStagger: noStagger)
        {
            Attack = FindAttack(enemy, attackName),
            BaseCriticalChance = crit,
            BaseEffectsChance = effects,
        };
        var counter = CombatSystem.Resolve(d);   // our defence, block, parry, crit, effects, knockback
        if (counter.Result == DamageResult.Success && enemy && enemy.isActiveAndEnabled)
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

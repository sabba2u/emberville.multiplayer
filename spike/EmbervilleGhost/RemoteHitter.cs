using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// "Who hit this" when the hitter is another player. The game's damage code wants an IDamageable aggressor (knockback
/// pushes away from aggressor.GetTransform(); props NRE without one), but other players are stand-ins with no game
/// components. This stands in for them: their stand-in's transform, never takes damage itself. It is not a
/// PlayerMaster, so the game gives no XP / player-only reactions for it (only the real hitter's copy does that).
/// </summary>
internal sealed class RemoteHitter : IDamageable
{
    readonly Transform at;

    RemoteHitter(Transform at) { this.at = at; }

    /// <summary>The aggressor to replay a hit with: our own player if it was us, else that player's stand-in (or null).</summary>
    public static IDamageable For(Plugin plugin, int playerId)
    {
        if (playerId < 0) return null;
        if (playerId == plugin.MyId) return LocalPlayer.Get();
        var t = plugin.AvatarOf(playerId);
        return t ? new RemoteHitter(t) : null;
    }

    /// <summary>An aggressor at a fixed point (the attacker isn't in our world any more): knockback pushes away from it.</summary>
    public static IDamageable At(Vector3 position)
    {
        var go = new GameObject("EmbervilleGhost_hit") { hideFlags = HideFlags.HideAndDontSave };
        go.transform.position = position;
        Object.Destroy(go, 2f);
        return new RemoteHitter(go.transform);
    }

    public EffectsHandler EffectsHandler => null;
    public bool IsDead => false;
    public DamageableType DamageableType => DamageableType.Player;
    public void ApplyDamage(DamageData damageData) { }
    public CharacterStats GetCharacterStats() => null;
    public Transform GetTransform() => at;
}

using HarmonyLib;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Deaths Twitch causes: real by default (chat can kill you: the game's own death, respawn and all), then a breather
/// from mean effects. The streamer can make them free instead (config Deaths.Free, the Streaming tab): then a death
/// Twitch caused costs nothing. A hit from a creature Twitch sent (Hooks.IsTwitchCreature), or any hit while a
/// Twitch effect has marked the player (poison, instant death), counts. Dying to one of those: the game's own death
/// (respawn, XP penalty, the world reset) doesn't run. Instead you fall where you stand (the game's dying state, so it
/// looks the same, played to its end), get up at half health with your bag as it was, and mean effects take a breather.
/// The dying state's last step, the game's "player died" event (the respawn), is held back while it's ours.
/// Dying to anything else works the game's normal way.
/// </summary>
[HarmonyPatch]
internal static class TwitchDeath
{
    const float Breather = 20f, Down = 3.3f;   // (the dying state's own time: 0.375 + 2.6 s, then the death event)
    static float markedUntil = -100f, twitchHitAt = -100f;
    static PlayerMaster falling;
    static float upAt;
    static bool inputOwed;
    static BepInEx.Configuration.ConfigEntry<bool> free;

    public static void Bind(BepInEx.Configuration.ConfigFile config) =>
        free = config.Bind("Deaths", "Free", false, "A death Twitch caused costs nothing (you get up where you fell). Off: chat can kill you for real.");

    /// <summary>Twitch deaths cost nothing (else they're real).</summary>
    public static bool Free { get => free.Value; set => free.Value = value; }   // (up again during a cutscene or a menu: the controls come back once it's over)

    /// <summary>Until then, poison and burn ticks stop at 1 health (Twitch's, plus a moment's margin).</summary>
    public static float PoisonUntil = -100f;

    /// <summary>Down, but getting up (a free Twitch death, second wind): not a death to count (the arena's runs).</summary>
    public static bool Falling => falling;

    /// <summary>Hits in the next seconds count as Twitch's.</summary>
    public static void Mark(float seconds) => markedUntil = Mathf.Max(markedUntil, Time.time + seconds);

    /// <summary>The last hit was Twitch's (just now).</summary>
    public static bool Recent => Time.time - twitchHitAt < 1f;

    static bool Local(PlayerMaster p) => Singleton<GameManager>.HasMain && ReferenceEquals(Singleton<GameManager>.main.player, p);

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)), HarmonyPrefix]
    static void RememberHit(PlayerMaster __instance, ref DamageData damageData)
    {
        if (!Local(__instance)) return;
        // Twitch's poison and fire take you down to 1 health, never further (their ticks are effect hits)
        if (damageData.Result == DamageResult.Effect && Time.time < PoisonUntil && damageData.Amount >= __instance.currentHealth)
            damageData.Amount = System.Math.Max(0, __instance.currentHealth - 1);
        bool twitch = Time.time < markedUntil || (damageData.Aggressor is Object o && o && Hooks.IsTwitchCreature(o));
        if (twitch) twitchHitAt = Time.time;
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die)), HarmonyPrefix]
    static bool InsteadOfDying(PlayerMaster __instance)
    {
        if (!Local(__instance)) return true;
        if (NiceEffects.CatchDeath(__instance)) { FakeDeath(__instance, "Second wind!", "chat had your back: up you get, full health", health: 1f); return false; }   // (second wind: up again, whatever it was)
        if (!Recent) return true;
        if (!Free)
        {
            // (for real: the game's death runs; mean effects take a breather while you're back on your feet)
            Streamer.Queue.Breather(Breather);
            Overlay.Banner("Twitch chat got you", "");
            return true;
        }
        FakeDeath(__instance);
        return false;
    }

    /// <summary>The game's "player died" (PlayerDead's last step, which respawns you): not for a Twitch death.</summary>
    [HarmonyPatch(typeof(Bus<PlayerEvent>), nameof(Bus<PlayerEvent>.Invoke)), HarmonyPrefix]
    static bool HoldDeathEvent(PlayerEvent evnt, ref bool __result)
    {
        if (!falling || evnt.EventType != PlayerEvent.Event_Type.Death) return true;
        __result = true;
        return false;
    }

    /// <summary>Instant death: for real (the game's own death), or the free fall when deaths are free. False: already down.</summary>
    public static bool Kill(PlayerMaster p)
    {
        if (!p || p.IsDead || falling) return false;
        if (Free) return FakeDeath(p);
        Log.Info("Streaming: instant death, for real");
        twitchHitAt = Time.time;
        p.currentHealth = 0;
        p.Die();
        return true;
    }

    /// <summary>Fall, and get up again where you fell (Tick). False: already down.</summary>
    static string upBanner, upSub;
    static float upHealth = 0.5f;

    public static bool FakeDeath(PlayerMaster p, string banner = "Twitch can't kill you for real", string sub = null, float health = 0.5f)
    {
        upBanner = banner; upSub = sub; upHealth = health;
        if (!p || falling || p.IsDead) return false;
        Log.Info("Streaming: a Twitch death (free): down, then up again here");
        p.effectsHandler?.StopAllEffects();   // (as the game's death does: no poison left to kill you again)
        p.goreDeath = false;   // (the plain fall: the gore one takes longer)
        p.currentHealth = 1;
        p.ChangeState(p.stateDead);
        falling = p;
        upAt = Time.time + Down;
        Streamer.Queue.Breather(Breather);
        return true;
    }

    /// <summary>Every frame (Plugin.Update).</summary>
    public static void Tick()
    {
        if (inputOwed && Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Playing && !UIPanels.HasOpenedPanel)
        {
            inputOwed = false;
            MainInput.Actions.Player.Enable();
            MainInput.Actions.Shared.Enable();
        }
        if (!falling || Time.time < upAt) { if (!falling) falling = null; return; }
        var p = falling;
        falling = null;
        p.ChangeState(p.stateIdle);   // (after the dying state's own end; its death event was held back: no respawn)
        inputOwed = true;   // (the dying state switched the controls off; on again at once if nothing else holds them)
        p.currentHealth = 0;
        p.RestoreHealthPercentage(upHealth);
        Overlay.Banner(upBanner, upSub ?? $"back up: a {Breather:0} s breather from mean effects");
    }
}

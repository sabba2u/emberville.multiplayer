using System;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// What the multiplayer mod gives us when it's installed (it sets these in its Awake); on our own, the defaults: no
/// menu or overlay drawn, no raids.
/// </summary>
public static class Hooks
{
    /// <summary>True when the multiplayer mod draws our ImGui (the menu tab and the overlay).</summary>
    public static Func<bool> DrawsUi = () => false;
    /// <summary>Creatures that fit the area around there: (around, how many, toughness x, viewer) → any came?</summary>
    public static Func<Vector3, int, float, string, bool> SpawnRaid;
    /// <summary>Hostile chickens rushing in from the screen's edges: (around, waves, toughness x, viewer) → started?</summary>
    public static Func<Vector3, int, float, string, bool> ChickenRaid;
    /// <summary>Is this one of the creatures Twitch sent (a raid's, a chicken raid's)?</summary>
    public static Func<UnityEngine.Object, bool> IsTwitchCreature = _ => false;
    /// <summary>A creature on the player's side or not to be hurt (pets, summons, story NPCs): chain lightning leaves it.</summary>
    public static Func<Enemy, bool> IsFriendly = _ => false;
    /// <summary>A boss (the multiplayer mod's mini-bosses): chain lightning takes less of its health.</summary>
    public static Func<Enemy, bool> IsBoss = _ => false;
    /// <summary>A mystery chest near there: (around, a trap?, viewer) → placed? A trap springs a raid when opened.</summary>
    public static Func<Vector3, bool, string, bool> SpawnChest;
    /// <summary>A message for the player: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
}

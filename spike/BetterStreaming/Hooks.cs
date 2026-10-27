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
    /// <summary>A creature the game counts as wildlife (no enemy type) that's been turned against you, e.g. the chicken
    /// level's chickens: a foe, for chain lightning and chatters' names.</summary>
    public static Func<Enemy, bool> IsHostileFauna = _ => false;
    /// <summary>A boss (the multiplayer mod's mini-bosses): chain lightning takes less of its health.</summary>
    public static Func<Enemy, bool> IsBoss = _ => false;
    /// <summary>A mystery chest near there: (around, a trap?, viewer) → placed? A trap springs a raid when opened.</summary>
    public static Func<Vector3, bool, string, bool> SpawnChest;
    /// <summary>The Bestiary's creatures (the multiplayer mod's): each one's name and its picture, as its parts (sprite,
    /// where it sits in the creature's own units, mirrored, tint). Empty without the pack.</summary>
    public static Func<System.Collections.Generic.IReadOnlyList<(string Name, System.Collections.Generic.IReadOnlyList<(Sprite Sprite, Rect Box, bool Flip, Color Colour)> Parts)>> Creatures =
        () => new (string, System.Collections.Generic.IReadOnlyList<(Sprite, Rect, bool, Color)>)[0];
    /// <summary>The game's whole item catalogue once loaded (the multiplayer mod loads it), else null.</summary>
    public static Func<System.Collections.Generic.IReadOnlyList<ItemData>> Catalogue = () => null;
    /// <summary>The storm's weather: rain on (true: done; false: no weather mod), and the weather before it put back.</summary>
    public static Func<bool> StormOn;
    public static Action StormOff;
    /// <summary>A lightning strike there (to look at: the damage is ours).</summary>
    public static Action<Vector3> Strike;
    /// <summary>Battle chickens fighting for you: (around, how many, seconds) → how many came.</summary>
    public static Func<Vector3, int, float, int> BattleChickens;
    /// <summary>Creatures of the area rising to fight for you: (around, how many, seconds, viewer) → how many came.</summary>
    public static Func<Vector3, int, float, string, int> Guardians;
    /// <summary>Your pet golden for a while: (seconds) → done? (false: no pet out)</summary>
    public static Func<float, bool> GoldenPet;
    /// <summary>A big golden chicken by you (no pet out): (where, seconds) → how many came.</summary>
    public static Func<Vector3, float, int> GoldenChicken;
    /// <summary>Our summons still about: "chickens", "golden", "guardians".</summary>
    public static Func<string, int> SummonsAlive;
    /// <summary>A chest's worth of loot, as a descent floor of that depth rolls it.</summary>
    public static Func<int, System.Collections.Generic.IEnumerable<ItemObject>> RollLoot;
    /// <summary>A hireling companion for a while (Better Companions): (viewer, seconds) → empty: done; else why not.</summary>
    public static Func<string, float, string> Hire;
    /// <summary>A message for the player: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
}

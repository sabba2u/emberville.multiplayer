using System;

namespace BetterCompanions;

/// <summary>
/// What the multiplayer mod tells us when it's installed (it sets these in its Awake); on our own, the defaults.
/// </summary>
public static class Hooks
{
    /// <summary>A message for the player: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>Someone's typing (chat, a text box): keys aren't for us.</summary>
    public static Func<bool> Typing = () => false;
    /// <summary>The player is busy (a menu, a cutscene, dead...).</summary>
    public static Func<bool> Busy = () => UIPanels.HasOpenedPanel || !MainInput.Actions.Player.enabled;
    /// <summary>Creatures that aren't fair game (story NPCs built as enemies).</summary>
    public static Func<Enemy, bool> Ignore = e => e && e.name.StartsWith("NPC_");
    /// <summary>The creature the player has locked on to (Better Combat), which the companion goes for first.</summary>
    public static Func<Enemy> LockTarget = () => null;
    /// <summary>True when the multiplayer mod hands us its middle clicks (Plugin.Send); else we read them ourselves.</summary>
    public static bool HasRally;
    /// <summary>True when the multiplayer mod's HUD draws the companion's name tag (Plugin.DrawHud); else CompanionLabel does.</summary>
    public static bool DrawsHud;
    /// <summary>A teammate's downed companion within reach of there (co-op): F is for reviving it.</summary>
    public static Func<UnityEngine.Vector3, float, bool> DownedNear = (_, _) => false;
    /// <summary>In co-op: this creature's AI runs on someone else's game (we only point our own creatures at the companion).</summary>
    public static Func<Enemy, bool> RemoteRun = _ => false;
    /// <summary>A small menu of the game's buttons growing up from a screen point (the multiplayer mod's UI kit): title, then
    /// (label, on click, enabled) each; returns it (to close: destroy it). Null without that mod.</summary>
    public static Func<UnityEngine.Vector2, string, (string label, Action onClick, bool enabled)[], UnityEngine.Object> Popup;
}

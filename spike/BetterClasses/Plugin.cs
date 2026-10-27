using System;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BetterClasses;

/// <summary>
/// Better Classes: new classes for the player, built from the game's own parts until the full game brings its own.
/// The Ninja (in the game's empty Archer slot): fast, fights with thrown weapons (arrows for now, thrown with the punch
/// moves), vanishes in smoke when it dodges, and has the hunter's hook and fire from its palms on its skill bar.
/// Learn it at the class screen like any other, or /ninja.
/// </summary>
[BepInPlugin(Guid, "Better Classes", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterclasses";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;

    void Awake()
    {
        Logger_ = Logger;
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo($"Better Classes v{Version}: the Ninja (/ninja)");
    }

    void Update()
    {
        try { Ninja.Tick(); Hook.Tick(); }
        catch (Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod ----

    /// <summary>A chat command (/ninja); true if ours.</summary>
    public static bool Command(string cmd)
    {
        string low = (cmd ?? "").Trim().ToLowerInvariant();
        if (low == "ninja") { Hooks.Say(Ninja.Become()); return true; }
        return false;
    }

    /// <summary>The line for /help.</summary>
    public const string Help = "/ninja - become a Ninja (Better Classes)";
}

/// <summary>What the multiplayer mod tells us (defaults: on our own).</summary>
public static class Hooks
{
    /// <summary>A reply to /ninja: the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>The multiplayer mod's Hunter's Hook thrown by the Ninja's hook skill (walls, guardians, online);
    /// false or unset: our own simple hook.</summary>
    public static Func<PlayerMaster, bool> ThrowHook;
    /// <summary>A creature on your side (pets, summons, a companion's): our skills leave it alone.</summary>
    public static Func<Enemy, bool> IsFriendly;
}

/// <summary>Messages: the BepInEx log, and (toast) a line in the game's notification list on the right.</summary>
internal static class Log
{
    static NotificationsManager notifications;

    public static void Info(string text) => Plugin.Logger_.LogInfo(text);
    public static void Warn(string text) => Plugin.Logger_.LogWarning(text);

    public static void Note(string text)
    {
        if (!notifications) notifications = UnityEngine.Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(typeof(Log), null, text, 0f, 1);
    }
}

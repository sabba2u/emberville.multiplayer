using UnityEngine;

namespace BetterCompanions;

/// <summary>Messages: the BepInEx log, and (toast) a line in the game's notification list on the right.</summary>
internal static class Log
{
    static NotificationsManager notifications;

    public static void Info(string text, bool toast = false) { Plugin.Logger_.LogInfo(text); if (toast) Note(text); }
    public static void Warn(string text, bool toast = false) { Plugin.Logger_.LogWarning(text); if (toast) Note(text); }

    public static void Note(string text, Sprite icon = null)
    {
        if (!notifications) notifications = Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(typeof(Log), icon, text, 0f, 1);
    }
}

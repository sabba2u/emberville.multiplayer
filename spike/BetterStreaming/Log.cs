using UnityEngine;

namespace BetterStreaming;

/// <summary>Messages: the BepInEx log, and (toast) a line in the game's notification list on the right.</summary>
internal static class Log
{
    static NotificationsManager notifications;
    static int mainThread = -1;
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> later = new System.Collections.Concurrent.ConcurrentQueue<string>();

    /// <summary>From Plugin.Awake: which thread is Unity's (toasts from the Twitch tasks wait for it).</summary>
    public static void MarkMainThread() => mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
    static bool OnMain => System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread;

    public static void Info(string text, bool toast = false) { Plugin.Logger_.LogInfo(text); if (toast) Note(text); }
    public static void Warn(string text, bool toast = false) { Plugin.Logger_.LogWarning(text); if (toast) Note(text); }

    /// <summary>Every frame (main thread): toasts asked for from a background task.</summary>
    public static void Flush() { while (later.TryDequeue(out var t)) Note(t); }

    public static void Note(string text, Sprite icon = null)
    {
        if (!OnMain) { later.Enqueue(text); return; }   // (Unity calls only on its own thread)
        if (!notifications) notifications = Object.FindAnyObjectByType<NotificationsManager>();
        if (notifications) notifications.AddNotification(typeof(Log), icon, text, 0f, 1);
    }
}

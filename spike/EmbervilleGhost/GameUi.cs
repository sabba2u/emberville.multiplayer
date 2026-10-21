using Cysharp.Threading.Tasks;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The game's own bordered boxes, for our messages, so they look like the game's:
///  - GeneralPrompt: title + message + OK (a static delegate the game itself calls for save errors);
///  - ConfirmationPrompt: message + two buttons, awaited (the recycler's and the stats screen's "are you sure?").
/// Both live in the game's UI; when there isn't one we can show (main menu, or the yes/no box sitting inside a closed
/// panel), the caller falls back to our own window or a toast.
/// </summary>
internal static partial class GameUi
{
    /// <summary>The game's title + message + OK box. False if it isn't available right now.</summary>
    public static bool Message(string title, string text)
    {
        if (GeneralPrompt.ShowPrompt == null || !Singleton<GeneralPrompt>.HasMain) return false;
        GeneralPrompt.ShowPrompt(title, text);
        return true;
    }

    static NotificationsManager notifications;

    /// <summary>
    /// A line in the game's notification list on the right (icon + text, "(12)" when the same owner adds up), like
    /// "Collected Carrot". Plain text: NotificationsManager.AddNotification is what the game's own events end up in.
    /// </summary>
    public static bool Notify(object owner, Sprite icon, string text, int amount = 1, float seconds = 0f)
    {
        if (!notifications) notifications = Object.FindAnyObjectByType<NotificationsManager>();
        if (!notifications) return false;
        notifications.AddNotification(owner, icon, text, seconds, amount);
        return true;
    }

    /// <summary>An item's name in the player's language (its asset name if that fails).</summary>
    public static string ItemName(ItemData data)
    {
        try { var s = data.Name == null || data.Name.IsEmpty ? null : data.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(s)) return s; }
        catch { }
        return data.name;
    }

    /// <summary>The game's yes/no box. Null if there's none we can show (then ask some other way).</summary>
    public static async UniTask<bool?> Ask(string message, string yes, string no)
    {
        ConfirmationPrompt prompt = null;
        foreach (var p in Resources.FindObjectsOfTypeAll<ConfirmationPrompt>())
            if (p && p.gameObject.scene.IsValid() && p.transform.parent && p.transform.parent.gameObject.activeInHierarchy) { prompt = p; break; }
        if (!prompt) return null;
        return await prompt.ShowPrompt(message, yes, no);
    }
}

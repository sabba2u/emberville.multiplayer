using System;
using System.Linq;
using BetterNPCsAndQuests.Core;

namespace BetterNPCsAndQuests;

/// <summary>
/// Where our actions run: on our own or as host, right here on the Book; as a guest, here too (so the conversation
/// reacts at once) and sent to the host, whose story then comes back to everyone. A guest's turn-in uses the guest's
/// own bag: the items are taken here, the host only hears "handed in, for step n".
/// </summary>
internal static class Net
{
    static bool bookDirty, hooked, firstBook = true;
    static float bookAt;

    /// <summary>The host: send the story to the guests a moment after it changes (several changes, one message).</summary>
    public static void Tick()
    {
        if (!hooked) { hooked = true; State.Book.Changed += () => { if (!bookDirty) { bookDirty = true; bookAt = UnityEngine.Time.unscaledTime + 0.25f; } }; }
        if (bookDirty && UnityEngine.Time.unscaledTime >= bookAt)
        {
            bookDirty = false;
            if (Hooks.IsHost()) Hooks.SendBook(State.Book.Save());
        }
    }

    /// <summary>A guest: the host's files arrived (on joining, or after the host read them again).</summary>
    public static void OnFiles(byte[] data)
    {
        if (!Hooks.IsClient()) return;
        try { Files.Remote = Files.Unpack(data); }
        catch (Exception e) { Log.Warn($"NPCs and quests: the host's files: {e.Message}"); return; }
        State.Reload();
    }

    /// <summary>A guest: the host's story. The first one after joining applies quietly (no rewards for what was done before).</summary>
    public static void OnBook(string text)
    {
        if (!Hooks.IsClient()) return;
        State.Book.Load(text);
        QuestBridge.ApplyAll(quiet: firstBook);
        firstBook = false;
    }

    /// <summary>Left a co-op game (or it ended): back to our own files and our own world's story.</summary>
    public static void Disconnected()
    {
        firstBook = true;
        if (Files.Remote == null) return;
        Files.Remote = null;
        WorldFile.Forget();
        State.Reload();
    }

    public static void Do(string action)
    {
        action = action.Trim();
        if (action.StartsWith("give ")) { GiveBox.Ask(action.Substring(5).Trim()); return; }   // (the give box: for the one talking, here)
        if (!Hooks.IsClient() || action.StartsWith("command ")) { State.Book.Run(action, Items.MyBag); return; }   // (a command is for the one talking: here)
        var parts = action.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "turnin")
        {
            int step = State.Book.Progress(parts[1]).Step;
            if (!State.Book.TurnIn(parts[1], Items.MyBag)) return;   // not enough: nothing taken, nothing sent
            Hooks.Ask($"handed {parts[1]} {step}");                  // (for that step only: the host may have moved on)
            return;
        }
        State.Book.Run(action);
        Hooks.Ask(action);
    }

    /// <summary>The host: a guest asked for an action (or handed something in, or reported an event).</summary>
    public static void OnAsk(string text)
    {
        if (!Hooks.IsHost() || string.IsNullOrEmpty(text)) return;
        var parts = text.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0] == "handed" && int.TryParse(parts[2], out var step))
        {
            if (!State.Book.HandedIn(parts[1], step)) Log.Info($"NPCs and quests: a guest's turn-in for {parts[1]} step {step + 1} came too late");
        }
        else if (parts.Length >= 2 && parts[0] == "event") State.Book.Raise(parts[1], parts.Length > 2 ? parts[2] : null);
        else if (Parse.IsAction(text)) State.Book.Run(text);
        else Log.Warn($"NPCs and quests: a guest asked for \"{text}\"");
    }
}

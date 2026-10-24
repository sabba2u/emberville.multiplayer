using BetterCompanion.Core;
using UnityEngine;

namespace BetterCompanion;

/// <summary>
/// /companion: status; /companion N: copy save slot N; /companion new; /companion dismiss / call. (Chat commands come
/// through the multiplayer mod's chat, which hands them to Plugin.Command.) Replacing a companion you have asks
/// first (type it again within 15 s), and keeps a dated copy of the old one.
/// </summary>
internal static class Commands
{
    static readonly Confirm confirm = new();

    public static void Run(string args)
    {
        args = (args ?? "").Trim().ToLowerInvariant();
        if (args == "") { Hooks.Say(CompanionSave.Has ? $"Companion: {CompanionSave.Describe()}, {Companion.State}." : "Companion: none. /companion 2 copies save slot 2, /companion new starts a new one."); return; }
        if (args == "dismiss") { Hooks.Say(Companion.Dismiss()); return; }
        if (args == "call") { Hooks.Say(Companion.Call()); return; }
        bool isNew = args == "new";
        if (!isNew && !int.TryParse(args, out _)) { Hooks.Say("Companion: /companion, /companion <slot>, /companion new, /companion dismiss, /companion call"); return; }
        if (CompanionSave.Has && !confirm.Ask(args, Time.unscaledTime))
        {
            Hooks.Say($"Companion: this replaces {CompanionSave.Data.playerName} (level {CompanionSave.Level}) and the gear you gave them " +
                      $"(a copy is kept in the Saves folder). Type /companion {args} again within 15 s to go ahead.");
            return;
        }
        var before = CompanionSave.Data;
        Hooks.Say(isNew ? CompanionSave.StartNew() : CompanionSave.CloneFrom(int.Parse(args)));
        if (CompanionSave.Data != before) Companion.Reload();   // (refused: the companion stays as it is)
    }
}

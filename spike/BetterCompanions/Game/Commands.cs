using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// /companion: status; /companion N: copy save slot N; /companion new; /companion dismiss / call. (Chat commands come
/// through the multiplayer mod's chat, which hands them to Plugin.Command.) The one it replaces goes to your other
/// companions (CompanionRoster): Insert > Companions swaps it back.
/// </summary>
internal static class Commands
{
    public static void Run(string args)
    {
        args = (args ?? "").Trim().ToLowerInvariant();
        if (args == "") { Hooks.Say(CompanionSave.Has ? $"Companion: {CompanionSave.Describe()}, {Companion.State}." : "Companion: none. /companion 2 copies save slot 2, /companion new starts a new one."); return; }
        if (args == "dismiss") { Hooks.Say(Companion.Dismiss()); return; }
        if (args == "call") { Hooks.Say(Companion.Call()); return; }
        // step 1 of the real companion (a second, real player from its save): out, gone, up again
        if (args == "real" || args == "real on" || args == "real off")
        {
            if (!CompanionSave.Has) { Hooks.Say("Companion: none yet (/companion 2 copies save slot 2)."); return; }
            CompanionSave.Meta.real = args != "real off";
            if (CompanionSave.Meta.real) CompanionSave.Meta.dismissed = false;   // (asking for it real means: out, too)
            CompanionSave.Write();
            Hooks.Say(CompanionSave.Meta.real
                ? $"Companion: {CompanionSave.Data.playerName} is a real player now (the game's own moves, attacks and dodges). /companion real off goes back."
                : $"Companion: {CompanionSave.Data.playerName} is back to the old body.");
            Companion.Reload();
            return;
        }
        bool isNew = args == "new";
        if (!isNew && !int.TryParse(args, out _)) { Hooks.Say("Companion: /companion, /companion <slot>, /companion new, /companion dismiss, /companion call"); return; }
        var before = CompanionSave.Data;
        Hooks.Say(isNew ? CompanionSave.StartNew() : CompanionSave.CloneFrom(int.Parse(args)));
        if (CompanionSave.Data != before) Companion.Reload();   // (refused: the companion stays as it is)
    }
}

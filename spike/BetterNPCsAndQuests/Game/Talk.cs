using System;
using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace BetterNPCsAndQuests;

/// <summary>
/// Our NPCs talk in the game's own dialogue window: each gets a hidden DialogueTrigger (never enabled, so it never
/// registers with the game's save) that plays their .twee through DialogueManager. While it's ours, the window's
/// $variables, (if: ...) conditions and (set: ...) commands go to our flags (with numbers), the speaker's name and
/// portrait come from our NPC, and (trigger: "...") runs our actions: start / turnin / advance &lt;quest&gt;, set $x to ...
/// </summary>
[HarmonyPatch]   // (without it PatchAll skips the patches in this class)
internal static class Talk
{
    /// <summary>The NPC we're talking to right now (null when not).</summary>
    public static Npc Speaking;

    /// <summary>Talking now: the game's dialogue window is really playing ours (so a conversation that ended some other
    /// way, or a scene that changed under it, never leaves our NPCs refusing to talk).</summary>
    public static bool IsOpen
    {
        get
        {
            if (Speaking != null && (!Singleton<DialogueManager>.HasMain || !Singleton<DialogueManager>.main.IsPlaying)) Speaking = null;
            return Speaking != null;
        }
    }

    static readonly Dictionary<string, DialogueTrigger> triggers = new(StringComparer.OrdinalIgnoreCase);

    static bool Ours(DialogueTrigger t) => t && t.uniqueID != null && t.uniqueID.StartsWith("bnq_");

    public static void Open(Npc npc)
    {
        string why = IsOpen ? "already talking to " + Speaking.Def.Name : !Singleton<DialogueManager>.HasMain ? "no dialogue window in this scene"
            : Singleton<DialogueManager>.main.IsPlaying ? "the game's dialogue is already playing" : !State.Lib.Dialogue.ContainsKey(npc.Def.Id) ? "no dialogue loaded" : null;
        if (why != null) { Log.Warn($"NPCs and quests: can't talk to {npc.Def.Name}: {why}"); return; }
        var text = State.Lib.Dialogue[npc.Def.Id];
        try
        {
            var t = TriggerFor(npc.Def.Id);
            npc.Body.FaceFront();   // (to us, the screen: so the portrait shows their face, as the inventory shows yours)
            Listen(Singleton<DialogueManager>.main);
            Speaking = npc;
            t.isOpened = false;   // (left set if the game's window went away without telling it: then it would never open again)
            // (TextAsset by reflection: Unity 6 also has a span constructor, which this build can't resolve)
            var asset = (TextAsset)Activator.CreateInstance(typeof(TextAsset), text);
            asset.name = npc.Def.Id;
            t.TriggerDialogue(asset);
            Log.Info($"NPCs and quests: talking to {npc.Def.Name}");
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: talking to {npc.Def.Name}: {e}"); Ended(); }
    }

    static DialogueTrigger TriggerFor(string id)
    {
        if (triggers.TryGetValue(id, out var t) && t) return t;
        var go = new GameObject("BNQ_Dialogue_" + id);
        go.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(go);
        t = go.AddComponent<DialogueTrigger>();
        t.enabled = false;   // no Start: it never registers with the game's save; TriggerDialogue works all the same
        t.uniqueID = "bnq_" + id;
        t.triggers = new List<DialogueTriggerReceiver>();
        t.onStartDialogue = new CustomEventList { events = new List<CustomEvent>() };
        t.onEndDialogue = new CustomEventList { events = new List<CustomEvent>() };
        t.onStartDialogueEvent = new UnityEvent();
        t.onEndDialogueEvent = new UnityEvent();
        go.SetActive(true);
        triggers[id] = t;
        return t;
    }

    static void Ended() => Speaking = null;

    // (patches, not the manager's events: the publicized build sees each event twice)
    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.EndDialogue)), HarmonyPostfix]
    static void AfterEnd() { if (IsOpen) Ended(); }

    /// <summary>
    /// The window's (trigger: ...) reach us through its own OnDialogueTrigger event (a patch on its one-line Trigger
    /// method can miss: the runtime may inline it). Subscribed once per dialogue manager, by reflection (the
    /// publicized build sees the event twice).
    /// </summary>
    static DialogueManager listening;
    static void Listen(DialogueManager dm)
    {
        if (listening == dm) return;
        listening = dm;
        var ev = typeof(DialogueManager).GetEvent("OnDialogueTrigger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (ev == null) { Log.Warn("NPCs and quests: the dialogue window has no OnDialogueTrigger event"); return; }
        ev.AddEventHandler(dm, new Action<string>(v => { if (OursNow) OnTrigger(v); }));
    }

    /// <summary>(trigger: "...") in our NPC's dialogue: one of our actions.</summary>
    static void OnTrigger(string value)
    {
        value = (value ?? "").Trim().Trim('"');
        Log.Info($"NPCs and quests: trigger \"{value}\"");
        if (Parse.IsAction(value)) Net.Do(value);
        else Log.Info($"NPCs and quests: trigger \"{value}\" isn't one of ours (start / turnin / advance <quest>, set $x to ...)");
    }

    // ---- the dialogue window, while it's ours ----

    static bool OursNow => Singleton<DialogueManager>.HasMain && Ours(Singleton<DialogueManager>.main.activeDialogueTrigger);

    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.GetVariableValue)), HarmonyPrefix]
    static bool GetVariable(DialogueTrigger __instance, string key, ref bool __result)
    {
        if (!Ours(__instance)) return true;
        __result = State.Book.Read(key) != 0;
        return false;
    }

    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.SetVariableValue)), HarmonyPrefix]
    static bool SetVariable(DialogueTrigger __instance, string key, bool value)
    {
        if (!Ours(__instance)) return true;
        Net.Do($"set ${FlagStore.Key(key)} to {(value ? "true" : "false")}");
        return false;
    }

    /// <summary>
    /// A choice's condition ("if: $x is 2 and $has_softwood_10", "else", none), read by us (numbers, our flags) while
    /// it's our NPC. On CheckConditionals, which the window calls for every choice, rather than on its EvaluateIf
    /// alone: a small private method like that one can be inlined into its caller, and a patch on it then never runs.
    /// </summary>
    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.CheckConditionals)), HarmonyPrefix]
    static bool Conditions(string input, ref bool __result)
    {
        if (!OursNow) { NoteReader(false); return true; }
        NoteReader(true);
        if (string.IsNullOrEmpty(input)) { __result = true; return false; }
        int colon = input.IndexOf(':');
        string kind = (colon < 0 ? input : input.Substring(0, colon)).Trim();
        __result = kind == "else" || kind == "if" && Expr.Test(input.Substring(colon + 1), State.Book.Read);
        return false;
    }

    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.EvaluateIf)), HarmonyPrefix]
    static bool EvaluateIf(string input, ref bool __result)
    {
        if (!OursNow) return true;
        __result = Expr.Test(input, State.Book.Read);
        return false;
    }

    static int noted = -1;
    static void NoteReader(bool ours)
    {
        int now = ours ? 1 : 0;
        if (noted == now || Speaking == null) return;
        noted = now;
        var dm = Singleton<DialogueManager>.main;
        Log.Info(ours ? "NPCs and quests: reading conditions ourselves"
                      : $"NPCs and quests: talking to {Speaking.Def.Name} but the window's trigger is {(dm && dm.activeDialogueTrigger ? dm.activeDialogueTrigger.uniqueID : "none")}");
    }

    [HarmonyPatch(typeof(DialogueManager), nameof(DialogueManager.ExecuteCommands)), HarmonyPrefix]
    static bool ExecuteCommand(string command)
    {
        if (!OursNow) return true;
        int colon = command.IndexOf(':');
        if (colon < 0 || command.Substring(0, colon).Trim() != "set") return true;   // trigger / trigger_out: the game's (→ OnTrigger)
        string action = "set " + command.Substring(colon + 1).Trim();
        if (Parse.IsAction(action)) Net.Do(action);
        else Log.Warn($"NPCs and quests: can't do \"({command})\"");
        return false;
    }

    /// <summary>The speaker's name, colour and portrait: ours, when the game has no NPC by that id.</summary>
    [HarmonyPatch(typeof(NPCDatabase), nameof(NPCDatabase.GetEntry)), HarmonyPostfix]
    static void Entry(string id, ref NPCEntry __result)
    {
        if (__result.Sprite || !string.IsNullOrEmpty(__result.Name)) return;
        var def = State.Lib.Npcs.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || n.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (def == null) return;
        __result = new NPCEntry
        {
            ID = id,
            Name = def.Name,
            Color = ColorUtility.TryParseHtmlString(def.Color, out var c) ? c : Color.white,
            Sprite = Portraits.For(def),
        };
    }
}

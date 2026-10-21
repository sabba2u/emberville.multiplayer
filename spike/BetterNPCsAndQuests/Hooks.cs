using System;

namespace BetterNPCsAndQuests;

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

    /// <summary>
    /// "command &lt;text&gt;" in a conversation: another mod's chat command ("pet chicken", "clucking", "risen"), run for
    /// the player who's talking. By default any installed mod with a public static bool Command(string) gets a turn
    /// (Better Pets and Summons, Better Combat...), so it works without the multiplayer mod too.
    /// </summary>
    public static Func<string, bool> Command = cmd =>
    {
        foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
        {
            var mod = info.Instance;
            if (!mod || mod is Plugin) continue;
            var m = mod.GetType().GetMethod("Command", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null);
            try { if (m != null && m.ReturnType == typeof(bool) && (bool)m.Invoke(null, new object[] { cmd })) { Log.Info($"NPCs and quests: \"{cmd}\" done by {info.Metadata.Name}"); return true; } }
            catch (Exception e) { Log.Warn($"NPCs and quests: \"{cmd}\" in {info.Metadata.Name}: {e.InnerException?.Message ?? e.Message}"); return false; }
        }
        Log.Warn($"NPCs and quests: no mod knows the command \"{cmd}\"");
        return false;
    };

    /// <summary>
    /// Flags other mods work out ($has_pet from Better Pets and Summons): any installed mod with a public static
    /// int? NpcFlag(string) is asked; null = not theirs. Looked up once.
    /// </summary>
    public static Func<string, int?> Flag = name =>
    {
        if (flagMethods == null)
        {
            flagMethods = new System.Collections.Generic.List<System.Reflection.MethodInfo>();
            foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
            {
                if (!info.Instance || info.Instance is Plugin) continue;
                var m = info.Instance.GetType().GetMethod("NpcFlag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m != null && m.ReturnType == typeof(int?)) flagMethods.Add(m);
            }
        }
        foreach (var m in flagMethods)
        {
            try { var v = (int?)m.Invoke(null, new object[] { name }); if (v.HasValue) return v; }
            catch (Exception e) { Log.Warn($"NPCs and quests: ${name}: {e.InnerException?.Message ?? e.Message}"); }
        }
        return null;
    };
    static System.Collections.Generic.List<System.Reflection.MethodInfo> flagMethods;

    // ---- co-op (the multiplayer mod): the host's world holds the story ----

    /// <summary>We host a co-op game.</summary>
    public static Func<bool> IsHost = () => false;
    /// <summary>We're a guest in someone's game: their files and story count, ours stay untouched.</summary>
    public static Func<bool> IsClient = () => false;
    /// <summary>A guest: send this to the host (an action, "handed &lt;quest&gt; &lt;step&gt;", or "event &lt;name&gt; &lt;arg&gt;").</summary>
    public static Action<string> Ask = _ => { };
    /// <summary>The multiplayer mod draws the NPCs' name cards (like Rory's): our own text labels stay hidden.</summary>
    public static bool DrawsLabels;
    /// <summary>The host read its files again (the guests get the new set).</summary>
    public static Action FilesChanged = () => { };
    /// <summary>The host: the whole story (Book.Save text) for every guest, after any change.</summary>
    public static Action<string> SendBook = _ => { };
    /// <summary>Another player within this distance of a point (NPCs stop wandering for them too).</summary>
    public static Func<UnityEngine.Vector3, float, bool> OthersNear = (_, _) => false;
    /// <summary>The host: this NPC now walks to this point (or stops there), for the guests.</summary>
    public static Action<string, UnityEngine.Vector3> SendWander = (_, _) => { };
}

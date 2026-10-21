using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// Better NPCs and Quests: NPCs, Twine dialogue, quests and world rules, all from files in this mod's folder (npcs/,
/// quests/, rules/). NPCs talk in the game's own dialogue window; quests show in the game's own journal and quest list;
/// our own story flags are saved per world in our own file (never in the game's save). Works on its own; next to the
/// multiplayer mod, the host's files, flags and quests are shared with everyone (it fills in our Hooks).
/// </summary>
[BepInPlugin(Guid, "Better NPCs and Quests", Version)]
// (no dependency on the multiplayer mod: it depends on us, loads after us and fills in our Hooks)
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betternpcsandquests";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;
    internal static string Folder;

    void Awake()
    {
        Logger_ = Logger;
        Folder = System.IO.Path.GetDirectoryName(Info.Location);
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        State.Reloaded += Portraits.Forget;
        State.Book.Command = cmd => Hooks.Command(cmd);
        Logger.LogInfo($"Better NPCs and Quests v{Version}: {Folder}");
    }

    void Update()
    {
        try
        {
            if (GameData.HasWorldData)
            {
                Items.Preload();
                if (!State.Loaded) { State.Book.Computed = n => Hooks.Flag(n) ?? Items.Computed(n); State.Reload(); }
            }
            WorldFile.Tick();
            Npcs.Tick();
            QuestBridge.Tick();
            Net.Tick();
            Events.Hook();
            Window.Tick();
            GiveBox.Tick();
        }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    // ---- for the multiplayer mod ----

    /// <summary>The "NPCs and Quests" tab of its Insert menu (ImGui).</summary>
    public static void DrawMenu() => Menu.Draw();

    /// <summary>The NPCs' name cards, as the multiplayer mod draws Rory's (ImGui, from its HUD).</summary>
    public static void DrawLabels() => Menu.DrawLabels();

    /// <summary>The host's files (npcs/, quests/, rules/, portraits), for a guest.</summary>
    public static byte[] FilesPacked() => Files.Pack(Files.Local());

    /// <summary>The host's story (flags and quest progress), for a guest.</summary>
    public static string BookText() => State.Book.Save();

    public static void OnFiles(byte[] data) => Net.OnFiles(data);
    public static void OnBook(string text) => Net.OnBook(text);
    public static void OnAsk(string text) => Net.OnAsk(text);
    public static void OnWander(string npcId, Vector3 to) => Wander.Remote(npcId, to);
    public static void Disconnected() => Net.Disconnected();
}

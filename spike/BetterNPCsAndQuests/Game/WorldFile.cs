using System;
using System.IO;

namespace BetterNPCsAndQuests;

/// <summary>
/// Our flags and quest progress, per world, in our own file next to the world's save (&lt;world&gt;.bnq). The game's own
/// save is never touched, but ours is written when the game saves (GameData's OnSave), so the story and the bag (items
/// handed in, rewards) always roll back together. As a guest in someone's game, the host's story is used instead and
/// nothing is written; our own world's story is read again after leaving.
/// </summary>
internal static class WorldFile
{
    static string loadedFor;
    static bool hooked;

    static string FileFor(string world) => SaveLoad.GetPath(world + ".bnq");

    public static void Tick()
    {
        if (!hooked) { hooked = true; GameData.Register(GameData.Call.OnSave, (_, _) => Save()); }
        if (Hooks.IsClient()) { loadedFor = null; return; }   // the host's story (Net.OnBook); ours is read again after
        string world = GameData.HasWorldData ? GameData.GetWorldPrefix : null;
        if (world == loadedFor) return;
        loadedFor = world;
        Load();
    }

    /// <summary>Forget what's loaded (after being a guest): our own file is read again at the next tick.</summary>
    public static void Forget() => loadedFor = null;

    static void Load()
    {
        string text = "";
        if (loadedFor != null)
            try { string path = FileFor(loadedFor); if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (Exception e) { Log.Warn($"NPCs and quests: can't read the world's file: {e.Message}"); }
        State.Book.Load(text);
        QuestBridge.AfterLoad();
    }

    static void Save()
    {
        if (loadedFor == null || Hooks.IsClient()) return;
        try
        {
            string path = FileFor(loadedFor), tmp = path + ".tmp";
            File.WriteAllText(tmp, State.Book.Save());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: can't save the world's file: {e.Message}"); }
    }
}

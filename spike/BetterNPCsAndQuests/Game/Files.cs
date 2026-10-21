using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterNPCsAndQuests.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BetterNPCsAndQuests;

internal static class LocalPlayer
{
    public static PlayerMaster Get() => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    public static string Scene => SceneManager.GetActiveScene().name;
}

/// <summary>
/// Our files: the mod folder's npcs/, quests/ and rules/ (paths relative to the mod folder, with "/"), or, while we're a
/// guest in someone's game, the host's set (kept in memory only, never written to our folder).
/// </summary>
internal static class Files
{
    static readonly string[] Roots = { "npcs", "quests", "rules" };

    /// <summary>The host's files while we're connected to them; null otherwise.</summary>
    public static Dictionary<string, byte[]> Remote;

    public static Dictionary<string, byte[]> Current => Remote ?? Local();

    public static Dictionary<string, byte[]> Local()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            string dir = Path.Combine(Plugin.Folder, root);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { map[path.Substring(Plugin.Folder.Length + 1).Replace('\\', '/')] = File.ReadAllBytes(path); }
                catch (Exception e) { Log.Warn($"NPCs: can't read {path}: {e.Message}"); }
            }
        }
        return map;
    }

    /// <summary>All files in one blob (count, then path + bytes each), for the host to send.</summary>
    public static byte[] Pack(Dictionary<string, byte[]> map)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(map.Count);
        foreach (var kv in map) { w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); }
        w.Flush();
        return ms.ToArray();
    }

    public static Dictionary<string, byte[]> Unpack(byte[] data)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) { string path = r.ReadString(); map[path] = r.ReadBytes(r.ReadInt32()); }
        return map;
    }
}

/// <summary>What's loaded: the files read (Lib) and the world's story (Book, one for the whole game, refilled per world).</summary>
internal static class State
{
    public static Library Lib = Library.Load(new Dictionary<string, byte[]>());
    public static readonly Book Book = new(Lib);
    public static bool Loaded;
    /// <summary>After every (re)load of the files.</summary>
    public static event Action Reloaded;

    public static void Reload()
    {
        try
        {
            Lib = Library.Load(Files.Current);
            Book.Reattach(Lib);
            Loaded = true;
            string from = Files.Remote != null ? " (the host's)" : "";
            Log.Info($"NPCs and quests{from}: {Lib.Npcs.Count} NPCs, {Lib.Quests.Count} quests, {Lib.Rules.Count} rules");
            foreach (var p in Lib.Problems) Log.Warn("NPCs and quests: " + p);
            foreach (var p in Lib.FlagWarnings) Log.Warn("NPCs and quests: " + p);
            if (Items.Ready) Items.Check();
            Reloaded?.Invoke();
            if (Hooks.IsHost()) Hooks.FilesChanged();
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: loading failed: {e}"); }
    }
}

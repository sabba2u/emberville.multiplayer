using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Nobody loses a character:
///  - In use: every copy of the game on this PC marks the character it's playing (a lock file with its process id,
///    refreshed every few seconds, in a folder all copies share). The join window greys out characters another
///    running copy has, and joining refuses them: the same character in two games at once would save over itself.
///  - Backups: before the mod switches characters or worlds (joining from inside a game), the character files involved
///    are copied to EmbervilleGhost_Backups next to the saves, the last 10 of each kept. To restore one, copy it back
///    over the .evp in the save folder (with the game closed).
/// </summary>
internal static class CharacterSafety
{
    const int KeepBackups = 10;
    const float LockRefresh = 3f, LockStale = 12f;

    static float nextLock;
    static string lockedPrefix;

    static string Shared => Path.Combine(Application.persistentDataPath, "EmbervilleGhost_Locks");
    static string LockFile(string prefix) => Path.Combine(Shared, prefix + ".lock");

    /// <summary>Every frame: keep our character's lock fresh while we're in a game.</summary>
    public static void Tick()
    {
        if (Time.unscaledTime < nextLock) return;
        nextLock = Time.unscaledTime + LockRefresh;
        try
        {
            string prefix = LocalPlayer.Get() && GameData.HasPlayerData ? GameData.playerFilePrefix : null;
            if (lockedPrefix != null && lockedPrefix != prefix) Release();
            if (prefix == null) return;
            Directory.CreateDirectory(Shared);
            File.WriteAllText(LockFile(prefix), Process.GetCurrentProcess().Id.ToString());
            lockedPrefix = prefix;
        }
        catch (Exception e) { NetLog.Warn($"Character lock: {e.Message}"); }
    }

    public static void Release()
    {
        try
        {
            if (lockedPrefix != null && File.Exists(LockFile(lockedPrefix)) &&
                File.ReadAllText(LockFile(lockedPrefix)).Trim() == Process.GetCurrentProcess().Id.ToString())
                File.Delete(LockFile(lockedPrefix));
        }
        catch { }
        lockedPrefix = null;
    }

    /// <summary>Is this character being played by another running copy of the game on this PC?</summary>
    public static bool InUseElsewhere(string prefix)
    {
        try
        {
            string path = LockFile(prefix);
            if (!File.Exists(path)) return false;
            if ((DateTime.Now - File.GetLastWriteTime(path)).TotalSeconds > LockStale) return false;   // left behind by a crash
            if (!int.TryParse(File.ReadAllText(path).Trim(), out int pid) || pid == Process.GetCurrentProcess().Id) return false;
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }   // that process is gone
        }
        catch { return false; }
    }

    /// <summary>Copy this character's save file to the backups (the last few kept).</summary>
    public static void Backup(string prefix, string why)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        try
        {
            string file = SaveLoad.GetPath(prefix + ".evp");
            if (!File.Exists(file)) return;
            string dir = Path.Combine(Path.GetDirectoryName(file), "EmbervilleGhost_Backups");
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.evp");
            File.Copy(file, copy, overwrite: true);
            foreach (var old in new DirectoryInfo(dir).GetFiles(prefix + "_*.evp").OrderByDescending(f => f.LastWriteTime).Skip(KeepBackups))
                old.Delete();
            NetLog.Info($"Backed up character {prefix} ({why}) to {copy}");
        }
        catch (Exception e) { NetLog.Warn($"Character backup failed: {e.Message}"); }
    }
}

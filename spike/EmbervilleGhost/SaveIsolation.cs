using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Several copies on one PC. Characters and worlds are SHARED (every copy sees every character, so you can make one
/// in any window and load it in another); only the options file (window size, volume...) is per copy, so copy #2's
/// windowed 1280x720 doesn't fight copy #1's settings.
///
/// Every save goes through SaveLoad.GetPath(fileName) = persistentDataPath/Saves/fileName; we patch that one method
/// for "options.eopt" only. (DuckovGhost redirected the whole data folder; here that hid characters made in one copy
/// from the others.) Rule while testing: don't load the SAME character in two copies at once, the last save wins.
/// </summary>
[HarmonyPatch(typeof(SaveLoad), nameof(SaveLoad.GetPath))]
internal static class SaveIsolation
{
    const string OptionsFile = "options.eopt";

    static Mutex instanceMutex; // held for the process lifetime
    static ConfigEntry<bool> separateOptions;
    static string optionsFolder;

    public static int CopyNumber { get; private set; } = 1;
    public static string DataPath => Path.Combine(Application.persistentDataPath, "Saves");

    public static void Init(ConfigFile config)
    {
        separateOptions = config.Bind("Saves", "SeparateOptionsPerCopy", true,
            "Give the 2nd/3rd... running copy its own options file (characters and worlds are always shared)");

        CopyNumber = ClaimCopyNumber();
        if (!separateOptions.Value || CopyNumber == 1) return;

        optionsFolder = Path.Combine(Application.persistentDataPath, $"EmbervilleGhost_Copy{CopyNumber}");
        string mine = Path.Combine(optionsFolder, OptionsFile);
        string original = Path.Combine(DataPath, OptionsFile);
        if (!File.Exists(mine) && File.Exists(original))
        {
            Directory.CreateDirectory(optionsFolder);
            File.Copy(original, mine);
        }
        NetLog.Info($"Copy #{CopyNumber}: shared characters, own options in {optionsFolder}", toast: true);
    }

    static bool Prefix(string fileName, ref string __result)
    {
        // The host's world we joined (GuestWorld): its own folder per copy, never next to (or in the list of) our own worlds.
        if (fileName != null && fileName.StartsWith(GuestWorld.Prefix))
        {
            __result = Path.Combine(Application.persistentDataPath, $"EmbervilleGhost_Guest{CopyNumber}", fileName);
            return false;
        }
        if (optionsFolder == null || fileName != OptionsFile) return true;
        __result = Path.Combine(optionsFolder, fileName);
        return false;
    }

    /// <summary>First process to grab "EmbervilleGhost_Copy1" is copy 1, next free slot is copy 2, etc.</summary>
    static int ClaimCopyNumber()
    {
        for (int n = 1; n <= 8; n++)
        {
            var m = new Mutex(true, $"Local\\EmbervilleGhost_Copy{n}", out bool created);
            if (created)
            {
                instanceMutex = m;
                return n;
            }
            m.Dispose();
        }
        return 9;
    }
}

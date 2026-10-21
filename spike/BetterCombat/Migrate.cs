using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace BetterCombat;

/// <summary>
/// These settings used to live in the multiplayer mod's config (spike.embervilleghost.cfg). On this mod's first run
/// (its own config file doesn't exist yet), the values of the keys it owns are copied over from there, so a pet keeps
/// its level and name and the switches stay as they were.
/// </summary>
internal static class Migrate
{
    public static void FromGhost(ConfigFile config, bool firstRun, params string[] sections)
    {
        if (!firstRun) return;
        string old = Path.Combine(Paths.ConfigPath, "spike.embervilleghost.cfg");
        if (!File.Exists(old)) return;
        try
        {
            string section = null;
            int n = 0;
            foreach (var raw in File.ReadAllLines(old))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                if (section == null || Array.IndexOf(sections, section) < 0 || line.StartsWith("#") || !line.Contains("=")) continue;
                int eq = line.IndexOf('=');
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                foreach (var def in config.Keys)
                    if (def.Section == section && def.Key == key) { config[def].SetSerializedValue(value); n++; }
            }
            if (n > 0) { config.Save(); Plugin.Logger_.LogInfo($"Took over {n} settings from the multiplayer mod's config"); }
        }
        catch (Exception e) { Plugin.Logger_.LogWarning($"Settings from the multiplayer mod's config: {e.Message}"); }
    }
}

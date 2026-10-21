using BepInEx;
using BepInEx.Logging;

namespace BetterLooting;

/// <summary>
/// Better Looting: the loot magnet (items near you picked up by themselves, per category) and a green arrow over items
/// on the ground that are better than what you wear. Works on its own (settings in its config file); next to the
/// multiplayer mod, its switches show in the Insert menu (Options).
/// For the multiplayer mod: DrawMenu() draws the switches.
/// </summary>
[BepInPlugin(Guid, "Better Looting", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterlooting";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger_;

    void Awake()
    {
        Logger_ = Logger;
        bool firstRun = !System.IO.File.Exists(Config.ConfigFilePath);
        Magnet.Bind(Config);
        LootArrows.Bind(Config);
        Migrate.FromGhost(Config, firstRun, "Magnet", "Loot");
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo($"Better Looting v{Version}: loot magnet, green arrows over better gear");
    }

    void Update()
    {
        try { Magnet.Tick(); LootArrows.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    /// <summary>The switches, for the multiplayer mod's Insert menu (it draws ImGui).</summary>
    public static void DrawMenu()
    {
        Magnet.DrawMenu();
        LootArrows.DrawMenu();
    }
}

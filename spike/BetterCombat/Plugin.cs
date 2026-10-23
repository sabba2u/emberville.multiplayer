using BepInEx;
using BepInEx.Logging;

namespace BetterCombat;

/// <summary>
/// Better Combat: bow lock-on (arrows curve onto the creature nearest your aim; /bowlock, /bowlock 15) and moving while
/// you attack (/moveattack). Works on its own (settings in its config file); next to the multiplayer mod, its switches
/// show in the Insert menu (Options) and the chat commands work there.
/// For the multiplayer mod: Command(text) runs a chat command (true = it was ours), DrawMenu() draws the switches.
/// </summary>
[BepInPlugin(Guid, "Better Combat", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.bettercombat";
    public const string Version = "0.2.0";
    internal static ManualLogSource Logger_;

    void Awake()
    {
        Logger_ = Logger;
        bool firstRun = !System.IO.File.Exists(Config.ConfigFilePath);
        BowAssist.Bind(Config);
        MoveAttack.Bind(Config);
        LockOn.Bind(Config);
        Migrate.FromGhost(Config, firstRun, "Combat");
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo($"Better Combat v{Version}: lock-on (G), bow lock-on (/bowlock), move while attacking (/moveattack)");
    }

    void Update()
    {
        try { LockOn.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    /// <summary>A chat command ("bowlock", "bowlock 15", "moveattack", "moveattack 70"); true if it was one of ours.</summary>
    public static bool Command(string cmd)
    {
        cmd = (cmd ?? "").Trim().ToLowerInvariant();
        if (cmd == "moveattack" || cmd.StartsWith("moveattack ")) { MoveAttack.Command(cmd.Length > 10 ? cmd.Substring(11) : ""); return true; }
        if (cmd.StartsWith("bowlock ") && float.TryParse(cmd.Substring(8).Trim().TrimEnd('°'), out float deg)) { BowAssist.SetAngle(deg); return true; }
        if (cmd == "bowlock") { BowAssist.Toggle(); return true; }
        return false;
    }

    /// <summary>The switches, for the multiplayer mod's Insert menu (it draws ImGui).</summary>
    public static void DrawMenu()
    {
        BowAssist.DrawMenu();
        MoveAttack.DrawMenu();
        LockOn.DrawMenu();
    }
}

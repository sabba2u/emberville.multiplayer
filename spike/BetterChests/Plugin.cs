using BepInEx;
using BepInEx.Logging;

namespace BetterChests;

/// <summary>
/// Better Chests: walk up to a chest and press X, and everything in your bag that the chest already holds goes in
/// (QuickStack); press V to give it a name and a colour (ChestLooks). Works alone or next to the Emberville multiplayer mod, which hooks QuickStack.Run to do the same
/// in a session (the area owner's real contents, other players' locks, the new contents to everyone).
/// </summary>
[BepInPlugin(Guid, "Better Chests", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterchests";
    internal const string Version = "0.1.0";
    internal static ManualLogSource Log;

    void Awake()
    {
        Log = Logger;
        QuickStack.Bind(Config);
        Hint.Bind(Config);
        ChestLooks.Bind(Config);
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Log.LogInfo($"Better Chests v{Version}: press {QuickStack.KeyName} at a chest to put in what it already holds, {ChestLooks.KeyName} to name / colour it");
    }

    void Update()
    {
        try { QuickStack.Tick(); ChestSearch.Tick(); Favourites.Tick(); }
        catch (System.Exception e) { Log.LogWarning(e); }
    }
}

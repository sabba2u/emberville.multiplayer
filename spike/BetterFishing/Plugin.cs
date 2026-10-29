using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BetterFishing;

/// <summary>
/// Better Fishing: every catch brings something up (food or a material; now and then anything, up to legendary), and you can
/// fish wherever the game has fish shadows in the water (the starting area's pond...). Works on its own.
/// </summary>
[BepInPlugin(Guid, "Better Fishing", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterfishing";
    public const string Version = "0.1.1";
    internal static ManualLogSource Logger_;
    static ConfigEntry<float> rare;

    void Awake()
    {
        Logger_ = Logger;
        rare = Config.Bind("Fishing", "RareChance", 25f, "Chance (%) that a catch is anything at all (up to legendary) instead of food or a material");
        Fishing.RareChance = UnityEngine.Mathf.Clamp01(rare.Value / 100f);
        rare.SettingChanged += (_, _) => Fishing.RareChance = UnityEngine.Mathf.Clamp01(rare.Value / 100f);
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo($"Better Fishing v{Version}: a catch from every cast, spots at the fish shadows");
    }

    void Start() => Catalogue.Preload();

    void Update()
    {
        try { Fishing.Tick(); }
        catch (System.Exception e) { Logger.LogWarning(e); }
    }
}

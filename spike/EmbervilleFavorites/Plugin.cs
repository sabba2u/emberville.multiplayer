using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace EmbervilleFavorites;

/// <summary>
/// Emberville Favourite Weapons. In the inventory, hover a weapon and press F5-F8 to put it on that key; while playing,
/// F5-F8 (or a click on the bar) wield it. The bar of four slots above the game's HUD is made from the game's own belt
/// slot, so it looks and behaves like the rest of the HUD. Works alone or next to the multiplayer mod.
/// </summary>
[BepInPlugin("spike.emberville.favorites", "Emberville Favourite Weapons", Version)]
public class Plugin : BaseUnityPlugin
{
    internal const string Version = "0.1.0";
    internal static ManualLogSource Log;

    void Awake()
    {
        Log = Logger;
        Favorites.Bind(Config);
        WeaponWheel.Bind(Config);
        Tutorial.Bind(Config);
        new Harmony("spike.emberville.favorites").PatchAll();
        Log.LogInfo($"Emberville Favourite Weapons v{Version}: hover a weapon in the inventory and press F5-F8; hold Left Alt (pad: LT) for the wheel");
    }

    void Update()
    {
        try
        {
            Favorites.Tick();
            FavoritesBar.Tick();
            WeaponWheel.Tick();
            Tutorial.Tick();
        }
        catch (System.Exception e) { Log.LogWarning(e); }
    }
}

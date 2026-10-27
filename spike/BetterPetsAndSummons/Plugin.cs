using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BetterPetsAndSummons;

/// <summary>
/// Better Pets and Summons: a pet that follows you and fights (/pet), the Staff of Clucking (/clucking: every attack
/// summons a battle chicken) and the Staff of the Risen (/risen: raise fallen creatures to fight for you), all built
/// on one creature stand-in engine (PetBody). Works on its own (settings in its config file); next to the multiplayer
/// mod, its switches show in the Insert menu (Pets), its chat commands work, its name tags and health bars are drawn,
/// teammates see your pet and summons, and descent guardians drop the staffs.
/// On its own, Insert opens its pets menu (PetWindow, the game's look): pets, their name and colour, and the two staffs.
/// For the multiplayer mod: Command, DrawMenu, DrawHud, Rally, HoldingStaff, MaybeDrop, and the Hooks it fills in.
/// </summary>
[BepInPlugin(Guid, "Better Pets and Summons", Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "spike.emberville.betterpetsandsummons";
    public const string Version = "0.1.3";
    internal static ManualLogSource Logger_;

    void Awake()
    {
        Logger_ = Logger;
        bool firstRun = !System.IO.File.Exists(Config.ConfigFilePath);
        Pet.Bind(Config);
        Migrate.FromGhost(Config, firstRun, "Pet");
        new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        Logger.LogInfo($"Better Pets and Summons v{Version}: /pet, /clucking, /risen");
    }

    void Update()
    {
        try { Pet.Tick(); CluckStaff.Tick(); RisenStaff.Tick(); PetWindow.Tick(); }   // (Insert: our own pets menu, when the multiplayer mod isn't there)
        catch (System.Exception e) { Logger.LogWarning(e); }
    }

    /// <summary>A chat command ("pet ...", "clucking", "risen"); true if it was one of ours.</summary>
    public static bool Command(string cmd)
    {
        cmd = (cmd ?? "").Trim();
        string low = cmd.ToLowerInvariant();
        if (low == "pet" || low.StartsWith("pet ")) { Pet.Command(cmd.Length > 3 ? cmd.Substring(4) : ""); return true; }
        if (low == "clucking") { CluckStaff.Give(); return true; }
        if (low == "risen") { RisenStaff.Give(); return true; }
        return false;
    }

    /// <summary>For NPC conversations (Better NPCs and Quests asks every mod): $has_pet = you have a pet (1 / 0).</summary>
    public static int? NpcFlag(string name) => name == "has_pet" ? (Pet.enabled.Value && Pet.owned.Value ? 1 : 0) : (int?)null;

    /// <summary>The pet's settings, for the multiplayer mod's Insert menu (ImGui).</summary>
    public static void DrawMenu() => Pet.DrawMenu();

    /// <summary>The two staffs, into your bag (the multiplayer mod's Better Combat tab: New Weapons).</summary>
    public static void GiveCluckStaff() => CluckStaff.Give();

    /// <summary>Battle chickens with no staff (Better Streaming): how many came.</summary>
    public static int SummonChickens(Vector3 at, int count, int level, float life, Color? tint = null, float size = 0f, string tag = null) => CluckStaff.Flock(at, count, level, life, tint, size, tag);

    /// <summary>Summons for Better Streaming still about: "chickens", "golden" (its golden chickens), "guardians".</summary>
    public static int SummonsAlive(string tag) => tag == "guardians" ? RisenStaff.GuardiansAlive() : CluckStaff.Alive(tag);

    /// <summary>Those creatures rise beside there and fight for you (Better Streaming's guardians): how many came.</summary>
    public static int RaiseGuardians(Vector3 at, System.Collections.Generic.IEnumerable<string> prefabs, float hp, float damage, float life) => RisenStaff.Guardians(at, prefabs, hp, damage, life);

    /// <summary>Your pet golden for a while (Better Streaming): false: no pet out.</summary>
    public static bool GoldenPet(float seconds) => Pet.Golden(seconds);
    public static void GiveRisenStaff() => RisenStaff.Give();

    /// <summary>Name tags and health bars over our pet and summons (ImGui, drawn by the multiplayer mod's HUD).</summary>
    public static void DrawHud() { Pet.DrawLabels(); CluckStaff.Draw(); RisenStaff.Draw(); }

    /// <summary>A middle-click order: the chickens and the risen run there.</summary>
    public static void Rally(Vector3 point) { CluckStaff.Rally(point); RisenStaff.Rally(point); }

    /// <summary>Holding one of the staffs (a middle click is then an order, not a ping).</summary>
    public static bool HoldingStaff => CluckStaff.HeldNow || RisenStaff.HeldNow;

    /// <summary>A guardian's death in the descent: now and then, a staff among its loot.</summary>
    public static void MaybeDrop(Vector3 at, int floor) { CluckStaff.MaybeDrop(at, floor); RisenStaff.MaybeDrop(at, floor); }
}

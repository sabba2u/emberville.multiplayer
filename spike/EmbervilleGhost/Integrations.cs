using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The split-out mods, when they're installed: Better Combat (bow lock-on, move while attacking), Better Looting (the
/// magnet, green arrows) and Better Pets and Summons (pets, the two staffs, the creature stand-in engine). Each works
/// on its own; with this mod they also get their switches in the Insert menu, their chat commands, their name tags and
/// health bars, co-op sync (teammates' pets and summons), and the descent guardians' staff drops.
/// Every call into one of them sits in its own small method (NoInlining) behind its Has* check: if a mod isn't
/// installed, the code that names its types is never compiled by the runtime, so nothing breaks.
/// </summary>
internal static class Integrations
{
    public const string CombatGuid = "spike.emberville.bettercombat";
    public const string LootingGuid = "spike.emberville.betterlooting";
    public const string PetsGuid = "spike.emberville.betterpetsandsummons";
    public const string NpcsGuid = "spike.emberville.betternpcsandquests";
    public const string WeatherGuid = "spike.emberville.betterweather";

    public static bool HasCombat { get; private set; }
    public static bool HasLooting { get; private set; }
    public static bool HasPets { get; private set; }
    public static bool HasNpcs { get; private set; }
    public static bool HasWeather { get; private set; }

    /// <summary>From Plugin.Awake (they load first: soft dependencies).</summary>
    public static void Init()
    {
        HasCombat = Chainloader.PluginInfos.ContainsKey(CombatGuid);
        HasLooting = Chainloader.PluginInfos.ContainsKey(LootingGuid);
        HasPets = Chainloader.PluginInfos.ContainsKey(PetsGuid);
        if (HasPets) PetHooks();
        HasNpcs = Chainloader.PluginInfos.ContainsKey(NpcsGuid);
        if (HasNpcs) NpcHooks();
        HasWeather = Chainloader.PluginInfos.ContainsKey(WeatherGuid);
        if (HasWeather) WeatherSync.Hook();
        NetLog.Info("Split-out mods: " + string.Join(", ", new List<string> {
            HasCombat ? "Better Combat" : null, HasLooting ? "Better Looting" : null, HasPets ? "Better Pets and Summons" : null,
            HasNpcs ? "Better NPCs and Quests" : null, HasWeather ? "Better Weather" : null }.FindAll(x => x != null)) +
            (HasCombat || HasLooting || HasPets || HasNpcs || HasWeather ? "" : "none"), toast: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PetHooks()
    {
        BetterPetsAndSummons.Hooks.Ignore = EnemySync.LocalOnly;
        BetterPetsAndSummons.Hooks.IsBoss = MiniBoss.IsBoss;
        BetterPetsAndSummons.Hooks.Say = Chat.System;
        BetterPetsAndSummons.Hooks.Typing = () => ImGuiUnity.Typing || Chat.IsOpen;
        BetterPetsAndSummons.Hooks.Busy = () => PlayerStatusUtil.LocalBusy;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NpcHooks()
    {
        BetterNPCsAndQuests.Hooks.Say = Chat.System;
        BetterNPCsAndQuests.Hooks.Typing = () => ImGuiUnity.Typing || Chat.IsOpen;
        BetterNPCsAndQuests.Hooks.Busy = () => PlayerStatusUtil.LocalBusy;
        BetterNPCsAndQuests.Hooks.IsHost = () => NpcSync.Plugin != null && NpcSync.Plugin.Connected && NpcSync.Plugin.IsHost;
        BetterNPCsAndQuests.Hooks.IsClient = () => NpcSync.Plugin != null && NpcSync.Plugin.IsClient;
        BetterNPCsAndQuests.Hooks.Ask = NpcSync.Ask;
        BetterNPCsAndQuests.Hooks.SendBook = NpcSync.SendBook;
        BetterNPCsAndQuests.Hooks.SendWander = NpcSync.SendWander;
        BetterNPCsAndQuests.Hooks.OthersNear = NpcSync.OthersNear;
        BetterNPCsAndQuests.Hooks.FilesChanged = NpcSync.SendFilesToAll;
        BetterNPCsAndQuests.Hooks.DrawsLabels = true;   // (their names drawn like Rory's: Hud → DrawHud → DrawLabels)
        // our own chat commands an NPC can run too ("command respec": Ellie); the rest as before
        var others = BetterNPCsAndQuests.Hooks.Command;
        BetterNPCsAndQuests.Hooks.Command = cmd =>
        {
            if (string.Equals(cmd?.Trim(), "respec", System.StringComparison.OrdinalIgnoreCase)) { Respec.Run(); return true; }
            return others(cmd) || Chat.Run(cmd);   // (then any of our chat commands: /tp, /summon, /dungeon...)
        };
    }

    /// <summary>Better NPCs and Quests' Insert tab.</summary>
    public static void DrawNpcs()
    {
        if (HasNpcs) NpcsMenu(); else Missing("Better NPCs and Quests (NPCs, dialogue and quests from files)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void NpcsMenu() => BetterNPCsAndQuests.Plugin.DrawMenu();
    [MethodImpl(MethodImplOptions.NoInlining)] static void NpcLabels() => BetterNPCsAndQuests.Plugin.DrawLabels();

    /// <summary>Something happened in the dungeon, for Better NPCs and Quests' rules (boss_kill, descent_floor).</summary>
    public static void NpcEvent(string ev, string arg) { if (HasNpcs) NpcEventCall(ev, arg); }

    [MethodImpl(MethodImplOptions.NoInlining)] static void NpcEventCall(string ev, string arg) => BetterNPCsAndQuests.Events.Raise(ev, arg);

    // ---- chat ----

    /// <summary>A chat command one of them handles (/bowlock, /moveattack, /pet, /clucking, /risen); true if handled.</summary>
    public static bool Command(string cmd)
    {
        if (HasCombat && CombatCommand(cmd)) return true;
        if (HasPets && PetsCommand(cmd)) return true;
        if (HasWeather && WeatherCommand(cmd)) return true;
        if (chestsCommand != null && chestsCommand.Invoke(null, new object[] { cmd }) is bool ok && ok) return true;   // /stash (Better Chests)
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static bool CombatCommand(string c) => BetterCombat.Plugin.Command(c);

    /// <summary>Better Combat's lock-on: where to aim (the locked creature), if locked.</summary>
    public static bool LockedAim(out Vector3 at)
    {
        at = Vector3.zero;
        return HasCombat && CombatAim(out at);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool CombatAim(out Vector3 at) => BetterCombat.LockOn.AimPoint(out at);
    static readonly System.Reflection.MethodInfo chestsCommand =
        HarmonyLib.AccessTools.TypeByName("BetterChests.Plugin") is System.Type ct ? HarmonyLib.AccessTools.Method(ct, "Command", new[] { typeof(string) }) : null;
    [MethodImpl(MethodImplOptions.NoInlining)] static bool PetsCommand(string c) => BetterPetsAndSummons.Plugin.Command(c);
    [MethodImpl(MethodImplOptions.NoInlining)] static bool WeatherCommand(string c) => BetterWeather.Plugin.Command(c);

    /// <summary>Better Weather's Insert tab.</summary>
    public static void DrawWeather()
    {
        if (HasWeather) WeatherMenu(); else Missing("Better Weather (a rain storm: /rain, snow: /snow)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void WeatherMenu() => BetterWeather.Plugin.DrawMenu();

    // ---- the Insert menu ----

    public static void DrawCombat()
    {
        if (HasCombat) CombatMenu(); else Missing("Better Combat (bow lock-on, move while attacking)");
    }

    public static void DrawLooting()
    {
        if (HasLooting) LootingMenu(); else Missing("Better Looting (the magnet, green arrows over better gear)");
    }

    public static void DrawPets()
    {
        if (HasPets) PetsMenu(); else Missing("Better Pets and Summons (pets, the Staff of Clucking, the Staff of the Risen)");
    }

    static void Missing(string what)
    {
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.TextDisabled($"Not installed: {what}.");
        ImGuiNET.ImGui.TextDisabled("    It comes in the mod pack: unzip the pack again, or add its folder to BepInEx/plugins.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void LootingMenu() => BetterLooting.Plugin.DrawMenu();
    [MethodImpl(MethodImplOptions.NoInlining)] static void CombatMenu() => BetterCombat.Plugin.DrawMenu();
    [MethodImpl(MethodImplOptions.NoInlining)] static void PetsMenu() => BetterPetsAndSummons.Plugin.DrawMenu();

    // ---- the HUD (ImGui) ----

    public static void DrawHud()
    {
        if (HasNpcs) NpcLabels();
        if (!HasPets) return;
        PetsHud();
        RemotePets.DrawLabels();
        SummonSync.Draw();
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void PetsHud() => BetterPetsAndSummons.Plugin.DrawHud();

    // ---- every frame: teammates' pets and summons ----

    public static void Tick()
    {
        if (!HasPets) return;
        RemotePets.Tick();
        SummonSync.Tick();
    }

    public static void ForgetTeammates()
    {
        if (!HasPets) return;
        RemotePets.ForgetAll();
        SummonSync.Clear();
    }

    // ---- our pet, for the player state ----

    public static byte PetKindByte => HasPets ? PetKind() : (byte)0;
    public static int PetLevel => HasPets ? PetLvl() : 1;
    public static string PetName => HasPets ? PetNm() : "";
    public static Color PetTint => HasPets ? PetColour() : Color.white;

    [MethodImpl(MethodImplOptions.NoInlining)] static byte PetKind() => BetterPetsAndSummons.Pet.KindByte;
    [MethodImpl(MethodImplOptions.NoInlining)] static int PetLvl() => BetterPetsAndSummons.Pet.Level;
    [MethodImpl(MethodImplOptions.NoInlining)] static string PetNm() => BetterPetsAndSummons.Pet.Out ? BetterPetsAndSummons.Pet.Name : "";
    [MethodImpl(MethodImplOptions.NoInlining)] static Color PetColour() => BetterPetsAndSummons.Pet.Tint;

    // ---- the staffs ----

    /// <summary>Holding the Staff of Clucking / of the Risen: a middle click is an order to the summons, not a ping.</summary>
    public static bool HoldingStaff => HasPets && Holding();
    [MethodImpl(MethodImplOptions.NoInlining)] static bool Holding() => BetterPetsAndSummons.Plugin.HoldingStaff;

    public static void Rally(Vector3 point) { if (HasPets) RallyTo(point); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void RallyTo(Vector3 p) => BetterPetsAndSummons.Plugin.Rally(p);

    /// <summary>A descent guardian's death: now and then, a staff among its loot.</summary>
    public static void MaybeDrop(Vector3 at, int floor) { if (HasPets) Drop(at, floor); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop(Vector3 at, int floor) => BetterPetsAndSummons.Plugin.MaybeDrop(at, floor);
}

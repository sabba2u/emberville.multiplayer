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
    public const string CompanionGuid = "spike.emberville.bettercompanions";

    public static bool HasCombat { get; private set; }
    public static bool HasLooting { get; private set; }
    public static bool HasPets { get; private set; }
    public static bool HasNpcs { get; private set; }
    public static bool HasWeather { get; private set; }
    public static bool HasCompanion { get; private set; }

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
        HasCompanion = Chainloader.PluginInfos.ContainsKey(CompanionGuid);
        if (HasCompanion) CompanionHooks();
        if (translations != null) TranslationsHooks();
        NetLog.Info("Split-out mods: " + string.Join(", ", new List<string> {
            HasCombat ? "Better Combat" : null, HasLooting ? "Better Looting" : null, HasPets ? "Better Pets and Summons" : null,
            HasNpcs ? "Better NPCs and Quests" : null, HasWeather ? "Better Weather" : null, HasCompanion ? "Better Companions" : null }.FindAll(x => x != null)) +
            (HasCombat || HasLooting || HasPets || HasNpcs || HasWeather || HasCompanion ? "" : "none"), toast: false);
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
        if (HasCompanion && CompanionCommand(cmd)) return true;
        if (chestsCommand != null && chestsCommand.Invoke(null, new object[] { cmd }) is bool ok && ok) return true;   // /stash (Better Chests)
        if (translationsCommand != null && translationsCommand.Invoke(null, new object[] { cmd }) is bool lang && lang) return true;   // /lang (Better Translations)
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static bool CombatCommand(string c) => BetterCombat.Plugin.Command(c);
    [MethodImpl(MethodImplOptions.NoInlining)] static Enemy CombatLocked() => BetterCombat.LockOn.Target ? BetterCombat.LockOn.Target.GetComponent<Enemy>() : null;

    /// <summary>Better Combat's lock-on: where to aim (the locked creature), if locked.</summary>
    public static bool LockedAim(out Vector3 at)
    {
        at = Vector3.zero;
        return HasCombat && CombatAim(out at);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool CombatAim(out Vector3 at) => BetterCombat.LockOn.AimPoint(out at);
    static readonly System.Reflection.MethodInfo chestsCommand =
        HarmonyLib.AccessTools.TypeByName("BetterChests.Plugin") is System.Type ct ? HarmonyLib.AccessTools.Method(ct, "Command", new[] { typeof(string) }) : null;
    // Better Translations: by name (no build dependency): its commands, its /help line, its replies in our chat
    static readonly System.Type translations = HarmonyLib.AccessTools.TypeByName("BetterTranslations.Plugin");
    static readonly System.Reflection.MethodInfo translationsCommand =
        translations != null ? HarmonyLib.AccessTools.Method(translations, "Command", new[] { typeof(string) }) : null;
    public static readonly string TranslationsHelp = translations != null ? HarmonyLib.AccessTools.Field(translations, "Help")?.GetValue(null) as string : null;
    static void TranslationsHooks()
    {
        var say = HarmonyLib.AccessTools.Field(HarmonyLib.AccessTools.TypeByName("BetterTranslations.Hooks"), "Say");
        say?.SetValue(null, (System.Action<string>)Chat.System);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool PetsCommand(string c) => BetterPetsAndSummons.Plugin.Command(c);
    [MethodImpl(MethodImplOptions.NoInlining)] static bool WeatherCommand(string c) => BetterWeather.Plugin.Command(c);
    [MethodImpl(MethodImplOptions.NoInlining)] static bool CompanionCommand(string c) => BetterCompanions.Plugin.Command(c);

    /// <summary>Better Companions: our chat, busy and typing checks, and which creatures are story NPCs.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CompanionHooks()
    {
        BetterCompanions.Hooks.Say = Chat.System;
        BetterCompanions.Hooks.Typing = () => ImGuiUnity.Typing || Chat.IsOpen;
        BetterCompanions.Hooks.Busy = () => PlayerStatusUtil.LocalBusy;
        BetterCompanions.Hooks.Ignore = EnemySync.LocalOnly;
        BetterCompanions.Hooks.RemoteRun = e => !EnemySync.Runs(e);   // (a creature someone else's game runs: theirs to point)
        BetterCompanions.Hooks.HasRally = true;   // (Pings hands every middle click to Rally)
        BetterCompanions.Hooks.DrawsHud = true;
        BetterCompanions.Hooks.Popup = (at, title, items) => GameUi.Popup(at, 6f, title, items);   // (its Class button's picker)
        BetterCompanions.Hooks.DownedNear = CompanionSync.DownedNear;   // (F at a teammate's downed companion: revive, not loot)   // (its name tag: DrawHud → CompanionDraw)
        BetterCompanions.Hooks.Hostile = Arena.IsHen;   // (the chicken level's chickens: foes, though fauna)
        if (HasCombat) BetterCompanions.Hooks.LockTarget = CombatLocked;
    }

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
        // the mods' own weapons, into your bag
        ImGuiNET.ImGui.SeparatorText("New Weapons");
        if (!HasPets) ImGuiNET.ImGui.BeginDisabled();
        if (ImGuiNET.ImGui.Button("Give me the Staff of Clucking")) GiveCluck();
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Give me the Staff of the Risen")) GiveRisen();
        if (!HasPets) ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Give me the Hunter's Hook")) HunterHook.Give();
        if (!HasPets) ImGuiNET.ImGui.TextDisabled("(the staffs come with Better Pets and Summons)");
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void GiveCluck() => BetterPetsAndSummons.Plugin.GiveCluckStaff();
    [MethodImpl(MethodImplOptions.NoInlining)] static void GiveRisen() => BetterPetsAndSummons.Plugin.GiveRisenStaff();

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

    static float slowLogAt;

    public static void DrawCompanion()
    {
        if (!HasCompanion) { Missing("Better Companions (a companion: one of your characters, fighting beside you)"); return; }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        CompanionMenu();
        double menuMs = watch.Elapsed.TotalMilliseconds;
        ImGuiNET.ImGui.SeparatorText("Respec");
        var t = CompanionRespec();
        if (t == null) ImGuiNET.ImGui.TextDisabled("Call your companion to change its levels here.");
        else Respec.Draw(t);
        double allMs = watch.Elapsed.TotalMilliseconds;
        if (allMs > 4 && Time.unscaledTime > slowLogAt)
        {
            slowLogAt = Time.unscaledTime + 2f;
            NetLog.Info($"Companions tab: {allMs:0.0} ms a frame (its menu {menuMs:0.0}, respec {allMs - menuMs:0.0})", toast: false);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionMenu() => BetterCompanions.Plugin.DrawMenu();

    /// <summary>Its levels by hand (Respec), for Insert > Companions: null when it has no real body out.</summary>
    public static Respec.Target CompanionRespec() => HasCompanion ? RespecOf() : null;
    [MethodImpl(MethodImplOptions.NoInlining)] static Respec.Target RespecOf()
    {
        var p = BetterCompanions.Plugin.CompanionPlayer;
        var data = BetterCompanions.Plugin.CompanionData;
        if (!p || data == null) return null;
        return new Respec.Target
        {
            Id = "companion", Who = data.playerName, P = p, Data = data,
            Save = BetterCompanions.Plugin.SaveCompanion,
            SkillsChanged = BetterCompanions.Plugin.CompanionSkillsChanged,
            KillXp = BetterCompanions.Plugin.CompanionKillXp,
        };
    }

    // ---- the HUD (ImGui) ----

    public static void DrawHud()
    {
        if (HasNpcs) NpcLabels();
        if (HasCompanion) { CompanionDraw(); CompanionSync.Draw(); }
        if (!HasPets) return;
        PetsHud();
        RemotePets.DrawLabels();
        SummonSync.Draw();
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionDraw() => BetterCompanions.Plugin.DrawHud();
    [MethodImpl(MethodImplOptions.NoInlining)] static void PetsHud() => BetterPetsAndSummons.Plugin.DrawHud();

    // ---- every frame: teammates' pets and summons ----

    public static void Tick()
    {
        if (HasCompanion) CompanionSync.Tick();
        if (!HasPets) return;
        RemotePets.Tick();
        SummonSync.Tick();
    }

    public static void ForgetTeammates()
    {
        if (HasCompanion) CompanionSync.Clear();
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

    public static void Rally(Vector3 point, bool companion = true) { if (HasPets) RallyTo(point); if (companion && HasCompanion) CompanionSend(point); }

    /// <summary>A companion is out: our middle clicks are orders to it (the red mark), our lines its path.</summary>
    public static bool CompanionOut => HasCompanion && IsCompanionOut();
    [MethodImpl(MethodImplOptions.NoInlining)] static bool IsCompanionOut() => BetterCompanions.Plugin.CompanionOut;

    public static void RallyPath(List<Vector3> points, bool stay = false) { if (HasCompanion) CompanionPath(points, stay); }
    /// <summary>An order to your companion alone (Shift: it holds the spot).</summary>
    public static void CompanionTo(Vector3 point, bool stay) { if (HasCompanion) CompanionSendStay(point, stay); }
    /// <summary>Your companion, holding a spot: back to following you.</summary>
    public static void CompanionFollow() { if (HasCompanion) CompanionBack(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionSendStay(Vector3 p, bool stay) => BetterCompanions.Plugin.Send(p, stay);
    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionBack() => BetterCompanions.Plugin.Follow();

    /// <summary>Your companion, for its card (TeamHud): its body, name, level, health 0..1, downed (and the revive so far), holding a spot.</summary>
    public static bool CompanionCard(out GameObject body, out string name, out int level, out float health01, out bool down, out float revive01, out bool holding)
    {
        body = null; name = ""; level = 0; health01 = 0f; down = false; revive01 = 0f; holding = false;
        return HasCompanion && CompanionCardOf(out body, out name, out level, out health01, out down, out revive01, out holding);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CompanionCardOf(out GameObject body, out string name, out int level, out float health01, out bool down, out float revive01, out bool holding)
    {
        body = BetterCompanions.Plugin.CompanionRoot;
        name = BetterCompanions.Plugin.CompanionName;
        level = BetterCompanions.Plugin.CompanionLevel;
        health01 = BetterCompanions.Plugin.CompanionHealth;
        down = BetterCompanions.Plugin.CompanionDown;
        revive01 = BetterCompanions.Plugin.CompanionRevive;
        holding = BetterCompanions.Plugin.Post != null;
        return body;
    }

    /// <summary>Your companion's card menu: the Heal button (words, pressable), healing it with one of your potions, trading.</summary>
    public static (string label, bool can) CompanionHealOption() => HasCompanion ? HealOptionOf() : ("Heal", false);
    public static string CompanionHeal() => HasCompanion ? HealIt() : "";
    public static void CompanionTrade() { if (HasCompanion) TradeIt(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static (string, bool) HealOptionOf() => BetterCompanions.Plugin.HealOption();
    [MethodImpl(MethodImplOptions.NoInlining)] static string HealIt() => BetterCompanions.Plugin.Heal();
    [MethodImpl(MethodImplOptions.NoInlining)] static void TradeIt() => BetterCompanions.Plugin.Trade();

    /// <summary>Your companion's Kymia, 0..1 (false: it has none, or none out).</summary>
    /// <summary>Your companion's power slot i (0-3): its skill's icon (none: empty), cooldown left / total, running, when cast.</summary>
    public static bool CompanionPower(int i, out Sprite icon, out float left, out float total, out bool running, out float castAt)
    {
        icon = null; left = 0f; total = 1f; running = false; castAt = -100f;
        return HasCompanion && PowerOf(i, out icon, out left, out total, out running, out castAt);
    }
    /// <summary>Its card's power slot clicked: that power now. Empty: done; else why not.</summary>
    public static string CompanionUsePower(int i) => HasCompanion ? UsePower(i) : "No companion.";
    [MethodImpl(MethodImplOptions.NoInlining)] static string UsePower(int i) => BetterCompanions.Plugin.UseCompanionPower(i);

    /// <summary>The powers your companion could slot: icon, name, rank, ID.</summary>
    public static List<(Sprite icon, string name, int rank, string id)> CompanionPowerChoices() =>
        HasCompanion ? PowerChoices() : new List<(Sprite, string, int, string)>();
    [MethodImpl(MethodImplOptions.NoInlining)] static List<(Sprite icon, string name, int rank, string id)> PowerChoices() => BetterCompanions.Plugin.CompanionPowerChoices();

    public static void CompanionSetPower(int i, string id) { if (HasCompanion) SetPower(i, id); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetPower(int i, string id) => BetterCompanions.Plugin.SetCompanionPower(i, id);

    [MethodImpl(MethodImplOptions.NoInlining)] static bool PowerOf(int i, out Sprite icon, out float left, out float total, out bool running, out float castAt) =>
        BetterCompanions.Plugin.CompanionPower(i, out icon, out left, out total, out running, out castAt);

    public static bool CompanionKymia(out float kymia01) { kymia01 = 0f; return HasCompanion && KymiaOf(out kymia01); }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool KymiaOf(out float k) { k = BetterCompanions.Plugin.CompanionKymia; return k >= 0f; }

    /// <summary>Where your companion stands, when one is out.</summary>
    public static bool CompanionAt(out Vector3 at) { at = default; return HasCompanion && CompanionPosition(out at); }
    [MethodImpl(MethodImplOptions.NoInlining)] static bool CompanionPosition(out Vector3 at)
    {
        var root = BetterCompanions.Plugin.CompanionRoot;
        at = root ? root.transform.position : default;
        return root;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionPath(List<Vector3> p, bool stay) => BetterCompanions.Plugin.SendPath(p, stay);
    [MethodImpl(MethodImplOptions.NoInlining)] static void CompanionSend(Vector3 p) => BetterCompanions.Plugin.Send(p);
    [MethodImpl(MethodImplOptions.NoInlining)] static void RallyTo(Vector3 p) => BetterPetsAndSummons.Plugin.Rally(p);

    /// <summary>A descent guardian's death: now and then, a staff among its loot.</summary>
    public static void MaybeDrop(Vector3 at, int floor) { if (HasPets) Drop(at, floor); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop(Vector3 at, int floor) => BetterPetsAndSummons.Plugin.MaybeDrop(at, floor);
}

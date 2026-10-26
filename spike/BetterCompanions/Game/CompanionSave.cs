using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterCompanions.Core;
using UnityEngine;

namespace BetterCompanions;

/// <summary>A few things about the companion the game's PlayerData has no place for (kept next to its save).</summary>
[Serializable]
internal class CompanionMeta
{
    public int sourceSlot;        // the game slot it was copied from (0: a new adventurer)
    public bool dismissed;        // sent away with /companion dismiss
    public string favMelee = "", favRanged = "";   // its two favourite weapons (item keys)
    public bool real;             // a real second player (RealBrain), not the puppet body
}

/// <summary>
/// The companion's save: a copy of one of your other characters (the game's own PlayerData), kept in its own file
/// next to the game's saves, one per character you play: Saves/companion_slotN.evc (N = the slot you're playing).
/// Not .evp: the game lists every .evp in that folder as a character. The game's slot files are only ever read.
/// Every write first copies the old file to .bak.
/// </summary>
internal static class CompanionSave
{
    public const string Extension = ".evc";
    public static PlayerData Data;
    public static CompanionMeta Meta = new();
    public static int PlayingSlot;
    public static bool Has => Data != null;
    public static bool Dirty;   // changed in play (a weapon swap): written soon, not every time
    public static event Action Changed;

    static string File(int playing) => Rules.CompanionFile(playing) + Extension;
    static string MetaFile(int playing) => Rules.CompanionFile(playing) + ".json";

    /// <summary>The game just loaded a character: load its companion, if it has one.</summary>
    public static bool Load()
    {
        int slot = Rules.SlotFromPrefix(GameData.playerFilePrefix) ?? 0;
        if (slot != 0 && slot == PlayingSlot && Data != null)
        {
            // a change of scene: the same character, so keep what's in memory (XP, swaps) and make sure it's on disk
            if (Dirty) { Dirty = false; Write(); }
            return true;
        }
        if (Dirty) { Dirty = false; Write(); }   // (the last character's companion, before switching)
        Data = null;
        Meta = new CompanionMeta();
        PlayingSlot = slot;
        if (PlayingSlot == 0) { Log.Info($"Companion: no companion here (playing '{GameData.playerFilePrefix}')"); return false; }
        try
        {
            string json = SaveLoad.Load(File(PlayingSlot));
            if (json == null) return false;
            Data = JsonUtility.FromJson<PlayerData>(json);
            string meta = SaveLoad.Load(MetaFile(PlayingSlot), obfuscate: false);
            if (!string.IsNullOrEmpty(meta)) Meta = JsonUtility.FromJson<CompanionMeta>(meta) ?? new CompanionMeta();
            Log.Info($"Companion: loaded {Describe()}");
        }
        catch (Exception e) { Data = null; Log.Warn($"Companion: couldn't read {File(PlayingSlot)}: {e.Message}"); }
        Changed?.Invoke();
        return Data != null;
    }

    /// <summary>Save the companion (its PlayerData and its notes), keeping the previous file as .bak.</summary>
    public static void Write()
    {
        if (Data == null || PlayingSlot == 0) return;
        try
        {
            Backup(File(PlayingSlot));
            SaveLoad.Save(File(PlayingSlot), JsonUtility.ToJson(Data));
            SaveLoad.Save(MetaFile(PlayingSlot), JsonUtility.ToJson(Meta, prettyPrint: true), obfuscate: false);
        }
        catch (Exception e) { Log.Warn($"Companion: couldn't save: {e.Message}", toast: true); }
    }

    /// <summary>The file as it is now to .bak, but only if it's a good one (never a damaged file over the last good copy).</summary>
    static void Backup(string name)
    {
        string path = SaveLoad.GetPath(name);
        if (!System.IO.File.Exists(path)) return;
        try { if (JsonUtility.FromJson<PlayerData>(SaveLoad.Load(name)) == null) return; }
        catch { return; }
        System.IO.File.Copy(path, path + ".bak", overwrite: true);
    }

    /// <summary>Before a companion is replaced (/companion N, /companion new): it goes to your other companions (the roster).</summary>
    static void KeepReplaced() => CompanionRoster.Keep();

    /// <summary>Its file changed under it (a swap from the roster): read again.</summary>
    public static void Reread()
    {
        Dirty = false;
        Data = null;
        Load();
    }

    /// <summary>The save's items from the companion's loaded items (what it wears and its bag).</summary>
    public static void FromItems(System.Collections.Generic.List<InventoryItem> items)
    {
        if (Data == null || items == null) return;
        Data.playerInventory = items.Select(i => i.ToSavedItem()).ToList();
    }

    /// <summary>/companion N: copy game slot N (read only) into the companion; an empty slot makes a new one.</summary>
    public static string CloneFrom(int slot)
    {
        if (PlayingSlot == 0) return "Companion: load a character first.";
        string slotFile = GameData.GetPlayerFileName(slot) + ".evp";
        bool exists = System.IO.File.Exists(SaveLoad.GetPath(slotFile));
        string why = Rules.CanClone(slot, PlayingSlot, exists);
        if (why != null) return "Companion: " + why + ".";
        if (!exists) return StartNew(slot);
        PlayerData copy;
        try { copy = JsonUtility.FromJson<PlayerData>(SaveLoad.Load(slotFile)); }
        catch (Exception e) { return $"Companion: save slot {slot} can't be read ({e.Message}); your companion is unchanged."; }
        if (copy == null) return $"Companion: save slot {slot} is empty or damaged; your companion is unchanged.";
        KeepReplaced();
        Data = copy;
        Meta = new CompanionMeta { sourceSlot = slot, real = true };   // (the real body: its class, powers, the game's own moves)
        Write();
        Changed?.Invoke();
        Log.Info($"Companion: copied slot {slot} ({Data.playerName}, level {Level})");
        return $"Companion: {Data.playerName} (level {Level}) joins you, copied from slot {slot}.";
    }

    /// <summary>A brand-new adventurer: the game's default character, bare-handed, level 1.</summary>
    public static string StartNew(int fromEmptySlot = 0)
    {
        if (PlayingSlot == 0) return "Companion: load a character first.";
        KeepReplaced();
        Data = new PlayerData { playerName = "Adventurer" };
        Meta = new CompanionMeta { sourceSlot = fromEmptySlot, real = true };
        Write();
        Changed?.Invoke();
        Log.Info("Companion: a new adventurer");
        return fromEmptySlot > 0 ? $"Companion: slot {fromEmptySlot} is empty, so a new adventurer joins you." : "Companion: a new adventurer joins you.";
    }

    /// <summary>The ready-made companions: a name, a class (learned and on), and a weapon of its kind in hand.</summary>
    public static readonly (string name, PlayerClassType cls, WeaponCategory weapon, string what)[] Presets =
    {
        ("Sabba", PlayerClassType.Knight, WeaponCategory.OneHandedSwords, "a knight"),
        ("Ellie", PlayerClassType.Mage, WeaponCategory.MagicStaffs, "a mage"),
        ("Rory", PlayerClassType.Archer, WeaponCategory.Bows, "an archer"),
        ("GraveBear", PlayerClassType.Wanderer, WeaponCategory.TwoHandedSwords, "a wanderer"),
    };

    /// <summary>
    /// How each ready-made one looks and dresses (playerSetup, as the customization screen saves it: hair, facial hair,
    /// face extras, skin, hair colour, facial hair colour, clothes colour, body type 0 = man / 1 = woman), and the weight of
    /// its clothes. Ellie and Rory as the NPCs of that name are (each a copy of your character): Ellie with hairdo 2 and a
    /// woman's body, in light clothes; Rory with the hairdo three along from yours, in light leathers. Sabba: a blond man
    /// in heavy armour. GraveBear: a dark-haired man, a wanderer in medium armour and a cloak.
    /// </summary>
    static List<int> PresetLooks(int i)
    {
        var mine = GameData.playerData?.playerSetup;
        var you = mine != null && mine.Count >= 8 ? new List<int>(mine) : new List<int> { 0, 0, 0, 0, 0, 0, 0, 0 };
        switch (i)
        {
            case 0: return new List<int> { 0, 0, 0, 0, 1, 1, 5, 0 };   // Sabba: blond (hair colour 1), blue clothes
            case 1: you[0] = 2; you[1] = 0; you[7] = 1; return you;    // Ellie: hairdo 2, no beard, a woman
            case 2: you[0] += 3; you[7] = 0; return you;               // Rory: three hairdos along from yours
            default: return new List<int> { 5, 0, 0, 2, 8, 8, 14, 0 };  // GraveBear: dark hair (hair colour 8), black clothes
        }
    }

    static readonly GearWeight[] PresetWeight = { GearWeight.Heavy, GearWeight.Light, GearWeight.Light, GearWeight.Medium };
    static readonly GearSlot[][] PresetSlots =
    {
        new[] { GearSlot.UpperBody, GearSlot.LowerBody },                  // (no helmet: his hair shows)
        new[] { GearSlot.UpperBody, GearSlot.LowerBody, GearSlot.Cloak },
        new[] { GearSlot.UpperBody, GearSlot.LowerBody },
        new[] { GearSlot.UpperBody, GearSlot.LowerBody, GearSlot.Cloak },
    };

    /// <summary>The items to hand: the game's catalogue once loaded, else what's in memory.</summary>
    static List<ItemData> ItemsToHand()
    {
        IEnumerable<ItemData> all = Hooks.Catalogue?.Invoke();
        if (all == null || !all.Any()) all = Resources.FindObjectsOfTypeAll<ItemData>();
        return all.Where(d => d && !string.IsNullOrEmpty(d.addressKey)).GroupBy(d => d.addressKey).Select(g => g.First()).OrderBy(d => d.name).ToList();
    }

    static void Wear(ItemData d) =>
        Data.playerInventory.Add(new SavedItem { Key = d.addressKey, Amount = 1, Section = InventorySection.Gear, SlotIndex = -1, Values = new ItemRandomValues(d, 0) });

    /// <summary>A ready-made companion (level 1, as a new one, with its class, its looks, clothes and a weapon). The one
    /// out goes to the roster.</summary>
    public static string StartPreset(int i)
    {
        if (PlayingSlot == 0) return "Companion: load a character first.";
        var (name, cls, cat, what) = Presets[i];
        KeepReplaced();
        Data = new PlayerData { playerName = name, equippedClass = cls, playerSetup = PresetLooks(i) };
        string id = cls.GetEmbervilleID();
        Data.KnownClasses.Add(id);
        Data.LearnedClasses.Add(id);
        var items = ItemsToHand();
        // a weapon of its kind, worn: the plainest there is (none to hand: bare-handed for now)
        var weapon = items.OfType<CollectableWeapon>().Where(w => w.weapon && w.weapon.weaponCategory == cat)
            .OrderBy(w => w.maxDamageRange.y).ThenBy(w => w.name).FirstOrDefault();
        if (weapon) Wear(weapon);
        // clothes: the first of its weight for each slot (as the NPCs pick theirs), else any for that slot
        var armour = items.OfType<CollectableArmor>().Where(a => a.gear != null && a.gear.layerIndex >= 0).ToList();
        int dressed = 0;
        foreach (var slot in PresetSlots[i])
        {
            var piece = armour.FirstOrDefault(a => a.Weight == PresetWeight[i] && (a.gear.gearSlot & slot) != 0) ?? armour.FirstOrDefault(a => (a.gear.gearSlot & slot) != 0);
            if (piece) { Wear(piece); dressed++; }
        }
        Meta = new CompanionMeta { real = true };
        Write();
        Changed?.Invoke();
        Log.Info($"Companion: {name}, {what} ({cls}, {(weapon ? weapon.name : "no weapon to hand")}, {dressed} pieces of clothing)");
        return $"Companion: {name}, {what}, joins you" + (weapon ? "." : $" (bare-handed: no {cat} to hand yet; give one through Trade).")
            + (dressed == 0 ? " (No clothes to hand yet: the item list is still loading. Try again in a moment.)" : "");
    }

    /// <summary>Its character level: its base class's XP (as the character screen shows it), not its combat class's.</summary>
    public static int Level
    {
        get
        {
            if (Data == null) return 0;
            var body = RealPlayer.Current ? RealPlayer.Current.characterStats : null;
            var you = Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player ? Singleton<GameManager>.main.player.characterStats : null;
            var baseClass = (body ? body.baseClass : null) ?? (you ? you.baseClass : null);   // (everyone's base class is the same one)
            object key = baseClass ? baseClass.playerClassType : Data.equippedClass;
            try { return Math.Max(1, Data.GetCurrentLevel(key)); } catch { return 1; }
        }
    }

    public static string Describe() => Data == null ? "none" :
        $"{Data.playerName}, level {Level}, " + (Meta.sourceSlot > 0 ? $"copied from slot {Meta.sourceSlot}" :
            Data.equippedClass != PlayerClassType.None ? $"{Data.equippedClass}" : "a new adventurer");
}

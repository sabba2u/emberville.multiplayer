using System;
using System.IO;
using System.Linq;
using BetterCompanion.Core;
using UnityEngine;

namespace BetterCompanion;

/// <summary>A few things about the companion the game's PlayerData has no place for (kept next to its save).</summary>
[Serializable]
internal class CompanionMeta
{
    public int sourceSlot;        // the game slot it was copied from (0: a new adventurer)
    public bool dismissed;        // sent away with /companion dismiss
    public string favMelee = "", favRanged = "";   // its two favourite weapons (item keys)
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

    /// <summary>Before a companion is replaced (/companion N, /companion new): a dated copy that later saves don't touch.</summary>
    static void KeepReplaced()
    {
        if (PlayingSlot == 0) return;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var name in new[] { File(PlayingSlot), MetaFile(PlayingSlot) })
        {
            string path = SaveLoad.GetPath(name);
            if (System.IO.File.Exists(path)) System.IO.File.Copy(path, $"{path}.replaced-{stamp}.bak", overwrite: true);
        }
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
        Meta = new CompanionMeta { sourceSlot = slot };
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
        Meta = new CompanionMeta { sourceSlot = fromEmptySlot };
        Write();
        Changed?.Invoke();
        Log.Info("Companion: a new adventurer");
        return fromEmptySlot > 0 ? $"Companion: slot {fromEmptySlot} is empty, so a new adventurer joins you." : "Companion: a new adventurer joins you.";
    }

    public static int Level => Data == null ? 0 : Math.Max(1, Data.GetCurrentLevel(Data.equippedClass));

    public static string Describe() => Data == null ? "none" :
        $"{Data.playerName}, level {Level}, " + (Meta.sourceSlot > 0 ? $"copied from slot {Meta.sourceSlot}" : "a new adventurer");
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// Testing commands for weapons and masteries:
/// /weapons: every weapon in the game's item catalogue, by type (and the types with none).
/// /give name [count]: any item whose name or ID has that in it, into your bag (what doesn't fit lands at your feet).
/// /unlockmasteries: every weapon type discovered, so its mastery shows (the game does it when you first equip one).
/// The catalogue (every ItemData in the game's Addressables) is loaded once, the first time it's needed.
/// </summary>
internal static class Arsenal
{
    static List<ItemData> items;
    static bool loading;
    static int pending;
    static float startedAt;
    static readonly List<Action> waiting = new List<Action>();

    public static void Weapons() => WithItems(() =>
    {
        var weapons = items.OfType<CollectableWeapon>().Where(w => w.weapon).ToList();
        foreach (WeaponCategory cat in Enum.GetValues(typeof(WeaponCategory)))
        {
            if (cat == WeaponCategory.None) continue;
            var these = weapons.Where(w => w.weapon.weaponCategory == cat).Select(NameOf).Distinct().OrderBy(n => n).ToList();
            if (these.Count == 0) Chat.System($"{cat}: (none in the game)", new ImGuiNET.Vector4(1f, 0.4f, 0.35f, 1f));   // (red: a line is one colour)
            else Chat.System($"{cat}: " + string.Join(", ", these));
        }
        Chat.System($"{weapons.Count} weapons. /give name: one into your bag.");
    });

    public static void Give(string args) => WithItems(() =>
    {
        args = (args ?? "").Trim();
        int count = 1;
        int space = args.LastIndexOf(' ');
        if (space > 0 && int.TryParse(args.Substring(space + 1), out int n)) { count = Mathf.Clamp(n, 1, 999); args = args.Substring(0, space).Trim(); }
        if (args.Length == 0) { Chat.System("/give name [count]: e.g. /give rustic bow, /give axe 2 (/weapons lists the weapons)"); return; }
        string q = args.ToLowerInvariant();
        var found = items.Where(i => NameOf(i).ToLowerInvariant().Contains(q) || (i.addressKey ?? i.name).ToLowerInvariant().Contains(q)).ToList();
        if (found.Count == 0) { Chat.System($"No item called \"{args}\"."); return; }
        var exact = found.Where(i => NameOf(i).ToLowerInvariant() == q || (i.addressKey ?? "").ToLowerInvariant() == q).ToList();
        if (exact.Count > 0) found = exact;
        if (found.Select(NameOf).Distinct().Count() > 1)
        {
            Chat.System($"\"{args}\" could be: " + string.Join(", ", found.Select(NameOf).Distinct().Take(12)) + (found.Count > 12 ? ", ..." : "") + ". Be more exact.");
            return;
        }
        var data = found[0];
        var me = LocalPlayer.Get();
        if (!me || !Singleton<Inventory>.HasMain) { Chat.System("Load into a world first."); return; }
        int given = 0, dropped = 0;
        for (int k = 0; k < count; k++)   // (one at a time: weapons and gear don't stack)
        {
            var item = new ItemObject(data, data.IsStackable ? count : 1);
            int rest = Singleton<Inventory>.main.AddItem(item);
            if (rest > 0) { item.DropToWorld(me.transform.position, rest, 1f); dropped += rest; }
            given += data.IsStackable ? count : 1;
            if (data.IsStackable) break;
        }
        Chat.System($"Gave {given} {NameOf(data)}" + (dropped > 0 ? $" ({dropped} at your feet: your bag is full)." : "."));
    });

    /// <summary>Only the weapon types the game has finished: a weapon in the catalogue with its attacks. (The mastery screen
    /// breaks on one without: Throwing Weapons in the demo.) A broken one discovered earlier is taken back off.</summary>
    public static void UnlockMasteries() => WithItems(() =>
    {
        if (!GameData.HasPlayerData) { Chat.System("Load into a world first."); return; }
        var found = GameData.playerData.DiscoveredWeapons;
        var ready = new HashSet<WeaponCategory>(items.OfType<CollectableWeapon>()
            .Where(w => w.weapon && w.weapon.primaryAttack != null && w.weapon.primaryAttack.Count > 0 && w.weapon.secondaryAttack != null && w.weapon.secondaryAttack.Count > 0)
            .Select(w => w.weapon.weaponCategory));
        var added = new List<string>();
        var removed = new List<string>();
        foreach (WeaponCategory cat in Enum.GetValues(typeof(WeaponCategory)))
        {
            if (cat == WeaponCategory.None) continue;
            if (ready.Contains(cat)) { if (!found.Contains(cat)) { found.Add(cat); added.Add(cat.ToString()); } }
            else if (found.Contains(cat)) { found.Remove(cat); removed.Add(cat.ToString()); }
        }
        Chat.System(added.Count > 0 ? $"Masteries unlocked: {string.Join(", ", added)}." : "Every finished weapon mastery is already unlocked.");
        if (removed.Count > 0) Chat.System($"Taken back off (not finished in the game, its screen breaks): {string.Join(", ", removed)}.");
        var missing = Enum.GetValues(typeof(WeaponCategory)).Cast<WeaponCategory>().Where(c => c != WeaponCategory.None && !ready.Contains(c)).ToList();
        if (missing.Count > 0) Chat.System($"No finished weapons yet: {string.Join(", ", missing)}.");
    });

    /// <summary>/lockmasteries: back off every weapon type you've no mastery XP in (what /unlockmasteries added, never used).
    /// /lockmasteries all: every one but the weapon in your hands (the game adds that back the moment you equip it anyway).
    /// The XP itself stays: one you lock and later pick up again comes back at the level it was.</summary>
    public static void LockMasteries(string args)
    {
        if (!GameData.HasPlayerData) { Chat.System("Load into a world first."); return; }
        bool all = (args ?? "").Trim() == "all";
        var me = LocalPlayer.Get();
        var held = me && me.characterStats && me.characterStats.activeWeapon ? me.characterStats.activeWeapon.weaponCategory : WeaponCategory.None;
        var found = GameData.playerData.DiscoveredWeapons;
        var removed = new List<string>();
        foreach (var cat in found.ToList())
        {
            if (cat == held) continue;
            int xp = 0;
            try { xp = GameData.playerData.GetPlayerExperience(cat).TotalXP; } catch { }
            if (!all && xp > 0) continue;
            found.Remove(cat);
            removed.Add(cat.ToString());
        }
        Chat.System(removed.Count > 0 ? $"Masteries locked: {string.Join(", ", removed)}." :
            all ? "Only the weapon in your hands is unlocked." : "Every unlocked mastery has XP in it (/lockmasteries all locks those too).");
    }

    static string NameOf(ItemData d)
    {
        try { var s = d.Name == null || d.Name.IsEmpty ? null : d.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(s)) return s; } catch { }
        return d.addressKey ?? d.name;
    }

    // ---- the catalogue ----

    static void WithItems(Action then)
    {
        if (items != null) { then(); return; }
        waiting.Add(then);
        if (loading) return;
        var keys = new HashSet<string>();
        foreach (var locator in Addressables.ResourceLocators)
            foreach (var key in locator.Keys)
                if (key is string s && locator.Locate(s, typeof(ItemData), out var locs))
                    foreach (var l in locs) keys.Add(l.PrimaryKey);
        if (keys.Count == 0) { waiting.Clear(); Chat.System("Couldn't find the game's item list."); return; }
        loading = true;
        startedAt = Time.unscaledTime;
        pending = keys.Count;
        var got = new List<ItemData>();
        Chat.System($"Looking through the game's {keys.Count} items...");
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result) got.Add(h.Result);
                pending--;
            };
        loaded = got;
    }

    static List<ItemData> loaded;

    /// <summary>Every frame: once the catalogue's in (or a load hangs), what was asked for.</summary>
    public static void Tick()
    {
        if (!loading) return;
        if (pending > 0 && Time.unscaledTime - startedAt < 30f) return;
        loading = false;
        items = loaded.Distinct().ToList();
        NetLog.Info($"Arsenal: {items.Count} items, {items.OfType<CollectableWeapon>().Count()} weapons");
        var run = waiting.ToList();
        waiting.Clear();
        foreach (var a in run) { try { a(); } catch (Exception e) { NetLog.Warn($"Arsenal: {e.Message}"); } }
    }
}

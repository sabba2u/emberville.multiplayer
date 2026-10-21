using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BetterNPCsAndQuests.Core;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BetterNPCsAndQuests;

/// <summary>
/// The game's items for us: the catalogue (every ItemData), items by name or kind, the bag (main section only: what you
/// wear doesn't count), the has_... flags, and rewards.
/// </summary>
internal static class Items
{
    static List<ItemData> pool;
    static Dictionary<string, ItemData> byName;
    static bool loading;
    /// <summary>While we take or give items ourselves (not a pickup).</summary>
    internal static bool Ours;

    public static bool Ready => byName != null;
    public static IReadOnlyList<ItemData> Pool => pool;

    public static void Preload()
    {
        if (pool != null || loading) return;
        loading = true;
        var keys = new HashSet<string>();
        foreach (var locator in Addressables.ResourceLocators)
            foreach (var key in locator.Keys)
                if (key is string s && locator.Locate(s, typeof(ItemData), out var locs))
                    foreach (var l in locs) keys.Add(l.PrimaryKey);
        var found = new List<ItemData>();
        int pending = keys.Count;
        if (pending == 0) { loading = false; return; }   // the catalogue isn't up yet: asked again later
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result) found.Add(h.Result);
                if (--pending == 0) { pool = found.Distinct().OrderBy(d => d.addressKey).ToList(); loading = false; Index(); }
            };
    }

    static void Index()
    {
        var map = new Dictionary<string, ItemData>();
        foreach (var d in pool)
        {
            if (!string.IsNullOrEmpty(d.addressKey)) map[Norm(d.addressKey)] = d;
            if (!string.IsNullOrEmpty(d.name) && !map.ContainsKey(Norm(d.name))) map[Norm(d.name)] = d;
            try { string shown = d.Name?.GetLocalizedString(); if (!string.IsNullOrEmpty(shown) && !map.ContainsKey(Norm(shown))) map[Norm(shown)] = d; }
            catch { }
        }
        found.Clear();
        byName = map;
        Check();
    }

    /// <summary>Item names in the quest files that match nothing (once the catalogue's here).</summary>
    internal static void Check()
    {
        foreach (var q in State.Lib.Quests)
        {
            var names = q.Steps.SelectMany(st => st.Objectives).Where(o => o.Kind == "collect" && o.Item.Name != null).Select(o => o.Item.Name)
                .Concat(q.Rewards.Where(r => r.Kind == "item").Select(r => r.Item.Name));
            foreach (var n in names.Distinct())
            {
                var d = Find(n);
                if (d) Log.Info($"NPCs and quests: \"{n}\" in {q.File} is {d.name}");
                else { string msg = $"{q.File}: no item called \"{n}\""; Log.Warn("NPCs and quests: " + msg); State.Book.Problems.Add(msg); }
            }
        }
    }

    static string Norm(string s) => Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[\s_]+", "_");

    static readonly Dictionary<string, ItemData> found = new();

    /// <summary>By internal name, asset name or shown name; else the item whose name has all these words ("softwood" →
    /// Item_Log_Softwood), materials first, then the shortest name.</summary>
    public static ItemData Find(string name)
    {
        if (byName == null) return null;
        string key = Norm(name);
        if (byName.TryGetValue(key, out var d)) return d;
        if (found.TryGetValue(key, out d)) return d;
        var words = key.Split('_');
        d = byName.Where(kv => words.All(w => kv.Key.Split('_').Contains(w)))
            .OrderByDescending(kv => (kv.Value.itemCategory & ItemCategory.Material) != 0).ThenBy(kv => kv.Key.Length)
            .Select(kv => kv.Value).FirstOrDefault();
        found[key] = d;
        return d;
    }

    public static bool Matches(ItemData d, ItemSpec spec)
    {
        if (!d) return false;
        if (spec.Name != null)
        {
            // by name, not by object: the bag's item and the catalogue's can be two copies of the same asset
            var want = Find(spec.Name);
            if (!want) return false;
            if (!string.IsNullOrEmpty(want.addressKey) && want.addressKey == d.addressKey) return true;
            return want.name == d.name;
        }
        var weapon = d is CollectableWeapon cw && cw.weapon ? cw.weapon.weaponCategory : WeaponCategory.None;
        return spec.Category switch
        {
            "sword" => weapon == WeaponCategory.OneHandedSwords || weapon == WeaponCategory.TwoHandedSwords,
            "axe" => weapon == WeaponCategory.OneHandedAxes || weapon == WeaponCategory.TwoHandedAxes,
            "mace" => weapon == WeaponCategory.OneHandedMaces || weapon == WeaponCategory.TwoHandedMaces,
            "dagger" => weapon == WeaponCategory.Daggers,
            "bow" => weapon == WeaponCategory.Bows,
            "crossbow" => weapon == WeaponCategory.Crossbows,
            "staff" => weapon == WeaponCategory.MagicStaffs,
            "wand" => weapon == WeaponCategory.MagicWands,
            "weapon" => (d.itemCategory & ItemCategory.Weapon) != 0,
            "armor" => (d.itemCategory & ItemCategory.Armor) != 0,
            "material" => (d.itemCategory & ItemCategory.Material) != 0,
            "consumable" => (d.itemCategory & ItemCategory.Consumable) != 0,
            "gear" => (d.itemCategory & ItemCategory.Gear) != 0,
            _ => false,
        };
    }

    /// <summary>The local player's main bag.</summary>
    internal sealed class Bag : IBag
    {
        /// <summary>The main bag, without favourites (Better Chests' heart: never handed over), the poorest first (so
        /// "any sword" takes your worst sword, not your best).</summary>
        static IEnumerable<InventoryItem> Main() =>
            Singleton<Inventory>.HasMain
                ? Singleton<Inventory>.main.allItems.Where(i => i != null && i.section == InventorySection.Main && i.itemObject != null && !Favourite(i.itemObject))
                    .OrderBy(i => (int)(i.itemObject.randomValues?.quality ?? ItemQuality.None)).ToList()
                : Enumerable.Empty<InventoryItem>();

        public int Count(ItemSpec spec) => Main().Where(i => Matches(i.itemObject.data, spec)).Sum(i => Math.Max(1, i.itemObject.amount));

        public bool Take(ItemSpec spec, int n)
        {
            if (Count(spec) < n) return false;
            Ours = true;
            try
            {
                foreach (var i in Main().Where(i => Matches(i.itemObject.data, spec)))
                {
                    if (n <= 0) break;
                    int have = Math.Max(1, i.itemObject.amount), use = Math.Min(have, n);
                    n -= use - Singleton<Inventory>.main.ConsumeItem(i.itemObject, use);
                }
            }
            finally { Ours = false; }
            return n <= 0;
        }
    }

    public static readonly Bag MyBag = new();

    const ItemRandomValues.Attr FavouriteMark = (ItemRandomValues.Attr)85;   // Better Chests' favourite heart

    static bool Favourite(ItemObject item) =>
        item.randomValues?.strAttributes != null && item.randomValues.strAttributes.Exists(a => a != null && a.Key == FavouriteMark && a.Value == "1");

    static readonly Regex HasAny = new(@"^has_any_([a-z]+?)(?:_(\d+))?$");
    static readonly Regex Has = new(@"^has_(.+?)(?:_(\d+))?$");

    /// <summary>has_any_sword, has_any_material_5, has_wood_10, has_iron_ore: 1 / 0; null if it isn't one of these.</summary>
    static string lastSeen;

    public static int? Computed(string flag)
    {
        if (!flag.StartsWith("has_")) return null;
        var m = HasAny.Match(flag);
        ItemSpec spec;
        if (m.Success && Parse.Categories.Contains(m.Groups[1].Value)) spec = new ItemSpec { Category = m.Groups[1].Value };
        else
        {
            m = Has.Match(flag);
            if (!m.Success) return null;
            spec = new ItemSpec { Name = m.Groups[1].Value };
            if (Find(spec.Name) == null) return 0;   // unknown item (or the catalogue isn't loaded yet)
        }
        int n = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
        int have = MyBag.Count(spec);
        string seen = $"{flag}: {have} of {n} ({(spec.Name != null ? Find(spec.Name)?.name : "any " + spec.Category)})";
        if (seen != lastSeen)
        {
            lastSeen = seen;
            Log.Info("NPCs and quests: " + seen + "; bag: " + string.Join(", ", Singleton<Inventory>.HasMain
                ? Singleton<Inventory>.main.allItems.Where(i => i?.itemObject?.data && i.section == InventorySection.Main).Select(i => $"{i.itemObject.data.name} x{i.itemObject.amount}")
                : Enumerable.Empty<string>()));
        }
        return have >= n ? 1 : 0;
    }

    /// <summary>A quest's reward, for the local player.</summary>
    public static void Give(RewardDef r)
    {
        var me = LocalPlayer.Get();
        if (!me || !Singleton<Inventory>.HasMain) return;
        Ours = true;
        try
        {
            switch (r.Kind)
            {
                case "gold": Singleton<Inventory>.main.AddGold(r.Amount); break;
                case "xp": me.GainXP(r.Amount); break;
                case "item":
                    var data = Find(r.Item.Name);
                    if (data) Add(data.CreateItem(r.Item.Count), me);
                    else Log.Warn($"NPCs and quests: no item called \"{r.Item.Name}\" for a reward");
                    break;
                case "random":
                    var pick = Random();
                    if (pick != null) Add(pick.CreateItem(1, UnityEngine.Random.Range(r.QMin, Math.Min(r.QMax, pick.qualityRange.y) + 1)), me);
                    break;
            }
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: reward: {e.Message}"); }
        finally { Ours = false; }

        ItemData Random()
        {
            var options = (pool ?? new List<ItemData>()).Where(d => (d.itemCategory & (ItemCategory.Weapon | ItemCategory.Armor | ItemCategory.Gear)) != 0
                && (d.itemIconUI || d.itemIconWorld) && d.qualityRange.y >= r.QMin).ToList();
            return options.Count > 0 ? options[UnityEngine.Random.Range(0, options.Count)] : null;
        }
    }

    static void Add(ItemObject item, PlayerMaster me)
    {
        int left = Singleton<Inventory>.main.AddItem(item);
        if (left > 0) item.DropToWorld(me.myTransform.position, left, 1f);
    }

    /// <summary>"Wood", "Any sword": for objective titles.</summary>
    public static string Label(ItemSpec spec)
    {
        if (spec.Name == null) return "Any " + spec.Category;
        var d = Find(spec.Name);
        try { string shown = d ? d.Name.GetLocalizedString() : null; if (!string.IsNullOrEmpty(shown)) return shown; } catch { }
        return spec.Name;
    }
}

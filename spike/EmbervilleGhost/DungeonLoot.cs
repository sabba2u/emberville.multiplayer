using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// Loot in the generated dungeons. Some rooms come with the game's own chests (their loot tables); we add more, spread
/// over the floor away from where you arrive, filled from everything in the game's item catalogue: materials,
/// potions and food, weapons, armour and gear. Deeper descent floors give more chests, more items each, better
/// quality and higher-tier equipment. All of it is rolled from the dungeon's seed, so co-op players see the same chests
/// with the same contents (and the chest sync keeps who-took-what straight).
/// </summary>
internal static class DungeonLoot
{
    // ---- the item catalogue (loaded once, in the background) ----

    static List<ItemData> pool;
    /// <summary>The catalogue once it's loaded (null before).</summary>
    internal static IReadOnlyList<ItemData> Pool => pool;
    static bool loading;

    /// <summary>Start loading every ItemData (call early: when a dungeon starts generating).</summary>
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
        if (pending == 0) { pool = found; loading = false; return; }
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result && Lootable(h.Result)) found.Add(h.Result);
                if (--pending == 0)
                {
                    pool = found.Distinct().OrderBy(d => d.addressKey).ToList();   // stable order: same rolls for everyone
                    loading = false;
                    NetLog.Info($"Dungeon loot: {pool.Count} kinds of items", toast: false);
                }
            };
    }

    static bool Lootable(ItemData d)
    {
        var c = d.itemCategory;
        if (c == ItemCategory.None || (c & (ItemCategory.Placeable | ItemCategory.Other)) != 0) return false;
        return d.itemIconUI || d.itemIconWorld;
    }

    // ---- chests ----

    /// <summary>What each of our chests will hold (filled when the game sets the chest up).</summary>
    static readonly Dictionary<Chest, (int seed, int floor, bool upgrade)> planned = new Dictionary<Chest, (int, int, bool)>();

    /// <summary>A dungeon chest's chance of holding a weapon better than the one you carry.</summary>
    const double UpgradeChance = 0.25;

    /// <summary>
    /// Chests for a freshly built dungeon (still switched off): the rooms' own get steady ids and aren't saved; ours go
    /// on open floor at least a walk away from the arrival, spread out.
    /// </summary>
    public static void Place(GameObject root, Dungeon.Where where, int genSeed, Vector3 arrival)
    {
        planned.Clear();
        var rng = new System.Random(genSeed ^ 0x5EED);
        int i = 0;
        foreach (var c in root.GetComponentsInChildren<Chest>(true))
        {
            c.chestID = $"EG_DG_{genSeed}_{where.Region}_{i++}";
            c.saveChest = false;
            c.mirrorChest = false;
        }

        var prefab = ChestPrefab();
        if (!prefab) { NetLog.Warn("Dungeon loot: no chest to copy", toast: false); return; }

        int floor = Mathf.Max(1, where.Floor);
        int want = Mathf.Min(10, 3 + floor / 2);
        var cs = Dungeon.cellSize;
        var cells = Dungeon.floorCells;
        if (cells.Count == 0) return;
        var start = new Vector3Int(Mathf.FloorToInt(arrival.x / cs.x), Mathf.FloorToInt(arrival.y / cs.y), 0);
        if (!cells.Contains(start)) start = cells.OrderBy(c => (c - start).sqrMagnitude).First();
        var dist = Delve.Distances(start, cells);
        var clear = Delve.Free != null && Delve.Free.Count > 0 && where.Floor > 0 ? Delve.Free : cells;   // (not on the furniture)
        var spots = dist.Where(p => p.Value >= 12 && Delve.Open(p.Key, clear, 1)).Select(p => p.Key).OrderBy(c => c.x).ThenBy(c => c.y).ToList();
        var picked = new List<Vector3Int>();
        for (int tries = 0; tries < 400 && picked.Count < want && spots.Count > 0; tries++)
        {
            var c = spots[rng.Next(spots.Count)];
            if (picked.Any(p => Mathf.Abs(p.x - c.x) + Mathf.Abs(p.y - c.y) < 14)) continue;
            picked.Add(c);
        }

        // the rooms' decorative chests (art only, nothing to open): replaced by real ones where they stood
        var spotsAt = Decorative(root).ToList();
        spotsAt.AddRange(picked.Take(Mathf.Max(0, want - spotsAt.Count)).Select(c => new Vector3((c.x + 0.5f) * cs.x, (c.y + 0.5f) * cs.y, 0f)));
        int n = 0;
        foreach (var at in spotsAt)
        {
            var go = Object.Instantiate(prefab.gameObject, root.transform);
            go.name = $"EG_Chest_{n}";
            go.transform.position = at;
            go.SetActive(true);   // under the switched-off root: Start waits until we arrive
            var chest = go.GetComponent<Chest>();
            chest.chestID = $"EG_DG_{genSeed}_{where.Region}_x{n}";
            chest.saveChest = false;
            chest.mirrorChest = false;
            chest.shareContent = ChestShare.None;
            chest.isInitialized = false;
            chest.isOpened = chest.alreadyOpened = false;
            planned[chest] = (unchecked(genSeed * 31 + n * 977 + 7), floor, true);   // rolled when the chest is set up (the catalogue may still be loading now)
            n++;
        }
        NetLog.Info($"Dungeon loot: {n} chests ({decorCount} were decorative ones, now real; +{i} of the rooms' own)", toast: false);
    }

    /// <summary>One chest of ours out in the world (Better Streaming's mystery chest): not saved, rolled like a floor's
    /// chest when it's set up; empty: nothing in it. Null: no chest to copy.</summary>
    internal static Chest MakeChest(Vector3 at, string id, int seed, int floor, bool empty)
    {
        var prefab = ChestPrefab();
        if (!prefab) return null;
        Preload();
        var go = Object.Instantiate(prefab.gameObject, at, Quaternion.identity);
        go.name = "EG_" + id;
        var chest = go.GetComponent<Chest>();
        chest.chestID = id;
        chest.saveChest = false;
        chest.mirrorChest = false;
        chest.shareContent = ChestShare.None;
        chest.isInitialized = false;
        chest.isOpened = chest.alreadyOpened = false;
        planned[chest] = (empty ? int.MinValue : seed, floor, false);
        go.SetActive(true);
        return chest;
    }

    /// <summary>A boss's chest at a dungeon's end (DungeonEnd): bigger, and full of gear better than yours (BossLoot).</summary>
    internal static Chest MakeBossChest(Transform parent, Vector3 at, string id, int seed)
    {
        var c = MakeChest(at, id, seed, 1, false);
        if (!c) return null;
        planned.Remove(c);
        bossChests[c] = seed;
        c.transform.SetParent(parent, true);
        c.transform.localScale = Vector3.one * 1.5f;
        return c;
    }

    static readonly Dictionary<Chest, int> bossChests = new Dictionary<Chest, int>();

    /// <summary>
    /// A boss's treasure: 3 weapons (the first of your own weapon's kind) and 2-3 pieces of armour or gear, at high
    /// quality, each as strong as gear 5-8 levels above yours (of up to 40 tries, the one whose level is nearest that,
    /// at or over it first), but asking only your level; and a stack of potions.
    /// </summary>
    internal static List<InventoryItem> BossLoot(System.Random rng)
    {
        if (pool == null || pool.Count == 0) return null;
        int yours = Integrations.YourGearLevel();
        var weapons = pool.Where(d => Has(d, ItemCategory.Weapon)).ToList();
        var armour = pool.Where(d => Has(d, ItemCategory.Armor) || Has(d, ItemCategory.Gear)).ToList();
        var mine = LocalPlayer.Get() ? LocalPlayer.Get().currentWeaponItem?.data as CollectableWeapon : null;
        var kin = mine && mine.weapon ? weapons.OfType<CollectableWeapon>().Where(d => d.weapon && d.weapon.weaponCategory == mine.weapon.weaponCategory).Cast<ItemData>().ToList() : new List<ItemData>();
        var picks = new List<List<ItemData>> { kin.Count > 0 ? kin : weapons, weapons, weapons };
        int pieces = rng.Next(2, 4);
        for (int i = 0; i < pieces; i++) picks.Add(armour.Count > 0 ? armour : weapons);
        var items = new List<InventoryItem>();
        foreach (var from in picks)
        {
            if (from.Count == 0) continue;
            int target = yours + rng.Next(5, 9);
            ItemObject best = null;
            int bestScore = int.MaxValue, bestLevel = 0;
            for (int tries = 0; tries < 40 && bestScore > 0; tries++)
            {
                var data = from[rng.Next(from.Count)];
                int quality = Mathf.Clamp(rng.Next(4, 7), data.qualityRange.x, data.qualityRange.y);
                ItemObject candidate;
                try { candidate = new ItemObject(data, 1, quality); }
                catch (System.Exception) { continue; }
                int level = LevelOf(candidate);
                int score = level >= target ? level - target : (target - level) * 3;
                if (score < bestScore) { bestScore = score; best = candidate; bestLevel = level; }
            }
            if (best == null) continue;
            foreach (var a in best.randomValues.intAttributes)   // (as strong as that level's, asking only yours)
                if (a != null && a.Key == ItemRandomValues.Attr.LevelRequirement && a.Value > yours) a.Value = yours;
            items.Add(new InventoryItem(best, items.Count, InventorySection.Chest));
            NetLog.Info($"Boss treasure: {best.data.name} (quality {best.randomValues.quality}, as strong as level {bestLevel}, asks level {yours})", toast: false);
        }
        var potions = pool.Where(d => d.name.StartsWith("Item_Potion")).ToList();
        if (potions.Count > 0)
        {
            var p = potions[rng.Next(potions.Count)];
            try { items.Add(new InventoryItem(new ItemObject(p, rng.Next(2, 5), -1), items.Count, InventorySection.Chest)); } catch { }
        }
        return items;
    }

    static int decorCount;

    /// <summary>
    /// Chest-looking props in the rooms with no Chest on them (or around them): where each stood (they're removed).
    /// </summary>
    static List<Vector3> Decorative(GameObject root)
    {
        var found = new List<Vector3>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t || t.name.IndexOf("chest", System.StringComparison.OrdinalIgnoreCase) < 0 || t.name.StartsWith("EG_")) continue;
            if (t.GetComponentInChildren<Chest>(true) || t.GetComponentInParent<Chest>(true)) continue;   // a real one (or part of one)
            if (t.parent && t.parent.name.IndexOf("chest", System.StringComparison.OrdinalIgnoreCase) >= 0 && !t.parent.GetComponentInParent<Chest>(true)) continue;   // its parent counts already
            found.Add(t.position);
            Object.Destroy(t.gameObject);
        }
        decorCount = found.Count;
        return found;
    }

    static Chest ChestPrefab()
    {
        var all = Resources.FindObjectsOfTypeAll<Chest>().Where(c => c && c.artClosed && c.artOpened && c.selectArrow).ToList();
        return all.FirstOrDefault(c => !c.gameObject.scene.IsValid() && c.lootTable) ?? all.FirstOrDefault(c => c.lootTable) ?? all.FirstOrDefault();
    }

    /// <summary>A chest's contents: count and quality grow with the floor; equipment comes in more often deeper down and up
    /// to a higher tier (Biome1..8: one more every 3 floors).</summary>
    internal static List<InventoryItem> Roll(System.Random rng, int floor)
    {
        var list = new List<InventoryItem>();
        if (pool == null || pool.Count == 0) return null;   // not loaded: the chest's own loot table instead
        int count = Mathf.Min(14, rng.Next(3, 6) + floor / 2);
        int maxTier = 1 + floor / 3;
        float gearShare = Mathf.Min(0.6f, 0.25f + 0.025f * floor);
        var qLo = Mathf.Min(6, floor / 4);
        var qHi = Mathf.Min(6, 1 + floor / 2);
        var materials = pool.Where(d => Has(d, ItemCategory.Material)).ToList();
        var consumables = pool.Where(d => Has(d, ItemCategory.Consumable)).ToList();
        var equipment = pool.Where(d => (Has(d, ItemCategory.Weapon) || Has(d, ItemCategory.Armor) || Has(d, ItemCategory.Gear)) &&
                                        ((int)d.itemTier == 0 || (int)d.itemTier <= maxTier)).ToList();
        for (int slot = 0; slot < count; slot++)
        {
            double r = rng.NextDouble();
            ItemData data;
            int amount = 1, quality = -1;
            if (r < gearShare && equipment.Count > 0)
            {
                data = equipment[rng.Next(equipment.Count)];
                quality = qLo + rng.Next(qHi - qLo + 1);
            }
            else if (r < gearShare + 0.25 && consumables.Count > 0)
            {
                data = consumables[rng.Next(consumables.Count)];
                amount = data.IsStackable ? rng.Next(1, 4) : 1;
            }
            else if (materials.Count > 0)
            {
                data = materials[rng.Next(materials.Count)];
                amount = data.IsStackable ? rng.Next(2, 7) + floor : 1;
            }
            else data = pool[rng.Next(pool.Count)];
            try { list.Add(new InventoryItem(new ItemObject(data, amount, quality), slot, InventorySection.Chest)); }
            catch (System.Exception e) { NetLog.Warn($"Dungeon loot {data.addressKey}: {e.Message}", toast: false); }
        }
        return list;
    }

    static bool Has(ItemData d, ItemCategory c) => (d.itemCategory & c) != 0;

    /// <summary>
    /// A weapon better than the one you carry (as Minecraft Dungeons does it: going back down again and again is how
    /// your weapon gets better): the same weapon or another of its kind (sword, axe, bow...), at least your quality, its
    /// damage 15-30% over yours (both ends), and asking no higher a level than yours does. Null: fists (nothing to beat),
    /// or the catalogue isn't loaded.
    /// </summary>
    internal static ItemObject BetterWeapon(System.Random rng)
    {
        var me = LocalPlayer.Get();
        var mine = me ? me.currentWeaponItem : null;
        if (pool == null || mine?.data is not CollectableWeapon cw || mine.randomValues == null) return null;
        var kin = pool.OfType<CollectableWeapon>().Where(d => cw.weapon && d.weapon && d.weapon.weaponCategory == cw.weapon.weaponCategory).ToList();
        var data = kin.Count > 0 && rng.NextDouble() < 0.5 ? kin[rng.Next(kin.Count)] : cw;
        int quality = (int)mine.randomValues.quality;
        if (quality < data.qualityRange.x || quality > data.qualityRange.y) data = cw;   // (that kind can't come as good as yours)
        ItemObject item;
        try { item = new ItemObject(data, 1, quality); }
        catch (System.Exception e) { NetLog.Warn($"Better weapon {data.addressKey}: {e.Message}", toast: false); return null; }
        var was = mine.randomValues.damageRange;
        float up = 1.15f + (float)rng.NextDouble() * 0.15f;
        var rv = item.randomValues;
        int lo = Mathf.Max(rv.damageRange.x, Mathf.Max(was.x + 1, Mathf.CeilToInt(was.x * up)));
        int hi = Mathf.Max(rv.damageRange.y, Mathf.Max(was.y + 1, Mathf.CeilToInt(was.y * up)));
        rv.damageRange = new Vector2Int(lo, Mathf.Max(lo, hi));
        int yours = LevelOf(mine);
        foreach (var a in rv.intAttributes)
            if (a != null && a.Key == ItemRandomValues.Attr.LevelRequirement && a.Value > yours) a.Value = yours;
        NetLog.Info($"Dungeon loot: a better weapon: {data.name} {lo}-{hi} (yours: {mine.data.name} {was.x}-{was.y})", toast: false);
        return item;
    }

    /// <summary>An item's level requirement, as its card shows it (0: none).</summary>
    internal static int LevelOf(ItemObject item)
    {
        var attrs = item?.randomValues?.intAttributes;
        if (attrs == null) return 0;
        foreach (var a in attrs) if (a != null && a.Key == ItemRandomValues.Attr.LevelRequirement) return a.Value;
        return 0;
    }

    /// <summary>
    /// A gift of gear (Better Streaming's loot drop): 3-5 pieces of equipment (weapons, armour, gear), each better than
    /// what you have: its level requirement 1-3 above yours (70%), 3-6 above (20%) or 7-15 above (10%). Yours: the
    /// average requirement of what you wear and hold (none: your level). For each piece, up to 40 candidates (any
    /// equipment, any quality) are made and the one whose real requirement is nearest the target (at or over it first)
    /// is kept. And 1-2 stacks of health or Kymia potions. No food, no materials. Null: the catalogue isn't loaded.
    /// </summary>
    internal static List<ItemObject> GearAndPotions(System.Random rng, int yourLevel)
    {
        if (pool == null || pool.Count == 0) return null;
        var equipment = pool.Where(d => Has(d, ItemCategory.Weapon) || Has(d, ItemCategory.Armor) || Has(d, ItemCategory.Gear)).ToList();
        var potions = pool.Where(d => d.name.StartsWith("Item_Potion")).ToList();   // (health and Kymia, small and big)
        var list = new List<ItemObject>();
        int gear = equipment.Count > 0 ? rng.Next(3, 6) : 0;
        for (int i = 0; i < gear; i++)
        {
            double roll = rng.NextDouble();
            int target = yourLevel + (roll < 0.7 ? rng.Next(1, 4) : roll < 0.9 ? rng.Next(3, 7) : rng.Next(7, 16));
            ItemObject best = null;
            int bestScore = int.MaxValue;
            for (int tries = 0; tries < 40 && bestScore > 0; tries++)
            {
                var data = equipment[rng.Next(equipment.Count)];
                ItemObject candidate;
                try { candidate = new ItemObject(data, 1, rng.Next(0, 7)); }
                catch (System.Exception) { continue; }
                int level = LevelOf(candidate);
                int score = level >= target ? level - target : (target - level) * 3;   // (a bit over beats a bit under)
                if (score < bestScore) { bestScore = score; best = candidate; }
            }
            if (best != null) list.Add(best);
        }
        int stacks = potions.Count > 0 ? rng.Next(1, 3) : 0;
        for (int i = 0; i < stacks; i++)
        {
            var data = potions[rng.Next(potions.Count)];
            try { list.Add(new ItemObject(data, rng.Next(1, 4), -1)); }
            catch (System.Exception e) { NetLog.Warn($"Loot drop {data.addressKey}: {e.Message}", toast: false); }
        }
        return list;
    }

    /// <summary>Our chests: our contents instead of the prefab's loot table.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.CreateChestItems))]
    static class FillPatch
    {
        static bool Prefix(Chest __instance)
        {
            if (bossChests.TryGetValue(__instance, out int bossSeed))   // (a boss's: its treasure)
            {
                var loot = BossLoot(new System.Random(bossSeed));
                if (loot == null) return true;
                bossChests.Remove(__instance);
                var m = Singleton<ChestManager>.main;
                if (!m.HasID(__instance.chestID)) m.AddChest(__instance.chestID, loot, false, ChestShare.None);
                return false;
            }
            if (!planned.TryGetValue(__instance, out var plan)) return true;
            planned.Remove(__instance);
            var items = plan.seed == int.MinValue ? new List<InventoryItem>() : Roll(new System.Random(plan.seed), plan.floor);   // (MinValue: a trap, empty)
            if (items == null) return true;   // the catalogue didn't load: the chest's own loot table
            var lucky = new System.Random(plan.seed ^ 0x0B377E2);
            if (plan.upgrade && lucky.NextDouble() < UpgradeChance && BetterWeapon(lucky) is ItemObject better)
                items.Add(new InventoryItem(better, items.Count, InventorySection.Chest));   // (the next free slot)
            var manager = Singleton<ChestManager>.main;
            if (manager.HasID(__instance.chestID)) return false;
            manager.AddChest(__instance.chestID, items, false, ChestShare.None);   // not storage for crafting / building
            return false;
        }
    }
}

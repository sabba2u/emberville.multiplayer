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
    static readonly Dictionary<Chest, (int seed, int floor)> planned = new Dictionary<Chest, (int, int)>();

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
        var spots = dist.Where(p => p.Value >= 12 && Delve.Open(p.Key, cells, 1)).Select(p => p.Key).OrderBy(c => c.x).ThenBy(c => c.y).ToList();
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
            planned[chest] = (unchecked(genSeed * 31 + n * 977 + 7), floor);   // rolled when the chest is set up (the catalogue may still be loading now)
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
        planned[chest] = (empty ? int.MinValue : seed, floor);
        go.SetActive(true);
        return chest;
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

    /// <summary>Our chests: our contents instead of the prefab's loot table.</summary>
    [HarmonyPatch(typeof(Chest), nameof(Chest.CreateChestItems))]
    static class FillPatch
    {
        static bool Prefix(Chest __instance)
        {
            if (!planned.TryGetValue(__instance, out var plan)) return true;
            planned.Remove(__instance);
            var items = plan.seed == int.MinValue ? new List<InventoryItem>() : Roll(new System.Random(plan.seed), plan.floor);   // (MinValue: a trap, empty)
            if (items == null) return true;   // the catalogue didn't load: the chest's own loot table
            var manager = Singleton<ChestManager>.main;
            if (manager.HasID(__instance.chestID)) return false;
            manager.AddChest(__instance.chestID, items, false, ChestShare.None);   // not storage for crafting / building
            return false;
        }
    }
}

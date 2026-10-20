using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace EmbervilleGhost;

/// <summary>
/// /resources [amount]: a pile of every material (wood, stone, ore, ...) plus whatever the repairable buildings in the
/// loaded scenes cost, so the town can be fixed up. Items come from the game's Addressables catalogue (every ItemData
/// in it, materials kept). What doesn't fit in the bag lands at our feet.
/// </summary>
internal static class Stockpile
{
    static bool busy;

    public static void Give(int amount)
    {
        if (busy) { Chat.System("Still handing out the last pile..."); return; }
        if (!LocalPlayer.Get() || !Singleton<Inventory>.HasMain) { Chat.System("Load into a world first."); return; }
        amount = Mathf.Clamp(amount, 1, 999);

        // What the town's repairs cost (at least that much of each).
        var need = new Dictionary<string, int>();
        foreach (var b in Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            foreach (var c in b.itemCosts) if (c.Item) Need(need, c.Item.addressKey, c.Cost);
            foreach (var r in b.GetItemCosts) if (r?.itemData) Need(need, r.itemData.addressKey, r.Amount);
        }

        // Every item in the catalogue (one load per item, deduped by where it lives).
        var keys = new HashSet<string>();
        foreach (var locator in Addressables.ResourceLocators)
            foreach (var key in locator.Keys)
                if (key is string s && locator.Locate(s, typeof(ItemData), out var locs))
                    foreach (var l in locs) keys.Add(l.PrimaryKey);
        foreach (var k in need.Keys) keys.Add(k);
        if (keys.Count == 0) { Chat.System("Couldn't find the game's item list."); return; }

        busy = true;
        startedAt = Time.unscaledTime;
        pending = keys.Count; kinds = 0; dropped = 0; repairs = need.Count; perItem = amount; needs = need;
        seen.Clear(); queue.Clear();
        // Loaded callbacks only queue the item: adding it to the bag loads more (icons...) and Addressables
        // doesn't allow that from inside its own callback. Tick hands them out, a few per frame.
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result) queue.Enqueue(h.Result);
                pending--;
            };
    }

    static float startedAt;
    static int pending, kinds, dropped, repairs, perItem;
    static Dictionary<string, int> needs;
    static readonly HashSet<ItemData> seen = new HashSet<ItemData>();
    static readonly Queue<ItemData> queue = new Queue<ItemData>();

    /// <summary>Every frame: a few of the loaded items into the bag; the summary once they're all done.</summary>
    public static void Tick()
    {
        if (!busy) return;
        for (int n = 0; n < 8 && queue.Count > 0; n++)
        {
            var data = queue.Dequeue();
            if (!data || !seen.Add(data)) continue;
            try
            {
                needs.TryGetValue(data.addressKey ?? "", out int needed);
                bool material = (data.itemCategory & ItemCategory.Material) != 0 && data.IsStackable;
                int give = needed > 0 ? Mathf.Max(perItem, needed) : material ? perItem : 0;
                if (give <= 0 || !Singleton<Inventory>.HasMain) continue;
                var item = new ItemObject(data, give);
                int rest = Singleton<Inventory>.main.AddItem(item);
                var me = LocalPlayer.Get();
                if (rest > 0 && me) { item.DropToWorld(me.transform.position, rest, 1f); dropped++; }
                kinds++;
            }
            catch (System.Exception e) { NetLog.Warn($"Stockpile {data.addressKey}: {e.Message}"); }
        }
        if (queue.Count > 0 || (pending > 0 && Time.unscaledTime - startedAt < 30f)) return;   // a load that never ends: give up on it
        busy = false;
        Chat.System($"Gave {perItem} of {kinds} materials" + (repairs > 0 ? $" (and enough for the {repairs} repair costs here)" : "") +
                    (dropped > 0 ? $"; {dropped} didn't fit in your bag and are at your feet." : "."));
    }

    static void Need(Dictionary<string, int> need, string key, int amount)
    {
        if (string.IsNullOrEmpty(key)) return;
        need[key] = need.TryGetValue(key, out int had) ? had + amount : amount;
    }
}

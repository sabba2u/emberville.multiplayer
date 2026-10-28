using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// (trigger: "command shop gumu") in a conversation: the game's own shop screen, with a shop of ours behind it (a
/// StoreComponent made here, as the Town has one for Gumu). Its stock is Gumu's (the items of the game's LootTable_Gumu,
/// all of them) and a few pieces of good gear picked from the game's whole item list, better the higher your level;
/// fresh each day in the game (and each time the game starts), kept as you buy and sell in between. And "command learn sawmill": a blueprint learned, as
/// the game's quests do it (its recipe added to the world's known recipes).
/// </summary>
internal static class Shop
{
    // Gumu's stock: the game's LootTable_Gumu (item, how many)
    static readonly (string item, int min, int max)[] GumuStock =
    {
        ("Item_Shield", 1, 1), ("Item_PotionHealthSmall", 13, 17), ("Item_Wpn_Staff", 1, 1), ("Item_Quest_ScavengerBaneBook", 1, 1),
        ("Item_Carrot", 10, 20), ("Item_Tomato", 10, 20), ("Item_Plant_Tuber", 10, 20), ("Item_Light_Head", 1, 1),
        ("Item_Light_Lower", 1, 1), ("Item_Light_Upper", 1, 1), ("Item_Gem_Malachite", 20, 30), ("Item_Cloak", 1, 1),
        ("Item_Potato", 10, 30),
    };

    static readonly Dictionary<string, StoreComponent> shops = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> stockedOn = new(StringComparer.OrdinalIgnoreCase);   // (the day in the game)

    public static void Open(string who)
    {
        who = (who ?? "").Trim();
        if (!who.Equals("gumu", StringComparison.OrdinalIgnoreCase)) { Hooks.Say($"No shop called \"{who}\""); return; }
        var me = LocalPlayer.Get();
        if (!me || !Singleton<StoreManager>.HasMain || Singleton<StoreManager>.main.isLoading || !Items.Ready) { Hooks.Say("The shop isn't ready yet"); return; }
        if (!shops.TryGetValue(who, out var shop) || !shop)
        {
            var go = new GameObject("BNQ_Shop_" + who);
            go.SetActive(false);   // (its Awake and Start wait until it's filled in)
            var forward = go.AddComponent<TriggerForward>();
            shop = go.AddComponent<StoreComponent>();
            shop.triggerForward = forward;
            shop.storeID = "bnq_shop_" + who.ToLowerInvariant();
            shop.storeName = "Gumu";
            shop.saveStore = false;
            shop.sellingFactor = 1f;
            shop.buyingFactor = 0.25f;
            shop.storeItems = new List<LootItem>();
            shop.onOpenStoreEvents = new CustomEventList { events = new List<CustomEvent>() };
            shop.onCloseStoreEvents = new CustomEventList { events = new List<CustomEvent>() };
            shop.sfxOpen ??= new AK.Wwise.Event();
            shop.sfxClose ??= new AK.Wwise.Event();
            if (!Singleton<StoreManager>.main.HasID(shop.ID))
            {
                Singleton<StoreManager>.main.AddStore(shop, Stock(), new List<InventoryItem>(), false);
                stockedOn[who] = Singleton<TimeManager>.HasMain ? Singleton<TimeManager>.main.CurrentDay : 0;
            }
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(true);
            shops[who] = shop;
        }
        int day = Singleton<TimeManager>.HasMain ? Singleton<TimeManager>.main.CurrentDay : 0;
        if (!stockedOn.TryGetValue(who, out int last) || last != day)
        {
            stockedOn[who] = day;
            if (Singleton<StoreManager>.main.allStores.TryGetValue(shop.ID, out var stock))
            {
                stock.mainItems = Stock();
                stock.buybackItems = new List<InventoryItem>();
            }
        }
        shop.transform.position = me.transform.position;
        if (!shop.isOpened) shop.OpenStore();
    }

    static List<InventoryItem> Stock()
    {
        var list = new List<InventoryItem>();
        foreach (var (name, min, max) in GumuStock)
        {
            var data = Items.Pool.FirstOrDefault(d => d && d.name == name);
            if (!data) { Log.Warn($"NPCs and quests: Gumu's {name} isn't in the item list"); continue; }
            list.Add(new InventoryItem(data.CreateItem(UnityEngine.Random.Range(min, max + 1)), list.Count, InventorySection.Stock));
        }
        // and the good stuff: 3-5 weapons and armour from everything the game has, the quality by your level
        int level = GameData.playerData != null ? GameData.playerData.GetCurrentLevel(PlayerClassType.None) : 1;
        var (qmin, qmax) = level < 8 ? (ItemQuality.Good, ItemQuality.Superior) : level < 16 ? (ItemQuality.Superior, ItemQuality.Epic) : (ItemQuality.Epic, ItemQuality.Legendary);
        var gear = Items.Pool.Where(d => d && (d.itemCategory & (ItemCategory.Weapon | ItemCategory.Armor)) != 0 && (d.itemIconUI || d.itemIconWorld)
                                         && d.qualityRange.y >= (int)qmin).ToList();
        int n = UnityEngine.Random.Range(3, 6);
        for (int i = 0; i < n && gear.Count > 0; i++)
        {
            var d = gear[UnityEngine.Random.Range(0, gear.Count)];
            gear.Remove(d);
            int q = UnityEngine.Random.Range((int)qmin, Math.Min((int)qmax, d.qualityRange.y) + 1);
            if (UnityEngine.Random.value < 0.1f) q = Math.Min(q + 1, Math.Min((int)ItemQuality.Legendary, d.qualityRange.y));   // (now and then, one better)
            list.Add(new InventoryItem(d.CreateItem(1, q), list.Count, InventorySection.Stock));
        }
        Log.Info($"NPCs and quests: Gumu restocked ({list.Count} items, gear {qmin}-{qmax} for level {level})");
        return list;
    }

    // ---- the character creator ----

    static bool restyle;

    /// <summary>(trigger: "command restyle"): the game's own character creator (hair, beard, face, skin, colours, body,
    /// name), as at the start of a new game; what you pick is saved with your character when the game saves. Opened
    /// once the conversation has closed (it can't share the screen with it).</summary>
    public static void Restyle() => restyle = true;

    public static void Tick()
    {
        if (!restyle || Singleton<DialogueManager>.HasMain && Singleton<DialogueManager>.main.IsPlaying) return;
        restyle = false;
        var screen = UnityEngine.Object.FindAnyObjectByType<UICharCustomization>(FindObjectsInactive.Include);
        if (screen) screen.Show();
        else Hooks.Say("There's no mirror here");
    }

    // ---- blueprints ----

    // the game's recipe ids (its quests' rewards), for when the recipe itself isn't loaded here
    static readonly Dictionary<string, string> RecipeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sawmill"] = "crftrcp_station_sawmill", ["cookingpot"] = "crftrcp_station_cookingPot", ["cooking pot"] = "crftrcp_station_cookingPot",
        ["recyclemachine"] = "crftrcp_station_recycleMachine", ["recycle machine"] = "crftrcp_station_recycleMachine",
        ["anvil"] = "crftrcp_station_anvil", ["smelter"] = "crftrcp_station_smelter", ["armorcrafting"] = "crftrcp_station_armorCrafting",
        ["woodenchest"] = "crftrcp_wdnChst", ["wooden chest"] = "crftrcp_wdnChst",
    };

    public static void Learn(string what)
    {
        what = (what ?? "").Trim();
        var world = GameData.worldData;
        if (world == null) return;
        var recipe = Resources.FindObjectsOfTypeAll<CraftingRecipe>()
            .FirstOrDefault(r => r && (r.name.Equals(what, StringComparison.OrdinalIgnoreCase) || r.name.Replace("_", "").IndexOf(what.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) >= 0));
        string id = recipe ? recipe.RecipeID : RecipeIds.TryGetValue(what, out var known) ? known : null;
        if (id == null) { Log.Warn($"NPCs and quests: no blueprint called \"{what}\""); return; }
        if (world.knownRecipes.Contains(id)) return;
        world.knownRecipes.Add(id);
        Hooks.Say($"Learned a blueprint: {(recipe ? recipe.RecipeName.GetLocalizedString() : what)}");
        Log.Info($"NPCs and quests: learned {id}");
    }
}

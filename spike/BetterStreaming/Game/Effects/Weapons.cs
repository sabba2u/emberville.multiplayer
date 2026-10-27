using System.Linq;
using BetterStreaming.Core;
using HarmonyLib;
using UnityEngine;

namespace BetterStreaming;

/// <summary>
/// Fists only (the class also does "that kind only", for staffs or swords, taken out for now): for its time you fight with that kind. The first one of the kind in your bag
/// goes in your hand (none: a plain one is lent, and taken back after); fists: your weapon comes off. Meanwhile no other
/// main-hand weapon goes in your hand, whatever the way (drag, double-click, the favourites keys, the wheel: they all end
/// in CharacterStats.EquipItem, which is where it's stopped). When it ends, your weapon goes back in your hand, the lent
/// one leaves your bag (and your hand), and the end waits while a screen is open (the companion's gear screen puts
/// gear on the companion). A save or a scene change ends it first, so a lent weapon never reaches your save.
/// </summary>
[HarmonyPatch]
internal sealed class WeaponLock : IEffect
{
    public string Id { get; }
    readonly WeaponCategory[] kinds;   // (empty: fists)
    static WeaponLock active;
    static CharacterStats mine;        // (your stats when it began: the companion's are someone else's)
    static ItemObject before, lent;
    static bool ours, endOwed;

    WeaponLock(string id, params WeaponCategory[] kinds) { Id = id; this.kinds = kinds; }

    public static void Register()
    {
        foreach (var fx in new[]
        {
            new WeaponLock("fists"),   // (staffs only / swords only were taken out: fists only stays)
        })
            Streamer.Effects[fx.Id] = fx;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (_, _) => EndNow();
    }

    internal static Inventory Bag => Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
    internal static bool Locked => active != null;

    /// <summary>A weapon for the hand (not the dodge slot's).</summary>
    internal static bool HandWeapon(ItemData d) => d is CollectableWeapon w && w.weapon != null && (w.weapon.gearSlot & GearSlot.MainHand) != 0;
    bool Fits(ItemData d) => HandWeapon(d) && kinds.Contains(((CollectableWeapon)d).weapon.weaponCategory);

    public bool Available(out string why) { why = ""; return true; }

    public bool Ready(out string why)
    {
        if (!Effects.HavePlayer(out why)) return false;
        if (!Bag) { why = "no bag"; return false; }
        if (active != null && active != this) { why = "another weapon lock is on"; return false; }   // (one at a time: they'd fight)
        if (WeaponRoulette.Running) { why = "the weapon roulette is on"; return false; }
        if (UIPanels.HasOpenedPanel) { why = "a screen is open"; return false; }
        return true;
    }

    public bool Fire(Pending p, float size)
    {
        if (active == this) return true;   // (on already: the timer got longer)
        var bag = Bag;
        var me = Effects.Player;
        ours = true;
        try
        {
            var inHand = bag.allItems.FirstOrDefault(x => x.section == InventorySection.Gear && HandWeapon(x.itemObject?.data));
            if (kinds.Length == 0)
            {
                if (inHand != null && !bag.TryToUnequip(GearSlot.MainHand, -1, InventorySection.Main)) return false;   // (a full bag: it waits)
                Begin(me, inHand?.itemObject);
                return true;
            }
            if (inHand != null && Fits(inHand.itemObject.data)) { Begin(me, inHand.itemObject); return true; }   // (already holding one)
            var own = bag.allItems.FirstOrDefault(x => (x.section == InventorySection.Main || x.section == InventorySection.Belt) && Fits(x.itemObject?.data));
            ItemObject lending = null;
            if (own == null)
            {
                // none of that kind: a plain one lent (the bag stores a copy: found by what's new in it)
                var data = Weapons().Where(Fits).OrderBy(w => ((CollectableWeapon)w).maxDamageRange.y).FirstOrDefault();
                if (!data) return false;
                var had = new System.Collections.Generic.HashSet<InventoryItem>(bag.allItems);
                bag.AddItem(data.CreateItem(1, 0));
                own = bag.allItems.FirstOrDefault(x => !had.Contains(x) && x.itemObject?.data == data);
                if (own == null) return false;   // (a full bag)
                lending = own.itemObject;
            }
            if (!bag.TryToEquip(own.slotIndex, own.section, GearSlot.MainHand))
            {
                if (lending != null) bag.RemoveItem(lending);
                return false;
            }
            Begin(me, inHand?.itemObject);
            lent = lending;
            return true;
        }
        catch (System.Exception e) { Log.Warn($"Streaming: {Id}: {e.Message}"); EndNow(); return false; }
        finally { ours = false; }
    }

    void Begin(PlayerMaster me, ItemObject had) { active = this; mine = me.characterStats; before = had; endOwed = false; }

    public void Stop()
    {
        if (active != this) return;
        if (UIPanels.HasOpenedPanel) { endOwed = true; return; }   // (Tick ends it once the screen's closed)
        EndNow();
    }

    /// <summary>Every frame (Plugin.Update): an end that waited for a screen to close.</summary>
    public static void Tick()
    {
        if (endOwed && !UIPanels.HasOpenedPanel) EndNow();
    }

    /// <summary>Your weapon back in your hand, the lent one gone (from your hand too), the lock off.</summary>
    static void EndNow()
    {
        if (active == null) return;
        var bag = Bag;
        var me = Effects.Player;
        ours = true;
        try
        {
            if (bag && me)
            {
                var back = before != null ? bag.allItems.FirstOrDefault(x => x.itemObject == before && x.section != InventorySection.Gear) : null;
                if (back != null) bag.TryToEquip(back.slotIndex, back.section, GearSlot.MainHand);
                if (lent != null)
                {
                    if (me.characterStats.EquippedItems.Contains(lent)) { me.characterStats.RemoveItem(lent); me.characterStats.SetFallbackWeapon(); }
                    bag.RemoveItem(lent);
                    bag.UpdateEquippedList();
                }
            }
        }
        catch (System.Exception e) { Log.Warn($"Streaming: giving your weapon back: {e.Message}"); }
        finally
        {
            ours = false;
            if (active != null) Streamer.EndEarly(active.Id);
            active = null; mine = null; before = lent = null; endOwed = false;
        }
    }

    /// <summary>A save on its way: the lock ends first (no lent weapon saved, your own back in hand).</summary>
    [HarmonyPatch(typeof(GameData), nameof(GameData.SaveData)), HarmonyPrefix]
    static void BeforeSave() => EndNow();

    /// <summary>The weapons there are (the game's catalogue through the multiplayer mod, else those in memory).</summary>
    static System.Collections.Generic.IEnumerable<ItemData> Weapons()
    {
        var all = Hooks.Catalogue?.Invoke();
        return (all != null && all.Count > 0 ? all : (System.Collections.Generic.IReadOnlyList<ItemData>)Resources.FindObjectsOfTypeAll<CollectableWeapon>()).Where(d => d && !string.IsNullOrEmpty(d.addressKey));
    }

    /// <summary>While a lock is on, your hand takes only its kind (fists: none). Armour, the dodge slot and the
    /// companion's gear are as usual.</summary>
    [HarmonyPatch(typeof(CharacterStats), nameof(CharacterStats.EquipItem)), HarmonyPrefix]
    static bool OnlyThatKind(CharacterStats __instance, ItemObject itemObject)
    {
        // the roulette picks your weapon: no other goes in your hand meanwhile (its own swaps and its end do)
        if (WeaponRoulette.Running && !WeaponRoulette.Swapping && ReferenceEquals(__instance, WeaponRoulette.Stats) && HandWeapon(itemObject?.data))
        {
            Log.Note("Twitch says: the roulette picks your weapon!");
            return false;
        }
        if (active == null || ours || !ReferenceEquals(__instance, mine) || !HandWeapon(itemObject?.data) || active.Fits(itemObject.data)) return true;
        Log.Note(active.kinds.Length == 0 ? "Twitch says: fists only!" : $"Twitch says: {Streamer.Def(active.Id)?.Name.ToLowerInvariant()}!");
        return false;
    }
}

/// <summary>
/// Weapon roulette: for its time (1.5 minutes), every 5 s your weapon swaps for one of a different type from your bag
/// (the first swap at once), never the same type twice in a row. With fewer than two other types, fists are a type in
/// the draw too, so it always changes. Meanwhile no
/// other weapon goes in your hand. When it ends, the weapon you had goes back in your hand (you had none: fists). Not
/// while a weapon lock is on (they'd fight).
/// </summary>
internal sealed class WeaponRoulette : IEffect
{
    public string Id => "roulette";
    const float Every = 5f;
    public static bool Running { get; private set; }
    /// <summary>Its own swap or its end under way (the lock lets those through).</summary>
    public static bool Swapping { get; private set; }
    /// <summary>Your stats when it began (the companion's are someone else's).</summary>
    public static CharacterStats Stats { get; private set; }
    static ItemObject original;
    static float next;

    public bool Available(out string why) { why = ""; return true; }

    public bool Ready(out string why)
    {
        if (!Effects.HavePlayer(out why)) return false;
        if (!WeaponLock.Bag) { why = "no bag"; return false; }
        if (WeaponLock.Locked) { why = "a weapon lock is on"; return false; }
        if (UIPanels.HasOpenedPanel) { why = "a screen is open"; return false; }
        return true;
    }

    public bool Fire(Pending p, float size)
    {
        if (Running) return true;   // (on already: the timer got longer)
        Running = true;
        Stats = Effects.Player.characterStats;
        original = InHand()?.itemObject;
        next = Time.time;   // (the first swap now)
        return true;
    }

    static InventoryItem InHand() => WeaponLock.Bag?.allItems.FirstOrDefault(x => x.section == InventorySection.Gear && WeaponLock.HandWeapon(x.itemObject?.data));

    /// <summary>Every frame (Plugin.Update): the swaps (not in a screen or a cutscene: then at the next chance).</summary>
    public static void Tick()
    {
        if (!Running || Time.time < next || !Streamer.CanFight()) return;
        next = Time.time + Every;
        var bag = WeaponLock.Bag;
        if (!bag) return;
        Swapping = true;
        try
        {
            var hand = InHand();
            var others = bag.allItems.Where(x => (x.section == InventorySection.Main || x.section == InventorySection.Belt) && WeaponLock.HandWeapon(x.itemObject?.data)).ToList();
            // the draw: a different type of weapon from the one in hand, every time (fists are a type too, when
            // there are fewer than two other types to go round)
            var types = others.Select(TypeOf).Distinct().ToList();
            if (hand != null && types.Count(t => t != TypeOf(hand)) < 2) types.Add(Fists);
            string type = WeaponTypes.Next(hand == null ? Fists : TypeOf(hand), types, k => Random.Range(0, k));
            if (type == "") return;   // (nothing of another type to swap to)
            if (type == Fists) { if (bag.TryToUnequip(GearSlot.MainHand, -1, InventorySection.Main)) Effects.Say(Effects.Player, "Fists!"); return; }
            var pick = others.Where(x => TypeOf(x) == type).ToList();
            var w = pick[Random.Range(0, pick.Count)];
            if (bag.TryToEquip(w.slotIndex, w.section, GearSlot.MainHand)) Effects.Say(Effects.Player, WeaponName(w.itemObject));
        }
        catch (System.Exception e) { Log.Warn($"Streaming: weapon roulette: {e.Message}"); }
        finally { Swapping = false; }
    }

    const string Fists = "fists";

    /// <summary>A weapon's type (the game's category: one-handed swords, bows, staffs...).</summary>
    static string TypeOf(InventoryItem i) => i.itemObject?.data is CollectableWeapon w && w.weapon ? w.weapon.weaponCategory.ToString() : Fists;

    static string WeaponName(ItemObject o)
    {
        try { var n = o?.data?.Name?.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return o?.data ? o.data.name : "Swap!";
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        var bag = WeaponLock.Bag;
        if (!bag) return;
        Swapping = true;
        try
        {
            var hand = InHand();
            if (original != null)
            {
                if (hand?.itemObject == original) return;
                var back = bag.allItems.FirstOrDefault(x => x.itemObject == original && x.section != InventorySection.Gear);
                if (back != null) bag.TryToEquip(back.slotIndex, back.section, GearSlot.MainHand);
            }
            else if (hand != null) bag.TryToUnequip(GearSlot.MainHand, -1, InventorySection.Main);   // (you had none)
        }
        catch (System.Exception e) { Log.Warn($"Streaming: weapon roulette: giving your weapon back: {e.Message}"); }
        finally { original = null; Swapping = false; Stats = null; }
    }
}

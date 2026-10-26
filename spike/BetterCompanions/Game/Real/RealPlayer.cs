using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BetterCompanions;

/// <summary>Marks a second, real player (ours): added before it wakes, so every patch can tell it from you.</summary>
internal sealed class RealCompanionMark : MonoBehaviour { }

/// <summary>
/// A second, real player: the game's own PlayerMaster with everything on it (movement, the state machine, attacks
/// with their hitboxes and projectiles, dodge, damage taken, effects), not a puppet. The game never makes a player
/// (each level has one placed in it), so when a level's player wakes we copy it once, untouched, into a hidden
/// template; a companion is a copy of that template, loaded from its own save (class, attributes, gear, looks).
/// Everywhere the game assumes there's only one player is handled in RealPatches (see
/// docs/research/2026-10-24-second-player-map.md for the map).
///
/// Step 1: it stands there (no brain yet). /companion real, /companion real off, /companion real up.
/// </summary>
internal static class RealPlayer
{
    static readonly HashSet<PlayerMaster> all = new();
    static GameObject template;

    public static PlayerMaster Current { get; private set; }
    public static bool Is(PlayerMaster p) => p && all.Contains(p);

    // ---- the template ----

    /// <summary>A level's player just woke (PlayerMaster.Awake): copy it now, before anything has changed it.</summary>
    public static void TakeTemplate(PlayerMaster p)
    {
        if (template || !p || !p.CompareTag("Player")) return;
        var holder = new GameObject("BC_PlayerTemplate");
        holder.SetActive(false);
        SceneManager.MoveGameObjectToScene(holder, p.gameObject.scene);   // (goes with the level, as the player does)
        template = UnityEngine.Object.Instantiate(p.gameObject, holder.transform);
        template.name = "BC_PlayerTemplate_Player";
        Log.Info($"Companion: second-player template taken in {p.gameObject.scene.name}");
    }

    /// <summary>A real player from this save, standing at there.</summary>
    public static string Spawn(PlayerData data, Vector3 at)
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me || data == null) return "Companion: no character to make it from.";
        if (!template) return "Companion: no template yet (it's taken when a level loads): change area and try again.";
        Remove();
        var holder = new GameObject("BC_RealHolder");
        holder.SetActive(false);
        SceneManager.MoveGameObjectToScene(holder, me.gameObject.scene);
        GameObject copy = null;
        try
        {
            copy = UnityEngine.Object.Instantiate(template, holder.transform);
            copy.name = "BC_RealCompanion";
            copy.transform.localPosition = at;   // (the holder sits at the origin: this is where it wakes)
            Prepare(copy, data);
            copy.transform.SetParent(null, false);   // (wakes up here: Awake, then Start next frame)
        }
        catch (Exception e)
        {
            // never leave a half-made one behind
            if (copy) UnityEngine.Object.Destroy(copy);
            Log.Warn($"Companion (real): making it: {e}");
            return "Companion: couldn't make the real companion (see the log).";
        }
        finally { UnityEngine.Object.Destroy(holder); }
        var p = copy.GetComponent<PlayerMaster>();
        if (!p) { UnityEngine.Object.Destroy(copy); return "Companion: the copy has no PlayerMaster."; }
        Current = p;
        pending[p] = data;
        // the two bodies don't shove each other in doorways
        if (me.bodyCollider && p.bodyCollider) Physics2D.IgnoreCollision(me.bodyCollider, p.bodyCollider);
        CompanionLook.Apply(copy.GetComponent<AddonsHandler>(), data);
        return $"Companion: {data.playerName} is here, for real (standing still for now).";
    }

    /// <summary>Before it wakes: marked, untagged, and the parts that only make sense for you taken off.</summary>
    static void Prepare(GameObject copy, PlayerData data)
    {
        copy.AddComponent<RealCompanionMark>();
        // nothing finds it as "the player" (enemies waking later, doors, teleporters, stairs, interactables)
        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
            if (t.CompareTag("Player") || t.CompareTag("PlayerCenter") || t.CompareTag("FarmSelector")) t.tag = "Untagged";
        foreach (var c in copy.GetComponentsInChildren<Component>(true))
        {
            if (!c) continue;
            string n = c.GetType().Name;
            // E/F and interactables; a second sound listener; your heartbeat; your speech bubbles; death marks
            // (their OnDestroy would clear your pools)
            if (n == "InteractablesHandler" || n == "AkAudioListener" || n == "HeartBeatController" || n == "PlayerTextReaction" || n == "ObjectSpawner")
                UnityEngine.Object.DestroyImmediate(c);
        }
        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
            if (t.name == "FarmingTriggers") t.gameObject.SetActive(false);
        int minimap = LayerMask.NameToLayer("Minimap");
        if (minimap >= 0) foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) if (r.gameObject.layer == minimap) r.enabled = false;
        var stats = copy.GetComponent<CharacterStats>();
        if (stats)
        {
            stats.isPlayer = false;   // (else it follows your save, your bag's weight and your speed)
            stats.owner = copy;
        }
        CompanionLook.OwnMaterials(copy);
    }


    public static void Woke(PlayerMaster p)
    {
        all.Add(p);
        p.inputActions = new GameInputActions();   // its own, never switched on: your keys don't drive it
    }

    static readonly Dictionary<PlayerMaster, PlayerData> pending = new();

    /// <summary>After its Start (the game's own setup done, fallback weapon and class on): now it becomes the character.</summary>
    public static void Started(PlayerMaster p)
    {
        if (!pending.TryGetValue(p, out var data)) return;
        pending.Remove(p);
        var stats = p.characterStats;
        p.ShowPlayer(true);   // (the template was copied while the level still had you hidden)
        string step = "start";
        try
        {
            step = $"stats {(stats ? "ok" : "missing")}, base class {(stats && stats.baseClass ? stats.baseClass.name : "missing")}, save's attributes {(data.playerPrimaryStats == null ? "missing" : data.playerPrimaryStats.Count.ToString())}";
            data.GetSavedPlayerStats(stats.primStatDict);
            step += ", attributes read";
            stats.baseClass.conversionTable.Calculate(stats.primStatDict, stats.secStatDict);
            step += ", worked out";
            stats.baseClassXP = data.GetPlayerExperience(stats.baseClass.playerClassType);
        }
        catch (Exception e) { Log.Warn($"Companion (real): stats ({step}): {e}"); }
        try
        {
            // (no combat class chosen yet, "None": it keeps the game's starting one)
            var combat = data.equippedClass == PlayerClassType.None ? null
                : Resources.FindObjectsOfTypeAll<PlayerCombatClass>().FirstOrDefault(c => c && c.playerClassType == data.equippedClass && !c.name.EndsWith("(Clone)"));
            if (combat) stats.EquipClass(CompanionTree.Own(combat, data));   // (its own copy, with its own skill tree)
        }
        catch (Exception e) { Log.Warn($"Companion (real): class: {e.Message}"); }
        FillUp(p, 1f);   // (until now -1: the first hit would have killed it)
        Dress(p, data);
    }

    static async void Dress(PlayerMaster p, PlayerData data)
    {
        try
        {
            var items = await data.GetPlayerInventory();
            if (items == null || !p) return;
            var stats = p.characterStats;
            int k = 0;
            using (RealPatches.Scope())
            {
                foreach (var item in items)
                {
                    if (item.section != InventorySection.Gear) continue;
                    stats.EquipItem(item.itemObject);
                    k++;
                    if (item.itemObject.data is CollectableGear g)
                    {
                        var vis = data.gearVisibleStates.FirstOrDefault(x => x.Slot == g.gear.gearSlot);
                        if (vis != null && !vis.IsVisible) stats.ShowGear(g.gear, false);
                    }
                }
            }
            if (p != Current) return;
            RealGear.Bag = items.Where(i => i.section == InventorySection.Main).ToList();
            RealGear.Other = items.Where(i => i.section != InventorySection.Main && i.section != InventorySection.Gear).ToList();
            if (RealGear.Spare == null) PickSpare();
            FillUp(p, 1f);
            Log.Info($"Companion (real): {data.playerName} ready: {k} items worn, {RealGear.Bag.Count} carried, spare {(RealGear.Spare != null ? RealGear.Spare.data.name : "none")}, HP {p.CurrentHealthPoints}/{(int)stats.GetSecondaryStatValue(SecondaryStat.HealthPoints)}, " +
                     $"weapon {(p.currentWeapon ? p.currentWeapon.name : "none")}, class {(stats.equippedClass ? stats.equippedClass.name : "none")}");
        }
        catch (Exception e) { Log.Warn($"Companion (real): dressing: {e}"); }
    }

    /// <summary>No spare weapon yet (an older save): the best weapon of the other kind it carries becomes it.</summary>
    static void PickSpare()
    {
        var held = Current.currentWeapon;
        var heldStyle = Core.Rules.StyleOf(held ? (int)held.weaponCategory : 14);
        var best = RealGear.Bag.Where(i => !RealGear.IsSwap(i.slotIndex) && i.itemObject.data is CollectableWeapon w && (w.weapon.gearSlot & GearSlot.MainHand) != 0
                                           && Core.Rules.StyleOf((int)w.weapon.weaponCategory) != heldStyle)
            .OrderByDescending(i => (int)i.itemObject.data.itemTier).FirstOrDefault();
        if (best == null) return;
        best.slotIndex = RealGear.SpareIndex;
        RealGear.Save(write: false);
    }

    /// <summary>Health and Kymia to this much of full (what LoadPlayerState does for you, without your HUD).</summary>
    static void FillUp(PlayerMaster p, float health01)
    {
        using (RealPatches.Scope())
        {
            var stats = p.characterStats;
            p.currentHealth = Mathf.Max(1, Mathf.RoundToInt(stats.GetSecondaryStatValue(SecondaryStat.HealthPoints) * health01));
            p.UpdateMaxCurrentKymia();
            p.currentKymia = (int)stats.GetSecondaryStatValue(SecondaryStat.KymiaPoints);
            p.lastSavedState.CurrentHealth = p.currentHealth;
            p.lastSavedState.CurrentKymia = p.currentKymia;
            p.UpdatePlayerHealthBar(p.HealthPercent, useBarAnimation: false, silentUpdate: true);
        }
    }

    // ---- down and up ----

    public static bool Down(PlayerMaster p) => p && p.currentState == p.stateDead;

    public static void WentDown(PlayerMaster p)
    {
        Hooks.Say($"{CompanionSave.Data?.playerName ?? "Your companion"} is down! Hold F next to them to revive.");
    }

    /// <summary>Its armour's share of XP, as the game gives yours (PlayerMaster.GainArmorXP): each piece's weight
    /// (light, medium, heavy), and "mixed" when it wears all three.</summary>
    public static void ArmorXP(PlayerData data, CharacterStats stats, int value)
    {
        int weights = 0;
        foreach (var armor in stats.GetArmors() ?? new System.Collections.Generic.List<CollectableArmor>())
        {
            if (!armor) continue;
            data.GetPlayerExperience(armor.Weight);
            data.AddXP(armor.Weight, value);
            weights |= 1 << (int)armor.Weight;
        }
        if (weights == 14) { data.GetPlayerExperience(GearWeight.Mixed); data.AddXP(GearWeight.Mixed, value); }
    }

    /// <summary>A kill's XP, as PlayerMaster.GainXP gives it, into the companion's own save (its class, combat class, weapon).</summary>
    public static int GainXP(PlayerMaster p, int value)
    {
        var data = CompanionSave.Data;
        if (data == null || p != Current) return value;
        value = Mathf.RoundToInt(value * Singleton<GameManager>.main.CurrentDifficulty.XpGained * 0.01f);
        var stats = p.characterStats;
        int before = data.GetCurrentLevel(stats.baseClass.playerClassType);
        void Add(object key) { data.GetPlayerExperience(key); data.AddXP(key, value); }   // (AddXP stumbles on a key it hasn't seen)
        try
        {
            Add(stats.baseClass.playerClassType);
            if (stats.equippedClass) Add(stats.equippedClass.playerClassType);
            if (p.currentWeapon) Add(p.currentWeapon.weaponCategory);
            ArmorXP(data, stats, value);
        }
        catch (Exception e) { Log.Warn($"Companion (real): XP: {e.Message}"); }
        CompanionSave.Dirty = true;
        int after = data.GetCurrentLevel(stats.baseClass.playerClassType);
        if (after > before)
        {
            stats.baseClassXP = data.GetPlayerExperience(stats.baseClass.playerClassType);
            p.PlayExtraVFX("LevelUp");
            Hooks.Say($"{data.playerName} reached level {after}!");
            CompanionSave.Write();
        }
        return value;
    }

    public static string Up()
    {
        var p = Current;
        if (!p) return "Companion: no real companion out.";
        if (!Down(p)) return "Companion: it isn't down.";
        foreach (var a in p.GetComponentsInChildren<Animator>(true)) if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) { a.Rebind(); a.Update(0f); }
        p.ChangeState(p.stateIdle);   // (PlayerDead.ExitState shows it again and turns its collider and controls back on)
        FillUp(p, 0.5f);
        return "Companion: back on its feet.";
    }

    public static string Remove()
    {
        var p = Current;
        Current = null;
        if (!p) return "Companion: no real companion out.";
        UnityEngine.Object.Destroy(p.gameObject);
        return "Companion: the real companion is gone.";
    }

    public static void Gone(PlayerMaster p)
    {
        all.Remove(p);
        pending.Remove(p);
        if (Current == p) { Current = null; RealGear.Forget(); }
    }
}

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The rules in build mode anywhere (B), outside the estate: placing is free (nothing taken from the bag or the
/// shared chests, so removing gives nothing back), there's no per-kind limit, and the grid turns red over walls,
/// water and the map's own buildings (what would stop the player) and inside a locked estate. On an unlocked estate
/// the game's own rules apply.
/// </summary>
[HarmonyPatch]
internal static class BuildRules
{
    /// <summary>Where the ghost is now (where a placement would go), or the player when not placing.</summary>
    static Vector3 Spot()
    {
        var c = BuildMode.Controller;
        if (c && c.isPlacing && c.currentPlaceableData) return c.lastSnapPosition + c.currentPlaceableData.anchor;
        return LocalPlayer.Get() ? LocalPlayer.Get().transform.position : Vector3.zero;
    }

    /// <summary>Our rules at this spot (B mode, not on an unlocked estate).</summary>
    static bool Ours(Vector3 at) => BuildMode.On && !(BuildMode.EstateUnlocked && BuildMode.InEstate(at));

    [HarmonyPatch(typeof(PlaceableData), nameof(PlaceableData.HasResources)), HarmonyPrefix]
    static bool Free(ref bool __result)
    {
        if (!Ours(Spot())) return true;
        __result = true;
        return false;
    }

    [HarmonyPatch(typeof(PlaceableData), nameof(PlaceableData.GetPossibleAmount)), HarmonyPrefix]
    static bool Plenty(ref int __result)
    {
        if (!Ours(Spot())) return true;
        __result = 99;
        return false;
    }

    [HarmonyPatch(typeof(PlaceableData), nameof(PlaceableData.ConsumeResources)), HarmonyPrefix]
    static bool NoCost() => !Ours(Spot());

    /// <summary>Nothing was paid for what's built outside the estate: nothing comes back.</summary>
    [HarmonyPatch(typeof(PlaceableData), nameof(PlaceableData.ReturnResources)), HarmonyPrefix]
    static bool NoRefund(Vector3 position) => !(BuildMode.On && !BuildMode.InEstate(position)) && !BuildSave.IsOursAt(position);

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.GetPlacedAmount)), HarmonyPrefix]
    static bool NoLimit(ref int __result)
    {
        if (!Ours(Spot())) return true;
        __result = 0;
        return false;
    }

    // ---- not over walls, water, the map's buildings; not on a locked estate ----

    static readonly List<Collider2D> hits = new List<Collider2D>();

    /// <summary>What the walls check last found in the way (for build mode's "can't place here").</summary>
    public static string LastBlocker;

    [HarmonyPatch(typeof(PlacementObjectComponent), nameof(PlacementObjectComponent.CanPlace)), HarmonyPostfix]
    static void NotInWalls(PlacementObjectComponent __instance, ref bool __result)
    {
        if (!__result || !BuildMode.On) return;
        var at = __instance.transform.position;
        if (BuildMode.InEstate(at))
        {
            if (!BuildMode.EstateUnlocked) __result = false;
            return;   // (an unlocked estate: the game's own check)
        }
        // only world objects outside: farm plots, fences, roads are tiles painted on the estate's own layers
        var ctl = BuildMode.Controller;
        if (ctl && ctl.currentPlaceableData && !(ctl.currentPlaceableData is PlaceableWorldObjectData)) { __result = false; return; }
        var bounds = __instance.placementBounds;
        int mask = PlayerMask();
        if (!bounds || mask == 0) return;
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = mask };
        hits.Clear();
        Physics2D.OverlapCollider(bounds, filter, hits);
        foreach (var c in hits)
        {
            if (!c || c.isTrigger || c.transform.IsChildOf(__instance.transform)) continue;
            if (c.attachedRigidbody && c.attachedRigidbody.bodyType != RigidbodyType2D.Static) continue;   // (creatures, players: things that move)
            if (c.GetComponentInParent<Enemy>() || c.GetComponentInParent<PlayerMaster>()) continue;
            LastBlocker = $"{c.name} (layer {LayerMask.LayerToName(c.gameObject.layer)})";
            __result = false;
            return;
        }
    }

    /// <summary>The layers the player's body bumps into (walls, water, buildings).</summary>
    static int PlayerMask()
    {
        var me = LocalPlayer.Get();
        if (!me) return 0;
        Collider2D body = null;
        if (me.rb) foreach (var c in me.rb.GetComponents<Collider2D>()) if (c && !c.isTrigger) { body = c; break; }
        return Physics2D.GetLayerCollisionMask(body ? body.gameObject.layer : me.gameObject.layer);
    }
}

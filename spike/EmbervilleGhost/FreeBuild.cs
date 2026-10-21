using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// What can be placed where the game has no build controller (the demo has none at all: not in the tutorial area,
/// not in Town): the world objects the game has loaded (chests, stations, furniture), from its blueprint database if
/// that's loaded, else loose. Not trees, plots or buildings: placing those needs the estate's controller.
/// </summary>
internal static class BuildCatalog
{
    static List<PlaceableWorldObjectData> all = new List<PlaceableWorldObjectData>();
    static float refreshedAt = -10f;

    public static List<PlaceableWorldObjectData> All(bool refresh = false)
    {
        if (refresh || all.Count == 0 && Time.unscaledTime - refreshedAt > 1f) Refresh();
        return all;
    }

    public static PlaceableData Get(string id)
    {
        var c = BuildMode.Controller;
        var d = c ? c.dataBase.Get(id) : null;
        if (d) return d;
        d = all.FirstOrDefault(p => p && p.saveID == id);
        if (!d && Time.unscaledTime - refreshedAt > 1f) { Refresh(); d = all.FirstOrDefault(p => p && p.saveID == id); }
        return d;
    }

    /// <summary>Needs the estate's controller once placed (Place hands it to the controller): left out without one.</summary>
    public static bool NeedsController(PlaceableData d) =>
        d is PlaceableWorldObjectData w && w.placeObject && w.placeObject.GetComponent(typeof(IRemoveFromSave));

    static void Refresh()
    {
        refreshedAt = Time.unscaledTime;
        var found = new List<PlaceableWorldObjectData>();
        foreach (var db in Resources.FindObjectsOfTypeAll<PlaceablesDataBase>())
            found.AddRange(db.placeables.OfType<PlaceableWorldObjectData>());
        found.AddRange(Resources.FindObjectsOfTypeAll<PlaceableWorldObjectData>());
        all = found.Where(p => p && p.placeObject && p.ghostObject && !string.IsNullOrEmpty(p.saveID) && !NeedsController(p))
            .GroupBy(p => p.saveID).Select(g => g.First())
            .OrderBy(p => p.type).ThenBy(Name).ToList();
    }

    public static string Name(PlaceableData p)
    {
        try { var n = p.GetDisplayName(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return p.name.StartsWith("Place_") ? p.name.Substring(6) : p.name;
    }
}

/// <summary>
/// Build mode (B) without the game's build controller: our own small placer. A panel of what can be placed; the
/// object's own ghost on the mouse, green where it fits and red where it doesn't (the game's check plus BuildRules'
/// walls), left click place, wheel turn, right click / Esc stop; Remove takes back what was built this way. Free.
/// A placeable item used from the bag (a chest) starts placing it here too. Saved and synced as everything built
/// anywhere (BuildSave, PlaceSync).
/// </summary>
[HarmonyPatch]
internal static class FreeBuild
{
    public static bool Active { get; private set; }
    static PlaceableWorldObjectData placing;
    static GameObject ghost;
    static TurnableObject turnable;
    static IPlaceable ghostPlace;
    static Vector3 snap;
    static bool fresh, canPlace, removing;
    static string why;   // what's in the way, when the ghost is red
    static PlacementObjectComponent hovered;
    static readonly Dictionary<SpriteRenderer, Color> hoverColours = new Dictionary<SpriteRenderer, Color>();
    static List<PlaceableWorldObjectData> list = new List<PlaceableWorldObjectData>();
    static bool hadAttacks, hadSkills;

    static readonly Color Fits = new Color(0.55f, 1f, 0.55f, 0.8f), Blocked = new Color(1f, 0.4f, 0.4f, 0.8f);

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    public static void Open()
    {
        if (Active) return;
        Active = true;
        list = BuildCatalog.All(refresh: true);
        var me = Me;
        if (me) { hadAttacks = me.CanPerfomAttacks; hadSkills = me.CanPerformSkills; }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        var hidden = Resources.FindObjectsOfTypeAll<PlaceableController>();
        Plugin.Log.LogInfo($"Build mode (our own placer): {list.Count} placeables loaded; inactive build controllers in memory: {hidden.Length}");
    }

    public static void Close()
    {
        if (!Active) return;
        StopPlacing();
        StopRemoving();
        Active = false;
        var me = Me;
        if (me) { me.CanPerfomAttacks = hadAttacks; me.CanPerformSkills = hadSkills; }
    }

    public static void Start(PlaceableWorldObjectData data)
    {
        StopPlacing();
        StopRemoving();
        if (!data || !data.ghostObject) return;
        placing = data;
        ghost = UnityEngine.Object.Instantiate(data.InitializeAndGetGhostObject());
        ghost.SetActive(true);
        ghost.TryGetComponent(out turnable);
        if (ghost.TryGetComponent(out ghostPlace)) ghostPlace.DisableColliders();
        fresh = true;
        Plugin.Log.LogInfo($"Build mode: placing {data.saveID}, ghost {ghost.name}: {ghost.GetComponentsInChildren<SpriteRenderer>().Length} sprites, placement check: {ghostPlace != null}");
    }

    static void StopPlacing()
    {
        if (ghost) UnityEngine.Object.Destroy(ghost);
        ghost = null;
        placing = null;
        turnable = null;
        ghostPlace = null;
    }

    static void StopRemoving()
    {
        Unhover();
        removing = false;
    }

    /// <summary>Every frame while it's on (BuildMode.Tick).</summary>
    public static void Tick()
    {
        if (!Active) return;
        var me = Me;
        if (me) { me.CanPerfomAttacks = false; me.CanPerformSkills = false; }   // (a click places, it doesn't swing)
        var mouse = Mouse.current;
        var cam = Camera.main;
        if (mouse == null || !cam) return;
        bool overPanel = ImGui.GetIO().WantCaptureMouse;
        var kb = Keyboard.current;
        bool stop = mouse.rightButton.wasPressedThisFrame || kb != null && kb.escapeKey.wasPressedThisFrame;
        Vector3 world = cam.ScreenToWorldPoint(mouse.position.ReadValue());
        world.z = 0f;

        if (placing)
        {
            if (!ghost) { StopPlacing(); return; }
            if (stop) { StopPlacing(); return; }
            float s = placing.snapScale > 0f ? placing.snapScale : 1f;
            var at = new Vector3(Mathf.Floor(world.x / s) * s, Mathf.Floor(world.y / s) * s, 0f);
            bool turned = false;
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel != 0f && turnable && !overPanel) { turnable.Rotate(wheel > 0f ? 1 : -1); turned = true; }
            if (fresh || turned || at != snap)
            {
                fresh = false;
                snap = at;
                ghost.transform.position = at + placing.anchor;
                Recheck();
            }
            if (mouse.leftButton.wasPressedThisFrame && !overPanel)
            {
                if (canPlace) Place();
                else { NetLog.Info($"Can't place it here: {why}", toast: true); Plugin.Log.LogInfo($"Build mode: refused at {snap}: {why}"); }
            }
        }
        else if (removing)
        {
            if (stop) { StopRemoving(); return; }
            var under = overPanel ? null : Under(world);
            if (under != hovered)
            {
                Unhover();
                hovered = under;
                if (under) foreach (var r in under.GetComponentsInChildren<SpriteRenderer>()) { hoverColours[r] = r.color; r.color = Blocked; }
            }
            if (mouse.leftButton.wasPressedThisFrame && !overPanel && hovered) Remove(hovered);
        }
        else if (mouse.leftButton.wasPressedThisFrame && !overPanel)
            NetLog.Info("Pick something to place in the Build panel first", toast: true);
    }

    static void Recheck()
    {
        BuildRules.LastBlocker = null;
        canPlace = ghostPlace == null || ghostPlace.CanPlace(placing.anyTagLayers, placing.withTagLayers, placing.tagToCollide, false);
        why = canPlace ? null : BuildRules.LastBlocker != null ? "a wall or the map's own " + BuildRules.LastBlocker : GameBlocker();
        foreach (var r in ghost.GetComponentsInChildren<SpriteRenderer>()) r.color = canPlace ? Fits : Blocked;
    }

    /// <summary>What the game's own check (PlacementObjectComponent.CanPlace) found in the way, by name.</summary>
    static string GameBlocker()
    {
        if (!(ghostPlace is PlacementObjectComponent poc) || !poc.placementBounds) return "something in the way";
        var filter = new ContactFilter2D { useTriggers = true, useLayerMask = true, layerMask = (int)placing.withTagLayers | (int)placing.anyTagLayers };
        var hits = new List<Collider2D>();
        Physics2D.OverlapCollider(poc.placementBounds, filter, hits);
        foreach (var col in hits)
        {
            if (!col || poc.ignoreColliders.Contains(col)) continue;
            bool any = ((1 << col.gameObject.layer) & (int)placing.anyTagLayers) != 0;
            if (any || !string.IsNullOrEmpty(placing.tagToCollide) && col.CompareTag(placing.tagToCollide))
                return $"{col.name} (layer {LayerMask.LayerToName(col.gameObject.layer)}, tag {col.tag})";
        }
        return "something in the way";
    }

    static void Place()
    {
        var data = placing;
        var pos = snap + data.anchor;
        var dir = turnable ? turnable.CurrentDirection : Direction.Down;
        (bool continuous, string saveId) placed;
        try { placed = data.Place(pos, dir, null, BuildSave.Parent); }
        catch (Exception e) { NetLog.Warn($"Build mode: placing '{data.saveID}': {e.Message}"); return; }
        BuildSave.Adopt(null, data, pos, dir, placed.saveId);
        PlaceSync.SendPlaced(data.saveID, pos, dir, placed.saveId, anywhere: true);
        try { if (Me) data.sfxOnPlace.Post(Me.gameObject); } catch { }
        if (!placed.continuous) { StopPlacing(); return; }
        Recheck();
    }

    /// <summary>Something built this way under the mouse (the smallest, where several overlap).</summary>
    static PlacementObjectComponent Under(Vector3 at)
    {
        var parent = BuildSave.Existing;
        if (!parent) return null;
        PlacementObjectComponent best = null;
        float bestArea = float.MaxValue;
        foreach (var comp in parent.GetComponentsInChildren<PlacementObjectComponent>())
            foreach (var r in comp.GetComponentsInChildren<SpriteRenderer>())
            {
                var b = r.bounds;
                if (at.x < b.min.x || at.x > b.max.x || at.y < b.min.y || at.y > b.max.y) continue;
                float area = b.size.x * b.size.y;
                if (area < bestArea) { bestArea = area; best = comp; }
            }
        return best;
    }

    static void Unhover()
    {
        foreach (var kv in hoverColours) if (kv.Key) kv.Key.color = kv.Value;
        hoverColours.Clear();
        hovered = null;
    }

    static void Remove(PlacementObjectComponent comp)
    {
        string id = comp.RelatedPlaceableDataID ?? "";
        var at = comp.transform.position;
        Unhover();
        BuildSave.Unrecord(LocalPlayer.Scene, id, at);
        try { comp.OnRemove(); } catch (Exception e) { NetLog.Warn($"Build mode: removing '{id}': {e.Message}"); }
        UnityEngine.Object.Destroy(comp.gameObject);
        try { var data = BuildCatalog.Get(id); if (data && Me) data.sfxOnRemove.Post(Me.gameObject); } catch { }
        PlaceSync.SendRemoved(id, at);
    }

    // ---- the panel ----

    public static void Draw()
    {
        if (!Active) return;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + 16, vp.WorkPos.y + vp.WorkSize.y * 0.5f), ImGuiCond.Appearing, new Vector2(0f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(312, 470), ImGuiCond.Appearing);
        bool open = true;
        if (ImGui.Begin($"Build   ({BuildMode.KeyName} to leave)###freebuild", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            var grey = new Vector4(0.75f, 0.75f, 0.75f, 1f);
            if (placing) ImGui.TextColored(grey, $"{BuildCatalog.Name(placing)}: left click place, wheel turn, right click stop");
            else if (removing) ImGui.TextColored(grey, "Click something you built to take it away; right click stop");
            else ImGui.TextColored(grey, "Pick something to place");
            if (ImGui.Button(removing ? "Stop removing" : "Remove")) { if (removing) StopRemoving(); else { StopPlacing(); removing = true; } }
            ImGui.Separator();
            ImGui.BeginChild("items");
            if (list.Count == 0) ImGui.TextWrapped("Nothing placeable is loaded in this area.");
            const float cell = 64f;
            int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().x + 6) / (cell + 6)));
            int i = 0;
            foreach (var p in list)
            {
                if (!p) continue;
                if (i % perRow != 0) ImGui.SameLine();
                i++;
                ImGui.PushID(p.saveID);
                var at = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable("##p", placing == p, ImGuiSelectableFlags.None, new Vector2(cell, cell))) Start(p);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(BuildCatalog.Name(p));
                var icon = p.uiIcon ? p.uiIcon : p.uiBigIcon;
                if (icon)
                {
                    float w = icon.rect.width, h = icon.rect.height, fit = (cell - 8) / Mathf.Max(w, h);
                    var a = new Vector2(at.x + (cell - w * fit) / 2, at.y + (cell - h * fit) / 2);
                    Bestiary.Sprite(ImGui.GetWindowDrawList(), icon, a, new Vector2(a.x + w * fit, a.y + h * fit), 0xFFFFFFFF, false);
                }
                else ImGui.GetWindowDrawList().AddText(new Vector2(at.x + 4, at.y + cell / 2 - 8), 0xFFFFFFFF, BuildCatalog.Name(p));
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        ImGui.End();
        if (!open) BuildMode.Leave();
    }

    // ---- a placeable item used from the bag (a chest): placed here, where the game would say "Can't use it here" ----

    [HarmonyPatch(typeof(ItemAction.StartPlacingItem), nameof(ItemAction.StartPlacingItem.Execute)), HarmonyPrefix]
    static bool FromBag(ItemAction.StartPlacingItem __instance)
    {
        if (!BuildMode.Enabled || BuildMode.Controller || !(__instance.placeable is PlaceableWorldObjectData data) || BuildCatalog.NeedsController(data)) return true;
        if (!BuildMode.TryOn()) return true;
        UIPanels.CloseAll();
        if (Active) Start(data);
        return false;
    }
}

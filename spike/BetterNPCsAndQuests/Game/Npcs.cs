using System.Collections.Generic;
using System.Linq;
using BetterNPCsAndQuests.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BetterNPCsAndQuests;

/// <summary>One NPC standing in the current scene.</summary>
internal sealed class Npc
{
    public NpcDef Def;
    public NpcBody Body;
    public Vector3 Home;
    public TalkSpot Spot;
    public Wander.Plan Walk = new();
}

/// <summary>
/// The NPCs of the current scene: built when the scene loads or the files are read again, from the defs whose scene is
/// this one (coordinates only, fixed scenes only). Each joins the game's interact list (F: talk), like Rory and chests.
/// </summary>
internal static class Npcs
{
    static readonly List<Npc> here = new();
    static string builtFor;
    static bool dirty = true, hooked;

    public static IReadOnlyList<Npc> Here => here;

    public static Npc Find(string id) => here.FirstOrDefault(n => n.Def.Id.Equals(id, System.StringComparison.OrdinalIgnoreCase));

    public static void Tick()
    {
        if (!hooked)
        {
            hooked = true;
            SceneManager.sceneLoaded += (_, _) => dirty = true;
            State.Reloaded += () => dirty = true;
        }
        if (!State.Loaded) return;
        var me = LocalPlayer.Get();
        if (!me) return;
        string scene = LocalPlayer.Scene;
        if (dirty || scene != builtFor || here.Any(n => n.Body.Lost)) Build(scene);
        foreach (var n in here)
        {
            if (n.Spot) n.Spot.PickLayer();
            n.Body.DressGear();
            n.Body.ShowName(!Hooks.DrawsLabels);
            Wander.Tick(n);
        }
    }

    static void Build(string scene)
    {
        Clear();
        dirty = false;
        builtFor = scene;
        foreach (var def in State.Lib.Npcs.Where(d => d.Scene.Equals(scene, System.StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var at = new Vector3(def.X, def.Y, 0f);
                var body = NpcBody.Create(def, at);
                if (body == null) continue;
                var n = new Npc { Def = def, Body = body, Home = at };
                n.Spot = TalkSpot.Add(n);
                here.Add(n);
                Log.Info($"NPCs and quests: {def.Name} at ({at.x:0.0}, {at.y:0.0}), talk area on layer {LayerMask.LayerToName(n.Spot.gameObject.layer)} ({n.Spot.gameObject.layer}); you are at {(Vector2)LocalPlayer.Get().transform.position}");
            }
            catch (System.Exception e) { Log.Warn($"NPCs and quests: can't place {def.Name}: {e.Message}"); }
        }
        if (here.Count > 0) Log.Info($"NPCs and quests: {string.Join(", ", here.Select(n => n.Def.Name))} in {scene}");
    }

    public static void Clear()
    {
        foreach (var n in here) n.Body.Destroy();
        here.Clear();
        builtFor = null;
    }

    /// <summary>For the Insert tab: where you stand, and whether an NPC could stand there.</summary>
    public static (string scene, Vector2 pos, bool fixedScene) Readout()
    {
        var me = LocalPlayer.Get();
        string scene = LocalPlayer.Scene;
        var p = me ? (Vector2)me.transform.position : Vector2.zero;
        return (scene, p, !Parse.NotFixedScenes.Any(s => s.Equals(scene, System.StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>The game's interact for an NPC: on the player's list while they stand by them (F talks).</summary>
internal sealed class TalkSpot : MonoBehaviour, IInteractable
{
    public Npc Npc;
    public bool Near;

    public static TalkSpot Add(Npc npc)
    {
        var go = new GameObject("BNQ_Talk");
        go.transform.SetParent(npc.Body.Root.transform, false);
        var circle = go.AddComponent<CircleCollider2D>();
        circle.isTrigger = true;
        circle.radius = 1.6f;
        circle.offset = new Vector2(0f, 0.6f);
        var spot = go.AddComponent<TalkSpot>();
        spot.Npc = npc;
        spot.PickLayer();
        return spot;
    }

    bool sure;   // the layer came from a chest (the one the game's interact check sees)
    float nextTry;

    /// <summary>
    /// The layer the game's interact check sees (it finds us when the player's "PlayerCenter" shape enters our circle):
    /// a chest's, like Rory's. Early in loading there may be no chest yet: then a layer that touches the player's
    /// PlayerCenter one, and again with a chest's as soon as there is one (Npcs.Tick).
    /// </summary>
    public void PickLayer()
    {
        if (sure || Time.unscaledTime < nextTry) return;
        nextTry = Time.unscaledTime + 1f;   // (looked for once a second)
        var chest = Object.FindAnyObjectByType<Chest>(FindObjectsInactive.Include);
        if (chest) { gameObject.layer = chest.gameObject.layer; sure = true; return; }
        var me = LocalPlayer.Get();
        var center = me ? System.Array.Find(me.GetComponentsInChildren<Collider2D>(true), c => c && c.CompareTag("PlayerCenter")) : null;
        if (!center) return;
        int theirs = center.gameObject.layer;
        if (!Physics2D.GetIgnoreLayerCollision(gameObject.layer, theirs)) return;
        for (int l = 0; l < 32; l++)
            if (!string.IsNullOrEmpty(LayerMask.LayerToName(l)) && !Physics2D.GetIgnoreLayerCollision(l, theirs)) { gameObject.layer = l; return; }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.CompareTag("PlayerCenter") || !other.attachedRigidbody || !other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h)) return;
        h.AddInteractable(transform, this);
        Log.Info($"NPCs and quests: in reach of {Npc.Def.Name}");
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (!other.CompareTag("PlayerCenter") || !other.attachedRigidbody || !other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h)) return;
        h.RemoveInteractable(transform);
    }

    public void OnEnableInteraction(GameObject owner) { Near = true; Log.Info($"NPCs and quests: {Npc.Def.Name} can be talked to (F)"); }
    public void OnDisableInteraction(GameObject owner) => Near = false;
    public void OnInteract(GameObject owner) { Log.Info($"NPCs and quests: F on {Npc.Def.Name}"); Talk.Open(Npc); }
    public bool CanInteract() => !Talk.IsOpen;
    public bool CanRepeat(out float interval) { interval = 0f; return false; }
}

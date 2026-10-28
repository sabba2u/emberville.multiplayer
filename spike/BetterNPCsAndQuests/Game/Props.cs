using System.Linq;
using BetterNPCsAndQuests.Core;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// "prop = CampTent_Square_Big_Front Variant, 0, 2.5" in npc.txt: one of the game's own objects (a tent, a crate...)
/// set down by the NPC, as it is in the game's own rooms: its colliders too, so you walk around it. The dungeon rooms'
/// pieces come in with the game's biomes (loaded here once). It goes when the NPC does.
/// </summary>
internal static class Props
{
    static bool loaded;

    public static void Place(NpcDef def, Transform npc)
    {
        if (!loaded) { loaded = true; try { Resources.Load("AllBiomes"); } catch (System.Exception e) { Log.Warn($"NPCs and quests: loading the biomes: {e.Message}"); } }
        var prefab = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g && g.name == def.Prop && !g.scene.IsValid());
        if (!prefab) { Log.Warn($"NPCs and quests: {def.Name}'s prop \"{def.Prop}\" isn't in the game"); return; }
        var holder = new GameObject("BNQ_prop_holder");
        holder.SetActive(false);   // (its scripts don't wake up before it's in place)
        var prop = Object.Instantiate(prefab, holder.transform);
        prop.name = "BNQ_prop_" + def.Id;
        prop.transform.SetParent(npc, false);
        prop.transform.position = npc.position + new Vector3(def.PropX, def.PropY, 0f);
        Object.Destroy(holder);
    }
}

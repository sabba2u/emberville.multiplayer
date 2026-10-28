using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterNPCsAndQuests.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BetterNPCsAndQuests;

/// <summary>
/// An NPC's body: a copy of the local player's character with the game's scripts, physics and colliders stripped (the
/// way the multiplayer mod makes teammates' stand-ins, and Rory), dressed from npc.txt (hair, outfit weight, what they
/// hold), with a name label over the head. Walked by setting the animator's own Speed / DirFactor and turned with the
/// character's flip pivots.
/// </summary>
internal sealed class NpcBody
{
    public GameObject Root { get; private set; }
    public NpcDef Def { get; private set; }
    public bool Lost => !Root;

    Animator[] animators;
    readonly List<Transform> flipPivots = new();
    TextMeshPro label;
    bool dressed;
    Transform weaponFolder;
    readonly Dictionary<GearSlot, List<Transform>> equip = new();
    readonly List<GameObject> hairFolders = new();

    public static NpcBody Create(NpcDef def, Vector3 at)
    {
        var me = LocalPlayer.Get();
        if (!me) return null;
        var holder = new GameObject("BNQ_holder");
        holder.SetActive(false);
        GameObject copy;
        try
        {
            copy = UnityEngine.Object.Instantiate(me.gameObject, holder.transform);
            copy.name = "BNQ_" + def.Id;
            Strip(copy);
            copy.transform.SetParent(null, true);
        }
        finally { UnityEngine.Object.Destroy(holder); }

        var body = new NpcBody { Root = copy, Def = def };
        body.animators = copy.GetComponentsInChildren<Animator>(true);
        foreach (var a in body.animators)
        {
            if (!a) continue;
            a.fireEvents = false;
            PuppetStateBehaviours.Puppets.Add(a);
        }
        foreach (var pivot in me.charPivotFlip)
        {
            var found = Find(me, copy, pivot);
            if (found) body.flipPivots.Add(found);
        }
        // their minimap marker would read as another player: hide it
        int minimap = LayerMask.NameToLayer("Minimap");
        if (minimap >= 0) foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) if (r.gameObject.layer == minimap) r.enabled = false;
        copy.transform.position = at;
        if (def.Sheet != null && !SheetBody.Dress(copy, def))
            Log.Warn($"NPCs and quests: {def.Name}'s sheet \"{def.Sheet}\" isn't loaded here (Atwor_Anim comes with TutorialArea): looks like you for now");
        if (def.Prop != null) Props.Place(def, copy.transform);
        body.DressHair(me, copy);
        body.Label(copy, def);
        copy.SetActive(true);
        body.Facing(def.Facing);
        return body;
    }

    static Transform Find(PlayerMaster me, GameObject copy, Transform original)
    {
        var path = PathFrom(me.transform, original);
        return path == null ? null : path.Length == 0 ? copy.transform : copy.transform.Find(path);
    }

    // ---- looks ----

    void DressHair(PlayerMaster me, GameObject copy)
    {
        var addons = me.GetComponentInChildren<AddonsHandler>(true);
        if (!addons) return;
        if (Def.Hair >= 0 && addons.hairAssets.Count > 0)
        {
            var hair = addons.hairAssets[Def.Hair % addons.hairAssets.Count];
            ShowIn(me, copy, addons, AddonSlot.HairOverlay, hair.overlayHairIndex);
            ShowIn(me, copy, addons, AddonSlot.HairFront, hair.frontHairIndex);
            ShowIn(me, copy, addons, AddonSlot.HairBehind, hair.behindHairIndex);
        }
        if (addons.weaponFolder) weaponFolder = Find(me, copy, addons.weaponFolder);
        foreach (var ef in addons.equipFolders)
        {
            if (ef.folders == null) continue;
            equip[ef.slot] = ef.folders.Select(f => Find(me, copy, f)).Where(t => t).ToList();
        }
        foreach (var h in addons.hairFolders)
        {
            var t = h ? Find(me, copy, h.transform) : null;
            if (t) hairFolders.Add(t.gameObject);
        }
    }

    static void ShowIn(PlayerMaster me, GameObject copy, AddonsHandler addons, AddonSlot slot, int index)
    {
        var af = addons.addonFolders.Find(f => f.slot == slot);
        if (af.folders == null) return;
        foreach (var f in af.folders)
        {
            var mine = Find(me, copy, f);
            if (!mine) continue;
            for (int i = 0; i < mine.childCount; i++) mine.GetChild(i).gameObject.SetActive(i == index);
        }
    }

    void Wear(GearSlot slot, int layer)
    {
        if (!equip.TryGetValue(slot, out var folders)) return;
        foreach (var f in folders)
            for (int i = 0; i < f.childCount; i++) f.GetChild(i).gameObject.SetActive(i == layer);
        if (slot == GearSlot.Head) foreach (var h in hairFolders) if (h) h.SetActive(layer < 0);
    }

    /// <summary>Outfit and held weapon need the item catalogue: done once it's loaded (from Npcs.Tick).</summary>
    public void DressGear()
    {
        if (dressed || !Items.Ready || !Root) return;
        dressed = true;
        try
        {
            var weight = Def.Outfit switch { "medium" => GearWeight.Medium, "heavy" => GearWeight.Heavy, "none" => GearWeight.None, _ => GearWeight.Light };
            var armour = Items.Pool.OfType<CollectableArmor>().Where(a => a.gear != null && a.gear.layerIndex >= 0).OrderBy(a => a.name).ToList();
            Wear(GearSlot.Head, -1);
            foreach (var slot in new[] { GearSlot.UpperBody, GearSlot.LowerBody, GearSlot.Cloak })
            {
                var a = weight == GearWeight.None ? null
                    : armour.FirstOrDefault(x => x.Weight == weight && (x.gear.gearSlot & slot) != 0) ?? armour.FirstOrDefault(x => (x.gear.gearSlot & slot) != 0);
                Wear(slot, a != null ? a.gear.layerIndex : -1);
            }
            if (!weaponFolder) return;
            int index = -1;
            string hold = (Def.Holding ?? "none").Trim();
            if (!hold.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                var spec = Parse.Categories.Contains(hold.ToLowerInvariant()) ? new ItemSpec { Category = hold.ToLowerInvariant() } : new ItemSpec { Name = hold };
                var w = Items.Pool.OfType<CollectableWeapon>()
                    .FirstOrDefault(x => x.weapon != null && x.weapon.weaponIndex >= 0 && x.weapon.weaponIndex < weaponFolder.childCount && Items.Matches(x, spec));
                if (w != null) index = w.weapon.weaponIndex;
                else Log.Warn($"NPCs and quests: {Def.Name} can't hold \"{hold}\" (no such weapon)");
            }
            for (int i = 0; i < weaponFolder.childCount; i++) weaponFolder.GetChild(i).gameObject.SetActive(i == index);
        }
        catch (Exception e) { Log.Warn($"NPCs and quests: dressing {Def.Name}: {e.Message}"); }
    }

    void Label(GameObject copy, NpcDef def)
    {
        var go = new GameObject("BNQ_Name");
        go.transform.SetParent(copy.transform, false);
        go.transform.localPosition = new Vector3(0f, 2.55f, 0f);
        label = go.AddComponent<TextMeshPro>();
        var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderByDescending(f => f.name.IndexOf("Ember", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
        if (font) label.font = font;
        label.text = def.Name;
        label.fontSize = 3f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = ColorUtility.TryParseHtmlString(def.Color, out var c) ? c : Color.white;
        label.outlineWidth = 0.2f;
        label.outlineColor = new Color32(0, 0, 0, 200);
        label.rectTransform.sizeDelta = new Vector2(8f, 1f);
        var like = copy.GetComponentsInChildren<SpriteRenderer>(true).FirstOrDefault();
        var mr = label.GetComponent<MeshRenderer>();
        if (like && mr) { mr.sortingLayerID = like.sortingLayerID; mr.sortingOrder = 32000; }
    }

    public void ShowName(bool on) { if (label) label.gameObject.SetActive(on); }

    // ---- moving ----

    public void Walk(Vector3 dir, float speed)
    {
        if (!Root) return;
        Root.transform.position += dir.normalized * speed * Time.deltaTime;
        Turn(dir);
        SetFloat("Speed", speed);
    }

    public void Stop() => SetFloat("Speed", 0f);

    public void Face(Vector3 lookAt) { if (Root) Turn(lookAt - Root.transform.position); }

    public void FaceFront() { if (Root) Turn(Vector3.down); }

    void Facing(string facing)
    {
        switch (facing)
        {
            case "up": Turn(Vector3.up); break;
            case "left": Turn(Vector3.left); break;
            case "right": Turn(Vector3.right); break;
            default: Turn(Vector3.down); break;
        }
    }

    void Turn(Vector3 dir)
    {
        if (dir.sqrMagnitude < 0.0001f) return;
        bool side = Mathf.Abs(dir.x) > Mathf.Abs(dir.y);
        SetFloat("DirFactor", side ? 0f : dir.y > 0 ? 1f : -1f);
        if (side) foreach (var p in flipPivots) if (p) p.rotation = Quaternion.Euler(0f, dir.x < 0 ? 180f : 0f, 0f);
        if (label) label.transform.rotation = Quaternion.identity;
    }

    void SetFloat(string name, float value)
    {
        int hash = Animator.StringToHash(name);
        foreach (var a in animators)
        {
            if (!a || !a.isActiveAndEnabled || !a.runtimeAnimatorController) continue;
            foreach (var p in a.parameters)
                if (p.nameHash == hash && p.type == AnimatorControllerParameterType.Float) { a.SetFloat(hash, value); break; }
        }
    }

    public void Destroy()
    {
        if (animators != null) foreach (var a in animators) PuppetStateBehaviours.Puppets.Remove(a);
        PuppetStateBehaviours.Puppets.RemoveWhere(a => !a);
        if (Root) UnityEngine.Object.Destroy(Root);
        Root = null;
    }

    // ---- the copy (as the multiplayer mod's RemoteAvatar does it) ----

    static void Strip(GameObject copy)
    {
        foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "Untagged";
        // the player's dyes (the multiplayer mod's GearDye marks its tinted sprites) off: the NPC wears their own colours
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb && mb.GetType().FullName == "EmbervilleGhost.GearDye+Mark" && mb.GetComponent<SpriteRenderer>() is SpriteRenderer sr)
                sr.color = new Color(1f, 1f, 1f, sr.color.a);
        for (int pass = 0; pass < 4; pass++)
        {
            bool any = false;
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb || IsUnityVisual(mb.GetType()) || Required(mb)) continue;
                UnityEngine.Object.DestroyImmediate(mb);
                any = true;
            }
            if (!any) break;
        }
        foreach (var c in copy.GetComponentsInChildren<Collider2D>(true)) UnityEngine.Object.DestroyImmediate(c);
        foreach (var j in copy.GetComponentsInChildren<Joint2D>(true)) UnityEngine.Object.DestroyImmediate(j);
        foreach (var rb in copy.GetComponentsInChildren<Rigidbody2D>(true)) UnityEngine.Object.DestroyImmediate(rb);
        foreach (var l in copy.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.DestroyImmediate(l);
        foreach (var c in copy.GetComponentsInChildren<Component>(true))
            if (c && c.GetType().Name == "NavMeshAgent") UnityEngine.Object.DestroyImmediate(c);
    }

    static bool Required(Component c)
    {
        var t = c.GetType();
        foreach (var other in c.GetComponents<Component>())
        {
            if (!other || other == c) continue;
            foreach (RequireComponent rc in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                if (rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(t) || rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(t) || rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(t))
                    return true;
        }
        return false;
    }

    static bool IsUnityVisual(Type type)
    {
        string asm = type.Assembly.GetName().Name;
        if (asm.StartsWith("Unity.InputSystem") || asm.StartsWith("Unity.Cinemachine")) return false;
        if (asm.StartsWith("Unity.Behavior") || asm.StartsWith("Unity.AI")) return false;
        return asm.StartsWith("UnityEngine") || asm.StartsWith("Unity.");
    }

    internal static string PathFrom(Transform root, Transform child)
    {
        if (!child) return null;
        if (child == root) return "";
        var parts = new List<string>();
        for (var t = child; t != root; t = t.parent)
        {
            if (!t) return null;
            parts.Insert(0, t.name);
        }
        return string.Join("/", parts);
    }
}

/// <summary>
/// The animator controllers' state scripts expect the real character's components; on our copies they'd throw, so
/// they're skipped for our NPCs' animators (the multiplayer mod does the same for its stand-ins).
/// </summary>
[HarmonyPatch]
internal static class PuppetStateBehaviours
{
    public static readonly HashSet<Animator> Puppets = new();

    static readonly string[] Callbacks = { "OnStateEnter", "OnStateUpdate", "OnStateExit", "OnStateMove", "OnStateIK", "OnStateMachineEnter", "OnStateMachineExit" };

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(PlayerMaster).Assembly))
        {
            if (!typeof(StateMachineBehaviour).IsAssignableFrom(type) || type.IsAbstract) continue;
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(Callbacks, m.Name) < 0) continue;
                var ps = m.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType == typeof(Animator)) yield return m;
            }
        }
    }

    static bool Prefix(Animator __0) => !Puppets.Contains(__0);
}

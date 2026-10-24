using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterCompanion;

/// <summary>
/// The companion's body: a copy of the player's character (the way the multiplayer mod makes teammates' stand-ins)
/// with the game's scripts, physics and colliders stripped, except three it keeps working: CharacterStats (its real
/// stats, from its own save), AddonsHandler (hair, face, gear and weapon shown on the rig) and EffectsHandler (status
/// effects). Its looks come from its save (CompanionLook). Walked by setting the animator's own Speed / DirFactor and
/// turned with the character's flip pivots.
/// </summary>
internal sealed class CompanionBody
{
    public GameObject Root { get; private set; }
    public Animator[] Animators { get; private set; }
    public CharacterStats Stats { get; private set; }
    public CompanionUnit Unit { get; private set; }
    public bool Lost => !Root;
    public Vector3 Position => Root ? Root.transform.position : Vector3.zero;
    public List<InventoryItem> Items { get; private set; }   // its save's items, loaded (gear worn, the rest its bag)
    public Mover Mover { get; private set; }

    readonly List<Transform> flipPivots = new();
    Transform extraFx;
    PlayerData data;

    static readonly HashSet<string> Keep = new() { nameof(CharacterStats), nameof(AddonsHandler), nameof(EffectsHandler) };

    public static CompanionBody Create(PlayerData data, Vector3 at)
    {
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        if (!me || data == null) return null;
        var holder = new GameObject("BC_holder");
        holder.SetActive(false);
        var body = new CompanionBody { data = data };
        GameObject copy;
        try
        {
            copy = UnityEngine.Object.Instantiate(me.gameObject, holder.transform);
            copy.name = "BC_Companion";
            // the collider creatures' attacks look for on a player (its hurtbox): kept, as a trigger
            var hurtPath = PathFrom(me.transform, me.iDamageableCollider ? me.iDamageableCollider.transform : null);
            var hurt = hurtPath == null ? null : hurtPath.Length == 0 ? copy.transform : copy.transform.Find(hurtPath);
            Strip(copy, hurt ? hurt.GetComponents<Collider2D>() : null);
            body.Root = copy;
            body.Stats = copy.GetComponent<CharacterStats>();
            body.Unit = copy.AddComponent<CompanionUnit>();   // before Awake: CharacterStats and EffectsHandler look for it there
            body.Unit.Stats = body.Stats;
            body.Unit.Addons = copy.GetComponent<AddonsHandler>();
            body.Unit.Effects = copy.GetComponent<EffectsHandler>();
            if (hurt && hurt != copy.transform) hurt.gameObject.AddComponent<CompanionHurtbox>().Unit = body.Unit;
            if (!hurt) Log.Warn("Companion body: the player has no hurtbox to copy: creatures can't hit the companion");
            if (body.Stats)
            {
                body.Stats.isPlayer = false;   // (else it would follow the player's save, inventory weight and speed)
                body.Stats.owner = copy;
                var cls = ClassFor(data.equippedClass);
                if (cls) body.Stats.baseClass = cls;
            }
            copy.transform.SetParent(null, true);
        }
        finally { UnityEngine.Object.Destroy(holder); }

        body.Animators = copy.GetComponentsInChildren<Animator>(true);
        foreach (var a in body.Animators)
        {
            if (!a) continue;
            a.fireEvents = false;   // (its attack clips call PerformAttackHit: hits are ours to make)
            PuppetStateBehaviours.Puppets.Add(a);
        }
        foreach (var pivot in me.charPivotFlip)
        {
            var found = Find(me, copy, pivot);
            if (found) body.flipPivots.Add(found);
        }
        body.extraFx = copy.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Player_ExtraFxAnimations");
        int minimap = LayerMask.NameToLayer("Minimap");
        if (minimap >= 0) foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) if (r.gameObject.layer == minimap) r.enabled = false;
        copy.transform.position = at;
        body.Mover = new Mover(copy.transform);
        CompanionLook.OwnMaterials(copy);
        copy.SetActive(true);   // the kept scripts' Awake / Start run here
        body.SetupStats();
        CompanionLook.Apply(body.Unit.Addons, data);
        body.Turn(Vector3.down);
        body.Dress();
        return body;
    }

    static PlayerBaseClass ClassFor(PlayerClassType type) =>
        Resources.FindObjectsOfTypeAll<PlayerBaseClass>().FirstOrDefault(c => c && c.playerClassType == type && !c.name.EndsWith("(Clone)"));

    /// <summary>Its stats from its own save (what CharacterStats does for the player on load).</summary>
    void SetupStats()
    {
        if (!Stats) { Log.Warn("Companion body: no CharacterStats on the copy"); return; }
        try
        {
            Stats.CreateStats();
            data.GetSavedPlayerStats(Stats.primStatDict);
            Stats.baseClass.conversionTable.Calculate(Stats.primStatDict, Stats.secStatDict);
            Stats.baseClassXP = data.GetPlayerExperience(Stats.baseClass.playerClassType);   // (creates the key too)
        }
        catch (Exception e) { Log.Warn($"Companion body: stats: {e.Message}"); }
        Unit.RefreshHealth();
        Unit.Health = Unit.MaxHealth;
    }

    /// <summary>Wear what its save has in the gear slots (the item catalogue loads them, so this finishes later).</summary>
    public async void Dress()
    {
        try
        {
            foreach (var worn in Stats.EquippedItems.ToList()) Stats.RemoveItem(worn);
            ItemObject none = null;
            Unit.EquipPrimaryWeapon(ref none);
            var items = await data.GetPlayerInventory();
            if (items == null || !Root) return;
            Items = items;
            int k = 0;
            foreach (var item in items)
            {
                if (item.section != InventorySection.Gear) continue;
                Stats.EquipItem(item.itemObject);
                k++;
                if (item.itemObject.data is CollectableGear g)
                {
                    var vis = data.gearVisibleStates.FirstOrDefault(x => x.Slot == g.gear.gearSlot);
                    if (vis != null && !vis.IsVisible) Stats.ShowGear(g.gear, false);
                }
            }
            Unit.RefreshHealth();
            Arms.PickFavourites(this);
            Log.Info($"Companion body: stats ok, {k} items worn, HP {Unit.Health}/{Unit.MaxHealth}, weapon {(Unit.Weapon ? Unit.Weapon.name : "bare hands")}");
        }
        catch (Exception e) { Log.Warn($"Companion body: dressing: {e}"); }
    }

    // ---- moving ----

    public void SetWalk(float speed) => SetFloat("Speed", speed);

    public void Face(Vector2 dir) => Turn(dir);
    public bool Flipped => flipPivots.Count > 0 && flipPivots[0] && Mathf.Abs(flipPivots[0].eulerAngles.y - 180f) < 1f;

    void Turn(Vector3 dir)
    {
        if (dir.sqrMagnitude < 0.0001f) return;
        bool side = Mathf.Abs(dir.x) > Mathf.Abs(dir.y);
        SetFloat("DirFactor", side ? 0f : dir.y > 0 ? 1f : -1f);
        if (side) foreach (var p in flipPivots) if (p) p.rotation = Quaternion.Euler(0f, dir.x < 0 ? 180f : 0f, 0f);
    }

    /// <summary>Set off one of the player animator's triggers (an attack, a hit, the dash...).</summary>
    public void Play(string trigger)
    {
        int hash = Animator.StringToHash(trigger);
        foreach (var a in Animators)
        {
            if (!a || !a.isActiveAndEnabled || !a.runtimeAnimatorController) continue;
            foreach (var p in a.parameters)
                if (p.nameHash == hash && p.type == AnimatorControllerParameterType.Trigger) { a.SetTrigger(hash); break; }
        }
    }

    /// <summary>
    /// Up again after the death pose: the player's animator has no way back from Dead (the game reloads the scene
    /// instead), so it starts over from its default state, still facing the same way.
    /// </summary>
    public void Rise()
    {
        bool flip = Flipped;
        foreach (var a in Animators) if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) { a.Rebind(); a.Update(0f); }
        Turn(flip ? Vector3.left : Vector3.right);
    }

    /// <summary>One of the extra-FX animator's effects (Spawn, Teleport, LevelUp...), on a clone so it doesn't cut another off.</summary>
    public void Fx(string trigger)
    {
        if (!extraFx) return;
        var clone = UnityEngine.Object.Instantiate(extraFx.gameObject, extraFx.parent);
        var a = clone.GetComponent<Animator>();
        if (a) { a.fireEvents = false; a.SetTrigger(trigger); }
        UnityEngine.Object.Destroy(clone, 3f);
    }

    void SetFloat(string name, float value)
    {
        int hash = Animator.StringToHash(name);
        foreach (var a in Animators)
        {
            if (!a || !a.isActiveAndEnabled || !a.runtimeAnimatorController) continue;
            foreach (var p in a.parameters)
                if (p.nameHash == hash && p.type == AnimatorControllerParameterType.Float) { a.SetFloat(hash, value); break; }
        }
    }

    public void Destroy()
    {
        if (Animators != null) foreach (var a in Animators) PuppetStateBehaviours.Puppets.Remove(a);
        PuppetStateBehaviours.Puppets.RemoveWhere(a => !a);
        CompanionLook.Forget(Root);
        if (Root) UnityEngine.Object.Destroy(Root);
        Root = null;
    }

    // ---- the copy ----

    static Transform Find(PlayerMaster me, GameObject copy, Transform original)
    {
        var path = PathFrom(me.transform, original);
        return path == null ? null : path.Length == 0 ? copy.transform : copy.transform.Find(path);
    }

    static string PathFrom(Transform root, Transform child)
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

    /// <summary>
    /// Everything of the game's off the copy but the three scripts we keep (and what they need), and all physics.
    /// The copy has never been active, so nothing's OnDestroy runs (the death-mark spawners' would clear the player's
    /// own pools).
    /// </summary>
    static void Strip(GameObject copy, Collider2D[] keep)
    {
        foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "Untagged";
        foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb && mb.GetType().FullName == "EmbervilleGhost.GearDye+Mark" && mb.GetComponent<SpriteRenderer>() is SpriteRenderer sr)
                sr.color = new Color(1f, 1f, 1f, sr.color.a);   // (the player's dyes: the companion wears its own colours)
        for (int pass = 0; pass < 4; pass++)
        {
            bool any = false;
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb || IsUnityVisual(mb.GetType()) || Required(mb)) continue;
                if (mb.transform == copy.transform && Keep.Contains(mb.GetType().Name)) continue;
                UnityEngine.Object.DestroyImmediate(mb);
                any = true;
            }
            if (!any) break;
        }
        // its hurtbox stays, as a trigger: it can be hit but never blocks anyone
        foreach (var c in copy.GetComponentsInChildren<Collider2D>(true))
        {
            if (keep != null && System.Array.IndexOf(keep, c) >= 0) { c.isTrigger = true; continue; }
            UnityEngine.Object.DestroyImmediate(c);
        }
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
}

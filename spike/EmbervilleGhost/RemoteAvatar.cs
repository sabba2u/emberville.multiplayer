using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Another player's character in our world: a copy of OUR PlayerMaster with the game's simulation switched off
/// (the Duckov lesson: no AI, no game scripts, no physics on puppets). Only the art and its Animators stay; we move
/// it with transform.position and drive the Animators from the network (AnimSync).
///
/// How the copy is made: Instantiate under an inactive holder so none of the game's Awake/OnEnable run (PlayerMaster
/// would grab the input, register saves, push HUD events...), delete the game's components, then activate.
/// The copy starts out wearing OUR look; LookSync then switches it to theirs (parts, gear, colours).
/// </summary>
internal class RemoteAvatar
{
    public GameObject Root { get; private set; }
    public bool Lost => !Root;   // the scene it lived in was unloaded
    public bool Visible => Root && Root.activeSelf;
    public Vector3 HeadPosition => Root ? Root.transform.position + Vector3.up * 2.2f : Vector3.zero;

    Animator[] animators;
    Dictionary<string, Material> materials;   // our own copies (LookSync), so colours can differ from ours
    byte[] appliedLook;
    readonly List<Transform> flipPivots = new List<Transform>();
    bool flipped;

    public static RemoteAvatar Create(PlayerMaster template, int playerId)
    {
        var holder = new GameObject("EmbervilleGhost_holder");
        holder.SetActive(false);
        GameObject copy;
        Collider2D[] keep;
        try
        {
            copy = UnityEngine.Object.Instantiate(template.gameObject, holder.transform);
            copy.name = $"EmbervilleGhost_P{playerId + 1}";
            // The collider creatures' attacks look for on a player: keep it (as a trigger) so they can hit the stand-in.
            var hurtPath = PathFrom(template.transform, template.iDamageableCollider ? template.iDamageableCollider.transform : null);
            var hurtGo = hurtPath == null ? null : (hurtPath.Length == 0 ? copy.transform : copy.transform.Find(hurtPath));
            keep = hurtGo && template.iDamageableCollider ? hurtGo.GetComponents<Collider2D>() : null;
            Strip(copy, keep);
            copy.transform.SetParent(null, true);   // into the active scene, dies with it like the real player
        }
        finally
        {
            UnityEngine.Object.Destroy(holder);
        }

        var avatar = new RemoteAvatar { Root = copy };
        if (keep != null && keep.Length > 0)
        {
            foreach (var c in keep) if (c) c.isTrigger = true;   // hittable, but nothing bumps into it
            keep[0].gameObject.AddComponent<StandInTarget>().PlayerId = playerId;
        }
        else NetLog.Warn($"P{playerId + 1}'s stand-in has no hurtbox: creatures can't hit them here");
        avatar.materials = LookSync.OwnMaterials(copy);
        avatar.animators = AnimSync.Collect(copy);
        foreach (var a in avatar.animators)
        {
            if (!a) continue;
            a.fireEvents = false;                      // their event receivers are gone
            PuppetStateBehaviours.Puppets.Add(a);      // and the controller's state scripts would look for them
        }

        // PlayerMaster faces left by rotating these pivots 180 on Y; find the same ones in the copy.
        foreach (var pivot in template.charPivotFlip)
        {
            var path = PathFrom(template.transform, pivot);
            var found = path == null ? null : (path.Length == 0 ? copy.transform : copy.transform.Find(path));
            if (found) avatar.flipPivots.Add(found);
        }

        copy.SetActive(true);
        NetLog.Info($"Built a stand-in for P{playerId + 1} ({avatar.animators.Length} animators)");
        return avatar;
    }

    /// <summary>Remove everything that would make the copy act like a player. Keeps Unity's own visual components
    /// (sprites, 2D animation skinning, sorting groups, lights) and the <paramref name="keep"/> colliders (the hurtbox).</summary>
    static void Strip(GameObject copy, Collider2D[] keep)
    {
        foreach (var t in copy.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.tag = "Untagged";   // the game finds its player with FindWithTag("Player")
        }

        // Scripts first (several passes: a script can [RequireComponent] another), then physics.
        for (int pass = 0; pass < 4; pass++)
        {
            bool any = false;
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb || IsUnityVisual(mb.GetType())) continue;
                UnityEngine.Object.DestroyImmediate(mb);
                any = true;
            }
            if (!any) break;
        }
        foreach (var c in copy.GetComponentsInChildren<Collider2D>(true))
            if (keep == null || System.Array.IndexOf(keep, c) < 0) UnityEngine.Object.DestroyImmediate(c);
        foreach (var j in copy.GetComponentsInChildren<Joint2D>(true)) UnityEngine.Object.DestroyImmediate(j);
        foreach (var rb in copy.GetComponentsInChildren<Rigidbody2D>(true)) UnityEngine.Object.DestroyImmediate(rb);
        foreach (var l in copy.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.DestroyImmediate(l);
    }

    /// <summary>Keep Unity's rendering/animation scripts; everything else (game code, Wwise, input) goes.</summary>
    static bool IsUnityVisual(Type type)
    {
        string asm = type.Assembly.GetName().Name;
        if (asm.StartsWith("Unity.InputSystem") || asm.StartsWith("Unity.Cinemachine")) return false;
        return asm.StartsWith("UnityEngine") || asm.StartsWith("Unity.");
    }

    static string PathFrom(Transform root, Transform child)
    {
        if (!child) return null;
        if (child == root) return "";
        var parts = new List<string>();
        for (var t = child; t != root; t = t.parent)
        {
            if (!t) return null;   // not under root
            parts.Insert(0, t.name);
        }
        return string.Join("/", parts);
    }

    public void SetActive(bool on)
    {
        if (Root && Root.activeSelf != on) Root.SetActive(on);
    }

    public void Apply(Vector3 position, bool flip, AnimSync.Frame[] anim)
    {
        if (!Root) return;
        Root.transform.position = position;
        if (flip != flipped)
        {
            flipped = flip;
            foreach (var p in flipPivots) if (p) p.rotation = Quaternion.Euler(0f, flip ? 180f : 0f, 0f);
        }
        AnimSync.Apply(animators, anim);
    }

    /// <summary>Their hair, clothes, gear, colours (LookSync). Only does work when the look changed.</summary>
    public void ApplyLook(byte[] look)
    {
        if (!Root || look == null || look == appliedLook) return;
        appliedLook = look;
        LookSync.Apply(Root, materials, look);
    }

    public void Destroy()
    {
        if (animators != null) foreach (var a in animators) PuppetStateBehaviours.Puppets.Remove(a);
        PuppetStateBehaviours.Puppets.RemoveWhere(a => !a);   // ones that died with an unloaded scene
        if (Root) UnityEngine.Object.Destroy(Root);
        Root = null;
        if (materials != null) foreach (var m in materials.Values) if (m) UnityEngine.Object.Destroy(m);
        materials = null;
    }
}

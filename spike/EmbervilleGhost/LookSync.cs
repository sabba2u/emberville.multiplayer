using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace EmbervilleGhost;

/// <summary>
/// Game-independent "what does this character look like": which child objects are switched on, and the colours of
/// the character's materials. In Emberville every look choice is one of those two things:
///  - hair, beard, face extras, body type, armour pieces, weapons and facial expressions are folders whose children
///    get switched on/off (AddonsHandler.ShowHair/ShowAddon/ShowEquipment/ShowWeapon, FacialExpressionControl);
///  - hair / facial hair / clothes colour and skin tone are values on SHARED materials (UICharCustomization sets
///    _Color / _Index), so a copy of our character shows OUR colours until it gets its own material copies.
/// Children are matched by a hash of their path (the game reorders some folders: AddonsHandler.ReorderLockedFolders),
/// materials by their asset name, shader properties by index (same shader on both sides). Unknown ones are skipped.
/// Sent reliably, only when it changes.
/// </summary>
internal static class LookSync
{
    /// <summary>Encode a character's look (and display name).</summary>
    public static byte[] Capture(GameObject root, string name)
    {
        var w = new NetDataWriter();
        w.Put(name ?? "");

        var nodes = Nodes(root);
        w.Put(nodes.Count);
        foreach (var (hash, t) in nodes)
        {
            w.Put(hash);
            w.Put(t.gameObject.activeSelf);
        }

        var mats = Materials(root);
        w.Put(mats.Count);
        foreach (var m in mats)
        {
            w.Put(m.name);
            var shader = m.shader;
            int count = shader.GetPropertyCount();
            var props = new List<int>();
            for (int i = 0; i < count; i++)
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) props.Add(i);
            w.Put((short)count);
            w.Put((short)props.Count);
            foreach (int i in props)
            {
                int id = shader.GetPropertyNameId(i);
                w.Put((short)i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: { var c = m.GetColor(id); w.Put((byte)0); w.Put(c.r); w.Put(c.g); w.Put(c.b); w.Put(c.a); break; }
                    case ShaderPropertyType.Vector: { var v = m.GetVector(id); w.Put((byte)1); w.Put(v.x); w.Put(v.y); w.Put(v.z); w.Put(v.w); break; }
                    case ShaderPropertyType.Int: w.Put((byte)2); w.Put((float)m.GetInteger(id)); break;
                    default: w.Put((byte)2); w.Put(m.GetFloat(id)); break;   // Float, Range
                }
            }
        }
        return w.CopyData();
    }

    public static string ReadName(byte[] look) => look == null ? null : new NetDataReader(look).GetString();

    /// <summary>Give a stand-in its own copies of the materials, so colours can differ from ours.
    /// Returns original asset name -> its copy.</summary>
    public static Dictionary<string, Material> OwnMaterials(GameObject root)
    {
        var copies = new Dictionary<Material, Material>();
        var byName = new Dictionary<string, Material>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var shared = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < shared.Length; i++)
            {
                var m = shared[i];
                if (!m) continue;
                if (!copies.TryGetValue(m, out var copy))
                {
                    copies[m] = copy = new Material(m) { name = m.name };
                    byName[m.name] = copy;
                }
                shared[i] = copy;
                changed = true;
            }
            if (changed) r.sharedMaterials = shared;
        }
        return byName;
    }

    public static void Apply(GameObject root, Dictionary<string, Material> materials, byte[] look)
    {
        if (!root || look == null) return;
        var r = new NetDataReader(look);
        r.GetString();   // name

        var nodes = new Dictionary<uint, Transform>();
        foreach (var (hash, t) in Nodes(root)) nodes[hash] = t;
        int count = r.GetInt();
        for (int i = 0; i < count; i++)
        {
            uint hash = r.GetUInt();
            bool active = r.GetBool();
            if (nodes.TryGetValue(hash, out var t) && t.gameObject.activeSelf != active) t.gameObject.SetActive(active);
        }

        int matCount = r.GetInt();
        for (int i = 0; i < matCount; i++)
        {
            string name = r.GetString();
            materials.TryGetValue(name, out var m);
            var shader = m ? m.shader : null;
            bool sameShader = shader && shader.GetPropertyCount() == r.PeekShort();
            r.GetShort();
            int props = r.GetShort();
            for (int p = 0; p < props; p++)
            {
                int index = r.GetShort();
                byte kind = r.GetByte();
                Vector4 v = kind <= 1 ? new Vector4(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()) : new Vector4(r.GetFloat(), 0, 0, 0);
                if (!sameShader) continue;
                int id = shader.GetPropertyNameId(index);
                if (kind == 0) m.SetColor(id, v);
                else if (kind == 1) m.SetVector(id, v);
                else if (shader.GetPropertyType(index) == ShaderPropertyType.Int) m.SetInteger(id, (int)v.x);
                else m.SetFloat(id, v.x);
            }
        }
    }

    /// <summary>Every child (not the root) with a stable id: FNV-1a hash of its path from the root,
    /// with "#n" for same-named siblings.</summary>
    static List<(uint hash, Transform t)> Nodes(GameObject root)
    {
        var list = new List<(uint, Transform)>();
        Walk(root.transform, "", list);
        return list;
    }

    static void Walk(Transform parent, string path, List<(uint, Transform)> list)
    {
        var seen = new Dictionary<string, int>();
        foreach (Transform child in parent)
        {
            seen.TryGetValue(child.name, out int n);
            seen[child.name] = n + 1;
            string p = path + "/" + child.name + (n > 0 ? "#" + n : "");
            list.Add((Fnv(p), child));
            Walk(child, p, list);
        }
    }

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    /// <summary>Distinct materials on the character, in a stable order (by asset name).</summary>
    static List<Material> Materials(GameObject root)
    {
        var set = new Dictionary<string, Material>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m && !set.ContainsKey(m.name)) set[m.name] = m;
        var list = new List<Material>(set.Values);
        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return list;
    }
}

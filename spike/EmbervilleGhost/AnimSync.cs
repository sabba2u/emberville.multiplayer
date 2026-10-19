using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Game-independent animation sync: copy every Animator under a character (by index, same prefab on both sides).
/// Per animator: each layer's current state + normalized time, and every float/int/bool parameter (not triggers;
/// the state itself is what a trigger leads to). The receiver sets the parameters and jumps a layer to the sender's
/// state when it differs, so attacks, dodges, tool swings and idle/walk/run all match without knowing the game's
/// animation names. New in Emberville (Duckov mapped a handful of animator values by hand).
/// </summary>
internal static class AnimSync
{
    /// <summary>Same order on both sides: GetComponentsInChildren(includeInactive).</summary>
    public static Animator[] Collect(GameObject root) => root.GetComponentsInChildren<Animator>(true);

    public static void Write(NetDataWriter w, Animator[] animators)
    {
        w.Put((byte)animators.Length);
        foreach (var a in animators)
        {
            bool live = a && a.isActiveAndEnabled && a.runtimeAnimatorController;
            w.Put(live);
            if (!live) continue;

            w.Put((byte)a.layerCount);
            for (int l = 0; l < a.layerCount; l++)
            {
                var s = a.GetCurrentAnimatorStateInfo(l);
                w.Put(s.fullPathHash);
                w.Put(s.normalizedTime);
            }

            var ps = a.parameters;
            int count = 0;
            foreach (var p in ps) if (p.type != AnimatorControllerParameterType.Trigger) count++;
            w.Put((byte)count);
            foreach (var p in ps)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: w.Put(p.nameHash); w.Put((byte)0); w.Put(a.GetFloat(p.nameHash)); break;
                    case AnimatorControllerParameterType.Int: w.Put(p.nameHash); w.Put((byte)1); w.Put(a.GetInteger(p.nameHash)); break;
                    case AnimatorControllerParameterType.Bool: w.Put(p.nameHash); w.Put((byte)2); w.Put(a.GetBool(p.nameHash)); break;
                }
            }
        }
    }

    /// <summary>One animator's received values.</summary>
    public class Frame
    {
        public bool Live;
        public readonly List<(int hash, float time)> Layers = new List<(int, float)>();
        public readonly List<(int hash, byte kind, float value)> Params = new List<(int, byte, float)>();
    }

    public static Frame[] Read(NetDataReader r)
    {
        var frames = new Frame[r.GetByte()];
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i] = new Frame { Live = r.GetBool() };
            if (!f.Live) continue;
            int layers = r.GetByte();
            for (int l = 0; l < layers; l++) f.Layers.Add((r.GetInt(), r.GetFloat()));
            int count = r.GetByte();
            for (int p = 0; p < count; p++)
            {
                int hash = r.GetInt();
                byte kind = r.GetByte();
                float value = kind switch { 0 => r.GetFloat(), 1 => r.GetInt(), _ => r.GetBool() ? 1f : 0f };
                f.Params.Add((hash, kind, value));
            }
        }
        return frames;
    }

    public static void Apply(Animator[] animators, Frame[] frames)
    {
        if (frames == null) return;
        int n = Mathf.Min(animators.Length, frames.Length);
        for (int i = 0; i < n; i++)
        {
            var a = animators[i];
            var f = frames[i];
            if (!a || !f.Live || !a.isActiveAndEnabled || !a.runtimeAnimatorController) continue;

            foreach (var (hash, kind, value) in f.Params)
            {
                if (kind == 0) a.SetFloat(hash, value);
                else if (kind == 1) a.SetInteger(hash, (int)value);
                else a.SetBool(hash, value != 0f);
            }

            for (int l = 0; l < f.Layers.Count && l < a.layerCount; l++)
            {
                var (hash, time) = f.Layers[l];
                if (a.GetCurrentAnimatorStateInfo(l).fullPathHash == hash) continue;
                if (a.IsInTransition(l) && a.GetNextAnimatorStateInfo(l).fullPathHash == hash) continue;
                if (!a.HasState(l, hash)) continue;
                a.Play(hash, l, time - Mathf.Floor(time));   // looping states count up past 1
            }
        }
    }
}

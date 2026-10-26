using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Diagnostics: Unity's "Sprite outline generation failed - could not read texture pixel data" names nobody. A sprite
/// made from a texture that can't be read (with the outline mesh, the default) logs it; this says who made it, once per
/// caller: the texture, and the stack.
/// </summary>
[HarmonyPatch]
internal static class SpriteWatch
{
    static readonly HashSet<string> told = new HashSet<string>();

    static IEnumerable<MethodBase> TargetMethods() =>
        typeof(Sprite).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(Sprite.Create) && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Texture2D));

    static void Prefix(Texture2D texture, MethodBase __originalMethod, object[] __args)
    {
        if (!texture || texture.isReadable) return;
        // (only the outline kind reads the pixels: the full-rect kind doesn't)
        var ps = __originalMethod.GetParameters();
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].ParameterType == typeof(SpriteMeshType) && __args[i] is SpriteMeshType t && t == SpriteMeshType.FullRect) return;
        string stack = System.Environment.StackTrace;
        string caller = string.Join(" < ", stack.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at ") && !l.Contains("SpriteWatch") &&
            !l.Contains("System.Environment") && !l.Contains("UnityEngine.Sprite.Create") && !l.Contains("DMD<")).Take(4));
        if (!told.Add(caller)) return;
        NetLog.Warn($"Sprite from an unreadable texture ({texture.name} {texture.width}x{texture.height}): {caller}");
    }
}

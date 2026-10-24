using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BetterCompanion;

/// <summary>
/// The animator controllers' state scripts expect the real character's components; on our copies they'd throw, so
/// they're skipped for the companion's animators (the multiplayer mod does the same for its stand-ins).
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

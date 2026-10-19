using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Animator controllers can carry game scripts on their states (StateMachineBehaviour: OnStateEnter/Update/Exit...).
/// They live on the shared controller asset, so stripping the stand-in can't remove them, and they expect the real
/// player's components (Emberville's AttackRateChange: animator.GetComponent&lt;CharacterStats&gt;() -> null -> exception
/// every time a stand-in enters an attack state). Skip all of them for stand-in animators; whatever parameters they
/// would set arrive through AnimSync anyway.
/// Game-independent: finds every StateMachineBehaviour in Assembly-CSharp by reflection.
/// </summary>
[HarmonyPatch]
internal static class PuppetStateBehaviours
{
    /// <summary>Animators of stand-ins (RemoteAvatar adds/removes them).</summary>
    public static readonly HashSet<Animator> Puppets = new HashSet<Animator>();

    static readonly string[] Callbacks = { "OnStateEnter", "OnStateUpdate", "OnStateExit", "OnStateMove", "OnStateIK", "OnStateMachineEnter", "OnStateMachineExit" };

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(PlayerMaster).Assembly))
        {
            if (!typeof(StateMachineBehaviour).IsAssignableFrom(type) || type.IsAbstract) continue;
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (System.Array.IndexOf(Callbacks, m.Name) < 0) continue;
                var ps = m.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType == typeof(Animator)) yield return m;
            }
        }
    }

    static bool Prefix(Animator animator) => !Puppets.Contains(animator);
}

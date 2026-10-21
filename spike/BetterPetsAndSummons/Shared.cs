using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace BetterPetsAndSummons;

/// <summary>
/// What the multiplayer mod tells us when it's installed (it sets these in its Awake); on our own, the defaults.
/// </summary>
public static class Hooks
{
    /// <summary>A creature our pets / summons leave alone (story NPCs built as creatures).</summary>
    public static Func<Enemy, bool> Ignore = e => e && e.name.StartsWith("NPC_");
    /// <summary>One of the multiplayer mod's mini-boss guardians (risen at half strength).</summary>
    public static Func<Enemy, bool> IsBoss = _ => false;
    /// <summary>A reply to a chat command (/pet...): the chat window there; the game's notification list here.</summary>
    public static Action<string> Say = text => Log.Note(text);
    /// <summary>Someone's typing (chat, a text box): keys aren't for us.</summary>
    public static Func<bool> Typing = () => false;
    /// <summary>The player is busy (a menu, a cutscene, dead...): no right-click raising.</summary>
    public static Func<bool> Busy = () => UIPanels.HasOpenedPanel || !MainInput.Actions.Player.enabled;
}

// ---- small stand-ins with the multiplayer mod's helper names, so the moved code reads the same ----

internal static class NetLog
{
    public static void Info(string text, bool toast = false) => Log.Info(text, toast);
    public static void Success(string text, bool toast = true) => Log.Info(text, toast);
    public static void Warn(string text, bool toast = true) => Log.Warn(text, toast);
}

internal static class LocalPlayer
{
    public static PlayerMaster Get() => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
    public static string Scene => SceneManager.GetActiveScene().name;
}

internal static class Chat
{
    public static void System(string text) => Hooks.Say(text);
    public static bool IsOpen => Hooks.Typing();
}

internal static class ImGuiUnity { public static bool Typing => Hooks.Typing(); }
internal static class PlayerStatusUtil { public static bool LocalBusy => Hooks.Busy(); }
internal static class EnemySync { public static bool LocalOnly(Enemy e) => Hooks.Ignore(e); }
internal static class MiniBoss { public static bool IsBoss(Enemy e) => Hooks.IsBoss(e); }

internal static class Dungeon
{
    /// <summary>The biomes (Resources "AllBiomes") bring the wild creatures' prefabs into memory, in any scene.</summary>
    public static void AllSettings() { try { Resources.Load<AllBiomeSettings>("AllBiomes"); } catch { } }
}

/// <summary>The game's item catalogue (every ItemData, loaded once in the background), for the staffs' items.</summary>
internal static class DungeonLoot
{
    static List<ItemData> pool;
    internal static IReadOnlyList<ItemData> Pool => pool;
    static bool loading;

    public static void Preload()
    {
        if (pool != null || loading) return;
        loading = true;
        var keys = new HashSet<string>();
        foreach (var locator in Addressables.ResourceLocators)
            foreach (var key in locator.Keys)
                if (key is string s && locator.Locate(s, typeof(ItemData), out var locs))
                    foreach (var l in locs) keys.Add(l.PrimaryKey);
        var found = new List<ItemData>();
        int pending = keys.Count;
        if (pending == 0) { pool = found; loading = false; return; }
        foreach (var key in keys)
            Addressables.LoadAssetAsync<ItemData>(key).Completed += h =>
            {
                if (h.Status == AsyncOperationStatus.Succeeded && h.Result is CollectableWeapon) found.Add(h.Result);
                if (--pending == 0) { pool = found.Distinct().OrderBy(d => d.addressKey).ToList(); loading = false; }
            };
    }
}

/// <summary>The summons the multiplayer mod shows on teammates' screens (it reads CluckStaff/RisenStaff.Summons()).</summary>
public static class Summoned
{
    public readonly struct Summon
    {
        public readonly ushort Id;
        public readonly bool Risen;
        public readonly string Prefab;
        public readonly Vector3 At;
        public readonly float Size, Health;
        public readonly byte Attacks;
        public Summon(ushort id, bool risen, string prefab, Vector3 at, float size, float health, byte attacks)
        { Id = id; Risen = risen; Prefab = prefab; At = at; Size = size; Health = health; Attacks = attacks; }
    }

    static ushort nextId;
    public static ushort NextId() => ++nextId == 0 ? ++nextId : nextId;

    /// <summary>Ours now: every chicken and every risen.</summary>
    public static IEnumerable<Summon> All() => CluckStaff.Summons().Concat(RisenStaff.Summons());
}

/// <summary>
/// A creature (or anything) copied into a stand-in: game scripts, physics and the AI stripped, only Unity's own
/// visuals kept (the same as the multiplayer mod's player stand-ins).
/// </summary>
internal static class RemoteAvatar
{
    internal static void Strip(GameObject copy, Collider2D[] keep)
    {
        foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "Untagged";
        for (int pass = 0; pass < 4; pass++)
        {
            bool any = false;
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb || IsUnityVisual(mb.GetType())) continue;
                if (Required(mb)) continue;
                UnityEngine.Object.DestroyImmediate(mb);
                any = true;
            }
            if (!any) break;
        }
        foreach (var c in copy.GetComponentsInChildren<Collider2D>(true))
            if (keep == null || Array.IndexOf(keep, c) < 0) UnityEngine.Object.DestroyImmediate(c);
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
/// The animator controllers' state scripts (StateMachineBehaviour) expect the real creature's components; on our
/// stand-ins they'd throw, so they're skipped for stand-in animators.
/// </summary>
[HarmonyPatch]
internal static class PuppetStateBehaviours
{
    public static readonly HashSet<Animator> Puppets = new HashSet<Animator>();

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

    static bool Prefix(Animator animator) => !Puppets.Contains(animator);
}

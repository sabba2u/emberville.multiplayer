using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleGhost;

/// <summary>Our own character and where it is. Emberville has one PlayerMaster per scene (tag "Player").</summary>
internal static class LocalPlayer
{
    static PlayerMaster cached;

    public static PlayerMaster Get()
    {
        if (cached) return cached;
        if (Singleton<GameManager>.HasMain && Singleton<GameManager>.main.player) return cached = Singleton<GameManager>.main.player;
        var go = GameObject.FindWithTag("Player");
        return cached = go ? go.GetComponent<PlayerMaster>() : null;
    }

    static Animator[] animators;
    static PlayerMaster animatorsOf;

    /// <summary>Our character's Animators (AnimSync order), looked up once per character.</summary>
    public static Animator[] Animators(PlayerMaster me)
    {
        if (animatorsOf != me || animators == null) { animators = AnimSync.Collect(me.gameObject); animatorsOf = me; }
        return animators;
    }

    public static string Scene => SceneManager.GetActiveScene().name;

    /// <summary>
    /// The GameArea we're in. One scene holds several areas (town, house interiors, dungeon regions) and only the
    /// current one is switched on, so two players are only together when scene AND area match.
    /// Never touch Singleton.main without HasMain: its getter creates a fresh object if there is none.
    /// </summary>
    public static string Area =>
        Singleton<GameManager>.HasMain && Singleton<GameManager>.main.CurrentArea ? Singleton<GameManager>.main.CurrentArea.name : "";
}

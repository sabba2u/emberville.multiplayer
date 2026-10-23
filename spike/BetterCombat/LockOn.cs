using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace BetterCombat;

/// <summary>
/// Zelda's lock-on, all on G: point at an enemy and press G to lock on (G on the locked one lets go; G pointing at
/// nothing locks the nearest, and again the next one, nearest first, round and round). While locked your character
/// faces it whatever the mouse does, as if the mouse were held on it (PlayerMaster.GetLookDirection gets its direction
/// from the creature): you circle and strafe around it, and swings, spells and arrows (they leave along your facing) go
/// at it. A ring under it shows the lock. When it dies the lock moves on to the next one close by (or goes); too far
/// away, or another place: gone. Only you see your ring. F stays the game's interact key.
/// Lockable: enemies only: anything with the game's Enemy component (every creature, the mushroom huts too; not friendly
/// NPCs), and spider eggs. Never doors, furniture or barrels.
/// For other mods (the multiplayer mod's Hunter's Hook): Target and AimPoint.
/// </summary>
[HarmonyPatch]
public static class LockOn
{
    static ConfigEntry<bool> enabled;
    static ConfigEntry<Key> cycleKey;
    const float Pick = 1.4f, Next = 9f, Lose = 16f;

    public static Component Target { get; private set; }

    /// <summary>Where to aim when locked (the creature's middle); false when not locked.</summary>
    public static bool AimPoint(out Vector3 at)
    {
        at = Target ? Middle(Target) : Vector3.zero;
        return Target;
    }

    public static void Bind(ConfigFile config)
    {
        enabled = config.Bind("LockOn", "Enabled", true, "Point at an enemy and press the lock key to lock on (you face it; attacks go at it); with nothing under the mouse, the nearest / the next one");
        cycleKey = config.Bind("LockOn", "NextKey", Key.G, "The lock key: locks on to the enemy under the mouse (again: lets go), or to the nearest / the next one");
    }

    // ---- every frame ----

    public static void Tick()
    {
        if (!enabled.Value) { if (Target) Release(null); return; }
        var me = Me;
        if (Target && (!me || !Alive(Target) || Far(me, Target, Lose))) { var was = Target; Release(null); if (me && was && !Alive(was)) Lock(Nearest(me, Next, null)); }
        var kb = Keyboard.current;
        if (me && kb != null && Application.isFocused && Free && kb[cycleKey.Value].wasPressedThisFrame) Pressed(me);
        Ring.Follow(Target);
    }

    static bool Free => Singleton<GameManager>.HasMain && Singleton<GameManager>.main.gameState == GameState.Playing &&
                        MainInput.Actions.Player.enabled && !UIPanels.HasOpenedPanel;

    /// <summary>G: the enemy under the mouse (the locked one: let go), else the next one around.</summary>
    static void Pressed(PlayerMaster me)
    {
        var under = UnderMouse();
        if (!under) { Cycle(me); return; }
        if (under == Target) Release("let go"); else Lock(under);
    }

    /// <summary>G: the next creature around, nearest first (round and round).</summary>
    static void Cycle(PlayerMaster me)
    {
        var near = Targets().Where(e => !Far(me, e, Next)).OrderBy(e => Dist(me, e)).ToList();
        if (near.Count == 0) { Release(null); return; }
        int i = Target ? near.IndexOf(Target) : -1;
        Lock(near[(i + 1) % near.Count]);
    }

    static void Lock(Component e)
    {
        if (!e) return;
        Target = e;
        sceneOf = e.gameObject.scene.name;
    }

    static void Release(string why) { Target = null; sceneOf = null; Ring.Follow(null); }

    static string sceneOf;

    // ---- facing it (the game's own "where is the mouse", answered with the creature) ----

    [HarmonyPatch(typeof(PlayerMaster), "GetLookDirection"), HarmonyPrefix]
    static bool Face(PlayerMaster __instance)
    {
        if (!Target || __instance != Me || MainInput.CurrentScheme == ControllerScheme.Gamepad) return true;
        Vector2 d = Middle(Target) - __instance.transform.position;
        if (d.sqrMagnitude < 0.0001f) return true;
        __instance.lookDir = d.normalized;
        __instance.lastLookDir = __instance.lookDir;
        __instance.SetPlayerDirection();
        return false;
    }

    // ---- creatures ----

    static PlayerMaster Me => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;

    /// <summary>Everything you can lock on to: enemies (the game's Enemy: every creature) and spider eggs.</summary>
    static IEnumerable<Component> Targets() =>
        Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None).Where(Alive).Cast<Component>()
            .Concat(Object.FindObjectsByType<SpiderEgg>(FindObjectsSortMode.None).Where(Alive));

    static bool Alive(Component c) => c switch
    {
        Enemy e => e && e.isActiveAndEnabled && !e.IsDead && !e.name.StartsWith("NPC_"),
        SpiderEgg egg => egg && egg.isActiveAndEnabled && !egg.IsDead,
        _ => false,
    };

    static Vector3 Middle(Component e)
    {
        var col = e.GetComponentInChildren<Collider2D>();
        var p = col ? (Vector3)col.bounds.center : e.transform.position;
        p.z = 0f;
        return p;
    }

    static float Dist(PlayerMaster me, Component e) => ((Vector2)(Middle(e) - me.transform.position)).magnitude;
    static bool Far(PlayerMaster me, Component e, float range) => Dist(me, e) > range || e.gameObject.scene.name != (sceneOf ?? e.gameObject.scene.name);

    static Component Nearest(PlayerMaster me, float range, Component not) =>
        Targets().Where(e => e != not && Dist(me, e) <= range).OrderBy(e => Dist(me, e)).FirstOrDefault();

    /// <summary>The creature under the mouse (the nearest to it, close enough).</summary>
    static Component UnderMouse()
    {
        var cam = Camera.main;
        var mouse = Mouse.current;
        if (!cam || mouse == null) return null;
        var mp = mouse.position.ReadValue();
        var w = cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, -cam.transform.position.z)); w.z = 0f;
        Component best = null; float bestD = Pick;
        foreach (var e in Targets())
        {
            float d = ((Vector2)(Middle(e) - w)).magnitude;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    // ---- the Insert menu ----

    public static void DrawMenu()
    {
        bool on = enabled.Value;
        if (ImGui.Checkbox("Lock-on: point at an enemy, G to lock (you face it, attacks go at it); G at nothing: the nearest / the next one", ref on)) enabled.Value = on;
        if (Target) ImGui.TextDisabled($"Locked on: {Target.name}");
    }
}

/// <summary>The ring under the creature you're locked on to (pixel art, pulsing, just under its feet).</summary>
internal static class Ring
{
    static GameObject go;
    static SpriteRenderer sr;

    public static void Follow(Component e)
    {
        if (!e) { if (go) go.SetActive(false); return; }
        if (!go) Build();
        go.SetActive(true);
        // under its feet, on its own layer just below it
        var body = e.GetComponentsInChildren<SpriteRenderer>().Where(r => r.enabled && r.sprite).OrderBy(r => r.sortingOrder).FirstOrDefault();
        if (body) { sr.sortingLayerID = body.sortingLayerID; sr.sortingOrder = body.sortingOrder - 1; }
        var col = e.GetComponentInChildren<Collider2D>();
        float width = col ? Mathf.Clamp(col.bounds.size.x * 1.6f, 0.9f, 4f) : 1.2f;
        float pulse = 1f + Mathf.Sin(Time.time * 6f) * 0.06f;
        go.transform.position = new Vector3(e.transform.position.x, e.transform.position.y, 0f);
        go.transform.localScale = new Vector3(width * pulse, width * 0.5f * pulse, 1f);
    }

    static void Build()
    {
        go = new GameObject("BCombat_LockRing");
        Object.DontDestroyOnLoad(go);
        sr = go.AddComponent<SpriteRenderer>();
        // a pixel ring, 16 wide, drawn once
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f, r = Mathf.Sqrt(dx * dx + dy * dy);
                bool ring = r <= n / 2f - 0.5f && r >= n / 2f - 3.5f;
                bool tick = (Mathf.Abs(dx) < 1.5f || Mathf.Abs(dy) < 1.5f) && r <= n / 2f - 0.5f && r >= n / 2f - 7f;   // four notches
                px[y * n + x] = ring ? new Color32(255, 120, 70, 230) : tick ? new Color32(255, 200, 120, 230) : new Color32(0, 0, 0, 0);
            }
        tex.SetPixels32(px);
        tex.Apply();
        sr.sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        sr.sharedMaterial = new Material(shader) { name = "BCombat_LockRing" };
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// The Hunter's Hook dresses you for the part: while it's in your hand, your clothes turn yellow. The game colours
/// clothes with "_Color" on the clothes' shared material (character creation sets it: UICharCustomization
/// .SetClothesColor); we set that same value, so LookSync sends it to everyone like any colour change and your
/// teammates see you in yellow too. Your own colour is kept aside and put back when the hook leaves your hand. (Only
/// the material in memory changes: the save keeps your colour choice as a number, untouched.)
/// </summary>
internal static class HookOutfit
{
    static readonly Color Yellow = new Color(1f, 0.82f, 0.12f, 1f);
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly Dictionary<Material, Color> own = new Dictionary<Material, Color>();
    static bool logged;
    static float nextCheck;

    public static void Tick(PlayerMaster me)
    {
        bool want = me && HunterHook.HeldBy(me) && !me.IsDead;
        if (want && (own.Count == 0 || Time.unscaledTime >= nextCheck)) { Dress(me); nextCheck = Time.unscaledTime + 1f; }   // (again now and then: gear changes swap renderers)
        else if (!want && own.Count > 0) Undress();
    }

    static bool IsClothes(Renderer r, Material m) =>
        m && m.HasProperty(ColorId) &&
        (m.name.IndexOf("cloth", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
         r.name.IndexOf("cloth", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
         (r.transform.parent && r.transform.parent.name.IndexOf("cloth", System.StringComparison.OrdinalIgnoreCase) >= 0));

    static void Dress(PlayerMaster me)
    {
        foreach (var r in me.GetComponentsInChildren<Renderer>(true))
        {
            if (!r) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (!IsClothes(r, m)) continue;
                if (!own.ContainsKey(m)) own[m] = m.GetColor(ColorId);
                if (m.GetColor(ColorId) != Yellow) m.SetColor(ColorId, Yellow);
            }
        }
        if (!logged)
        {
            logged = true;
            NetLog.Info($"Hook outfit: {own.Count} clothes materials ({string.Join(", ", own.Keys.Select(m => m.name))})", toast: false);
            if (own.Count == 0)
                NetLog.Info("Hook outfit: renderers on the player: " + string.Join(", ", me.GetComponentsInChildren<Renderer>(true).Select(r => r.name + "/" + (r.sharedMaterial ? r.sharedMaterial.name : "-"))), toast: false);
        }
    }

    /// <summary>Your own colours back (also when leaving a world or quitting, so nothing stays yellow).</summary>
    public static void Undress()
    {
        foreach (var kv in own) if (kv.Key) kv.Key.SetColor(ColorId, kv.Value);
        own.Clear();
    }
}

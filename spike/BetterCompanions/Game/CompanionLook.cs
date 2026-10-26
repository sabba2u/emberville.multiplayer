using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The companion's face, hair, skin, body type and colours, from its save's playerSetup, applied the way the game's
/// customization screen (UICharCustomization) applies them to the player:
///   [0] hair, [1] facial hair, [2] face extras, [3] skin tone, [4] hair colour, [5] facial hair colour,
///   [6] clothes colour, [7] body type.
/// Skin tone and the three colours live on materials the player's rig shares (hair, facial hair, skin, clothes), so
/// the copy first gets its own copies of those, or it would recolour the player.
/// </summary>
internal static class CompanionLook
{
    const int SkinTones = 9;
    static readonly Dictionary<GameObject, Dictionary<Material, Material>> owned = new();

    static UICharCustomization Screen => Object.FindAnyObjectByType<UICharCustomization>(FindObjectsInactive.Include);

    public static void OwnMaterials(GameObject copy)
    {
        var screen = Screen;
        if (!screen) { Log.Warn("Companion look: no customization screen to read colours from"); return; }
        var shared = new[] { screen.hairMaterial, screen.facialHairMaterial, screen.skinMaterial, screen.clothesMaterial }.Where(m => m).Distinct().ToList();
        var mine = shared.ToDictionary(m => m, m => new Material(m) { name = m.name });
        owned[copy] = mine;
        foreach (var r in copy.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] && mine.TryGetValue(mats[i], out var m)) { mats[i] = m; changed = true; }
            if (changed) r.sharedMaterials = mats;
        }
    }

    public static void Apply(AddonsHandler addons, PlayerData data)
    {
        if (!addons) return;
        var setup = data?.playerSetup;
        if (setup == null || setup.Count < 8) setup = new List<int> { 0, 0, 0, 0, 0, 0, 0, 0 };
        int Wrap(int v, int n) => n <= 0 ? 0 : ((v % n) + n) % n;

        if (addons.hairAssets.Count > 0) addons.ShowHair(Wrap(setup[0], addons.hairAssets.Count));
        addons.ShowAddon(AddonSlot.FaceHair, Wrap(setup[1], addons.GetFolderCount(AddonSlot.FaceHair) + 1) - 1);
        addons.ShowAddon(AddonSlot.FaceAddon, Wrap(setup[2], addons.GetFolderCount(AddonSlot.FaceAddon) + 1) - 1);
        addons.ShowAddon(AddonSlot.Clothes, setup[7]);

        var screen = Screen;
        if (!screen || !owned.TryGetValue(addons.gameObject, out var mine)) return;
        Material Mine(Material shared) => shared && mine.TryGetValue(shared, out var m) ? m : null;
        Color Pick(List<Color> colors, int i) => colors != null && colors.Count > 0 ? colors[Wrap(i, colors.Count)] : Color.white;
        Mine(screen.skinMaterial)?.SetFloat("_Index", Wrap(setup[3], SkinTones));
        Mine(screen.hairMaterial)?.SetColor("_Color", Pick(screen.hairColors, setup[4]));
        Mine(screen.facialHairMaterial)?.SetColor("_Color", Pick(screen.hairColors, setup[5]));
        Mine(screen.clothesMaterial)?.SetColor("_Color", Pick(screen.clothesColors, setup[6]));
    }

    public static void Forget(GameObject copy)
    {
        if (copy && owned.TryGetValue(copy, out var mine)) foreach (var m in mine.Values) if (m) Object.Destroy(m);
        owned.Remove(copy);
        foreach (var dead in owned.Keys.Where(k => !k).ToList()) owned.Remove(dead);
    }
}

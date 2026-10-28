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
        Log.Info($"Companion look: {data?.playerName} saved setup [{(data?.playerSetup == null ? "none" : string.Join(",", data.playerSetup))}], {addons.hairAssets.Count} hairstyles" +
                 (data?.playerSetup != null && data.playerSetup.Count > 0 && addons.hairAssets.Count > 0
                     ? $", hairstyle {data.playerSetup[0] % addons.hairAssets.Count} = overlay {addons.hairAssets[data.playerSetup[0] % addons.hairAssets.Count].overlayHairIndex} front {addons.hairAssets[data.playerSetup[0] % addons.hairAssets.Count].frontHairIndex} behind {addons.hairAssets[data.playerSetup[0] % addons.hairAssets.Count].behindHairIndex}" : ""));
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

    /// <summary>Once it's dressed: its head as its own gear has it. The rig is a copy of yours, so a hood you wear came
    /// with it (and your hair switched off under it), and nothing it wears ever clears that: without a helmet of its
    /// own, the head slot emptied the game's way, which brings the hair back.</summary>
    public static void Head(AddonsHandler addons, CharacterStats stats)
    {
        if (!addons || stats == null) return;
        bool helmet = stats.EquippedItems.Any(i => i?.data is CollectableGear g && (g.gear.gearSlot & GearSlot.Head) != 0);
        if (!helmet) addons.ShowEquipment(GearSlot.Head, -1);
        // (testing: what its hair is doing)
        var sb = new System.Text.StringBuilder($"Companion hair: helmet {helmet}; hair folders ");
        foreach (var h in addons.hairFolders) sb.Append(h ? $"{h.name}:{(h.activeInHierarchy ? "on" : h.activeSelf ? "parent off" : "off")} " : "null ");
        foreach (var slot in new[] { AddonSlot.HairOverlay, AddonSlot.HairFront, AddonSlot.HairBehind })
        {
            var af = addons.addonFolders.Find(f => f.slot == slot);
            if (af.folders == null) { sb.Append($"| {slot}: none "); continue; }
            foreach (var f in af.folders)
            {
                if (!f) continue;
                int on = -1;
                for (int i = 0; i < f.childCount; i++) if (f.GetChild(i).gameObject.activeSelf) { on = i; break; }
                var sr = on >= 0 ? f.GetChild(on).GetComponentInChildren<SpriteRenderer>(true) : null;
                sb.Append($"| {slot} {f.name}{(f.gameObject.activeInHierarchy ? "" : " (hidden)")} shows {on}/{f.childCount}" +
                          (sr ? $" sprite {(sr.sprite ? sr.sprite.name : "none")} {(sr.enabled ? "" : "off ")}col {sr.color} mat {(sr.sharedMaterial ? sr.sharedMaterial.name + " " + (sr.sharedMaterial.HasProperty("_Color") ? sr.sharedMaterial.GetColor("_Color").ToString() : "") : "none")}" : "") + " ");
            }
        }
        Log.Info(sb.ToString());
    }

    public static void Forget(GameObject copy)
    {
        if (copy && owned.TryGetValue(copy, out var mine)) foreach (var m in mine.Values) if (m) Object.Destroy(m);
        owned.Remove(copy);
        foreach (var dead in owned.Keys.Where(k => !k).ToList()) owned.Remove(dead);
    }
}

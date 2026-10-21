using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;

namespace BetterChests;

/// <summary>
/// "X: quick deposit" under a chest's name while you stand at it. It's a copy of the chest's own name plate (the game's
/// font and backing), so it looks like part of the game, with our text in it.
/// </summary>
internal static class Hint
{
    static ConfigEntry<bool> show;
    static GameObject plate, plate2;
    static Chest shownOn;

    public static void Bind(ConfigFile config) =>
        show = config.Bind("QuickStack", "ShowHint", true, "Show \"X: quick deposit\" under a chest while you stand at it");

    /// <summary>Every frame, with the chest the player is at (null = none, or it's open / we're in a menu).</summary>
    public static void Tick(Chest at)
    {
        if (!show.Value) at = null;
        if (at == shownOn && (at == null || plate)) return;
        Hide();
        if (at == null || !at.titlePivot || !at.title) return;
        try { Show(at); }
        catch (System.Exception e) { Plugin.Log.LogWarning($"Hint: {e.Message}"); Hide(); }
    }

    static void Show(Chest chest)
    {
        plate = Plate(chest, $"{QuickStack.KeyName}: quick deposit   Shift+{QuickStack.KeyName}: all nearby", 1);
        plate2 = Plate(chest, $"{ChestLooks.KeyName}: customize", 2);   // a second line under it
        shownOn = chest;
    }

    /// <summary>A copy of the chest's name plate with our text, <paramref name="row"/> plates under the name.</summary>
    static GameObject Plate(Chest chest, string line, int row)
    {
        GameObject plate;
        var source = chest.titlePivot.transform;
        // copy it switched off, so the copy's localisation never runs and overwrites our text
        var holder = new GameObject("BC_HintHolder");
        holder.SetActive(false);
        plate = Object.Instantiate(source.gameObject, holder.transform);
        plate.name = "BC_QuickDepositHint";
        foreach (var l in plate.GetComponentsInChildren<LocalizeStringEvent>(true)) Object.DestroyImmediate(l);
        var text = plate.GetComponentInChildren<TMP_Text>(true);
        if (!text) { Object.Destroy(holder); return null; }
        text.text = line;
        text.fontSize *= 0.8f;
        text.color = new Color(1f, 0.86f, 0.55f, text.color.a);

        plate.transform.SetParent(source.parent, false);
        Object.Destroy(holder);
        // just under the chest's own name plate
        var fitter = chest.titleSizeFitter;
        float nameHeight = fitter && fitter.sprite ? fitter.sprite.transform.localScale.y : 0.5f;
        plate.transform.localPosition = source.localPosition + Vector3.down * (nameHeight * 1.15f * row + 0.05f) * source.localScale.y;
        plate.transform.localRotation = source.localRotation;
        plate.transform.localScale = source.localScale;
        plate.SetActive(true);
        text.ForceMeshUpdate();
        var myFitter = plate.GetComponentInChildren<TextMeshSizeFitter>(true);
        if (myFitter) myFitter.AdjustSize();
        return plate;
    }

    public static void Hide()
    {
        if (plate) Object.Destroy(plate);
        if (plate2) Object.Destroy(plate2);
        plate = plate2 = null;
        shownOn = null;
    }
}

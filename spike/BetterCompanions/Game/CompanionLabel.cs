using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace BetterCompanions;

/// <summary>
/// The text over the companion's head: its name and health; downed, "hold F to revive" and how far the revive has got.
/// </summary>
internal sealed class CompanionLabel
{
    readonly TextMeshPro text;
    string shown;

    public CompanionLabel(GameObject root)
    {
        var go = new GameObject("BC_Label");
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = new Vector3(0f, 2.55f, 0f);
        text = go.AddComponent<TextMeshPro>();
        var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderByDescending(f => f.name.IndexOf("Ember", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
        if (font) text.font = font;
        text.fontSize = 2.6f;
        text.alignment = TextAlignmentOptions.Center;
        text.outlineWidth = 0.2f;
        text.outlineColor = new Color32(0, 0, 0, 200);
        text.rectTransform.sizeDelta = new Vector2(8f, 1.5f);
        var like = root.GetComponentsInChildren<SpriteRenderer>(true).FirstOrDefault();
        var mr = text.GetComponent<MeshRenderer>();
        if (like && mr) { mr.sortingLayerID = like.sortingLayerID; mr.sortingOrder = 32000; }
    }

    /// <summary>Name and health; downed: the revive prompt and progress (0..1).</summary>
    public void Show(string name, int health, int max, bool down, float revive)
    {
        if (!text) return;
        string s = down
            ? $"<color=#FF7A7A>{name} is down</color>\n<size=70%>hold F to revive {Bar(revive)}</size>"
            : $"<color=#7FE0EC>{name}</color>\n<size=70%>{Bar(max > 0 ? (float)health / max : 0f)}</size>";
        if (s != shown) { shown = s; text.text = s; }
        text.transform.rotation = Quaternion.identity;
    }

    static string Bar(float f)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(f * 10f), 0, 10);
        return "<color=#7FE08A>" + new string('|', n) + "</color><color=#555555>" + new string('|', 10 - n) + "</color>";
    }
}

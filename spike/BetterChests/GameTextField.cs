using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// A text box in the game's own style: a copy of one of the game's text fields (character name, world name / seed:
/// TMP_InputField), its listeners and game scripts stripped. If none is loaded, one built from the game's pieces (the
/// InputFieldBackground sprite, its font). Real uGUI: it takes typing through the game's input system, pastes with
/// Ctrl+V, and lives in the screen it's put in.
/// </summary>
internal static class GameTextField
{
    public static TMP_InputField Make(Transform parent, string name, string placeholder, int limit)
    {
        TMP_InputField field = null;
        var template = Resources.FindObjectsOfTypeAll<TMP_InputField>()
            .Where(f => f && !f.name.StartsWith("BC_"))
            .OrderBy(f => f.name.Contains("Name") ? 0 : f.name.Contains("Seed") ? 1 : 2)
            .FirstOrDefault();
        if (template)
        {
            try
            {
                var holder = new GameObject("BC_FieldHolder");
                holder.SetActive(false);   // copied switched off: none of its scripts start
                var copy = Object.Instantiate(template.gameObject, holder.transform);
                foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb && mb.GetType().Assembly == typeof(Chest).Assembly) Object.DestroyImmediate(mb);   // the game's own (validation, saving the name...)
                copy.transform.SetParent(parent, false);
                Object.Destroy(holder);
                field = copy.GetComponent<TMP_InputField>();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Text field copy: {e.Message}"); field = null; }
        }
        if (!field) field = Build(parent);
        field.gameObject.name = name;
        field.onValueChanged.RemoveAllListeners();
        field.onEndEdit.RemoveAllListeners();
        field.onSubmit.RemoveAllListeners();
        field.onSelect.RemoveAllListeners();
        field.onDeselect.RemoveAllListeners();
        field.characterLimit = limit;
        field.contentType = TMP_InputField.ContentType.Standard;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.text = "";
        field.readOnly = false;            // (the copied field may have been a read-only one)
        field.interactable = true;
        field.characterValidation = TMP_InputField.CharacterValidation.None;
        field.inputValidator = null;
        field.onValidateInput = null;
        if (field.textComponent) field.textComponent.alignment = TextAlignmentOptions.MidlineLeft;   // typed from the left
        if (field.placeholder is TMP_Text ph) { ph.text = placeholder; ph.alignment = TextAlignmentOptions.MidlineLeft; }
        var le = field.GetComponent<LayoutElement>() ?? field.gameObject.AddComponent<LayoutElement>();
        le.ignoreLayout = true;   // placed by us, not by the panel's layout
        field.gameObject.SetActive(true);
        return field;
    }

    /// <summary>From the game's pieces: its field background, its font.</summary>
    static TMP_InputField Build(Transform parent)
    {
        var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name.Contains("Emberfont")) ?? TMP_Settings.defaultFontAsset;
        var bg = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "InputFieldBackground");

        var go = new GameObject("BC_Field", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = bg; img.type = Image.Type.Sliced;
        img.color = new Color(0.12f, 0.1f, 0.09f, 0.92f);

        var area = new GameObject("Text Area", typeof(RectTransform));
        area.transform.SetParent(go.transform, false);
        var art = (RectTransform)area.transform;
        art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
        art.offsetMin = new Vector2(8, 3); art.offsetMax = new Vector2(-8, -3);
        area.AddComponent<RectMask2D>();

        TextMeshProUGUI Label(string n, Color c)
        {
            var t = new GameObject(n, typeof(RectTransform));
            t.transform.SetParent(area.transform, false);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var tmp = t.AddComponent<TextMeshProUGUI>();
            if (font) tmp.font = font;
            tmp.fontSize = 18; tmp.color = c;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.enableWordWrapping = false;
            return tmp;
        }
        var placeholder = Label("Placeholder", new Color(1f, 0.88f, 0.7f, 0.5f));
        var text = Label("Text", new Color(1f, 0.93f, 0.82f));

        var field = go.AddComponent<TMP_InputField>();
        field.textViewport = art;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.fontAsset = font;
        field.pointSize = 18;
        return field;
    }
}

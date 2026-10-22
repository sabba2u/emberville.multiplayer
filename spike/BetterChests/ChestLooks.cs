using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterChests;

/// <summary>
/// V at a chest: give it a name, a colour, and what it takes (ChestFilter: weapons only...), in a window of the game's own
/// look (ChestUi). The name replaces the chest's own name plate (the game's font; hold the
/// game's show-names key to see every chest's), the colour tints the chest's art. Kept per world, by the chest's save
/// id, in "&lt;world&gt;.bcchests" next to the world's save.
///
/// For other mods (the multiplayer mod shares them): Changed fires on every local edit (id, name, colour hex; "" =
/// none, what it takes: ItemCategory bits, 0 = anything), Set(id, name, hex, takes) applies one from elsewhere without
/// firing it, All() lists every one in this world.
/// </summary>
[HarmonyPatch]
public static class ChestLooks
{
    static ConfigEntry<Key> key;
    public static string KeyName => key.Value.ToString();

    /// <summary>A local edit: chest id, name ("" = the game's), colour as RRGGBB ("" = none).</summary>
    public static Action<string, string, string, int> Changed;

    public static void Bind(ConfigFile config) =>
        key = config.Bind("ChestLooks", "Key", Key.V, "Press at a chest to name it and give it a colour");

    // the swatches: soft enough that the chest's wood still shows through (they multiply its art)
    static readonly (string name, string hex)[] Palette =
    {
        ("None", ""), ("Red", "FF8A80"), ("Orange", "FFC080"), ("Yellow", "FFF080"), ("Green", "A8F0A0"),
        ("Teal", "90E8E0"), ("Blue", "98C0FF"), ("Purple", "C8A8FF"), ("Pink", "FFA8D8"), ("Grey", "B8B8B8"), ("Dark", "707080"),
    };

    sealed class Entry { public string Name = "", Hex = ""; public int Takes; }

    // what a chest can be set to take: the game's own item categories
    internal static readonly (string name, ItemCategory cat)[] Kinds =
    {
        ("Weapons", ItemCategory.Weapon), ("Armour", ItemCategory.Armor), ("Materials", ItemCategory.Material),
        ("Consumables", ItemCategory.Consumable), ("Gear", ItemCategory.Gear), ("Placeables", ItemCategory.Placeable),
    };

    /// <summary>What this chest takes (ItemCategory bits; 0 = anything).</summary>
    public static int TakesOf(string id)
    {
        Load();
        return id != null && looks.TryGetValue(id, out var e) ? e.Takes : 0;
    }

    /// <summary>"Weapons / Armour" for these bits.</summary>
    internal static string TakesText(int takes)
    {
        var parts = new List<string>();
        foreach (var (name, cat) in Kinds) if ((takes & (int)cat) != 0) parts.Add(name);
        return string.Join(" / ", parts);
    }

    static readonly Dictionary<string, Entry> looks = new Dictionary<string, Entry>();
    static string loadedFor;

    // ---- the key and the box (the game's own window, buttons, text box and checkboxes: ChestUi) ----

    static Chest editing;
    static string nameEdit = "", hexEdit = "";
    static int takesEdit;
    static RectTransform box;
    static TMPro.TMP_InputField nameField;
    static readonly List<(string hex, GameObject mark)> swatches = new List<(string, GameObject)>();
    static readonly List<(ItemCategory cat, Action<bool> set)> checks = new List<(ItemCategory, Action<bool>)>();
    static TMPro.TextMeshProUGUI takesNote;
    static List<(UnityEngine.InputSystem.InputActionMap map, bool on)> savedMaps;

    public static bool Editing => editing;

    /// <summary>Every frame, with the chest the player is at (null = none).</summary>
    public static void Tick(Chest at)
    {
        Load();
        Refresh();
        ChestUi.Tooltip(editing ? "" : RefusedSlots.Hover);   // why a greyed-out bag slot can't go in
        var kb = Keyboard.current;
        if (editing)
        {
            if (!editing || box == null || kb != null && kb.escapeKey.wasPressedThisFrame) Close();
            else if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) { Save(editing, nameEdit, hexEdit, takesEdit); Close(); }
            return;
        }
        if (at == null || kb == null || !Application.isFocused) return;
        var k = kb[key.Value];
        if (k == null || !k.wasPressedThisFrame) return;
        Open(at);
    }

    static void Open(Chest chest)
    {
        if (string.IsNullOrEmpty(chest.ID)) return;
        editing = chest;
        looks.TryGetValue(chest.ID, out var e);
        nameEdit = e?.Name ?? "";
        hexEdit = e?.Hex ?? "";
        takesEdit = e?.Takes ?? 0;
        try { Build(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"Chest box: {ex}"); editing = null; return; }
        // the game mustn't walk / attack / open menus while it's up; its UI actions on (they drive the buttons), but
        // not its Esc (that would open the pause menu: ours closes the box)
        savedMaps = new List<(UnityEngine.InputSystem.InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            savedMaps.Add((map, map.enabled));
            if (map.name == "UI") { map.Enable(); MainInput.Actions.UI.Cancel.Disable(); }
            else map.Disable();
        }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        nameField.ActivateInputField();
    }

    static void Close()
    {
        editing = null;
        if (box) UnityEngine.Object.Destroy(box.gameObject);
        box = null;
        MainInput.Actions.UI.Cancel.Enable();
        if (savedMaps != null) foreach (var (map, was) in savedMaps) { if (was) map.Enable(); else map.Disable(); }
        savedMaps = null;
    }

    static void Build()
    {
        if (box) UnityEngine.Object.Destroy(box.gameObject);
        swatches.Clear(); checks.Clear();
        const float W = 560, H = 400;
        box = ChestUi.Window("Customize this chest", new Vector2(W, H), out _);

        var l = ChestUi.Label(box, "Name", 20, ChestUi.Dim);
        ChestUi.Place(l.rectTransform, 40, 66, 90, 34);
        nameField = GameTextField.Make(box, "BC_ChestName", "Chest", 32);
        ChestUi.Place((RectTransform)nameField.transform, 130, 66, W - 170, 34);
        nameField.text = nameEdit;
        nameField.onValueChanged.AddListener(v => nameEdit = v ?? "");

        l = ChestUi.Label(box, "Colour", 20, ChestUi.Dim);
        ChestUi.Place(l.rectTransform, 40, 118, 90, 30);
        for (int i = 0; i < Palette.Length; i++)
        {
            var (pname, hex) = Palette[i];
            var b = ChestUi.Button(box, "", () => { hexEdit = hex; MarkSwatch(); });
            var r = (RectTransform)b.transform;
            ChestUi.Place(r, 130 + i * 36, 116, 32, 32);
            var fill = ChestUi.Rect("Colour", r);
            ChestUi.Stretch(fill, 5, 5, 5, 5);
            var img = fill.gameObject.AddComponent<UnityEngine.UI.Image>();
            img.color = hex == "" ? new Color(0.22f, 0.2f, 0.2f) : Parse(hex);
            img.raycastTarget = false;
            if (hex == "") { var x = ChestUi.Label(fill, "x", 18, ChestUi.Dim, TMPro.TextAlignmentOptions.Center); ChestUi.Stretch(x.rectTransform); }
            var mark = ChestUi.Rect("Picked", r);
            ChestUi.Stretch(mark, -3, -3, -3, -3);
            var mi = ChestUi.Img(mark.gameObject, "UI_Hud_CommonElements2_16", ChestUi.Gold, 1f);
            mi.raycastTarget = false;
            if (!mi.sprite) mi.color = new Color(1f, 0.85f, 0.4f, 0.6f);
            swatches.Add((hex, mark.gameObject));
        }
        MarkSwatch();

        l = ChestUi.Label(box, "Takes", 20, ChestUi.Dim);
        ChestUi.Place(l.rectTransform, 40, 168, 90, 30);
        for (int i = 0; i < Kinds.Length; i++)
        {
            var cat = Kinds[i].cat;
            var set = ChestUi.Checkbox(box, Kinds[i].name, (takesEdit & (int)cat) != 0,
                on => { takesEdit = on ? takesEdit | (int)cat : takesEdit & ~(int)cat; NoteTakes(); },
                130 + (i % 3) * 136, 170 + (i / 3) * 34, 134);
            checks.Add((cat, set));
        }
        takesNote = ChestUi.Label(box, "", 17, ChestUi.Dim);
        ChestUi.Place(takesNote.rectTransform, 130, 240, W - 170, 26);
        NoteTakes();

        var save = ChestUi.Button(box, "Save", () => { Save(editing, nameEdit, hexEdit, takesEdit); Close(); }, square: true);
        ChestUi.Place((RectTransform)save.transform, 40, H - 84, 140, 40);
        var clear = ChestUi.Button(box, "Clear", () => { Save(editing, "", "", 0); Close(); }, square: true);
        ChestUi.Place((RectTransform)clear.transform, 196, H - 84, 140, 40);
        var cancel = ChestUi.Button(box, "Cancel", Close, square: true);
        ChestUi.Place((RectTransform)cancel.transform, W - 180, H - 84, 140, 40);
        var hint = ChestUi.Label(box, "Enter saves, Esc cancels", 16, ChestUi.Dim, TMPro.TextAlignmentOptions.Center);
        ChestUi.Place(hint.rectTransform, 40, H - 40, W - 80, 22);
    }

    static void MarkSwatch() { foreach (var (hex, mark) in swatches) if (mark) mark.SetActive(hex == hexEdit); }

    static void NoteTakes()
    {
        if (takesNote) takesNote.text = takesEdit == 0 ? "Nothing ticked: it takes anything" : "Only these go in";
    }

    // ---- storing and applying ----

    static void Save(Chest chest, string name, string hex, int takes)
    {
        if (!chest || string.IsNullOrEmpty(chest.ID)) return;
        Set(chest.ID, name, hex, takes);
        try { Changed?.Invoke(chest.ID, (name ?? "").Trim(), hex ?? "", takes); } catch (Exception ex) { Plugin.Log.LogWarning(ex); }
    }

    /// <summary>One chest's name and colour (from here, or from another mod: the multiplayer mod's teammates).</summary>
    public static void Set(string id, string name, string hex, int takes)
    {
        if (string.IsNullOrEmpty(id)) return;
        Load();
        name = (name ?? "").Trim();
        hex = hex ?? "";
        if (name.Length == 0 && hex.Length == 0 && takes == 0) looks.Remove(id);
        else looks[id] = new Entry { Name = name, Hex = hex, Takes = takes };
        applied.Clear();   // re-dress everything on the next Refresh
        Write();
    }

    /// <summary>Every chest's look in this world, as (id, name, hex).</summary>
    public static List<(string id, string name, string hex, int takes)> All()
    {
        Load();
        var list = new List<(string, string, string, int)>();
        foreach (var kv in looks) list.Add((kv.Key, kv.Value.Name, kv.Value.Hex, kv.Value.Takes));
        return list;
    }

    static float nextRefresh;
    static readonly Dictionary<Chest, string> applied = new Dictionary<Chest, string>();   // what each chest wears now
    static readonly Dictionary<SpriteRenderer, Color> ownColour = new Dictionary<SpriteRenderer, Color>();

    /// <summary>Twice a second: every chest here in its colour and name.</summary>
    static void Refresh()
    {
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.5f;
        foreach (var chest in UnityEngine.Object.FindObjectsByType<Chest>(FindObjectsSortMode.None))
        {
            if (!chest || string.IsNullOrEmpty(chest.ID)) continue;
            looks.TryGetValue(chest.ID, out var e);
            string want = e == null ? "" : e.Hex + "|" + e.Name + "|" + e.Takes;
            bool plate = e != null && (e.Name.Length > 0 || e.Takes != 0);
            if (applied.TryGetValue(chest, out var have) && have == want) { if (plate) Rename(chest, e); continue; }
            applied[chest] = want;
            Tint(chest, e == null || e.Hex == "" ? (Color?)null : Parse(e.Hex));
            if (plate) Rename(chest, e);
            else if (!string.IsNullOrEmpty(have)) RestoreTitle(chest);
        }
    }

    static void Tint(Chest chest, Color? c)
    {
        foreach (var sr in chest.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || (chest.titlePivot && sr.transform.IsChildOf(chest.titlePivot.transform))) continue;   // the art, not the name plate
            if (!ownColour.TryGetValue(sr, out var own)) ownColour[sr] = own = sr.color;
            sr.color = c.HasValue ? new Color(own.r * c.Value.r, own.g * c.Value.g, own.b * c.Value.b, own.a) : own;
        }
    }

    /// <summary>Our name on the chest's plate (after the game's own text, which UpdateTitle writes).</summary>
    static void Rename(Chest chest, Entry e)
    {
        if (!chest.titlePivot) return;
        string name = e.Name;
        if (name.Length == 0) { try { name = chest.Title.GetLocalizedString(); } catch { name = "Chest"; } }   // a filter, no name: the game's "Chest"
        if (e.Takes != 0) name += " - " + TakesText(e.Takes);
        // as the game does for its own name: opened and nothing left in it -> "Name (Empty)"
        try { if (chest.alreadyOpened && Singleton<ChestManager>.main.ChestCount(chest.ID) <= 0) name += " (Empty)"; } catch { }
        var text = chest.titlePivot.GetComponentInChildren<TMP_Text>(true);
        if (!text || text.text == name) return;
        text.text = name;
        text.ForceMeshUpdate();
        if (chest.titleSizeFitter) chest.titleSizeFitter.AdjustSize();
    }

    static void RestoreTitle(Chest chest)
    {
        try { chest.UpdateTitle(); } catch { }
    }

    [HarmonyPatch(typeof(Chest), "UpdateTitle"), HarmonyPostfix]
    static void AfterTitle(Chest __instance)
    {
        if (__instance && !string.IsNullOrEmpty(__instance.ID) && looks.TryGetValue(__instance.ID, out var e) && (e.Name.Length > 0 || e.Takes != 0))
            Rename(__instance, e);
    }

    static Color Parse(string hex) => ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.white;

    // ---- the file ----

    static string FileFor(string world) => SaveLoad.GetPath(world + ".bcchests");

    static void Load()
    {
        string world = GameData.HasWorldData ? GameData.GetWorldPrefix : null;
        if (world == loadedFor) return;
        loadedFor = world;
        looks.Clear();
        applied.Clear();
        if (world == null) return;
        try
        {
            string path = FileFor(world);
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3 && parts[0].Length > 0)
                    looks[parts[0]] = new Entry { Hex = parts[1], Name = parts[2], Takes = parts.Length >= 4 && int.TryParse(parts[3], out int t) ? t : 0 };
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Chest looks: {e.Message}"); }
    }

    static void Write()
    {
        if (loadedFor == null) return;
        try
        {
            var lines = new List<string>();
            foreach (var kv in looks) lines.Add($"{kv.Key}\t{kv.Value.Hex}\t{kv.Value.Name.Replace('\t', ' ').Replace('\n', ' ')}\t{kv.Value.Takes}");
            File.WriteAllLines(FileFor(loadedFor), lines);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Chest looks: {e.Message}"); }
    }
}

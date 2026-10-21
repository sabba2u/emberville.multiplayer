using System.Collections.Generic;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BetterPetsAndSummons;

/// <summary>
/// The pets menu in the game's own look (PetUi), for when the multiplayer mod isn't installed (it has its own Insert
/// menu with a Pets tab then; this one stays out of its way). Insert opens it: pets on / off, befriend or switch to a
/// creature, its name and colour, call it or send it home, and the two staffs. Esc or Insert closes it.
/// </summary>
internal static class PetWindow
{
    const string MultiplayerGuid = "spike.embervilleghost";
    static bool? standalone;
    static RectTransform win;
    static List<(InputActionMap map, bool on)> savedMaps;

    static bool Standalone => standalone ??= !Chainloader.PluginInfos.ContainsKey(MultiplayerGuid);
    public static bool Open => win;

    public static void Tick()
    {
        if (!Standalone) return;
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused) return;
        if (Open)
        {
            if (kb.escapeKey.wasPressedThisFrame || kb.insertKey.wasPressedThisFrame) Close();
            return;
        }
        if (!kb.insertKey.wasPressedThisFrame || !LocalPlayer.Get() || UIPanels.HasOpenedPanel || !MainInput.Actions.Player.enabled) return;
        Show();
    }

    static void Show()
    {
        try { Build(); }
        catch (System.Exception e) { Plugin.Logger_.LogWarning($"Pets menu: {e}"); if (win) Object.Destroy(win.gameObject); win = null; return; }
        // the game's controls off while it's up; its UI actions on (they drive the buttons), its Esc off (ours closes it)
        savedMaps = new List<(InputActionMap, bool)>();
        foreach (var map in MainInput.Actions.asset.actionMaps)
        {
            savedMaps.Add((map, map.enabled));
            if (map.name == "UI") { map.Enable(); MainInput.Actions.UI.Cancel.Disable(); }
            else map.Disable();
        }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    static void Close()
    {
        if (win) Object.Destroy(win.gameObject);
        win = null;
        MainInput.Actions.UI.Cancel.Enable();
        if (savedMaps != null) foreach (var (map, was) in savedMaps) { if (was) map.Enable(); else map.Disable(); }
        savedMaps = null;
    }

    /// <summary>Rebuilt after every change (it's small).</summary>
    static void Rebuild()
    {
        if (!win) return;
        Object.Destroy(win.gameObject);
        win = null;
        Build();
    }

    static void Build()
    {
        const float W = 700, H = 690;
        win = PetUi.Window("Pets and Summons", new Vector2(W, H), out _);
        float y = 64;

        PetUi.Checkbox(win, "Pets on", Pet.enabled.Value, on => { Pet.SetEnabled(on); Rebuild(); }, 40, y, 200);
        y += 40;

        if (Pet.enabled.Value)
        {
            if (Pet.owned.Value)
            {
                var info = PetUi.Label(win, $"{Pet.Name}, a {Pet.Kind.Label}: level {Pet.Level} ({Pet.xp.Value}/{Pet.XpNeeded} XP)", 20, PetUi.Gold);
                PetUi.Place(info.rectTransform, 40, y, W - 80, 28);
                y += 34;

                var l = PetUi.Label(win, "Name", 19, PetUi.Dim);
                PetUi.Place(l.rectTransform, 40, y, 80, 32);
                var field = GameTextField.Make(win, "BP_PetName", Pet.Kind.DefaultName, 20);
                PetUi.Place((RectTransform)field.transform, 120, y, 240, 32);
                field.text = Pet.petName.Value;
                field.onEndEdit.AddListener(v => { Pet.petName.Value = string.IsNullOrWhiteSpace(v) ? Pet.Kind.DefaultName : v.Trim(); });
                PetUi.Checkbox(win, "With you (out)", Pet.outNow.Value, on => { Pet.outNow.Value = on; Rebuild(); }, 390, y + 2, 240);
                y += 44;

                l = PetUi.Label(win, "Colour", 19, PetUi.Dim);
                PetUi.Place(l.rectTransform, 40, y, 80, 32);
                var colours = Pet.Colours;
                for (int i = 0; i < colours.Length; i++)
                {
                    var (cname, hex) = colours[i];
                    var b = PetUi.Button(win, "", () => { Pet.petColor.Value = hex; Rebuild(); });
                    var r = (RectTransform)b.transform;
                    PetUi.Place(r, 120 + i * 38, y, 34, 34);
                    var fill = PetUi.Rect("Colour", r);
                    PetUi.Stretch(fill, 6, 6, 6, 6);
                    var img = fill.gameObject.AddComponent<UnityEngine.UI.Image>();
                    img.color = ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.white;
                    img.raycastTarget = false;
                    if (Pet.petColor.Value.Equals(hex, System.StringComparison.OrdinalIgnoreCase))
                    {
                        var mark = PetUi.Rect("Picked", r);
                        PetUi.Stretch(mark, -3, -3, -3, -3);
                        var mi = PetUi.Img(mark.gameObject, "UI_Hud_CommonElements2_16", PetUi.Gold, 1f);
                        mi.raycastTarget = false;
                    }
                }
                y += 48;
            }
            else
            {
                var hint = PetUi.Label(win, "Pick a creature to befriend (it has to have been near you once).", 18, PetUi.Dim);
                PetUi.Place(hint.rectTransform, 40, y, W - 80, 28);
                y += 34;
            }

            var head = PetUi.Label(win, Pet.owned.Value ? "Switch to" : "Befriend", 19, PetUi.Dim);
            PetUi.Place(head.rectTransform, 40, y, 200, 28);
            y += 32;
            var kinds = Pet.Kinds;
            for (int i = 0; i < kinds.Length; i++)
            {
                var k = kinds[i];
                bool can = PetBody.Available(k);
                bool current = Pet.owned.Value && k == Pet.Kind;
                var b = PetUi.Button(win, k.Label + (current ? " (yours)" : ""), () => { Pet.Switch(k); Rebuild(); });
                PetUi.Place((RectTransform)b.transform, 40 + (i % 3) * 210, y + (i / 3) * 40, 200, 34);
                b.interactable = can && !current;
            }
            y += ((kinds.Length + 2) / 3) * 40 + 8;
        }

        var staffs = PetUi.Label(win, "Staffs", 19, PetUi.Dim);
        PetUi.Place(staffs.rectTransform, 40, y, 200, 28);
        y += 32;
        var cl = PetUi.Button(win, "Staff of Clucking", CluckStaff.Give);
        PetUi.Place((RectTransform)cl.transform, 40, y, 250, 38);
        var rs = PetUi.Button(win, "Staff of the Risen", RisenStaff.Give);
        PetUi.Place((RectTransform)rs.transform, 310, y, 250, 38);

        var close = PetUi.Button(win, "Close", Close);
        PetUi.Place((RectTransform)close.transform, W - 180, H - 70, 140, 40);
        var keys = PetUi.Label(win, "Insert or Esc closes this", 16, PetUi.Dim);
        PetUi.Place(keys.rectTransform, 40, H - 62, 300, 24);
    }
}

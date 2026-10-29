using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// MODS: a button above Options on the main menu and on the Esc menu (a copy of the game's Options button). It opens
/// the Insert menu, as the key does.
/// </summary>
internal static class ModsButton
{
    static float nextLook;

    public static void Tick()
    {
        if (Time.unscaledTime < nextLook) return;
        nextLook = Time.unscaledTime + 1f;
        try
        {
            // (the menus kept once found, and a button row done once: two searches of the whole scene twice a second,
            // and a walk of every button, were a hitch you could see)
            if (!menu) menu = UnityEngine.Object.FindAnyObjectByType<MainMenuManager>();
            if (menu && menu.buttonsMain && done.Add(menu.buttonsMain.GetInstanceID())) AddButton(menu.buttonsMain.transform);
            if (!ui) ui = UnityEngine.Object.FindAnyObjectByType<UIManager>();
            if (ui && ui.buttonsPause && done.Add(ui.buttonsPause.GetInstanceID())) AddButton(ui.buttonsPause.transform);
        }
        catch (Exception e) { NetLog.Warn($"Mods button: {e.Message}", toast: false); nextLook = Time.unscaledTime + 10f; }
    }

    static readonly HashSet<int> noOptions = new();
    static readonly HashSet<int> done = new();   // (button rows given ours already)
    static MainMenuManager menu;
    static UIManager ui;

    static void AddButton(Transform buttons)
    {
        if (noOptions.Contains(buttons.GetInstanceID()) || buttons.GetComponentsInChildren<Transform>(true).Any(t => t.name == "EV_ModsButton")) return;   // (the buttons may sit deeper than its children)
        var options = buttons.GetComponentsInChildren<Button>(true).FirstOrDefault(b => Calls(b, "ShowOptions"));
        if (!options) { noOptions.Add(buttons.GetInstanceID()); NetLog.Warn($"Mods button: no Options button under {buttons.name}", toast: false); return; }
        var go = UnityEngine.Object.Instantiate(options.gameObject, options.transform.parent, false);
        go.name = "EV_ModsButton";
        go.transform.SetSiblingIndex(options.transform.GetSiblingIndex());
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb && mb.GetType().FullName == "UnityEngine.Localization.Components.LocalizeStringEvent") UnityEngine.Object.DestroyImmediate(mb);
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = "MODS";
        var b = go.GetComponent<Button>();
        b.onClick = new Button.ButtonClickedEvent();
        b.onClick.AddListener(() => ImGuiUnity.SetOpen(!ImGuiUnity.IsOpen));   // (as Insert: the mods' menu)
        // the controller's up / down: through it
        var nav = options.navigation;
        if (nav.mode == Navigation.Mode.Explicit)
        {
            var above = nav.selectOnUp;
            var mine = nav; mine.selectOnDown = options; b.navigation = mine;
            nav.selectOnUp = b; options.navigation = nav;
            if (above) { var n = above.navigation; n.selectOnDown = b; above.navigation = n; }
        }
        NetLog.Info($"Mods button added ({buttons.name}; next to {options.name} under {options.transform.parent.name}: " +
            string.Join(", ", options.transform.parent.Cast<Transform>().Select(t => t.name)) + ")", toast: false);
    }

    static bool Calls(Button b, string method)
    {
        var e = b.onClick;
        for (int i = 0; i < e.GetPersistentEventCount(); i++) if (e.GetPersistentMethodName(i) == method) return true;
        return false;
    }
}

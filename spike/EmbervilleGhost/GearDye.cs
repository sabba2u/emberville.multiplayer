using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleGhost;

/// <summary>
/// Dyes for what you wear: right-click an equipped item in the inventory's gear slots, pick one of 12 colours, Dye.
///
/// Each worn piece is its own sprites (AddonsHandler.equipFolders: a folder per slot, the item's layerIndex picks the
/// child; a held weapon is a child of weaponFolder), so a dye is the tint of those sprites (SpriteRenderer.color; it
/// multiplies the art). The dye belongs to the item: kept in its ItemRandomValues.strAttributes under a key the game
/// doesn't use, which the save writes and reads with the item, so it comes back with the item when you wear it again.
/// Holding the Hunter's Hook dresses every armour piece in its yellow on top (the dyes come back when it leaves your
/// hand). Your teammates see it: dyed sprites carry a Mark, and LookSync sends their tints.
/// </summary>
[HarmonyPatch]
internal static class GearDye
{
    /// <summary>On a sprite we tinted (so LookSync knows which tints are dyes, and they can be taken off again).</summary>
    public sealed class Mark : MonoBehaviour { }

    const ItemRandomValues.Attr DyeKey = (ItemRandomValues.Attr)9001;
    static readonly Color HookYellow = new Color(1f, 0.85f, 0.15f);

    // the 12 boxes (they multiply the art: strong colours, so they show on dark pieces too)
    public static readonly (string name, Color colour)[] Palette =
    {
        ("None", Color.white),
        ("Red", new Color(1f, 0.32f, 0.3f)), ("Orange", new Color(1f, 0.6f, 0.25f)), ("Yellow", new Color(1f, 0.9f, 0.3f)),
        ("Green", new Color(0.45f, 0.95f, 0.4f)), ("Teal", new Color(0.35f, 0.9f, 0.85f)), ("Blue", new Color(0.4f, 0.6f, 1f)),
        ("Purple", new Color(0.72f, 0.48f, 1f)), ("Pink", new Color(1f, 0.55f, 0.85f)), ("Brown", new Color(0.72f, 0.5f, 0.32f)),
        ("Grey", new Color(0.62f, 0.62f, 0.66f)), ("Black", new Color(0.3f, 0.3f, 0.34f)),
    };

    // ---- the dye on an item ----

    public static Color? DyeOf(ItemObject item)
    {
        var attrs = item?.randomValues?.strAttributes;
        var a = attrs?.FirstOrDefault(x => x != null && x.Key == DyeKey);
        return a != null && ColorUtility.TryParseHtmlString("#" + a.Value, out var c) ? c : null;
    }

    static void SetDye(ItemObject item, Color? colour)
    {
        if (item?.randomValues == null) return;
        var attrs = item.randomValues.strAttributes ??= new List<ItemRandomValues.StrItemAttribute>();
        attrs.RemoveAll(x => x == null || x.Key == DyeKey);
        if (colour is Color c && c != Color.white) attrs.Add(new ItemRandomValues.StrItemAttribute(DyeKey, ColorUtility.ToHtmlStringRGB(c)));
        next = 0f;   // on now
    }

    // ---- tinting ----

    // (the tint goes on two ways: the sprite's own colour, and, for sprites drawn with a material that colours by its
    // _Color (the game's clothes colour material ignores the sprite's colour), that colour for this sprite alone)
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock block;
    static readonly HashSet<string> shadersSaid = new();

    public static void Tint(SpriteRenderer sr, Color c)
    {
        if (!sr) return;
        sr.color = new Color(c.r, c.g, c.b, sr.color.a);
        var mat = sr.sharedMaterial;
        if (mat && mat.HasProperty(ColorId))
        {
            block ??= new MaterialPropertyBlock();
            sr.GetPropertyBlock(block);
            block.SetColor(ColorId, mat.GetColor(ColorId) * new Color(c.r, c.g, c.b, 1f));
            sr.SetPropertyBlock(block);
        }
        if (mat && shadersSaid.Add(mat.shader ? mat.shader.name : "?"))
            NetLog.Info($"Dye: gear drawn with {mat.name} ({(mat.shader ? mat.shader.name : "?")}, _Color: {mat.HasProperty(ColorId)})", toast: false);
        if (!sr.GetComponent<Mark>()) sr.gameObject.AddComponent<Mark>();
    }

    public static void Untint(SpriteRenderer sr)
    {
        if (!sr) return;
        sr.color = new Color(1f, 1f, 1f, sr.color.a);
        if (sr.HasPropertyBlock())
        {
            block ??= new MaterialPropertyBlock();
            sr.GetPropertyBlock(block);
            block.Clear();
            sr.SetPropertyBlock(block);
        }
        var m = sr.GetComponent<Mark>();
        if (m) UnityEngine.Object.Destroy(m);
    }

    static readonly HashSet<SpriteRenderer> tinted = new();
    static int lastCount = -1;
    static float next;

    /// <summary>Every frame (Plugin): our worn pieces in their dyes (a few times a second: gear changes swap sprites).</summary>
    public static void Tick()
    {
        TickWindow();
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.25f;
        var me = LocalPlayer.Get();
        if (!me || me.characterStats == null) return;
        var addons = me.GetComponentInChildren<AddonsHandler>(true);
        if (!addons) return;
        bool hook = HunterHook.HeldBy(me) && !me.IsDead;
        var want = new Dictionary<SpriteRenderer, Color>();
        foreach (var item in me.characterStats.EquippedItems)
        {
            if (item?.data == null) continue;
            List<Transform> folders; int index; bool armour;
            if (item.data is CollectableWeapon w && w.weapon != null) { folders = addons.weaponFolder ? new List<Transform> { addons.weaponFolder } : null; index = w.weapon.weaponIndex; armour = false; }
            else if (item.data is CollectableGear g && g.gear != null) { folders = addons.equipFolders.Find(f => f.slot == g.gear.gearSlot).folders; index = g.gear.layerIndex; armour = true; }
            else continue;
            var colour = armour && hook ? HookYellow : DyeOf(item);
            if (colour == null || folders == null || index < 0) continue;
            foreach (var f in folders)
            {
                if (!f || index >= f.childCount) continue;
                foreach (var sr in f.GetChild(index).GetComponentsInChildren<SpriteRenderer>(true)) want[sr] = colour.Value;
            }
        }
        foreach (var sr in tinted) if (sr && !want.ContainsKey(sr)) Untint(sr);
        if (want.Count != lastCount) { lastCount = want.Count; NetLog.Info($"Dye: {want.Count} sprites of worn gear dyed", toast: false); }
        tinted.Clear();
        foreach (var kv in want) { Tint(kv.Key, kv.Value); tinted.Add(kv.Key); }
    }

    // ---- right-click on a gear slot ----

    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UpdateGearInventory)), HarmonyPostfix]
    static void GearSlots(InventoryUI __instance)
    {
        try
        {
            int n = 0;
            foreach (var cgs in __instance.gearSlots)
            {
                if (cgs?.slot == null) continue;
                var slot = cgs.gearSlot;
                cgs.slot.SetRightClickCallback(() => { NetLog.Info($"Dye: right-click on the {slot} slot", toast: false); Open(slot); });
                n++;
            }
            if (!saidSlots) { saidSlots = true; NetLog.Info($"Dye: right-click ready on {n} gear slots", toast: false); }
        }
        catch (Exception e) { NetLog.Warn($"Dye: gear slots: {e.Message}", toast: false); }
    }

    // ---- the box ----

    static GameUi.Window win;
    static ItemObject dyeing;
    static int picked;
    static readonly List<Image> edges = new();

    static bool saidSlots;

    static void Open(GearSlot slot)
    {
        var me = LocalPlayer.Get();
        if (!me || me.characterStats == null) { NetLog.Info("Dye: no player", toast: false); return; }
        var item = me.characterStats.EquippedItems.FirstOrDefault(x => SlotOf(x) is GearSlot s && (s & slot) != 0);
        if (item == null) { NetLog.Info($"Dye: nothing worn in the {slot} slot", toast: false); return; }
        NetLog.Info($"Dye: opening for {item}", toast: false);
        dyeing = item;
        var now = DyeOf(item);
        picked = 0;
        if (now != null) for (int i = 1; i < Palette.Length; i++) if (Close(Palette[i].colour, now.Value)) picked = i;
        Build(item);
        win.Show(true);
        win.Root.SetAsLastSibling();
    }

    static GearSlot? SlotOf(ItemObject x) =>
        x?.data is CollectableWeapon w && w.weapon != null ? w.weapon.gearSlot : x?.data is CollectableGear g && g.gear != null ? g.gear.gearSlot : null;

    static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.03f;

    static void Build(ItemObject item)
    {
        win?.Destroy();
        edges.Clear();
        win = GameUi.MakeWindow("Dye", new Vector2(560, 330), Cancel);
        var name = GameUi.Label(win.Content, item.ToString(), 22, GameUi.Text, TMPro.TextAlignmentOptions.Center);
        GameUi.Fixed(name, -1, 30);
        for (int row = 0; row < 2; row++)
        {
            var r = GameUi.Row(win.Content, 64, 10);
            r.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            for (int col = 0; col < 6; col++)
            {
                int i = row * 6 + col;
                var b = GameUi.Button(r, "", () => Pick(i), 52, 44, square: true);
                b.transition = Selectable.Transition.None;
                var fill = b.transform.Find("Fill")?.GetComponent<Image>();
                if (fill) fill.color = Palette[i].colour;
                if (i == 0) { var x = GameUi.Label(fill ? fill.transform : b.transform, "x", 20, GameUi.Dim, TMPro.TextAlignmentOptions.Center); GameUi.Stretch(x.rectTransform); }
                edges.Add(b.GetComponent<Image>());
            }
        }
        var buttons = GameUi.Row(win.Content, 56, 16);
        buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        GameUi.Button(buttons, "Dye", Dye, 150, 40, square: true);
        GameUi.Button(buttons, "Cancel", Cancel, 150, 40, square: true);
        MarkPicked();
    }

    static void Pick(int i) { picked = i; MarkPicked(); }

    static void MarkPicked()
    {
        for (int i = 0; i < edges.Count; i++)
            if (edges[i]) edges[i].color = i == picked ? GameUi.Gold : new Color(0.3f, 0.22f, 0.16f);
    }

    static void Dye()
    {
        if (dyeing != null) { SetDye(dyeing, picked == 0 ? null : Palette[picked].colour); NetLog.Info($"Dye: {dyeing} dyed {(picked == 0 ? "back to none" : Palette[picked].name)}", toast: false); }
        Cancel();
    }

    static void Cancel()
    {
        dyeing = null;
        win?.Show(false);
    }

    static void TickWindow()
    {
        if (win == null || !win.Shown) return;
        // the inventory closed under it, or Esc
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (!UIPanels.HasOpenedPanel || kb != null && kb.escapeKey.wasPressedThisFrame) Cancel();
    }
}

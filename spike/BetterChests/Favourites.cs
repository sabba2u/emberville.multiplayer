using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BetterChests;

/// <summary>
/// F over an item in the inventory (worn and held ones too) or a chest: a favourite (a little gold star at the slot's top right), F again: not.
/// J there: junk (a red X in the same corner), J again: not. An item is one or the other, never both.
/// Favourites never leave the bag by themselves: X, Shift+X and the chest screen's "send all / send commons" leave
/// them where they are (dragging one in by hand still works). The mark is kept on the item itself (a number attribute
/// the game saves with it; the favourite-weapons mod's F5-F8 keys ignore number attributes, so they still find it).
/// </summary>
[HarmonyPatch]
internal static class Favourites
{
    const ItemRandomValues.Attr Mark = (ItemRandomValues.Attr)85;   // (77-79: the staffs and the hook)
    const ItemRandomValues.Attr JunkMark = (ItemRandomValues.Attr)86;
    static readonly Dictionary<UISlot, (InventorySection section, int index)> slots = new Dictionary<UISlot, (InventorySection, int)>();
    static Sprite star, cross;

    public static bool Is(ItemObject item) => Has(item, Mark);
    public static bool IsJunk(ItemObject item) => Has(item, JunkMark);

    static bool Has(ItemObject item, ItemRandomValues.Attr mark) =>
        item?.randomValues?.strAttributes != null && item.randomValues.strAttributes.Exists(a => a != null && a.Key == mark && a.Value == "1");

    static void Set(ItemObject item, ItemRandomValues.Attr mark, bool on)
    {
        var list = item.randomValues.strAttributes;
        list.RemoveAll(a => a != null && (a.Key == Mark || a.Key == JunkMark));   // (one or the other)
        if (on) list.Add(new ItemRandomValues.StrItemAttribute(mark, "1"));
    }

    /// <summary>Every frame: F over a slot (there's one under the mouse only while the inventory or a chest is open).</summary>
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused) return;
        bool junk = kb.jKey.wasPressedThisFrame;
        if (junk && kb.shiftKey.isPressed) { DropJunk(); return; }
        if (!junk && !kb.fKey.wasPressedThisFrame) return;   // (a slot under the mouse = the screen is open)
        if (ChestLooks.Editing || ChestSearch.Typing) return;
        if (!Singleton<Inventory>.HasMain) return;
        var inv = Singleton<Inventory>.main;
        ItemObject item = null;
        foreach (var kv in slots)
        {
            if (!kv.Key || !kv.Key.isMouseOn || !kv.Key.isActiveAndEnabled) continue;
            var (section, index) = kv.Value;
            item = inv.allItems.Find(x => x.section == section && x.slotIndex == index)?.itemObject;
            break;
        }
        if (item == null)   // what you wear / hold: the equipment slots
            foreach (var kv in worn)
                if (kv.Key && kv.Key.isMouseOn && kv.Key.isActiveAndEnabled) { item = kv.Value; break; }
        if (item?.data == null) return;
        var mark = junk ? JunkMark : Mark;
        bool now = !Has(item, mark);
        Set(item, mark, now);
        QuickStack.Note(typeof(Favourites), item.data.itemIconUI,
            (junk ? (now ? "Junk: " : "No longer junk: ") : (now ? "Favourite: " : "No longer a favourite: ")) + item.ToString(), 1);
        inv.SetInventoryChanged();   // the slots again, with the star / X
        var ui = Object.FindAnyObjectByType<InventoryUI>();
        if (ui) ui.UpdateGearInventory();
    }

    /// <summary>A bag, chest or equipment slot under the mouse (the screen is open): F there is ours.</summary>
    static bool OverSlot()
    {
        foreach (var s in slots.Keys) if (s && s.isMouseOn && s.isActiveAndEnabled) return true;
        foreach (var s in worn.Keys) if (s && s.isMouseOn && s.isActiveAndEnabled) return true;
        return false;
    }

    // F is the game's interact key too, and interacting with an open chest closes it (or, with the bag open next to
    // something, uses that): not while F is marking the item under the mouse
    [HarmonyPatch(typeof(InteractablesHandler), "OnInteract"), HarmonyPrefix]
    static bool NotOverASlot() => !(Keyboard.current != null && Keyboard.current.fKey.isPressed && OverSlot());

    // J is the game's quest screen key: not while J is marking the item under the mouse, or Shift+J dropping the junk
    [HarmonyPatch(typeof(PanelsManager), "ToggleQuestsPanel"), HarmonyPrefix]
    static bool NotJunking() => !(Keyboard.current != null && Keyboard.current.jKey.isPressed && (OverSlot() || Keyboard.current.shiftKey.isPressed));

    /// <summary>Shift+J: everything marked junk out of the bag, onto the ground at your feet (the game's own drop:
    /// still there to pick back up).</summary>
    static void DropJunk()
    {
        if (ChestLooks.Editing || ChestSearch.Typing || !Singleton<Inventory>.HasMain) return;
        var inv = Singleton<Inventory>.main;
        var junk = inv.allItems.FindAll(x => x?.itemObject != null && x.section == InventorySection.Main && IsJunk(x.itemObject));
        int n = 0;
        foreach (var it in junk) if (inv.DisposeItem(it.slotIndex, it.section)) n++;
        QuickStack.Note(typeof(Favourites), null, n == 0 ? "No junk in your bag (J on an item marks it)" : $"Dropped {n} junk item{(n == 1 ? "" : "s")} at your feet", 1);
        if (n > 0) inv.SetInventoryChanged();
    }

    /// <summary>The equipment slots and what's in them (filled by InventoryUI.UpdateGearInventory, not with the rest).</summary>
    static readonly Dictionary<UISlot, ItemObject> worn = new Dictionary<UISlot, ItemObject>();

    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.UpdateGearInventory)), HarmonyPostfix]
    static void AfterGear(InventoryUI __instance)
    {
        worn.Clear();
        if (__instance.player == null || __instance.player.characterStats == null) return;
        foreach (var item in __instance.player.characterStats.EquippedItems)
        {
            GearSlot? at = item?.data is CollectableGear g ? g.gear.gearSlot : item?.data is CollectableWeapon w ? w.weapon.gearSlot : (GearSlot?)null;
            if (!at.HasValue) continue;
            var cs = __instance.gearSlots.Find(x => (x.gearSlot & at.Value) != 0);
            if (cs?.slot) worn[cs.slot] = item;
        }
        foreach (var cs in __instance.gearSlots)
        {
            if (!cs?.slot) continue;
            worn.TryGetValue(cs.slot, out var it);
            Badges(cs.slot, it);
        }
    }

    // ---- the star / X on the slots ----

    [HarmonyPatch(typeof(InventoryUI), "UpdateInventoryUI"), HarmonyPostfix]
    static void AfterFill(List<UISlot> slotList, List<InventoryItem> items, InventorySection section)
    {
        foreach (var gone in new List<UISlot>(slots.Keys)) if (!gone) slots.Remove(gone);
        for (int i = 0; i < slotList.Count; i++)
        {
            var slot = slotList[i];
            if (!slot) continue;
            slots[slot] = (section, i);
            var item = items.Find(x => x.section == section && x.slotIndex == i);
            Badges(slot, item?.itemObject);
        }
    }

    static void Badges(UISlot slot, ItemObject item)
    {
        bool fav = Is(item), junk = IsJunk(item);
        var b = BadgeOf(slot, "BC_Favourite", fav, Star);
        if (b) b.SetActive(fav);
        b = BadgeOf(slot, "BC_Junk", junk, Cross);
        if (b) b.SetActive(junk);
    }

    static GameObject BadgeOf(UISlot slot, string name, bool make, System.Func<Sprite> sprite)
    {
        var t = slot.transform.Find(name);
        if (t) { t.SetAsLastSibling(); return t.gameObject; }
        if (!make) return null;
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(slot.transform, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;   // top right
        rt.anchoredPosition = new Vector2(-3f, -3f);
        var size = ((RectTransform)slot.transform).rect.size;
        float side = Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.3f, 9f, 22f);
        rt.sizeDelta = new Vector2(side, side);
        var img = go.AddComponent<Image>();
        img.sprite = sprite();
        img.raycastTarget = false;
        rt.SetAsLastSibling();
        return go;
    }

    /// <summary>A pixel gold star (9x9, outlined), crisp like the game's own art.</summary>
    static Sprite Star() => star ? star : star = Pixels(new[]
    {
        "....O....",
        "...OYO...",
        "...OYO...",
        "OOOOWYOOO",
        "OYYYYYYYO",
        ".OYYYYYO.",
        "..OYYYO..",
        ".OYYOYYO.",
        ".OOO.OOO.",
    }, c => c == 'O' ? new Color32(60, 34, 6, 255) : c == 'Y' ? new Color32(255, 200, 40, 255) : new Color32(255, 244, 170, 255));

    /// <summary>A bold pixel red X (10x10, outlined).</summary>
    static Sprite Cross() => cross ? cross : cross = Pixels(new[]
    {
        "OOOO..OOOO",
        "ORROOOORRO",
        "ORRROORRRO",
        "OORRRRRROO",
        ".OORRRROO.",
        ".OORRRROO.",
        "OORRRRRROO",
        "ORRROORRRO",
        "ORROOOORRO",
        "OOOO..OOOO",
    }, c => c == 'O' ? new Color32(40, 12, 16, 255) : new Color32(226, 48, 56, 255));

    static Sprite Pixels(string[] rows, System.Func<char, Color32> colour)
    {
        int h = rows.Length, w = rows[0].Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                char c = rows[h - 1 - y][x];
                tex.SetPixel(x, y, c == '.' ? new Color32(0, 0, 0, 0) : colour(c));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
    }

    // ---- marked items on the ground ----

    // its name on the ground (the game shows it while Shift is held, or when you're near): a favourite's in shimmering
    // gold behind a big star, junk's in red behind a bold X (picked up as anything else is). The drops are pooled: a
    // plain one gets its own colour back.
    [HarmonyPatch(typeof(ItemDropObject), nameof(ItemDropObject.Setup)), HarmonyPostfix]
    static void MarkOnName(ItemDropObject __instance)
    {
        var t = __instance.title;
        if (!t) return;
        var look = t.GetComponent<NameLook>() ?? t.gameObject.AddComponent<NameLook>();
        if (!look.Kept) { look.Plain = t.color; look.Kept = true; }
        var item = __instance.itemObj;
        bool fav = Is(item), junk = !fav && IsJunk(item);
        look.enabled = fav;   // (the shimmer)
        if (fav) { t.text = "<b><size=240%><voffset=-0.3em>*</voffset></size> " + t.text + "</b>"; t.color = NameLook.Gold; }
        else if (junk) { t.text = "<b><size=170%>X</size> " + t.text + "</b>"; t.color = new Color32(255, 70, 70, 255); }
        else { t.color = look.Plain; t.ForceMeshUpdate(); }
        // a dark edge round the letters of a marked one, so gold and red read on any ground (its own copy of the
        // font's material: the plain names keep the game's)
        if (fav || junk) { t.outlineColor = new Color32(20, 10, 0, 255); t.outlineWidth = 0.28f; }
        else if (t.outlineWidth > 0f && look.Outlined) t.outlineWidth = 0f;
        look.Outlined = fav || junk;
        __instance.titleSizeFitter?.AdjustSize();
    }

    /// <summary>A favourite's name on the ground: gold, with a band of light running across it every couple of seconds.</summary>
    internal sealed class NameLook : MonoBehaviour
    {
        public static readonly Color32 Gold = new(255, 200, 60, 255);
        static readonly Color32 Bright = new(255, 250, 215, 255);
        public Color Plain;
        public bool Kept, Outlined;
        TMPro.TMP_Text text;

        void Awake() => text = GetComponent<TMPro.TMP_Text>();

        void OnDisable() { if (text && Kept) text.ForceMeshUpdate(); }   // (the plain colours back)

        void LateUpdate()
        {
            if (!text || !text.isActiveAndEnabled) return;
            text.ForceMeshUpdate();
            var info = text.textInfo;
            int n = info.characterCount;
            if (n == 0) return;
            float band = Mathf.Repeat(Time.unscaledTime * 0.6f, 1.6f) * n - 0.3f * n;   // (where the light is: sweeps, then rests)
            for (int i = 0; i < n; i++)
            {
                var c = info.characterInfo[i];
                if (!c.isVisible) continue;
                float glow = Mathf.Clamp01(1f - Mathf.Abs(i - band) / 2.5f);
                var col = Color32.Lerp(Gold, Bright, glow);
                var colors = info.meshInfo[c.materialReferenceIndex].colors32;
                for (int v = 0; v < 4; v++) colors[c.vertexIndex + v] = col;
            }
            text.UpdateVertexData(TMPro.TMP_VertexDataUpdateFlags.Colors32);
        }
    }

    // ---- favourites sit out every sweep into a chest (X, Shift+X, "send all / send commons") ----

    const InventorySection Aside = (InventorySection)(1 << 21);

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyPrefix]
    static void BeforeSweep(Inventory __instance, InventorySection fromSection, InventorySection toSection, out List<InventoryItem> __state)
    {
        __state = null;
        if (toSection != InventorySection.Chest) return;
        foreach (var it in __instance.allItems)
        {
            if (it.section != fromSection || !Is(it.itemObject)) continue;
            (__state ??= new List<InventoryItem>()).Add(it);
            it.section = Aside;
        }
    }

    [HarmonyPatch(typeof(Inventory), "ChangeAllSection"), HarmonyFinalizer]
    static void AfterSweep(Inventory __instance, InventorySection fromSection, List<InventoryItem> __state)
    {
        if (__state == null) return;
        foreach (var it in __state) it.section = fromSection;
        __instance.SetInventoryChanged();
    }
}

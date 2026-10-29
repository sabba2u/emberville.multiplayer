using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace BetterCompanions;

/// <summary>
/// The companion's panel, beside your inventory: open your inventory (I) with your companion close by and it opens
/// too; close yours and it goes. Its name and level, a live portrait, its gear slots (the game's own slot pictures)
/// with its swap weapons (up to four) at the side, its stats, an Attributes button, and its bag.
///
/// Its slots are the game's: the game fills them (icons, tooltips: InventoryUI.SetUISlot) and drags from and onto
/// them like its own. Every drop the game hands to InventoryUI.DragObject that involves one of ours comes here
/// (Drop) instead, and moves the item between your bag and the companion (RealGear).
/// Ctrl+click an item in your bag: into its bag. Double-click one of its items: into yours.
/// </summary>
[HarmonyPatch]
internal static partial class CompanionPanel
{
    const float Near = 1f;   // (I opens its panel too only right next to it: the card's Trade opens it from anywhere)

    static GameObject root;
    static RectTransform rt, box;   // the copy of your panel, and the box that shifts it right of yours
    static InventoryUI ui;
    static TextMeshProUGUI title, statsText;
    static readonly List<UISlot> bagSlots = new();
    static readonly Dictionary<UISlot, GearSlot> gearSlots = new();
    static readonly List<UISlot> swapSlots = new();
    static GameObject camRig;
    static Animator anim;
    static RenderTexture portrait;
    static int builtCapacity = -1;
    static bool dirty, wasOpen;

    public static bool Open => root && root.activeSelf;
    /// <summary>Opened from its card (Trade): shown however far away it is, until your bag closes.</summary>
    public static bool Asked;
    static bool nearOnOpen, wasLogged;
    static PanelsManager panelsManager;
    static string lastWhy;
    public static bool Ours(UISlot s) => s && (bagSlots.Contains(s) || gearSlots.ContainsKey(s) || swapSlots.Contains(s));

    /// <summary>Each frame: show beside your inventory while it's open (your bag, not a chest or store) and it's near.</summary>
    public static void Tick(PlayerMaster me)
    {
        var p = RealPlayer.Current;
        // (kept, not looked for every frame: a search of the whole scene, 5-6 ms a frame and growing with the scene)
        if (!panelsManager) panelsManager = Object.FindAnyObjectByType<PanelsManager>();
        var panels = panelsManager;
        Hear();
        bool invOpen = panels && panels.panelInventory && panels.panelInventory.activeInHierarchy;
        if (!invOpen) { wasOpen = false; Asked = false; wasLogged = false; lastWhy = null; Hide(); return; }
        ui = ui ? ui : Object.FindAnyObjectByType<InventoryUI>();
        // how close it was when you opened your bag (I): that decides, not where it wanders while it's open
        if (!wasOpen) nearOnOpen = me && p && Vector2.Distance(me.transform.position, p.transform.position) <= Near;
        string why =
            !me ? "no player" :
            !p ? "no real companion out (/companion real)" :
            RealPlayer.Down(p) ? "it's down" :
            RealGear.Bag == null ? "its items haven't loaded" :
            !Asked && !nearOnOpen ? $"it was {Vector2.Distance(me.transform.position, p.transform.position):0.0} away when you opened your bag (needs {Near})" :
            !ui ? "no inventory screen found" :
            ui.updatingSection != InventorySection.Main ? $"the inventory shows {ui.updatingSection}, not your bag" :
            panels.hasChestOpened || openChest && openChest.isOpened ? "a chest is open (or the stash)" :
            panels.hasStoreOpened ? "a shop is open" :
            Attributes.On ? "the character screen is showing the companion" :
            Classes.On ? "the class screen is showing the companion" :
            null;
        if (!wasOpen) { wasOpen = true; Log.Info(why == null ? "Companion panel: opening beside your inventory" : $"Companion panel: not shown: {why}"); }
        if (why != lastWhy) { if (wasLogged) Log.Info($"Companion panel: {(why == null ? "showing again" : "hidden: " + why)}"); lastWhy = why; }
        wasLogged = true;
        if (why != null) { Hide(); return; }
        if (!root || builtCapacity != RealGear.Capacity) Build(panels);
        if (!root) return;
        if (!root.activeSelf) { root.SetActive(true); dirty = true; Place(panels); Log.Info($"Companion panel: at {rt.localPosition} size {rt.sizeDelta} in {rt.parent.name} {((RectTransform)rt.parent).rect.size}"); }
        if (camRig) camRig.transform.position = p.transform.position;
        Follow(panels);
        if (dirty) { dirty = false; Refresh(); Dim(); }
        Selection();
        Typing(searchField && searchField.isFocused);
        Align(panels);
    }

    public static void Hide()
    {
        if (root && root.activeSelf)
        {
            ourHide = true;
            root.SetActive(false);
            ourHide = false;
            // the game's hint box (its Swap tab's): it only goes when the mouse leaves the tab, which never comes once the tab's gone
            if (Singleton<TutorialManager>.HasMain) Singleton<TutorialManager>.main.HideHintInfoBox();
            ClassPicker.Close();
        }
        if (query.Length > 0) { query = ""; if (searchField) searchField.SetTextWithoutNotify(""); }
        Typing(false);
        if (camRig) camRig.SetActive(false);
        Cover(null, false);
    }

    static void MarkDirty() => dirty = true;

    static bool ourHide;

    /// <summary>Says who switched the panel off, when it wasn't us (it kept vanishing right after opening).</summary>
    sealed class Watch : MonoBehaviour
    {
        void OnDisable()
        {
            if (ourHide) return;
            Log.Info($"Companion panel: switched off by something else (self {gameObject.activeSelf}, parent {(transform.parent ? transform.parent.gameObject.activeInHierarchy : false)}): {System.Environment.StackTrace}");
        }
    }

    // ---- an open chest (the stash is one too): the game says so on its bus ----

    static Chest openChest;
    static bool hearing;

    static void Hear()
    {
        if (hearing) return;
        hearing = true;
        System.Action<ChestEvent> heard = e =>
        {
            if (e.EventType == ChestEvent.Event_Type.Open) openChest = e.chest;
            else if (e.EventType == ChestEvent.Event_Type.Close && openChest == e.chest) openChest = null;
        };
        // (through reflection: the publicized assembly shows the event and its backing field under one name)
        typeof(Bus<ChestEvent>).GetEvent("Listeners").AddEventHandler(null, heard);
    }

    // ---- building it from the game's own pieces ----

    static void Build(PanelsManager panels)
    {
        Destroy();
        var inv = panels.panelInventory.transform as RectTransform;
        if (!inv || ui.gearSlots.Count == 0 || ui.mainSlots.Count == 0) return;

        // a copy of your inventory panel, woken only once the game's (and other mods') scripts are off it: just its looks and slots.
        // It sits in a box the size of the screen, shifted right of yours; the copy itself follows yours (sliding in and out with it).
        root = new GameObject("BC_CompanionPanel", typeof(RectTransform));
        root.SetActive(false);
        root.AddComponent<Watch>();
        box = (RectTransform)root.transform;
        box.SetParent(inv.parent, false);
        box.anchorMin = Vector2.zero; box.anchorMax = Vector2.one; box.offsetMin = box.offsetMax = Vector2.zero;
        box.SetSiblingIndex(inv.GetSiblingIndex() + 1);
        var copy = Object.Instantiate(inv.gameObject, box, false);
        copy.name = "BC_CompanionPanelCopy";
        copy.SetActive(true);
        Strip(copy);
        copy.AddComponent<AnimCalls>();   // (in place of the game's AnimEvents, stripped: its animations call these by name)
        anim = copy.GetComponent<Animator>();
        Log.Info($"Companion panel: animated {(anim ? anim.runtimeAnimatorController?.name ?? "(no controller)" : "no: your panel has no animator")}");
        foreach (var t in copy.GetComponentsInChildren<Transform>(true).Where(t => t.name == "EF_FavKey" || t.name == "BC_Favourite").ToList()) Object.DestroyImmediate(t.gameObject);   // (your F5-F8 labels, from the Favourites mod)
        rt = (RectTransform)copy.transform;

        // the same pieces as yours, found where yours are
        foreach (var c in ui.gearSlots)
        {
            if (c == null || !c.slot) continue;
            var slot = Twin<UISlot>(c.slot, inv);
            if (!slot) continue;
            gearSlots[slot] = c.gearSlot;
            Listen(slot);
            if (c.toggle) Twin<Transform>(c.toggle, inv)?.gameObject.SetActive(false);   // (the hide-gear switch: yours)
        }
        int cap = RealGear.Capacity;
        builtCapacity = cap;
        var grid = ui.mainSlots.Where(s => s && s.transform.parent == ui.mainSlotsHolder).Select(s => Twin<UISlot>(s, inv)).Where(s => s).ToList();
        for (int i = 0; i < grid.Count; i++)
        {
            Listen(grid[i]);
            if (i < cap) bagSlots.Add(grid[i]); else locked.Add(grid[i]);
        }

        // the bag panel at its side: its swap weapons
        var side = ui.bagPanel ? Twin<Transform>(ui.bagPanel.transform, inv) : null;
        var sideSlotList = side ? side.GetComponentsInChildren<UISlot>(true) : new UISlot[0];
        if (side && sideSlotList.Length > 0)
        {
            side.gameObject.SetActive(true);
            for (int i = 0; i < sideSlotList.Length; i++) sideSlotList[i].gameObject.SetActive(i < RealGear.SwapSlots);
            swapSlots.AddRange(sideSlotList.Take(RealGear.SwapSlots));
            foreach (var s in swapSlots) Listen(s);
            var layout = side.GetComponentInChildren<GridLayoutGroup>(true);
            if (layout)   // (one column of four)
            {
                layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                layout.constraintCount = 1;
            }
            foreach (var t in side.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t.GetComponentInParent<UISlot>()) continue;
                t.text = "Swap";
                // hovering its tab: the game's own hint box, saying what these slots are for
                var header = t.transform.parent && t.transform.parent != side && t.transform.parent.GetComponent<Graphic>() ? t.transform.parent : t.transform;
                header.GetComponent<Graphic>().raycastTarget = true;
                (header.GetComponent<UIHintBoxElement>() ?? header.gameObject.AddComponent<UIHintBoxElement>()).SetHintBoxData(SwapHint);
            }
        }
        else if (side) side.gameObject.SetActive(false);

        // its slots' extras (the selected box, the swipe light: the game's animator kept them hidden) as yours are now
        foreach (var s in bagSlots.Concat(gearSlots.Keys).Concat(swapSlots).Concat(locked)) Calm(s, inv);

        // not its: the filters, the chest and store pages, your gold and weight, the gamepad hint, the bin
        foreach (var c in new Component[] { ui.buttonFilterAll, ui.buttonFilterArmor, ui.buttonFilterWeapon, ui.buttonFilterMaterial, ui.buttonFilterConsumable,
                     ui.buttonFilterOther, ui.weightBarFill, ui.weightBarDiffFill, ui.gamepadTooltipHolder, ui.disposeSlot, ui.chestSlotsHolder, ui.storeSlotsHolder })
            if (c) Twin<Transform>(c, inv)?.gameObject.SetActive(false);
        var gold = ui.goldAmount ? Twin<Transform>(ui.goldAmount, inv) : null;
        var search = rt.GetComponentsInChildren<TMP_InputField>(true).FirstOrDefault();
        var font = rt.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t && t.font);

        // the header ("Equipment"): its name and level
        var texts = rt.GetComponentsInChildren<TextMeshProUGUI>(true);
        title = texts.FirstOrDefault(t => t.text.Trim() == "Equipment")
             ?? texts.FirstOrDefault(t => !t.GetComponentInParent<UISlot>() && !t.GetComponentInParent<Button>());
        if (title) { title.enableAutoSizing = true; title.fontSizeMin = 10f; title.fontSizeMax = title.fontSize; title.enableWordWrapping = false; }

        // the Quest Items tab: its Attributes; the Inventory tab does nothing
        var tab = ui.buttonKeyItems ? Twin<Button>(ui.buttonKeyItems, inv) : null;
        if (tab)
        {
            tab.onClick = new Button.ButtonClickedEvent();
            tab.onClick.AddListener(() => { try { Attributes.Open(); } catch (System.Exception e) { Log.Warn($"Companion panel: {e}"); } });
            foreach (var t in tab.GetComponentsInChildren<TextMeshProUGUI>(true)) t.text = "Stats";
        }
        // the Inventory tab: its Class (the game's class screen, showing its class; no class yet: the picker)
        var mine = ui.buttonMainItems ? Twin<Button>(ui.buttonMainItems, inv) : null;
        if (mine)
        {
            mine.onClick = new Button.ButtonClickedEvent();
            mine.interactable = true;   // (yours is the open tab: the game greys it out)
            Log.Info($"Companion panel: Class tab {mine.name} (was interactable {ui.buttonMainItems.interactable})");
            var classTab = (RectTransform)mine.transform;
            mine.onClick.AddListener(() => { try { Classes.Open(classTab); } catch (System.Exception e) { Log.Warn($"Companion panel: {e}"); } });
            foreach (var t in mine.GetComponentsInChildren<TextMeshProUGUI>(true)) t.text = "Class";
        }

        // its stats, along the bottom where your gold and the search box are
        var row = new List<RectTransform>();
        var goldBox = gold ? (gold.parent && gold.parent != rt ? gold.parent : gold) : null;
        if (goldBox) row.Add((RectTransform)goldBox);
        if (search) row.Add((RectTransform)search.transform);
        statsText = new GameObject("BC_Stats", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        statsText.transform.SetParent(rt, false);
        if (font) { statsText.font = font.font; statsText.fontSharedMaterial = font.fontSharedMaterial; }
        statsText.color = new Color(1f, 0.93f, 0.82f);
        statsText.enableAutoSizing = true; statsText.fontSizeMin = 13f; statsText.fontSizeMax = 20f;
        statsText.alignment = TextAlignmentOptions.MidlineLeft;
        statsText.enableWordWrapping = false;
        statsText.raycastTarget = false;
        // the strip where your coins and search box are: its search box (its stats: on its Attributes screen)
        Over(statsText.rectTransform, row);
        var strip = statsText.rectTransform;
        MakeSearch(strip.localPosition, strip.sizeDelta, search);
        statsText.gameObject.SetActive(false);
        if (goldBox) goldBox.gameObject.SetActive(false);
        if (search && search != searchField) search.gameObject.SetActive(false);

        // its portrait, filmed by its own camera
        var raw = rt.GetComponentsInChildren<RawImage>(true).FirstOrDefault(r => r.texture is RenderTexture);
        if (raw) Portrait(raw);

        Log.Info($"Companion panel: built from your inventory ({gearSlots.Count} gear, {bagSlots.Count} bag, {locked.Count} locked, {swapSlots.Count} swap, title {(title ? "yes" : "no")}, portrait {(raw ? "yes" : "no")})");
        root.SetActive(false);
        RealGear.Changed -= MarkDirty;
        RealGear.Changed += MarkDirty;
    }

    static readonly List<UISlot> locked = new();

    static UIHintBoxData swapHint;
    static UIHintBoxData SwapHint
    {
        get
        {
            if (swapHint) return swapHint;
            swapHint = ScriptableObject.CreateInstance<UIHintBoxData>();
            swapHint.name = "BC_SwapHint";
            swapHint.localizedTitle = new UnityEngine.Localization.LocalizedString();
            swapHint.localizedDescription = new UnityEngine.Localization.LocalizedString();
            swapHint.title = "Swap";
            swapHint.description = "Weapons only. Your companion swaps to these by itself as the fight needs: " +
                                   "its strongest bow or staff from afar, its strongest sword or other close-up weapon up close.";
            Object.DontDestroyOnLoad(swapHint);
            return swapHint;
        }
    }

    /// <summary>Off the copy, before it wakes: every script that isn't Unity's UI or one of the game's slots (the inventory's own code, its localisation, other mods' bits).</summary>
    static void Strip(GameObject go)
    {
        // the panel's own animator stays (it slides in and out as yours does: the same controller), any others go
        foreach (var a in go.GetComponentsInChildren<Animator>(true)) if (a.gameObject != go) Object.DestroyImmediate(a);
        foreach (var a in go.GetComponentsInChildren<Animation>(true)) Object.DestroyImmediate(a);
        var all = go.GetComponentsInChildren<MonoBehaviour>(true);
        for (int pass = 0; pass < 2; pass++)   // (a second pass for ones another one required)
            foreach (var m in all)
            {
                if (!m) continue;
                string ns = m.GetType().Namespace ?? "";
                bool keep = m is UISlot || m is UIDraggable || m is ButtonSounds || m is Selectable || ns.StartsWith("TMPro") || ns.StartsWith("UnityEngine") && !ns.StartsWith("UnityEngine.Localization");
                if (keep) continue;
                try { Object.DestroyImmediate(m); } catch { }
            }
    }

    /// <summary>Each piece of one of its slots on or off, and as faded, as in your slot at the same place (but what the game sets itself: icon, frame, lock...).</summary>
    static void Calm(UISlot s, Transform yours)
    {
        if (!s) return;
        var path = new List<int>();
        var t = s.transform;
        for (; t && t != rt; t = t.parent) path.Add(t.GetSiblingIndex());
        if (t != rt) return;
        Transform at = yours;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i] >= at.childCount) return;
            at = at.GetChild(path[i]);
        }
        var mine = at.GetComponent<UISlot>();
        if (!mine) return;
        var game = new HashSet<Transform>(new Component[] { s.frameImage, s.iconHolder, s.iconOutlineHolder, s.timer, s.amountDisplay, s.amountIcon }
            .Where(c => c).Select(c => c.transform).Concat(new[] { s.lockIcon, s.highlightElement, s.badge }.Where(g => g).Select(g => g.transform)));
        void Walk(Transform a, Transform b)
        {
            for (int i = 0; i < a.childCount && i < b.childCount; i++)
            {
                var x = a.GetChild(i); var y = b.GetChild(i);
                if (game.Contains(x)) continue;
                x.gameObject.SetActive(y.gameObject.activeSelf);
                if (x.TryGetComponent<Graphic>(out var gx) && y.TryGetComponent<Graphic>(out var gy)) gx.color = gy.color;
                Walk(x, y);
            }
        }
        Walk(s.transform, mine.transform);
    }

    /// <summary>The copy's piece at the same place as yours.</summary>
    static T Twin<T>(Component mine, Transform yours) where T : Component
    {
        if (!mine || !rt) return null;
        var path = new List<int>();
        var t = mine.transform;
        for (; t && t != yours; t = t.parent) path.Add(t.GetSiblingIndex());
        if (t != yours) return null;
        Transform at = rt;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i] >= at.childCount) return null;
            at = at.GetChild(path[i]);
        }
        return typeof(T) == typeof(Transform) ? at as T : at.GetComponent<T>();
    }

    /// <summary>A rect over all of these (in the copy's space).</summary>
    static void Over(RectTransform r, List<RectTransform> these)
    {
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.localScale = Vector3.one;
        if (these.Count == 0) { r.sizeDelta = new Vector2(300f, 40f); r.localPosition = new Vector3(0f, rt.rect.yMin + 40f, 0f); return; }
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        var c = new Vector3[4];
        foreach (var o in these)
        {
            o.GetWorldCorners(c);
            foreach (var w in c) { Vector2 l = rt.InverseTransformPoint(w); min = Vector2.Min(min, l); max = Vector2.Max(max, l); }
        }
        r.sizeDelta = max - min;
        r.localPosition = (min + max) / 2;
    }

    /// <summary>Right of your inventory (its side panel too), the same size as yours.</summary>
    static void Place(PanelsManager panels)
    {
        var inv = (RectTransform)panels.panelInventory.transform;
        var parent = (RectTransform)box.parent;
        var pr = parent.rect;

        // (the game's panels fill the screen: the window you see is what's drawn inside)
        box.localPosition = Vector3.zero;
        Follow(panels, all: true);
        Powers.Place();   // (its powers column against its right edge, before it's measured)
        var yours = Drawn(inv, parent);
        var ours = Drawn(rt, parent);
        float dx = yours.y + 8f - ours.x;
        float over = ours.y + dx - (pr.xMax - 5f);
        if (over > 0f) dx -= over;   // (no room for all of it: kept on screen)
        box.localPosition = new Vector3(dx, 0f, 0f);
        Log.Info($"Companion panel: yours spans x {yours.x:0}..{yours.y:0}, its {ours.x:0}..{ours.y:0}; shifted {dx:0} (screen to {pr.xMax:0})");
        if (panels.panelStats && panels.panelStats.activeInHierarchy)
        {
            var st = Drawn((RectTransform)panels.panelStats.transform, parent);
            if (st.x < ours.y + dx && st.y > ours.x + dx) Cover(panels.panelStats, true);
        }
    }

    /// <summary>Left and right (x, y) of what's drawn inside a panel, in that space: its images, not its full-screen frame.</summary>
    static Vector2 Drawn(RectTransform panel, RectTransform space)
    {
        float min = float.MaxValue, max = float.MinValue, limit = space.rect.width * 0.9f;
        var c = new Vector3[4];
        foreach (var g in panel.GetComponentsInChildren<Graphic>(false))
        {
            if (!g.enabled || g.color.a <= 0.01f) continue;
            g.rectTransform.GetWorldCorners(c);
            float a = space.InverseTransformPoint(c[0]).x, b = space.InverseTransformPoint(c[2]).x;
            if (b - a <= 0f || b - a >= limit) continue;
            min = Mathf.Min(min, a); max = Mathf.Max(max, b);
        }
        if (min > max) { panel.GetWorldCorners(c); min = space.InverseTransformPoint(c[0]).x; max = space.InverseTransformPoint(c[2]).x; }
        return new Vector2(min, max);
    }

    /// <summary>Its frame as yours (anchors, size). Where it is, how big, how faded: its animator, as yours (or, with none, yours copied).</summary>
    static void Follow(PanelsManager panels, bool all = false)
    {
        if (!rt || !panels || !panels.panelInventory) return;
        var inv = (RectTransform)panels.panelInventory.transform;
        rt.anchorMin = inv.anchorMin; rt.anchorMax = inv.anchorMax; rt.pivot = inv.pivot;
        rt.sizeDelta = inv.sizeDelta;
        if (anim && !all) return;
        rt.anchoredPosition = inv.anchoredPosition;
        rt.localScale = inv.localScale;
        rt.localRotation = inv.localRotation;
        if (inv.TryGetComponent<CanvasGroup>(out var g))
        {
            var mine = rt.GetComponent<CanvasGroup>() ?? rt.gameObject.AddComponent<CanvasGroup>();
            mine.alpha = g.alpha;
        }
    }

    // ---- its animation: the game's Enter / Exit on yours, on it too ----

    /// <summary>What the panel animations call by name (the game's AnimEvents' names): nothing to do here, the game's panel code isn't on it.</summary>
    sealed class AnimCalls : MonoBehaviour
    {
        void DisableObject() { }
        void OnEntry() { }
        void CallEnableEvent() { }
        void CallExitEvent() { }
        void Shake(float duration) { }
        void ShakeSmall() { }
        void ShakeMedium() { }
        void ShakeLarge() { }
    }

    static void Mirror(GameObject go, string trigger)
    {
        if (!anim || !root || !root.activeSelf || !go) return;
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        if (panels && go == panels.panelInventory) anim.SetTrigger(trigger);
    }

    [HarmonyPatch(typeof(AnimEvents), nameof(AnimEvents.Hide)), HarmonyPrefix]
    static void YoursHides(AnimEvents __instance) => Mirror(__instance ? __instance.gameObject : null, "Exit");

    [HarmonyPatch(typeof(Utils), nameof(Utils.AnimExit), typeof(GameObject)), HarmonyPrefix]
    static void YoursExits(GameObject go) => Mirror(go, "Exit");

    [HarmonyPatch(typeof(Utils), nameof(Utils.AnimEntry), typeof(GameObject)), HarmonyPrefix]
    static void YoursEnters(GameObject go) { if (go && go.activeSelf) Mirror(go, "Enter"); }

    [HarmonyPatch(typeof(Utils), nameof(Utils.AnimEntry), typeof(GameObject), typeof(System.Action)), HarmonyPrefix]
    static void YoursEntersThen(GameObject go) { if (go && go.activeSelf) Mirror(go, "Enter"); }

    static CanvasGroup covered;

    /// <summary>Your character screen, hidden under ours (not closed: the game keeps it open) or shown again.</summary>
    static void Cover(GameObject screen, bool hide)
    {
        if (hide)
        {
            covered = screen.GetComponent<CanvasGroup>() ?? screen.AddComponent<CanvasGroup>();
            covered.alpha = 0f;
            covered.blocksRaycasts = false;
            covered.interactable = false;
        }
        else if (covered)
        {
            covered.alpha = 1f;
            covered.blocksRaycasts = true;
            covered.interactable = true;
            covered = null;
        }
    }

    /// <summary>Drops onto it, and drags that start from it, come to Drop.</summary>
    static void Listen(UISlot slot)
    {
        if (!slot || !slot.TryGetComponent<UIDraggable>(out var d) || d.OnDropEvent == null) return;
        d.OnDropEvent.RemoveAllListeners();
        d.OnDropEvent.AddListener(Drop);
    }

    static void Portrait(RawImage raw)
    {
        var src = ui.playerPortraitCamera;
        if (!src) return;
        var theirs = ui.GetComponentsInChildren<RawImage>(true).FirstOrDefault(r => r && r.texture is RenderTexture);
        // (the same shape as yours: a square one stretched into your portrait's frame squashed it)
        portrait = theirs ? new RenderTexture(((RenderTexture)theirs.texture).descriptor) { name = "BC_Portrait" } : new RenderTexture(256, 256, 16) { name = "BC_Portrait" };
        camRig = Object.Instantiate(src);
        camRig.name = "BC_PortraitCamera";
        foreach (var c in camRig.GetComponentsInChildren<Camera>(true)) c.targetTexture = portrait;
        foreach (var l in camRig.GetComponentsInChildren<AudioListener>(true)) Object.Destroy(l);
        if (theirs) { raw.uvRect = theirs.uvRect; raw.material = theirs.material; }
        raw.texture = portrait;
        camRig.SetActive(false);
        RenderPipelineManager.beginCameraRendering -= BeforeCamera;
        RenderPipelineManager.beginCameraRendering += BeforeCamera;
        RenderPipelineManager.endCameraRendering -= AfterCamera;
        RenderPipelineManager.endCameraRendering += AfterCamera;
    }

    // The portrait cameras film the spot around a player, so yours caught the companion and its caught you:
    // while one of them draws, the other player isn't drawn.
    static readonly List<Renderer> hidden = new();

    static void BeforeCamera(ScriptableRenderContext _, Camera cam)
    {
        if (!root || !root.activeSelf || !cam) return;
        PlayerMaster other =
            camRig && cam.transform.IsChildOf(camRig.transform) ? (Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null) :
            ui && ui.playerPortraitCamera && cam.transform.IsChildOf(ui.playerPortraitCamera.transform) ? RealPlayer.Current :
            null;
        if (!other) return;
        foreach (var r in other.GetComponentsInChildren<Renderer>())
            if (r && !r.forceRenderingOff) { r.forceRenderingOff = true; hidden.Add(r); }
    }

    static void AfterCamera(ScriptableRenderContext _, Camera cam)
    {
        foreach (var r in hidden) if (r) r.forceRenderingOff = false;
        hidden.Clear();
    }

    static void Destroy()
    {
        if (root) Object.Destroy(root);
        if (camRig) Object.Destroy(camRig);
        if (portrait) { portrait.Release(); Object.Destroy(portrait); }
        root = null; camRig = null; portrait = null;
        bagSlots.Clear(); gearSlots.Clear(); locked.Clear(); swapSlots.Clear(); tinted.Clear(); greenLogged = false; picked = null;
        builtCapacity = -1;
    }

    // ---- showing what it has ----

    static void Refresh()
    {
        var p = RealPlayer.Current;
        if (!root || !p || RealGear.Bag == null) return;
        if (camRig && !camRig.activeSelf) camRig.SetActive(true);
        var stats = p.characterStats;
        var data = CompanionSave.Data;
        if (title) try { title.text = $"{data?.playerName}  <size=75%>Lv {CompanionSave.Level}</size>"; } catch { title.text = data?.playerName ?? ""; }
        string Stat(SecondaryStat st, string fmt) { try { return stats.GetSecondaryStatValue(st).ToString(fmt); } catch { return "?"; } }
        string points; try { points = stats.GetAvailablePoints().ToString(); } catch { points = "?"; }
        string damage; try { damage = stats.GetSecondaryStatValue(stats.CurrentAttackStat).ToString("0"); } catch { damage = "?"; }
        statsText.text =
            $"Health {p.CurrentHealthPoints}/{Stat(SecondaryStat.HealthPoints, "0")}   Kymia {(int)p.CurrentKymiaPoints}/{Stat(SecondaryStat.KymiaPoints, "0")}   Damage {damage}   Defence {Stat(SecondaryStat.Defense, "0")}\n" +
            $"Speed {Stat(SecondaryStat.MoveSpeed, "0.0")}   Critical {Stat(SecondaryStat.CriticalChance, "0.#")}%   Block {Stat(SecondaryStat.BlockChance, "0.#")}%   Points {points}";

        foreach (var kv in gearSlots)
        {
            var slot = kv.Key;
            var gs = kv.Value;
            ui.ClearUISlot(slot, setLocked: false);
            var worn = RealGear.Worn(gs);
            if (worn != null && (SlotOf(worn) & gs) != 0 && FirstSlotFor(worn) == slot)
            {
                ui.SetUISlot(slot, worn, worn.data.itemIconUI != null, showInfo: true, showPrice: false);
                var item = worn;
                slot.SetLeftDoubleClickCallback(() => ToYourBag(item, -1));
            }
        }
        for (int k = 0; k < swapSlots.Count; k++) Fill(swapSlots[k], RealGear.Swap(k));
        for (int i = 0; i < bagSlots.Count; i++) Fill(bagSlots[i], RealGear.At(i)?.itemObject);
        foreach (var s in locked)   // (past its bag's room; one by one: a slot whose button went with the game's scripts throws, after showing its lock)
            if (s) try { ui.ClearUISlot(s, setLocked: true); } catch { }
        Green();
    }

    static UISlot picked;

    /// <summary>The slot of ours you clicked (the game's selection) gets its white box, as yours do; the one before loses it.</summary>
    static void Selection()
    {
        var go = UnityEngine.EventSystems.EventSystem.current ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
        var now = go ? go.GetComponentInParent<UISlot>() : null;
        if (now && !Ours(now)) now = null;
        if (now == picked) return;
        if (picked) Mark(picked, false);
        picked = now;
        if (picked) Mark(picked, true);
    }

    /// <summary>A slot's selected box on or off (gear slots call it Selected, bag slots Highlight_Gamepad).</summary>
    static void Mark(UISlot s, bool on)
    {
        if (!s) return;
        foreach (var t in s.GetComponentsInChildren<Transform>(true))
            if (t.name == "Selected" || t.name == "Highlight_Gamepad") t.gameObject.SetActive(on);
    }

    static bool greenLogged;
    static readonly Dictionary<Graphic, Color> tinted = new();

    /// <summary>Its own colour, greener (not replaced: a dark frame stays dark), once.</summary>
    static void Tint(Graphic g)
    {
        if (!tinted.TryGetValue(g, out var was)) tinted[g] = was = g.color;
        g.color = new Color(was.r * 0.6f, Mathf.Min(1f, was.g * 1.05f), was.b * 0.65f, g.color.a);
    }
    static readonly HashSet<string> Effects = new() { "Selected", "Highlight_Gamepad", "Swiper", "SwipeLight", "Line" };

    /// <summary>Its slots' frames tinted green (yours stay as they are), and none of them lit up.</summary>
    static void Green()
    {
        foreach (var s in bagSlots.Concat(gearSlots.Keys).Concat(swapSlots).Concat(locked))
        {
            if (!s) continue;
            if (s.highlightElement) s.highlightElement.SetActive(false);
            // the game's hover / selected effects (its animator hides them on yours; ours has none): off
            foreach (var t in s.GetComponentsInChildren<Transform>(true))
                if (t != s.transform && Effects.Contains(t.name) && t.gameObject.activeSelf) t.gameObject.SetActive(false);
            if (s == picked) Mark(s, true);
            if (s.frameImage) Tint(s.frameImage);
        }
        if (greenLogged) return;
        greenLogged = true;
        var cell = bagSlots.FirstOrDefault(s => s);
        if (cell) Log.Info("Companion panel: a bag slot shows " + string.Join(", ", cell.GetComponentsInChildren<Graphic>(false)
            .Select(g => $"{g.name}[{(g is Image i && i.sprite ? i.sprite.name : "-")} #{ColorUtility.ToHtmlStringRGBA(g.color)} was #{(tinted.TryGetValue(g, out var o) ? ColorUtility.ToHtmlStringRGBA(o) : "-")}{(g == cell.frameImage ? " frame" : "")}]")));
        var worn = gearSlots.Keys.FirstOrDefault(s => s && RealGear.Worn(gearSlots[s]) != null);
        if (worn)   // (what a slot with something in it shows, piece by piece)
            Log.Info("Companion panel: a filled slot shows " + string.Join(", ", worn.GetComponentsInChildren<Image>(false)
                .Select(i => $"{i.name}[{(i.sprite ? i.sprite.name : "-")} #{ColorUtility.ToHtmlStringRGBA(i.color)}{(i == worn.frameImage ? " frame" : i == worn.iconOutlineHolder ? " outline" : i == worn.iconHolder ? " icon" : "")}]")));
    }

    // ---- its search box: "Search its bag...", like yours (the Better Chests mod's); what doesn't match goes dark ----

    static TMP_InputField searchField;
    static string query = "";
    static readonly Dictionary<UISlot, ItemObject> shown = new();
    static readonly Dictionary<ItemData, string> names = new();

    /// <summary>A copy of your bag's search box (or, without that mod, the panel's own text box), over the strip at the bottom.</summary>
    static void MakeSearch(Vector3 at, Vector2 size, TMP_InputField own)
    {
        searchField = null;
        var yours = GameObject.Find("BC_bcBagSearch");
        TMP_InputField field = null;
        if (yours && yours.GetComponent<TMP_InputField>())
        {
            var go = Object.Instantiate(yours, rt, false);
            go.name = "BC_CompanionSearch";
            go.SetActive(true);
            field = go.GetComponent<TMP_InputField>();
        }
        else if (own)
        {
            field = own;
            own.transform.SetParent(rt, true);
        }
        if (!field) { Log.Info("Companion panel: no search box to copy"); return; }
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onSelect = new TMP_InputField.SelectionEvent();
        field.onDeselect = new TMP_InputField.SelectionEvent();
        field.SetTextWithoutNotify("");
        if (field.placeholder is TMP_Text hint) hint.text = "Search its bag...";
        field.onValueChanged.AddListener(v => { query = (v ?? "").Trim().ToLowerInvariant(); Dim(); });
        field.onSubmit.AddListener(_ => field.DeactivateInputField());   // (Enter: done typing, the search stays)
        // as big as yours, against the strip's right edge (as yours is against your bag's)
        var r = (RectTransform)field.transform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.localScale = Vector3.one;
        var like = yours ? ((RectTransform)yours.transform).rect.size : Vector2.zero;
        float w = like.x > 0f ? Mathf.Min(like.x, size.x) : size.x;
        float h = like.y > 0f ? like.y : Mathf.Clamp(size.y, 26f, 44f);
        r.sizeDelta = new Vector2(w, h);
        r.localPosition = at + new Vector3((size.x - w) / 2f, 0f, 0f);
        r.SetAsLastSibling();
        searchField = field;
        Log.Info($"Companion panel: search box {(yours ? "copied from your bag's" : "the panel's own")}");
    }

    static Transform yourSearch;

    /// <summary>Each frame: its search box where yours is in your panel (the panels are copies), as big; where the Better
    /// Chests mod puts yours (beside your coins, to your bag's right edge) can change, so it follows.</summary>
    static void Align(PanelsManager panels)
    {
        if (!searchField || !rt) return;
        if (!yourSearch) { var go = GameObject.Find("BC_bcBagSearch"); yourSearch = go ? go.transform : null; }
        if (!yourSearch || !yourSearch.gameObject.activeInHierarchy) return;
        var inv = panels.panelInventory.transform;
        var c = new Vector3[4];
        ((RectTransform)yourSearch).GetWorldCorners(c);
        Vector2 lo = inv.InverseTransformPoint(c[0]), hi = inv.InverseTransformPoint(c[2]);
        var r = (RectTransform)searchField.transform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.localScale = Vector3.one;
        r.sizeDelta = hi - lo;
        r.localPosition = (lo + hi) / 2f;
    }

    static bool Matches(ItemObject o)
    {
        if (query.Length == 0 || o?.data == null) return true;
        if (!names.TryGetValue(o.data, out var n))
        {
            n = o.data.name;
            try { var l = o.data.Name == null || o.data.Name.IsEmpty ? null : o.data.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(l)) n = l; } catch { }
            names[o.data] = n = n.ToLowerInvariant();
        }
        return n.Contains(query);
    }

    /// <summary>Its bag and swap slots whose item doesn't match the search: dark (still yours to take).</summary>
    static void Dim()
    {
        foreach (var slot in bagSlots.Concat(swapSlots))
        {
            if (!slot) continue;
            shown.TryGetValue(slot, out var o);
            bool dim = o != null && !Matches(o);
            var t = slot.transform.Find("BC_SearchDim");
            if (!t && dim)
            {
                var img = new GameObject("BC_SearchDim", typeof(RectTransform)).AddComponent<Image>();
                t = img.transform;
                t.SetParent(slot.transform, false);
                var d = (RectTransform)t;
                d.anchorMin = Vector2.zero; d.anchorMax = Vector2.one; d.offsetMin = d.offsetMax = Vector2.zero;
                img.color = new Color(0.06f, 0.05f, 0.05f, 0.82f);
                img.raycastTarget = false;
            }
            if (t) { t.SetAsLastSibling(); t.gameObject.SetActive(dim); }
        }
    }

    static List<InputActionMap> keysOff;

    /// <summary>While its box is typed in, the game's keys are off (typing "i" doesn't close your bag); the screen's clicks still work.</summary>
    static void Typing(bool on)
    {
        if (on == (keysOff != null)) return;
        if (on)
        {
            keysOff = new();
            foreach (var map in MainInput.Actions.asset.actionMaps)
                if (map.enabled && map.name != "UI") { map.Disable(); keysOff.Add(map); }
        }
        else
        {
            foreach (var map in keysOff) map.Enable();
            keysOff = null;
        }
    }

    /// <summary>The inventory selects whatever slot the mouse passes over, which would take the keyboard from its box after
    /// one letter: while it's typed in, the selection only moves on a click.</summary>
    [HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem), nameof(UnityEngine.EventSystems.EventSystem.SetSelectedGameObject),
        new[] { typeof(GameObject), typeof(UnityEngine.EventSystems.BaseEventData) }), HarmonyPrefix]
    static bool KeepTyping(GameObject selected)
    {
        if (!searchField || !searchField.isFocused || selected == searchField.gameObject) return true;
        var m = Mouse.current;
        return m != null && (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame);
    }

    static GearSlot SlotOf(ItemObject o) => RealGear.SlotOf(o) ?? GearSlot.None;

    /// <summary>A two-hander covers two slots: shown in the first of ours it fits.</summary>
    static UISlot FirstSlotFor(ItemObject o) => gearSlots.FirstOrDefault(kv => (kv.Value & SlotOf(o)) != 0).Key;

    static void Fill(UISlot slot, ItemObject o)
    {
        if (!slot) return;
        ui.ClearUISlot(slot, setLocked: false);
        shown[slot] = o;
        if (o == null) return;
        ui.SetUISlot(slot, o, true, showInfo: true, showPrice: false);
        var item = o;
        slot.SetLeftDoubleClickCallback(() => ToYourBag(item, -1));
    }

    // ---- moving things ----

    enum K { None, YourBag, YourGear, Bag, Gear, Spare }

    static (K k, int index, GearSlot gear) Where(UISlot s)
    {
        int i = bagSlots.IndexOf(s);
        if (i >= 0) return (K.Bag, i, GearSlot.None);
        int k = swapSlots.IndexOf(s);
        if (k >= 0) return (K.Spare, RealGear.SpareIndex + k, GearSlot.MainHand);
        if (gearSlots.TryGetValue(s, out var gs)) return (K.Gear, -1, gs);
        if (!ui) return (K.None, -1, GearSlot.None);
        i = ui.mainSlots.IndexOf(s);
        if (i >= 0 && ui.updatingSection == InventorySection.Main) return (K.YourBag, i, GearSlot.None);
        if (ui.gearSlots.ContainsSlot(s, out _)) return (K.YourGear, -1, GearSlot.None);
        return (K.None, -1, GearSlot.None);
    }

    static int dropFrame = -1;
    static (UISlot, UISlot) dropLast;

    /// <summary>One of the game's drops where one end is ours.</summary>
    public static void Drop(UISlot from, UISlot to)
    {
        if (Time.frameCount == dropFrame && dropLast == (from, to)) return;   // (heard twice: once per listener)
        dropFrame = Time.frameCount;
        dropLast = (from, to);
        if (!RealPlayer.Current || RealGear.Bag == null || !ui) return;
        var a = Where(from);
        var b = Where(to);
        try { Move(a, b); }
        catch (System.Exception e) { Log.Warn($"Companion panel: moving: {e}"); }
        Done();
    }

    static Inventory Yours => ui.inventoryMain;
    static InventoryItem YourAt(int i) => Yours.allItems.Find(x => x.section == InventorySection.Main && x.slotIndex == i);

    static int YourFree(int prefer)
    {
        int cap = Yours.CurrentInventoryCapacity;
        if (prefer >= 0 && prefer < cap && YourAt(prefer) == null) return prefer;
        for (int i = 0; i < cap; i++) if (YourAt(i) == null) return i;
        return -1;
    }

    static void ToYou(ItemObject o, int index) => Yours.allItems.Add(new InventoryItem(o, index, InventorySection.Main));

    static bool Fits(ItemObject o, (K k, int index, GearSlot gear) at) =>
        at.k == K.Spare ? o.data is CollectableWeapon w && (w.weapon.gearSlot & GearSlot.MainHand) != 0
        : at.k == K.Gear ? (SlotOf(o) & at.gear) != 0
        : true;

    static void Move((K k, int index, GearSlot gear) a, (K k, int index, GearSlot gear) b)
    {
        if (a.k == K.YourGear || b.k == K.YourGear) { Hooks.Say("Companion: take it off yourself first (into your bag), then hand it over."); return; }
        if (a.k == K.None || b.k == K.None) return;

        ItemObject Get((K k, int index, GearSlot gear) at) => at.k switch
        {
            K.YourBag => YourAt(at.index)?.itemObject,
            K.Bag => RealGear.At(at.index)?.itemObject,
            K.Spare => RealGear.At(at.index)?.itemObject,
            K.Gear => RealGear.Worn(at.gear),
            _ => null,
        };

        var item = Get(a);
        if (item == null) return;
        if (!Fits(item, b)) { Hooks.Say("Companion: that doesn't go there."); return; }

        switch (a.k, b.k)
        {
            case (K.YourBag, K.Bag):
            {
                var theirs = RealGear.Take(b.index);
                Yours.allItems.Remove(YourAt(a.index));
                RealGear.Put(item, b.index);
                if (theirs != null) ToYou(theirs, a.index);
                break;
            }
            case (K.YourBag, K.Spare):
            {
                var old = RealGear.Take(b.index);
                Yours.allItems.Remove(YourAt(a.index));
                RealGear.Put(item, b.index);
                if (old != null) ToYou(old, a.index);
                break;
            }
            case (K.YourBag, K.Gear):
            {
                Yours.allItems.Remove(YourAt(a.index));
                var off = RealGear.Wear(item);
                int at = a.index;
                foreach (var o in off)
                {
                    int i = YourFree(at);
                    if (i >= 0) ToYou(o, i); else { int j = RealGear.FreeIndex(); if (j >= 0) RealGear.Put(o, j); else ToYou(o, YourFree(-1)); }
                    at = -1;
                }
                break;
            }
            case (K.Bag, K.YourBag):
            {
                var yours = YourAt(b.index);
                RealGear.Take(a.index);
                if (yours != null) { Yours.allItems.Remove(yours); RealGear.Put(yours.itemObject, a.index); }
                ToYou(item, b.index);
                break;
            }
            case (K.Spare, K.YourBag):
            case (K.Gear, K.YourBag):
                ToYourBag(item, b.index, refresh: false);
                break;
            case (K.Bag, K.Bag):
            case (K.Spare, K.Spare):
            {
                var other = RealGear.Take(b.index);
                RealGear.Take(a.index);
                RealGear.Put(item, b.index);
                if (other != null) RealGear.Put(other, a.index);
                break;
            }
            case (K.Bag, K.Spare):
            {
                var old = RealGear.Take(b.index);
                RealGear.Take(a.index);
                RealGear.Put(item, b.index);
                if (old != null) RealGear.Put(old, a.index);
                break;
            }
            case (K.Bag, K.Gear):
            case (K.Spare, K.Gear):
            {
                int back = a.index;
                RealGear.Take(back);
                var off = RealGear.Wear(item);
                foreach (var o in off)
                {
                    int i = RealGear.At(back) == null ? back : RealGear.FreeIndex();
                    if (i < 0) { int y = YourFree(-1); if (y >= 0) ToYou(o, y); else RealGear.Put(o, RealGear.Overflow); }
                    else RealGear.Put(o, i);
                    back = -1;
                }
                break;
            }
            case (K.Gear, K.Bag):
            case (K.Gear, K.Spare):
            {
                var there = RealGear.At(b.index)?.itemObject;
                if (there != null)
                {
                    if (!Fits(there, a)) { Hooks.Say("Companion: that slot's taken."); return; }
                    RealGear.Take(b.index);
                    foreach (var o in RealGear.Wear(there))
                        if (o != item) { int i = RealGear.FreeIndex(); if (i >= 0) RealGear.Put(o, i); else ToYou(o, YourFree(-1)); }
                }
                else RealGear.TakeOff(item);
                RealGear.Put(item, b.index);
                break;
            }
            case (K.Spare, K.Bag):
            {
                var other = RealGear.Take(b.index);
                RealGear.Take(a.index);
                RealGear.Put(item, b.index);
                if (other != null && Fits(other, a)) RealGear.Put(other, a.index);
                else if (other != null) RealGear.Put(other, RealGear.FreeIndex());
                break;
            }
        }
    }

    /// <summary>One of its things into your bag (there, or the first free slot).</summary>
    static void ToYourBag(ItemObject item, int index, bool refresh = true)
    {
        int to = YourFree(index);
        if (to < 0) { Hooks.Say("Companion: your bag is full."); return; }
        if (RealGear.Bag.Any(i => i.itemObject == item)) RealGear.Bag.RemoveAll(i => i.itemObject == item);
        else RealGear.TakeOff(item);
        ToYou(item, to);
        if (refresh) Done();
    }

    /// <summary>Ctrl+click in your bag: into its bag.</summary>
    static void FromYourBag(int index)
    {
        var it = YourAt(index);
        int to = RealGear.FreeIndex();
        if (it == null) return;
        if (to < 0) { Hooks.Say("Companion: its bag is full."); return; }
        Yours.allItems.Remove(it);
        RealGear.Put(it.itemObject, to);
        Done();
    }

    static void Done()
    {
        Yours.UpdateEquippedList();
        Yours.SetInventoryChanged();
        RealGear.Save();
        ui.UpdateMain();
        if (RealPlayer.Current) RealPlayer.Current.RecalculateHealthAndKymia();
        dirty = true;
    }

    // ---- the game's hooks ----

    /// <summary>The game's drop handler: drops involving our slots are ours.</summary>
    [HarmonyPatch(typeof(InventoryUI), nameof(InventoryUI.DragObject)), HarmonyPrefix]
    static bool GameDrop(UISlot fromSlot, UISlot toSlot)
    {
        if (!Open || !(Ours(fromSlot) || Ours(toSlot))) return true;
        Drop(fromSlot, toSlot);
        return false;
    }

    /// <summary>Ctrl+click on an item in your bag while its panel is open: into its bag.</summary>
    [HarmonyPatch(typeof(UISlot), nameof(UISlot.OnPointerClick)), HarmonyPrefix]
    static bool CtrlClick(UISlot __instance, UnityEngine.EventSystems.PointerEventData eventData)
    {
        if (!Open || eventData.button != UnityEngine.EventSystems.PointerEventData.InputButton.Left) return true;
        var kb = Keyboard.current;
        if (kb == null || !(kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)) return true;
        var at = Where(__instance);
        if (at.k != K.YourBag) return true;
        FromYourBag(at.index);
        return false;
    }

    // ---- its class: a picker of the classes you've learned ----

    internal static class ClassPicker
    {
        static Object menu;

        public static void Open(RectTransform above)
        {
            Close();
            var p = RealPlayer.Current;
            Log.Info($"Companion panel: Class tab clicked (companion {(p ? "here" : "missing")}, menus {(Hooks.Popup != null ? "yes" : "no")})");
            if (!p || Hooks.Popup == null) { Hooks.Say("Companion: picking its class needs the multiplayer mod's menus."); return; }
            // every class the game has, learned or not (its class screens' lists, and any other copy in memory)
            var found = new List<PlayerCombatClass>(Resources.FindObjectsOfTypeAll<PlayerCombatClass>());
            foreach (var u in Resources.FindObjectsOfTypeAll<UIClassUpgrade>()) if (u.allCombatClasses != null) found.AddRange(u.allCombatClasses);
            foreach (var s in Resources.FindObjectsOfTypeAll<ClassSelectionUI>()) if (s.combatClasses != null) found.AddRange(s.combatClasses);
            var classes = found.Where(c => c && c.playerClassType != PlayerClassType.None)
                .GroupBy(c => c.playerClassType).Select(g => g.First()).OrderBy(c => c.playerClassType).ToList();
            Log.Info($"Companion panel: classes in the game: {string.Join(", ", classes.Select(c => $"{c.playerClassType} ({c.ClassSkillTree?.skillNodes?.Count ?? 0} skills)"))}");
            var items = new List<(string, System.Action, bool)>();
            foreach (var c in classes)
            {
                var cls = c;
                bool now = p.characterStats.equippedClass && p.characterStats.equippedClass.playerClassType == cls.playerClassType;
                items.Add((NameOf(cls) + (now ? "  (now)" : ""), () => { Close(); Equip(cls); }, !now));
            }
            if (classes.Count == 0) items.Add(("No classes found", () => { }, false));
            bool none = !p.characterStats.equippedClass;
            items.Add(("None" + (none ? "  (now)" : ""), () => { Close(); Equip(null); }, !none));
            items.Add(("Cancel", Close, true));
            // the tab's top middle, on screen (its canvas may draw through a camera)
            var c4 = new Vector3[4];
            above.GetWorldCorners(c4);
            var canvas = above.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var at = RectTransformUtility.WorldToScreenPoint(cam, (c4[1] + c4[2]) / 2f);
            menu = Hooks.Popup(at, $"{CompanionSave.Data?.playerName}'s class", items.ToArray());
            Log.Info($"Companion panel: class picker at {at} ({classes.Count} classes)");
        }

        public static void Close()
        {
            if (menu) Object.Destroy(menu is Component comp ? comp.gameObject : menu);
            menu = null;
        }

        static string NameOf(PlayerCombatClass c)
        {
            try { var n = c.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return c.playerClassType.ToString();
        }

        /// <summary>The game's own class change, on it (its stats), into its save; then its skills for that class.</summary>
        static void Equip(PlayerCombatClass cls)
        {
            var p = RealPlayer.Current;
            if (!p || CompanionSave.Data == null) return;
            p.characterStats.EquipClass(CompanionTree.Own(cls, CompanionSave.Data));   // (null: none; else its own copy, its own tree)
            CompanionSave.Data.equippedClass = cls ? cls.playerClassType : PlayerClassType.None;
            p.RecalculateHealthAndKymia();
            CompanionSave.Write();
            try { RealSkills.Load(p); } catch (System.Exception e) { Log.Warn($"Companion skills: {e.Message}"); }
            dirty = true;
            Powers.Refresh();
            if (Classes.On) { if (cls) Classes.Refresh(); else { var pm = Object.FindAnyObjectByType<PanelsManager>(); if (pm) UIPanels.Close(pm.panelClass); } }
            Hooks.Say(cls ? $"{CompanionSave.Data.playerName} is a {NameOf(cls)} now." : $"{CompanionSave.Data.playerName} has no class now.");
            Log.Info($"Companion: class {(cls ? cls.playerClassType : PlayerClassType.None)}");
        }
    }

    // ---- its attributes, on the game's character screen ----

    internal static class Attributes
    {
        public static bool On;

        public static void Open()
        {
            var panels = Object.FindAnyObjectByType<PanelsManager>();
            if (!panels || !RealPlayer.Current) return;
            On = true;
            Hide();   // (the character screen, uncovered, shows the companion now)
            if (!panels.panelStats.activeInHierarchy)
            {
                panels.TogglePanel(PanelName.Character);
                if (!panels.panelStats.activeSelf)   // (the game said no: open it ourselves)
                {
                    Log.Info($"Companion: the game didn't open the character screen (state {Singleton<GameManager>.main.gameState}, waiting {panels.waitingConfirmations}, locked {panels.locked}, " +
                             $"modal {UIPanels.IsTopModal}, chest {panels.hasChestOpened}, store {panels.hasStoreOpened}, unlocked {GameData.gameBools.IsTrue(BoolID.UnlockedCharacterPanel)}): opening it");
                    panels.TogglePanel(panels.panelStats);
                }
                Log.Info($"Companion: attributes {(panels.panelStats.activeInHierarchy ? "open" : "still closed")}");
                string Prim(CharacterStats st) { try { return string.Join(" ", st.primStatDict.Select(kv => $"{kv.Key} {kv.Value.Value:0}")); } catch { return "?"; } }
                var you = Singleton<GameManager>.main.player;
                Log.Info($"Companion: its attributes {Prim(RealPlayer.Current.characterStats)}; yours {(you ? Prim(you.characterStats) : "?")}");
                Hooks.Say($"Showing {CompanionSave.Data?.playerName}'s attributes (close the screen to go back to trading).");
            }
            else foreach (var h in panels.panelStats.GetComponentsInChildren<UIStatsHandler>(true)) h.GatherStats(RealPlayer.Current.characterStats);
        }

        public static void Tick()
        {
            var kb = Keyboard.current;
            // (the test keys themselves: "any key" isn't newly pressed while Shift is already held down)
            bool key = kb != null && (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame || kb.minusKey.wasPressedThisFrame ||
                kb.numpadMinusKey.wasPressedThisFrame || kb.numpadMultiplyKey.wasPressedThisFrame || kb.digit8Key.wasPressedThisFrame ||
                kb.slashKey.wasPressedThisFrame || kb.numpadDivideKey.wasPressedThisFrame);
            if (!On && !key) return;
            var panels = Object.FindAnyObjectByType<PanelsManager>();
            bool showing = panels && panels.panelStats && panels.panelStats.activeInHierarchy;
            if (showing && key) TestKeys(panels);   // (yours with Shift on your own screen too)
            if (!On || showing) return;
            On = false;
            // back to trading with it (the game keeps no history: opening one screen closes the rest), unless you went
            // elsewhere or pressed Esc (everything closes)
            if (Classes.EscJustNow) { Log.Info("Companion: attributes closed (Esc)"); return; }
            if (panels && !UIPanels.HasOpenedPanel && Singleton<GameManager>.main.gameState == GameState.Playing)
            {
                Log.Info("Companion: attributes closed, back to its inventory");
                CardActions.Trade();
            }
        }
    }

    /// <summary>
    /// For testing, on the character screen: the companion's (its attributes showing) with the keys alone, yours with
    /// Shift held. + a level (no cap: past the game's last level, each costs what the last one did), its XP given as a
    /// kill gives it (the class on and the weapon's mastery too); - the attribute
    /// points back to spend again; * back to level 1 (no XP, points back); / a level down (points back if more were
    /// spent than the level gives). Keyboard or numpad (Shift+8 is *).
    /// </summary>
    static void TestKeys(PanelsManager panels)
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        var typing = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.GetComponent<TMP_InputField>();
        if (typing && typing.isFocused) return;
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed || kb.shiftKey.isPressed;
        bool up = kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame;
        bool reset = kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame;
        bool zero = kb.numpadMultiplyKey.wasPressedThisFrame || kb.digit8Key.wasPressedThisFrame && shift;
        bool down = kb.slashKey.wasPressedThisFrame || kb.numpadDivideKey.wasPressedThisFrame;
        if (!up && !reset && !zero && !down) return;
        Log.Info($"Test keys: {(up ? "+" : reset ? "-" : zero ? "*" : "/")} pressed, Shift {(shift ? "held" : "not held")}, companion's attributes {(Attributes.On ? "showing" : "not showing")}");
        // whose: Shift yours (here or on your own screen); without, the companion's (only while its attributes show)
        PlayerMaster p;
        PlayerData data;
        System.Action save;
        string who;
        if (shift)
        {
            p = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            data = GameData.HasPlayerData ? GameData.playerData : null;
            save = () => { };   // (the game saves yours itself)
            who = "You";
        }
        else
        {
            if (!Attributes.On) { Hooks.Say("Test keys: hold Shift for your own character (the keys alone are for the companion's attributes)."); return; }
            p = RealPlayer.Current;
            data = CompanionSave.Data;
            save = CompanionSave.Write;
            who = data?.playerName;
        }
        if (!p || data == null) { Log.Info($"Test keys: no {(shift ? "player" : "companion")} to change"); return; }
        var stats = p.characterStats;
        var key = stats.baseClass.playerClassType;
        var xp = data.GetPlayerExperience(key);   // (first: AddXP / SetXP on a key it hasn't got go wrong)
        string tag = shift ? "you" : "companion";
        if (up)
        {
            // the XP for the next level, given as a kill gives it: to the level, the class on, the weapon in hand (and
            // yours, the armour): the class screen and masteries go up with it
            int need = Mathf.Max(1, xp.NextLevel - xp.CurrentPoints);
            var cls = stats.equippedClass;
            int classBefore = cls ? data.GetPlayerExperience(cls.playerClassType).CurrentLevel : 0;
            if (shift) p.GainXP(need, multiply: false);   // (the game's own: no difficulty scaling here)
            else
            {
                data.AddXP(key, need);
                if (cls) { data.GetPlayerExperience(cls.playerClassType); data.AddXP(cls.playerClassType, need); }
                if (p.currentWeapon) { data.GetPlayerExperience(p.currentWeapon.weaponCategory); data.AddXP(p.currentWeapon.weaponCategory, need); }
                RealPlayer.ArmorXP(data, stats, need);
            }
            stats.baseClassXP = data.GetPlayerExperience(key);
            int classNow = cls ? data.GetPlayerExperience(cls.playerClassType).CurrentLevel : 0;
            p.PlayExtraVFX("LevelUp");
            save();
            string classLine = cls ? $"; {NameOfClass(cls)} class level {classNow}{(classNow > classBefore ? " (up)" : "")}" : "";
            Log.Info($"Test keys ({tag}): +{need} XP, level {stats.baseClassXP.CurrentLevel}{classLine}");
            Hooks.Say($"{who} {(shift ? "are" : "is")} level {stats.baseClassXP.CurrentLevel} ({stats.GetAvailablePoints()} points to spend){classLine}.");
            if (Classes.On) Classes.Refresh();
        }
        else if (reset)
        {
            Respec();
            save();
            Log.Info($"Test keys ({tag}): respec");
            Hooks.Say($"{(shift ? "Your" : who + "'s")} attributes reset: {stats.GetAvailablePoints()} points to spend.");
        }
        else if (zero)
        {
            data.SetXP(key, 0);
            stats.baseClassXP = data.GetPlayerExperience(key);
            Respec();
            save();
            Log.Info($"Test keys ({tag}): XP to 0");
            Hooks.Say($"{who} {(shift ? "are" : "is")} back to level {stats.baseClassXP.CurrentLevel} (no XP, attributes reset).");
        }
        else
        {
            int level = xp.CurrentLevel;
            if (level <= 1) { Hooks.Say($"{who} {(shift ? "are" : "is")} already level 1."); return; }
            data.SetXP(key, NoLevelCap.XpAt(data.GetTable(key), level - 1));
            stats.baseClassXP = data.GetPlayerExperience(key);
            bool respec = stats.GetAvailablePoints() < 0;   // (more spent than the lower level gives)
            if (respec) Respec();
            save();
            Log.Info($"Test keys ({tag}): level down to {stats.baseClassXP.CurrentLevel}{(respec ? ", respec" : "")}");
            Hooks.Say($"{who} {(shift ? "are" : "is")} level {stats.baseClassXP.CurrentLevel}{(respec ? " (attributes reset)" : "")} ({stats.GetAvailablePoints()} points to spend).");
        }
        // the screen, again (whoever it's showing)
        if (!panels.panelStats.activeInHierarchy) return;
        var shown = Attributes.On && RealPlayer.Current ? RealPlayer.Current.characterStats : Singleton<GameManager>.main.player.characterStats;
        foreach (var h in panels.panelStats.GetComponentsInChildren<UIStatsHandler>(true)) h.GatherStats(shown);
        foreach (var l in panels.panelStats.GetComponentsInChildren<UIStatsLevel>(true)) l.Setup(null);
        foreach (var c in panels.panelStats.GetComponentsInChildren<UIClassUpgrade>(true)) c.GatherStats(shown);

        // the attributes back to the class's starting values, the rest of the stats from them, into the save
        void Respec()
        {
            foreach (var b in stats.baseClass.primaryStats)
                if (stats.primStatDict.TryGetValue(b.stat, out var st)) st.BaseValue = b.baseValue;
            stats.baseClass.conversionTable.Calculate(stats.primStatDict, stats.secStatDict);
            data.SavePlayerStats(stats.primStatDict, stats.secStatDict);
            p.RecalculateHealthAndKymia();
        }
    }

    static string NameOfClass(PlayerCombatClass c)
    {
        try { var n = c.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
        return c.playerClassType.ToString();
    }

    [HarmonyPatch(typeof(UIStatsHandler), nameof(UIStatsHandler.GatherStats)), HarmonyPrefix]
    static void StatsOf(ref CharacterStats characterStats)
    {
        if (Attributes.On && RealPlayer.Current) characterStats = RealPlayer.Current.characterStats;
    }

    /// <summary>The character screen's level: the game reads it from your save (GameData.playerData); the companion's from its own.</summary>
    [HarmonyPatch(typeof(UIStatsLevel), nameof(UIStatsLevel.Setup)), HarmonyPrefix]
    static bool LevelOf(UIStatsLevel __instance)
    {
        var p = RealPlayer.Current;
        if (!Attributes.On || !p || p.characterStats.baseClassXP == null) return true;
        __instance.toDispose?.Dispose();   // (no longer following your level)
        __instance.toDispose = null;
        __instance.xp = p.characterStats.baseClassXP;
        __instance.UpdateUI();
        return false;
    }

    /// <summary>The character screen's class side (its class, its skill tree): the companion's too.</summary>
    [HarmonyPatch(typeof(UIClassUpgrade), nameof(UIClassUpgrade.GatherStats)), HarmonyPrefix]
    static void ClassOf(ref CharacterStats characterStats)
    {
        if (Attributes.On && RealPlayer.Current) characterStats = RealPlayer.Current.characterStats;
    }

    [HarmonyPatch(typeof(UIStatsHandler), "ApplyChanges"), HarmonyPrefix]
    static bool ApplyAttributes(UIStatsHandler __instance)
    {
        var p = RealPlayer.Current;
        if (!Attributes.On || !p || __instance.activeCharStats != p.characterStats) return true;
        p.characterStats.UpdateBaseValues(__instance.primCopy, __instance.secCopy);
        CompanionSave.Data.SavePlayerStats(__instance.primCopy, __instance.secCopy);
        CompanionSave.Write();
        p.RecalculateHealthAndKymia();
        __instance.GatherStats(p.characterStats);
        Log.Info("Companion: attributes saved");
        return false;
    }
}

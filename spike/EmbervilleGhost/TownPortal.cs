using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using ImGuiNET;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The town portal (Diablo's): a button on the right edge. Hold still for a moment (a channel: moving or being hit
/// stops it) and a swirling portal opens where you stand, its twin opens in town (the demo: the starting area, by the
/// door), and you're taken home. Step into either end (anyone: your friends too) to go to the other. The pair stays
/// until you open a new one; each player has their own pair, side by side in town with their name over it.
///
/// Inside a generated dungeon or a descent floor (built in this same scene, out at 4000,4000) the far end is that floor
/// (settings, seed, region, floor): going back builds it again, as joining a friend's floor does, and puts you at the
/// portal (Dungeon.ArriveNextAt); coming home leaves it for the town end (Dungeon.LeaveTo). Elsewhere in the starting
/// area's scene (another of its areas) it's that area and spot. Other scenes: not yet.
/// With a /sethome "home" (Homes), the pair's home end is there instead of town (beside the home's own portal).
/// Right-click on the button: your homes (and town) as a list, to go to one straight away.
/// The look is the game's own portal (the dungeon portal room's swirl: PadFx.Portal). Everyone's pairs are shared
/// (TownPortal message, the host passes them on to whoever joins).
/// </summary>
internal static class TownPortal
{
    public static Plugin Plugin;
    const string HomeScene = "TutorialArea";
    // the town end: in a row left of the spot outside the starting door (Delve's DefaultPad, -52.48, 66.57)
    static readonly Vector3 HomeSpot = new Vector3(-55.2f, 65.6f, 0f);
    const float RowStep = 1.9f, Channel = 3f, StepIn = 0.75f, Hold = 0.5f;

    sealed class Link
    {
        public int Owner; public string Name;
        public string Scene, Area; public bool InDungeon; public Dungeon.Where Where; public Vector3 At;
        public string HomeScene = "", HomeArea = ""; public Vector3 HomeAt;   // its owner's /sethome (empty: town)
        public GameObject Far, Town; public bool FarArmed, TownArmed; public float FarStood, TownStood;
    }

    static readonly Dictionary<int, Link> links = new();
    static readonly HashSet<int> sentTo = new();
    static string homeArea = "002_Cavern";   // the starting area's outdoors (its GameArea; checked again when we're by the door)

    static int MeId => Plugin != null && Plugin.Connected && Plugin.MyId >= 0 ? Plugin.MyId : 0;
    static bool Online => Plugin != null && Plugin.Connected && Plugin.MyId >= 0;

    // ---- every frame ----

    public static void Tick()
    {
        var me = LocalPlayer.Get();
        LearnHome(me);
        Button(me);
        TickChannel(me);
        foreach (var l in links.Values.ToList()) Show(l, me);
        Share();
    }

    static void LearnHome(PlayerMaster me)
    {
        if (!me || LocalPlayer.Scene != HomeScene || Dungeon.Active || !Singleton<GameManager>.HasMain) return;
        // only an area that really holds the town spot (walking in at the starting door, the house's area comes on a
        // moment before you're moved into it: that's not town)
        var area = Singleton<GameManager>.main.CurrentArea;
        if (area && Travel.Holds(area, HomeSpot) && Travel.Holds(area, me.transform.position)) homeArea = area.name;
    }

    /// <summary>In the starting area's outdoors: the town portals show (from anywhere in it).</summary>
    static bool TownHere => LocalPlayer.Scene == HomeScene && !Dungeon.Active && LocalPlayer.Area == homeArea;

    /// <summary>By the starting door: no casting a portal home (you're there).</summary>
    static bool InTown => LocalPlayer.Scene == HomeScene && !Dungeon.Active && LocalPlayer.Get() && LocalPlayer.Area == homeArea &&
                          ((UnityEngine.Vector2)(LocalPlayer.Get().transform.position - HomeSpot)).magnitude < 8f;

    /// <summary>This pair's home end: its owner's /sethome beside their home's own portal, else town (in its row).</summary>
    static bool HomeEndHere(Link l) => string.IsNullOrEmpty(l.HomeScene) ? TownHere
        : !Dungeon.Active && LocalPlayer.Scene == l.HomeScene && LocalPlayer.Area == l.HomeArea;
    static Vector3 HomeEndAt(Link l) => string.IsNullOrEmpty(l.HomeScene) ? TownSpot(l.Owner) : l.HomeAt + new Vector3(RowStep, 0f, 0f);

    static Vector3 TownSpot(int owner)
    {
        int slot = links.Keys.OrderBy(k => k).ToList().IndexOf(owner);
        return HomeSpot + new Vector3(-RowStep * Mathf.Max(0, slot), 0f, 0f);
    }

    // ---- the button (right edge, half way down) and the channel ----

    static bool hovered, stashHovered;

    /// <summary>Better Chests' shared stash (its Stash.Toggle), if it's installed.</summary>
    static readonly System.Reflection.MethodInfo stashToggle =
        HarmonyLib.AccessTools.TypeByName("BetterChests.Stash") is System.Type t ? HarmonyLib.AccessTools.Method(t, "Toggle") : null;
    static float channel = -1f, healthAtStart;
    static Vector3 startedAt;

    static void Button(PlayerMaster me)
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        hovered = stashHovered = false;
        bool shown = Buttons(me);
        if (shown && mouse != null && Application.isFocused && !ImGuiUnity.IsOpen)
        {
            var p = mouse.position.ReadValue();
            hovered = Over(portalBtn, p);
            stashHovered = stashToggle != null && stashBtn.Root && stashBtn.Root.gameObject.activeSelf && Over(stashBtn, p);
            if (hovered && mouse.leftButton.wasReleasedThisFrame) Pressed(me);
            if (hovered && mouse.rightButton.wasReleasedThisFrame) Places(new UnityEngine.Vector2(p.x, p.y));
            if (stashHovered && mouse.leftButton.wasReleasedThisFrame)
            {
                try { stashToggle.Invoke(null, null); }
                catch (System.Exception e) { NetLog.Warn($"Stash: {e.InnerException?.Message ?? e.Message}", toast: false); }
            }
        }
        // the places list: a click anywhere else (or Esc) closes it
        if (places && mouse != null && !hovered && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) &&
            !RectTransformUtility.RectangleContainsScreenPoint(places, mouse.position.ReadValue(), GameUi.Root && GameUi.Root.renderMode != RenderMode.ScreenSpaceOverlay ? GameUi.Root.worldCamera : null))
            ClosePlaces();
        if (places && UnityEngine.InputSystem.Keyboard.current?.escapeKey.wasPressedThisFrame == true) ClosePlaces();
        bool overPlaces = places && mouse != null && RectTransformUtility.RectangleContainsScreenPoint(places, mouse.position.ReadValue(),
            GameUi.Root && GameUi.Root.renderMode != RenderMode.ScreenSpaceOverlay ? GameUi.Root.worldCamera : null);
        if (hovered || stashHovered || overPlaces) GameInput.Block("townportal"); else GameInput.Unblock("townportal");
    }

    static RectTransform places;

    /// <summary>Right-click on the button: your /sethome places (and town) to go to now.</summary>
    static void Places(UnityEngine.Vector2 at)
    {
        ClosePlaces();
        var items = new List<(string, System.Action, bool)>();
        foreach (var n in Homes.Names())
        {
            string name = n;
            items.Add((name == "home" ? "Home" : name, () => { ClosePlaces(); Homes.Go(name); }, true));
        }
        items.Add(("Town", () => { ClosePlaces(); Travel.Go(new Travel.Spot { Scene = HomeScene, Area = homeArea, At = HomeSpot + new Vector3(0f, -1.3f, 0f) }); }, true));
        if (items.Count == 1) items.Insert(0, ("(no homes yet: /sethome)", null, false));
        items.Add(("Cancel", ClosePlaces, true));
        places = GameUi.Popup(at, 10f, "Go to", items.ToArray());
    }

    static void ClosePlaces()
    {
        if (places) Object.Destroy(places.gameObject);
        places = null;
    }

    static void Pressed(PlayerMaster me)
    {
        if (channel >= 0f) { channel = -1f; NetLog.Info("Town portal: cancelled", toast: true); return; }
        if (Dungeon.Busy) return;
        if (Homes.Get("home") is Travel.Spot h)
        {
            if (!Dungeon.Active && LocalPlayer.Scene == h.Scene && LocalPlayer.Area == h.Area && ((UnityEngine.Vector2)(me.transform.position - h.At)).magnitude < 8f)
            { NetLog.Info("You're already home", toast: true); return; }
        }
        else if (InTown) { NetLog.Info("You're already in town", toast: true); return; }

        channel = 0f;
        healthAtStart = me.CurrentHealthPoints;
        startedAt = me.transform.position;
        NetLog.Info("Opening a town portal... hold still", toast: true);
    }

    static void TickChannel(PlayerMaster me)
    {
        if (channel < 0f) return;
        if (!me || me.IsDead) { channel = -1f; return; }
        if (me.CurrentHealthPoints < healthAtStart - 0.01f) { channel = -1f; NetLog.Info("Town portal: interrupted (you were hit)", toast: true); return; }
        if (((UnityEngine.Vector2)(me.transform.position - startedAt)).magnitude > 0.3f) { channel = -1f; NetLog.Info("Town portal: interrupted (you moved)", toast: true); return; }
        channel += Time.deltaTime;
        if (channel < Channel) return;
        channel = -1f;
        Open(me);
    }

    /// <summary>The pair: here (just above you) and in town; then home you go.</summary>
    static void Open(PlayerMaster me)
    {
        var l = new Link
        {
            Owner = MeId,
            Name = GameData.HasPlayerData && !string.IsNullOrEmpty(GameData.playerData.playerName) ? GameData.playerData.playerName : "Player",
            Scene = LocalPlayer.Scene, Area = LocalPlayer.Area,
            InDungeon = Dungeon.Active && Dungeon.Current.HasValue,
            At = me.transform.position + new Vector3(0f, 1.1f, 0f),
        };
        if (l.InDungeon) l.Where = Dungeon.Current.Value;
        if (Homes.Get("home") is Travel.Spot h) { l.HomeScene = h.Scene; l.HomeArea = h.Area; l.HomeAt = h.At; }   // (to your /sethome)
        Set(l);
        Send(l);
        Home(l);
    }

    // ---- travel ----

    static GameArea AreaNamed(string name) =>
        Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => a && a.name == name);

    /// <summary>To this pair's town end (in front of it, not in it).</summary>
    static void Home(Link l)
    {
        l.TownArmed = false;
        // (the starting area: from anywhere, another scene too; its area found by the spot if we've never been)
        bool own = !string.IsNullOrEmpty(l.HomeScene);
        var to = new Travel.Spot { Scene = own ? l.HomeScene : HomeScene, Area = own ? l.HomeArea : homeArea, At = HomeEndAt(l) + new Vector3(0f, -1.3f, 0f) };
        if (Travel.Go(to)) NetLog.Info($"Through {l.Name}'s portal: {(own ? "home" : "to town")}", toast: true);
    }

    /// <summary>To this pair's far end (in front of it).</summary>
    static void Away(Link l)
    {
        l.FarArmed = false;
        var to = new Travel.Spot { Scene = l.Scene, Area = l.Area, InDungeon = l.InDungeon, Where = l.Where, At = l.At + new Vector3(0f, -1.3f, 0f) };
        if (Travel.Go(to)) NetLog.Info($"Through {l.Name}'s portal", toast: true);
    }

    // ---- the portals in the world ----

    static bool FarHere(Link l) => l.InDungeon
        ? Dungeon.Active && Dungeon.Current.HasValue && Dungeon.Current.Value == l.Where
        : !Dungeon.Active && LocalPlayer.Scene == l.Scene && LocalPlayer.Area == l.Area;

    static void Show(Link l, PlayerMaster me)
    {
        bool far = FarHere(l), town = HomeEndHere(l);
        Keep(ref l.Far, far, l.At, $"EG_TownPortal_{l.Owner}_far", Tint(l.Owner));
        Keep(ref l.Town, town, HomeEndAt(l), $"EG_TownPortal_{l.Owner}_town", Tint(l.Owner));
        if (!me || Dungeon.Busy || PlayerStatusUtil.LocalBusy || ImGuiUnity.Typing) return;
        if (far && Stepped(me, l.Far, ref l.FarArmed, ref l.FarStood)) Home(l);
        else if (town && Stepped(me, l.Town, ref l.TownArmed, ref l.TownStood)) Away(l);
    }

    /// <summary>Each player's portals in their own colour (the host's gold, as on the party strip; then blue, green, pink...).</summary>
    static readonly Color[] Tints =
    {
        new Color(1f, 0.82f, 0.35f), new Color(0.45f, 0.75f, 1f), new Color(0.5f, 1f, 0.55f), new Color(1f, 0.55f, 0.85f),
        new Color(0.75f, 0.55f, 1f), new Color(1f, 0.6f, 0.35f),
    };
    static Color Tint(int owner) => Tints[Mathf.Abs(owner) % Tints.Length];

    static void Keep(ref GameObject go, bool want, Vector3 at, string name, Color tint)
    {
        if (!want) { if (go) Object.Destroy(go); go = null; return; }
        if (go) { go.transform.position = at; return; }
        go = new GameObject(name);
        go.transform.position = at;
        bool beam = Beam(go.transform);
        if (!beam) PadFx.Portal(go.transform, at);   // (the dungeon's portal, if the beam isn't to be found)
        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true)) sr.color = new Color(tint.r, tint.g, tint.b, sr.color.a);
        return;
    }

    // ---- the look: the mage's magic beam effect (Attack_Mage_MagicBeam_Fx), smaller than the dungeon's portal ----

    /// <summary>A portal's look under parent, in this colour (also the custom rooms' teleporters and exits).</summary>
    internal static void Look(Transform parent, Vector3 at, Color tint)
    {
        if (!Beam(parent)) PadFx.Portal(parent, at);
        foreach (var sr in parent.GetComponentsInChildren<SpriteRenderer>(true)) sr.color = new Color(tint.r, tint.g, tint.b, sr.color.a);
    }

    const float BeamScale = 1f;
    static GameObject beamFx;
    static Sprite[] beamFrames;
    static bool beamLooked;
    static float lookAgain;

    /// <summary>The game's magic beam effect standing here: its own object and animation if it's loaded (scripts off),
    /// else its frames played as a loop. False if neither is there.</summary>
    static bool Beam(Transform parent)
    {
        if (!beamLooked || !beamFx && beamFrames.Length == 0 && Time.unscaledTime >= lookAgain)
        {
            beamLooked = true;
            lookAgain = Time.unscaledTime + 20f;   // (not loaded yet: look again later, a mage may have cast it by then)
            beamFx = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g && g.name == "Attack_Mage_MagicBeam_Fx" && g.GetComponentInChildren<SpriteRenderer>(true));
            beamFrames = Resources.FindObjectsOfTypeAll<Sprite>()
                .Where(sp => sp && System.Text.RegularExpressions.Regex.IsMatch(sp.name, @"^Attack_Mage_MagicBeam_\d+$"))
                .GroupBy(sp => sp.name).Select(g => g.First())
                .OrderBy(sp => int.Parse(sp.name.Substring(sp.name.LastIndexOf('_') + 1))).ToArray();
            NetLog.Info($"Town portal: the magic beam effect {(beamFx ? "found" : "not loaded")}, {beamFrames.Length} frames", toast: false);
        }
        var player = LocalPlayer.Get() ? LocalPlayer.Get().GetComponentInChildren<SpriteRenderer>() : null;
        if (beamFx)
        {
            var holder = new GameObject("EG_BeamHolder");
            holder.SetActive(false);
            try
            {
                var copy = Object.Instantiate(beamFx, holder.transform);
                copy.name = "EG_PortalBeam";
                foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb && mb.GetType().Assembly == typeof(PlayerMaster).Assembly) Object.DestroyImmediate(mb);
                foreach (var c in copy.GetComponentsInChildren<Collider2D>(true)) Object.DestroyImmediate(c);
                copy.transform.SetParent(parent, false);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one * BeamScale;
                foreach (var sr in copy.GetComponentsInChildren<SpriteRenderer>(true))
                    if (player) { sr.sortingLayerID = player.sortingLayerID; sr.sortingOrder = player.sortingOrder + 1; }
                copy.SetActive(true);
                foreach (var a in copy.GetComponentsInChildren<Animator>(true)) { a.enabled = true; a.Rebind(); }
            }
            finally { Object.Destroy(holder); }
            return true;
        }
        if (beamFrames.Length == 0) return false;
        var go = new GameObject("EG_PortalBeam");
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * BeamScale;
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = beamFrames[0];
        if (player) { r.sortingLayerID = player.sortingLayerID; r.sortingOrder = player.sortingOrder + 1; r.sharedMaterial = player.sharedMaterial; }
        go.AddComponent<Flipbook>().Frames = beamFrames;
        return true;
    }

    /// <summary>Frames played round and round (12 a second).</summary>
    sealed class Flipbook : MonoBehaviour
    {
        public Sprite[] Frames;
        SpriteRenderer r;
        void Awake() => r = GetComponent<SpriteRenderer>();
        void Update() { if (r && Frames != null && Frames.Length > 0) r.sprite = Frames[(int)(Time.time * 12f) % Frames.Length]; }
    }

    /// <summary>Stood in it a moment (after having walked away from it once).</summary>
    static bool Stepped(PlayerMaster me, GameObject portal, ref bool armed, ref float stood)
    {
        if (!portal) return false;
        float d = ((UnityEngine.Vector2)(me.transform.position - portal.transform.position)).magnitude;
        if (d > StepIn) { if (d > StepIn * 3f) armed = true; stood = 0f; return false; }
        if (!armed) return false;
        stood += Time.deltaTime;
        if (stood < Hold) return false;
        stood = 0f;
        PadFx.Burst(portal);
        return true;
    }

    static void Set(Link l)
    {
        if (links.TryGetValue(l.Owner, out var old)) { if (old.Far) Object.Destroy(old.Far); if (old.Town) Object.Destroy(old.Town); }
        links[l.Owner] = l;
        sentTo.Clear();   // (the host: everyone hears of it again)
    }

    // ---- the buttons (the game's own UI: a framed picture, its name under it) ----

    sealed class Side { public RectTransform Root; public UnityEngine.UI.Image Frame, Pic; public TMPro.TextMeshProUGUI Caption; }
    static RectTransform column, channelFill;
    static Side portalBtn, stashBtn;
    // (the game's frame, a shade darker; lit up under the mouse. No frame sprite loaded: a plain dark box)
    static Color FrameColour(Side b) => b.Frame.sprite ? new Color(0.8f, 0.8f, 0.8f, 1f) : new Color(0.13f, 0.11f, 0.1f, 0.96f);
    static Color FrameLit(Side b) => b.Frame.sprite ? Color.white : new Color(0.32f, 0.24f, 0.17f, 0.98f);

    static bool Over(Side b, UnityEngine.Vector2 mouse) => b != null && b.Root &&
        RectTransformUtility.RectangleContainsScreenPoint(b.Root, mouse, GameUi.Root.renderMode == RenderMode.ScreenSpaceOverlay ? null : GameUi.Root.worldCamera);

    /// <summary>Every frame: the buttons there (or not: no player, a menu up, the boss arena), up to date. True: shown.</summary>
    static bool Buttons(PlayerMaster me)
    {
        // (none in the boss arena: its panel is on that side, and a town portal out of a fight there is no use)
        bool want = me && !PlayerStatusUtil.LocalBusy && !Arena.Active;
        if (!want) { if (column) column.gameObject.SetActive(false); return false; }
        if (!column) Build();
        column.gameObject.SetActive(true);
        portalBtn.Caption.text = channel >= 0f ? $"Opening... {Mathf.CeilToInt(Channel - channel)}" : Homes.Has("home") ? "Home Portal" : "Town Portal";
        // (the portal's own colours: green with a /sethome home, as the home portals are)
        if (portalBtn.Pic.sprite) portalBtn.Pic.color = Homes.Has("home") ? new Color(0.55f, 1f, 0.75f) : Color.white;
        portalBtn.Frame.color = hovered ? FrameLit(portalBtn) : FrameColour(portalBtn);
        channelFill.parent.gameObject.SetActive(channel >= 0f);
        if (channel >= 0f) channelFill.anchorMax = new UnityEngine.Vector2(Mathf.Clamp01(channel / Channel), 1f);
        stashBtn.Root.gameObject.SetActive(stashToggle != null);
        stashBtn.Frame.color = stashHovered ? FrameLit(stashBtn) : FrameColour(stashBtn);
        return true;
    }

    static void Build()
    {
        column = GameUi.Rect("EG_SideButtons", GameUi.Root.transform);
        column.anchorMin = column.anchorMax = column.pivot = new UnityEngine.Vector2(1f, 0.5f);
        column.anchoredPosition = new UnityEngine.Vector2(-14f, 0f);
        var v = column.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        v.spacing = 8; v.childAlignment = TextAnchor.MiddleCenter;
        v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = v.childForceExpandHeight = false;
        var fit = column.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        fit.horizontalFit = fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        portalBtn = MakeSide("Portal", "portal.png");
        // the channel: a bar under the portal button, filling
        var bar = GameUi.Rect("Channel", column);
        GameUi.Fixed(bar, 104, 5);
        bar.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0f, 0f, 0f, 0.6f);
        channelFill = GameUi.Rect("Fill", bar);
        channelFill.anchorMin = UnityEngine.Vector2.zero; channelFill.anchorMax = new UnityEngine.Vector2(0f, 1f);
        channelFill.offsetMin = channelFill.offsetMax = UnityEngine.Vector2.zero;
        channelFill.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(0.55f, 0.75f, 1f, 1f);
        stashBtn = MakeSide("Stash", "stash.png");
        stashBtn.Caption.text = "Stash";
    }

    static Side MakeSide(string name, string picture)
    {
        var b = new Side { Root = GameUi.Rect("EG_Side_" + name, column) };
        GameUi.Fixed(b.Root, 104, 92);
        b.Frame = GameUi.Img(b.Root.gameObject, "UI_Hud_CommonElements_10", new Color(0.13f, 0.11f, 0.1f, 0.96f), 1.5f);
        var pic = GameUi.Rect("Picture", b.Root);
        pic.anchorMin = new UnityEngine.Vector2(0f, 0.3f); pic.anchorMax = UnityEngine.Vector2.one;
        pic.offsetMin = new UnityEngine.Vector2(14f, 0f); pic.offsetMax = new UnityEngine.Vector2(-14f, -10f);
        var img = b.Pic = pic.gameObject.AddComponent<UnityEngine.UI.Image>();
        img.sprite = GameUi.FileSprite(picture);
        img.preserveAspect = true;
        img.raycastTarget = false;
        if (!img.sprite) img.color = new Color(0f, 0f, 0f, 0f);
        b.Caption = GameUi.Label(b.Root, name, 15, GameUi.Text, TMPro.TextAlignmentOptions.Center);
        var cr = b.Caption.rectTransform;
        cr.anchorMin = UnityEngine.Vector2.zero; cr.anchorMax = new UnityEngine.Vector2(1f, 0.3f);
        cr.offsetMin = new UnityEngine.Vector2(2f, 6f); cr.offsetMax = new UnityEngine.Vector2(-2f, 0f);
        b.Caption.raycastTarget = false;
        return b;
    }

    // ---- the names over the portals (ImGui) ----

    public static void Draw()
    {
        if (!LocalPlayer.Get()) return;
        var cam = Camera.main;
        if (!cam) return;
        var bg = ImGui.GetBackgroundDrawList();
        foreach (var l in links.Values)
            foreach (var go in new[] { l.Far, l.Town })
            {
                if (!go) continue;
                var sp = cam.WorldToScreenPoint(go.transform.position + new Vector3(0f, 1.6f, 0f));
                if (sp.z < 0f) continue;
                string label = $"{l.Name}'s portal";
                var size = ImGui.CalcTextSize(label);
                var pos = new Vector2(sp.x - size.x / 2f, Screen.height - sp.y);
                bg.AddText(new Vector2(pos.x + 1f, pos.y + 1f), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f)), label);
                var t = Tint(l.Owner);
                bg.AddText(pos, ImGui.ColorConvertFloat4ToU32(new Vector4(t.r, t.g, t.b, 1f)), label);
            }
    }

    // ---- together ----

    static NetDataWriter Message(Link l)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.TownPortal);
        w.Put(l.Owner); w.Put(l.Name ?? ""); w.Put(l.Scene ?? ""); w.Put(l.Area ?? "");
        w.Put(l.InDungeon);
        w.Put(l.Where.Settings ?? ""); w.Put(l.Where.Seed); w.Put(l.Where.Region); w.Put(l.Where.Floor);
        w.Put(l.At.x); w.Put(l.At.y);
        w.Put(l.HomeScene ?? ""); w.Put(l.HomeArea ?? ""); w.Put(l.HomeAt.x); w.Put(l.HomeAt.y);   // (last: older copies stop reading before it)
        return w;
    }

    static void Send(Link l) { if (Online) Plugin.SendToAll(Message(l), DeliveryMethod.ReliableOrdered); }

    public static void OnMessage(NetDataReader r)
    {
        var l = new Link { Owner = r.GetInt(), Name = r.GetString(), Scene = r.GetString(), Area = r.GetString(), InDungeon = r.GetBool() };
        l.Where = new Dungeon.Where(r.GetString(), r.GetInt(), r.GetInt(), r.GetInt());
        l.At = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
        if (r.AvailableBytes > 0) { l.HomeScene = r.GetString(); l.HomeArea = r.GetString(); l.HomeAt = new Vector3(r.GetFloat(), r.GetFloat(), 0f); }
        if (Online && l.Owner == Plugin.MyId) return;   // (our own, back again)
        Set(l);
    }

    /// <summary>The host: every pair to whoever just joined. Everyone: theirs gone when they leave.</summary>
    static void Share()
    {
        if (!Online) return;
        var ids = Plugin.Players.Select(p => p.Id).ToList();
        foreach (var gone in links.Keys.Where(k => k != Plugin.MyId && !ids.Contains(k) && !(Plugin.IsHost == false && k == 0)).ToList())
        {
            var l = links[gone];
            if (l.Far) Object.Destroy(l.Far);
            if (l.Town) Object.Destroy(l.Town);
            links.Remove(gone);
        }
        if (!Plugin.IsHost) { sentTo.Clear(); return; }
        sentTo.RemoveWhere(id => !ids.Contains(id));
        foreach (var id in ids)
            if (sentTo.Add(id)) foreach (var l in links.Values) Plugin.SendTo(id, Message(l));
    }
}

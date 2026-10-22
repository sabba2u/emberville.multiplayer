using System.Linq;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// Rory, the archer by the descent's pad: a stand-in made like a teammate's (a copy of our character's art, the game's
/// scripts stripped), with a different hairdo and a bow, and the game's speech bubble. Walk up and press the game's
/// interact key and he tells you what happened to sabba2u (one line per press).
/// </summary>
internal static class Rory
{
    static RemoteAvatar body;
    static Talk talk;
    static readonly string[] Lines =
    {
        "Help! My friend sabba2u got pulled into the cursed dungeon!",
        "It goes down and down... forever. Nobody has ever found the bottom.",
        "Every floor the monsters get nastier. I heard something huge guards the stairs.",
        "Step on the glowing pad if you're brave enough. I'll keep watch up here!",
        "The other pad's our sparring ring. Practise on a boss or two there first.",
    };
    static int line = -1;
    static float sayUntil;

    public static bool Here => body != null && body.Root;
    public static GameObject Root => body != null ? body.Root : null;

    public static void Spawn(Vector3 at, Vector3 lookAt)
    {
        Remove();
        var me = LocalPlayer.Get();
        if (!me) return;
        try
        {
            body = RemoteAvatar.Create(me, 99);
            body.Root.name = "EG_Rory";
            // not a teammate: creatures leave him alone, nothing collides with him
            foreach (var t in body.Root.GetComponentsInChildren<StandInTarget>(true)) Object.Destroy(t);
            foreach (var c in body.Root.GetComponentsInChildren<Collider2D>(true)) Object.Destroy(c);
            Dress(me, body.Root);
            body.Root.transform.position = at;
            Face(me, body.Root, lookAt.x < at.x);

            // the game's interact (the same way a chest or a fishing spot joins the player's list)
            var trigger = new GameObject("Talk");
            trigger.transform.SetParent(body.Root.transform, false);
            var chest = Object.FindAnyObjectByType<Chest>(FindObjectsInactive.Include);
            if (chest) trigger.layer = chest.gameObject.layer;
            var circle = trigger.AddComponent<CircleCollider2D>();
            circle.isTrigger = true;
            circle.radius = 1.6f;
            circle.offset = new UnityEngine.Vector2(0f, 0.6f);
            talk = trigger.AddComponent<Talk>();
            line = -1;
        }
        catch (System.Exception e) { NetLog.Warn($"Rory: {e.Message}", toast: false); Remove(); }
    }

    public static void Remove()
    {
        body?.Destroy();
        body = null;
        talk = null;
    }

    /// <summary>A different hairdo than ours, and a bow in hand (the character's own folders: AddonsHandler).</summary>
    static void Dress(PlayerMaster me, GameObject copy)
    {
        var addons = me.GetComponentInChildren<AddonsHandler>(true);
        if (!addons) return;
        // hair: the same folders, a few styles along from ours
        int styles = addons.hairAssets.Count;
        if (styles > 1)
        {
            var hair = addons.hairAssets[(CurrentHair(addons) + 3) % styles];
            ShowIn(me, copy, addons, AddonSlot.HairOverlay, hair.overlayHairIndex);
            ShowIn(me, copy, addons, AddonSlot.HairFront, hair.frontHairIndex);
            ShowIn(me, copy, addons, AddonSlot.HairBehind, hair.behindHairIndex);
        }
        // a bow: the weapon folder's children are numbered (00_Weapon...), a weapon's own index says which is its art
        if (addons.weaponFolder)
        {
            var path = RemoteAvatar.PathFrom(me.transform, addons.weaponFolder);
            weaponFolder = path == null ? null : path.Length == 0 ? copy.transform : copy.transform.Find(path);
            bowDone = false;
            // and his own outfit, not ours: the same equipment folders, other pieces (once the catalogue's loaded)
            equip.Clear();
            foreach (var ef in addons.equipFolders)
            {
                if (ef.folders == null) continue;
                var mine = new System.Collections.Generic.List<Transform>();
                foreach (var f in ef.folders)
                {
                    var fp = RemoteAvatar.PathFrom(me.transform, f);
                    var t = fp == null ? null : fp.Length == 0 ? copy.transform : copy.transform.Find(fp);
                    if (t) mine.Add(t);
                }
                equip[ef.slot] = mine;
            }
            hairFolders.Clear();
            foreach (var h in addons.hairFolders)
            {
                var hp = RemoteAvatar.PathFrom(me.transform, h ? h.transform : null);
                var t = hp == null ? null : copy.transform.Find(hp);
                if (t) hairFolders.Add(t.gameObject);
            }
            DungeonLoot.Preload();   // the item catalogue: where we find a bow (Tick puts it in his hands once it's loaded)
        }
    }

    static Transform weaponFolder;
    static bool bowDone;
    static readonly System.Collections.Generic.Dictionary<GearSlot, System.Collections.Generic.List<Transform>> equip = new System.Collections.Generic.Dictionary<GearSlot, System.Collections.Generic.List<Transform>>();
    static readonly System.Collections.Generic.List<GameObject> hairFolders = new System.Collections.Generic.List<GameObject>();

    /// <summary>AddonsHandler.ShowEquipment on his copy: in the slot's folders only that piece (-1 = none; no hat shows his hair).</summary>
    static void Wear(GearSlot slot, int layer)
    {
        if (!equip.TryGetValue(slot, out var folders)) return;
        foreach (var f in folders)
            for (int i = 0; i < f.childCount; i++) f.GetChild(i).gameObject.SetActive(i == layer);
        if (slot == GearSlot.Head) foreach (var h in hairFolders) if (h) h.SetActive(layer < 0);
    }

    /// <summary>Light armour (an archer's leathers) for his body and legs, picked from the catalogue; no hat.</summary>
    static void Outfit()
    {
        var armour = DungeonLoot.Pool.OfType<CollectableArmor>().Where(a => a.gear != null && a.gear.layerIndex >= 0).OrderBy(a => a.name).ToList();
        CollectableArmor Pick(GearSlot slot) =>
            armour.FirstOrDefault(a => a.Weight == GearWeight.Light && (a.gear.gearSlot & slot) != 0) ??
            armour.FirstOrDefault(a => (a.gear.gearSlot & slot) != 0);
        Wear(GearSlot.Head, -1);
        foreach (var slot in new[] { GearSlot.UpperBody, GearSlot.LowerBody, GearSlot.Cloak })
        {
            var a = Pick(slot);
            Wear(slot, a != null ? a.gear.layerIndex : -1);
        }
        NetLog.Info($"Rory's outfit: {string.Join(", ", new[] { GearSlot.UpperBody, GearSlot.LowerBody, GearSlot.Cloak }.Select(sl => Pick(sl)?.name ?? "-"))}", toast: false);
    }

    static void GiveBow()
    {
        if (bowDone || DungeonLoot.Pool == null) return;
        bowDone = true;
        try { Outfit(); } catch (System.Exception e) { NetLog.Warn($"Rory's outfit: {e.Message}", toast: false); }
        if (!weaponFolder) return;
        var bow = DungeonLoot.Pool.OfType<CollectableWeapon>()
            .FirstOrDefault(w => w.weapon != null && w.weapon.weaponCategory == WeaponCategory.Bows && w.weapon.weaponIndex >= 0 && w.weapon.weaponIndex < weaponFolder.childCount);
        if (bow == null) { NetLog.Info("Rory: no bow in the item catalogue", toast: false); return; }
        int index = bow.weapon.weaponIndex;
        for (int i = 0; i < weaponFolder.childCount; i++) weaponFolder.GetChild(i).gameObject.SetActive(i == index);
        NetLog.Info($"Rory has a bow ({bow.name})", toast: false);
    }

    static int CurrentHair(AddonsHandler addons)
    {
        var front = addons.addonFolders.Find(f => f.slot == AddonSlot.HairFront);
        var first = front.folders?.FirstOrDefault();
        if (!first) return 0;
        for (int i = 0; i < first.childCount; i++)
            if (first.GetChild(i).gameObject.activeSelf)
                for (int h = 0; h < addons.hairAssets.Count; h++) if (addons.hairAssets[h].frontHairIndex == i) return h;
        return 0;
    }

    /// <summary>AddonsHandler.Show on the copy: in each of the slot's folders, only child number <paramref name="index"/> on.</summary>
    static void ShowIn(PlayerMaster me, GameObject copy, AddonsHandler addons, AddonSlot slot, int index)
    {
        var af = addons.addonFolders.Find(f => f.slot == slot);
        if (af.folders == null) return;
        foreach (var f in af.folders)
        {
            var path = RemoteAvatar.PathFrom(me.transform, f);
            var mine = path == null ? null : copy.transform.Find(path);
            if (!mine) continue;
            for (int i = 0; i < mine.childCount; i++) mine.GetChild(i).gameObject.SetActive(i == index);
        }
    }

    static void Face(PlayerMaster me, GameObject copy, bool left)
    {
        foreach (var pivot in me.charPivotFlip)
        {
            var path = RemoteAvatar.PathFrom(me.transform, pivot);
            var p = path == null ? null : path.Length == 0 ? copy.transform : copy.transform.Find(path);
            if (p) p.rotation = Quaternion.Euler(0f, left ? 180f : 0f, 0f);
        }
    }

    static void Say()
    {
        line = (line + 1) % Lines.Length;
        sayUntil = Time.unscaledTime + Mathf.Clamp(Lines[line].Split(' ').Length * 0.34f, 3f, 6f) + 1.5f;
        body?.SetBubble(Lines[line]);
    }

    /// <summary>Every frame: the bubble's time.</summary>
    public static void Tick()
    {
        if (!Here) return;
        GiveBow();
        if (sayUntil > 0f && Time.unscaledTime > sayUntil) { sayUntil = 0f; body.SetBubble(null); }
    }

    /// <summary>From Hud.Draw: his name (and the line, if the game's bubble isn't there).</summary>
    public static void Draw()
    {
        if (!Here || !body.Root.activeInHierarchy) return;
        var cam = Camera.main;
        if (!cam) return;
        var s = cam.WorldToScreenPoint(body.HeadPosition + Vector3.up * 0.4f);
        if (s.z < 0) return;
        var dl = ImGui.GetBackgroundDrawList();
        string name = talk && talk.Near ? "Rory  (talk)" : "Rory";
        var size = ImGui.CalcTextSize(name);
        float x = s.x, y = Screen.height - s.y;
        dl.AddRectFilled(new Vector2(x - size.x / 2 - 4, y - 1), new Vector2(x + size.x / 2 + 4, y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.5f)), 3f);
        dl.AddText(new Vector2(x - size.x / 2, y), ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 1f, 0.6f, 1f)), name);
        if (!body.HasBubble && sayUntil > 0f && line >= 0)
        {
            var t = Lines[line];
            var ts = ImGui.CalcTextSize(t);
            float ty = y - ts.y - 8;
            dl.AddRectFilled(new Vector2(x - ts.x / 2 - 6, ty - 3), new Vector2(x + ts.x / 2 + 6, ty + ts.y + 3), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.92f)), 6f);
            dl.AddText(new Vector2(x - ts.x / 2, ty), ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 1f)), t);
        }
    }

    /// <summary>The game's interact: he's on the player's list while they stand by him (like a chest).</summary>
    sealed class Talk : MonoBehaviour, IInteractable
    {
        public bool Near;
        bool greeted;

        void OnTriggerEnter2D(Collider2D other)
        {
            if (!other.CompareTag("PlayerCenter") || !other.attachedRigidbody || !other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h)) return;
            h.AddInteractable(transform, this);
            if (!greeted) { greeted = true; line = -1; Say(); }   // he calls out the first time you come by
        }

        void OnTriggerExit2D(Collider2D other)
        {
            if (!other.CompareTag("PlayerCenter") || !other.attachedRigidbody || !other.attachedRigidbody.TryGetComponent<InteractablesHandler>(out var h)) return;
            h.RemoveInteractable(transform);
        }

        public void OnEnableInteraction(GameObject owner) => Near = true;
        public void OnDisableInteraction(GameObject owner) => Near = false;
        public void OnInteract(GameObject owner) => Say();
        public bool CanInteract() => true;
        public bool CanRepeat(out float interval) { interval = 0f; return false; }
    }
}

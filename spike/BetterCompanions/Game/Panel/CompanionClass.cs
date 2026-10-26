using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterCompanions;

/// <summary>
/// The companion's own skill tree. The game keeps a tree's points on its nodes, and each node reads them from your save
/// (GameData.playerData.nodeInfos): a class shared with you would show (and spend) your points. So its class gets a
/// copy of the tree, every node copied, each reading its points from the companion's save instead.
/// </summary>
internal static class CompanionTree
{
    const string Mark = " (BC companion)";

    /// <summary>Its class (a copy of the game's: never the one you'd equip) with a tree of its own.</summary>
    public static PlayerCombatClass Own(PlayerCombatClass cls, PlayerData data)
    {
        if (!cls || data == null) return cls;
        if (!cls.name.EndsWith("(Clone)") && !cls.name.EndsWith(Mark)) { cls = Object.Instantiate(cls); }
        cls.name = cls.name.Replace("(Clone)", "").Trim() + Mark;
        var tree = cls.ClassSkillTree;
        if (!tree || tree.name.EndsWith(Mark)) return cls;
        var copy = Object.Instantiate(tree);
        copy.name = tree.name + Mark;
        var map = new Dictionary<SkillNode, SkillNode>();
        foreach (var n in tree.skillNodes.Where(n => n))
        {
            var c = Object.Instantiate(n);
            c.name = n.name;
            map[n] = c;
        }
        data.nodeInfos ??= new List<SkillNodeInfo>();
        foreach (var c in map.Values)
        {
            if (c.parent && map.TryGetValue(c.parent, out var p)) c.parent = p;
            var info = data.nodeInfos.Find(x => x.Id == c.uniqueID);
            if (info == null) { info = new SkillNodeInfo { Id = c.uniqueID, CurrentPoints = 0 }; data.nodeInfos.Add(info); }
            info.CommitedPoints = info.CurrentPoints;
            c.info = info;   // (its points: the companion's, never looked up in yours)
        }
        copy.skillNodes = tree.skillNodes.Select(n => n && map.TryGetValue(n, out var c) ? c : n).ToList();
        cls.skillTree = copy;   // (ClassSkillTree reads this)
        return cls;
    }
}

internal static partial class CompanionPanel
{
    // ---- its class, on the game's class screen (its tree, its class level, its points) ----

    internal static class Classes
    {
        public static bool On;

        public static void Open(RectTransform tab)
        {
            var panels = Object.FindAnyObjectByType<PanelsManager>();
            var p = RealPlayer.Current;
            if (!panels || !p || CompanionSave.Data == null) return;
            if (!p.characterStats.equippedClass) { ClassPicker.Open(tab); return; }   // (no class yet: pick one first)
            p.characterStats.equippedClass = CompanionTree.Own(p.characterStats.equippedClass, CompanionSave.Data);
            On = true;
            Hide();   // (the class screen, uncovered, shows the companion's)
            if (!panels.panelClass.activeInHierarchy) panels.TogglePanel(panels.panelClass);
            else Refresh();
            Log.Info($"Companion: class screen {(panels.panelClass.activeInHierarchy ? "open" : "still closed")} ({p.characterStats.equippedClass.playerClassType})");
            Hooks.Say($"Showing {CompanionSave.Data.playerName}'s class (close the screen to go back to trading).");
        }

        /// <summary>The class screen again (a new class: its tree). Its code isn't on the screen itself: found as the game
        /// has it, anywhere in the scene.</summary>
        public static void Refresh()
        {
            if (!RealPlayer.Current) return;
            foreach (var c in Resources.FindObjectsOfTypeAll<UIClassUpgrade>())
                if (c && c.gameObject.scene.IsValid()) c.GatherStats(RealPlayer.Current.characterStats);
        }

        static float escAt = -10f;

        /// <summary>Esc was pressed a moment ago (the screen closing because of it: everything closes, no trading).</summary>
        public static bool EscJustNow => Time.unscaledTime - escAt < 1f;

        public static void Tick()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) escAt = Time.unscaledTime;
            if (!On) return;
            var panels = Object.FindAnyObjectByType<PanelsManager>();
            if (panels && panels.panelClass && panels.panelClass.activeInHierarchy) return;
            On = false;
            ClassPicker.Close();   // (its picker with it)
            // back to trading with it (its X), unless you went elsewhere or pressed Esc (everything closes)
            if (EscJustNow) { Log.Info("Companion: class screen closed (Esc)"); return; }
            if (panels && !UIPanels.HasOpenedPanel && Singleton<GameManager>.main.gameState == GameState.Playing)
            {
                Log.Info("Companion: class screen closed, back to its inventory");
                CardActions.Trade();
            }
        }
    }

    /// <summary>The class screen reads the class's level from your save (GameData.playerData): the companion's while
    /// it shows the companion's (just while it gathers what to show).</summary>
    [HarmonyPatch(typeof(UIClassUpgrade), nameof(UIClassUpgrade.GatherStats))]
    static class ClassScreenData
    {
        static PlayerData yours;

        static void Prefix(ref CharacterStats characterStats)
        {
            if (!Classes.On || !RealPlayer.Current || CompanionSave.Data == null) return;
            characterStats = RealPlayer.Current.characterStats;
            yours = GameData.playerData;
            GameData.playerData = CompanionSave.Data;
        }

        static void Finalizer()
        {
            if (yours == null) return;
            GameData.playerData = yours;
            yours = null;
        }
    }

    /// <summary>A point spent on its tree: its skills (not yours), into its save.</summary>
    [HarmonyPatch(typeof(UIClassUpgrade), "OnSkillNodeApplied"), HarmonyPrefix]
    static bool ClassNodeApplied(UIClassUpgrade __instance)
    {
        var p = RealPlayer.Current;
        if (!Classes.On || !p) return true;
        __instance.UpdateModelAvailablePoints(0);
        CompanionSave.Write();
        try { RealSkills.Load(p); } catch (System.Exception e) { Log.Warn($"Companion skills: {e.Message}"); }
        Powers.Refresh();
        Log.Info("Companion: class points saved");
        return false;
    }

    /// <summary>The class screen's Change Class button: its class picker, not yours.</summary>
    [HarmonyPatch(typeof(PanelsManager), nameof(PanelsManager.TogglePanel), typeof(PanelName)), HarmonyPrefix]
    static bool ChangeItsClass(PanelName value)
    {
        if (!Classes.On || value != PanelName.ClassSelector) return true;
        var at = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject?.transform as RectTransform;
        var panels = Object.FindAnyObjectByType<PanelsManager>();
        ClassPicker.Open(at ? at : panels ? (RectTransform)panels.panelClass.transform : null);
        return false;
    }

    // ---- its powers: four slots at the right of its inventory, the skills it casts by itself ----

    internal static class Powers
    {
        static readonly List<(Image icon, RectTransform box)> slots = new();
        static GameObject column;
        static RectTransform swapColumn;
        static Object menu;
        const int Count = 4;

        /// <summary>From Build: a copy of its swap column, at the right of the panel, as four power slots.</summary>
        public static void Build(Transform side)
        {
            slots.Clear();
            if (!side || !rt) return;
            var col = Object.Instantiate(side.gameObject, side.parent, false);
            col.name = "BC_Powers";
            swapColumn = (RectTransform)side;
            // (just the looks: the slots' markers (selected, new, locked) and item code off, a click of our own on)
            foreach (var s in col.GetComponentsInChildren<UISlot>(true))
            {
                foreach (var g in new[] { s.badge, s.lockIcon, s.highlightElement }) if (g) g.SetActive(false);
                if (s.amountDisplay) s.amountDisplay.gameObject.SetActive(false);
                if (s.amountIcon) s.amountIcon.gameObject.SetActive(false);
                if (s.timer) s.timer.gameObject.SetActive(false);
                if (s.iconOutlineHolder) s.iconOutlineHolder.gameObject.SetActive(false);
                Object.DestroyImmediate(s);
            }
            foreach (var h in col.GetComponentsInChildren<UIHintBoxElement>(true)) Object.DestroyImmediate(h);
            foreach (var t in col.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (!t.transform.parent || !t.transform.parent.name.StartsWith("UISlot")) t.text = "Powers";
            var cells = col.GetComponentsInChildren<Transform>(true).Where(t => t.parent && t.parent.GetComponent<GridLayoutGroup>()).ToList();
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                cell.gameObject.SetActive(i < Count);
                if (i >= Count) continue;
                int slot = i;
                var icon = cell.GetComponentsInChildren<Image>(true).FirstOrDefault(im => im.name == "Icon");
                foreach (var t in cell.GetComponentsInChildren<TextMeshProUGUI>(true)) t.text = "";
                var hit = cell.GetComponent<Image>() ?? cell.gameObject.AddComponent<Image>();
                hit.raycastTarget = true;
                if (!cell.GetComponent<Image>().sprite) hit.color = new Color(0f, 0f, 0f, 0.01f);
                cell.gameObject.AddComponent<PowerClick>().On = () => Pick(slot);   // (left or right click, as your skill bar's)
                slots.Add((icon, (RectTransform)cell));
            }
            column = col;   // (put against the panel's right edge by Place, once it's showing and can be measured)
            col.SetActive(true);
            Log.Info($"Companion powers: {slots.Count} slots ({cells.Count(c => c.gameObject.activeSelf)} showing of {cells.Count})");
            Refresh();
        }

        /// <summary>From Place (the panel showing): against its right edge, as its swap column is against its left.</summary>
        public static void Place()
        {
            if (!column || !rt) return;
            // the panel itself (without either column), and how far the swap column tucks under its left edge: the
            // same at its right edge, level with it
            bool swapOn = swapColumn && swapColumn.gameObject.activeSelf;
            column.SetActive(false);
            if (swapOn) swapColumn.gameObject.SetActive(false);
            var main = Drawn(rt, rt);
            if (swapOn) swapColumn.gameObject.SetActive(true);
            column.SetActive(true);
            var swap = swapOn ? Drawn(swapColumn, rt) : new Vector2(main.x - 60f, main.x);
            var mine = Drawn((RectTransform)column.transform, rt);   // (what it draws: its frame may be wider)
            float tuck = swap.y - main.x;   // (how far into the panel the swap column reaches)
            float dx = main.y - tuck - mine.x;
            column.transform.position += rt.TransformVector(new Vector3(dx, 0f, 0f));
            if (swapOn) column.transform.position = new Vector3(column.transform.position.x, swapColumn.position.y, column.transform.position.z);
            Log.Info($"Companion powers: panel {main.x:0}..{main.y:0}, swap {swap.x:0}..{swap.y:0}, column moved {dx:0} (now {Drawn((RectTransform)column.transform, rt).x:0}..{Drawn((RectTransform)column.transform, rt).y:0})");
        }

        /// <summary>A column's own frame, left to right, in the panel's space.</summary>
        static Vector2 Span(RectTransform r)
        {
            var c = new Vector3[4];
            r.GetWorldCorners(c);
            return new Vector2(rt.InverseTransformPoint(c[0]).x, rt.InverseTransformPoint(c[2]).x);
        }

        /// <summary>Its slotted skills' icons.</summary>
        public static void Refresh()
        {
            var data = CompanionSave.Data;
            var learned = Learned();
            for (int i = 0; i < slots.Count; i++)
            {
                var (icon, _) = slots[i];
                if (!icon) continue;
                string id = data?.equippedSkills?.FirstOrDefault(e => e.Value == i).Key;
                var skill = id == null ? null : learned.FirstOrDefault(s => s.ID == id);
                icon.sprite = skill ? skill.uiIcon : null;
                icon.enabled = skill && skill.uiIcon;
                icon.color = Color.white;
            }
        }

        /// <summary>The skills it could slot: active ones it has points in, on its own tree.</summary>
        static List<Skill> Learned()
        {
            var cls = RealPlayer.Current ? RealPlayer.Current.characterStats.equippedClass : null;
            if (!cls || !cls.ClassSkillTree) return new List<Skill>();
            return cls.ClassSkillTree.skillNodes.Where(n => n && n.skill && n.CurrentPoints > 0 &&
                n.skill.activationType != Skill.ActivationType.Passive && !n.skill.IsImproved).Select(n => n.skill).ToList();
        }

        static GameObject strip;
        static readonly List<RectTransform> stripCells = new();

        /// <summary>As your skill bar's right click: the powers it has unlocked in a row beside the slot (each with its
        /// rank), and an X to empty it.</summary>
        static void Pick(int slot)
        {
            bool same = strip && openFor == slot;
            Close();
            if (same) return;   // (its slot again: just closed)
            var data = CompanionSave.Data;
            var p = RealPlayer.Current;
            if (data == null || !p || slot >= slots.Count) return;
            data.equippedSkills ??= new List<IntNamed>();
            var learned = LearnedNodes();
            if (learned.Count == 0)
                Hooks.Say(p.characterStats.equippedClass ? $"{data.playerName} has no powers yet: spend its class points (Class)." : $"{data.playerName} has no class yet: pick one (Class).");
            var box = slots[slot].box;
            strip = new GameObject("BC_PowerPick", typeof(RectTransform));
            strip.transform.SetParent(rt, false);
            strip.transform.SetAsLastSibling();
            openFor = slot;
            float step = box.rect.width + 4f;
            int i = 0;
            foreach (var n in learned)
            {
                var node = n;
                Cell(++i, node.skill.uiIcon, node.CurrentPoints.ToString(), () => { Close(); Set(slot, node.skill); });
            }
            Cell(++i, null, "X", () => { Close(); Set(slot, null); });

            void Cell(int at, Sprite icon, string label, System.Action click)
            {
                var c = Object.Instantiate(box.gameObject, strip.transform);
                c.name = "BC_PowerOption";
                foreach (var old in c.GetComponents<PowerClick>()) Object.DestroyImmediate(old);
                c.AddComponent<PowerClick>().On = click;
                c.transform.position = box.position + rt.TransformVector(new Vector3(at * step, 0f, 0f));
                var im = c.GetComponentsInChildren<Image>(true).FirstOrDefault(x => x.name == "Icon");
                if (im) { im.sprite = icon; im.enabled = icon; im.color = Color.white; }
                var text = c.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
                if (text)
                {
                    text.gameObject.SetActive(true);
                    text.text = label;
                    if (!icon)   // (the X: big, in the middle)
                    {
                        var tr = text.rectTransform;
                        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = tr.offsetMax = Vector2.zero;
                        text.alignment = TextAlignmentOptions.Center;
                        text.enableAutoSizing = true; text.fontSizeMin = 10f; text.fontSizeMax = 40f;
                    }
                }
                stripCells.Add((RectTransform)c.transform);
            }
        }

        static int openFor = -1;

        /// <summary>Each frame: a click anywhere but the row closes it; so does the panel going away.</summary>
        public static void Tick()
        {
            if (!strip) return;
            if (!column || !column.activeInHierarchy) { Close(); return; }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !(mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return;
            var canvas = rt.GetComponentInParent<Canvas>()?.rootCanvas;
            var cam = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            var at = mouse.position.ReadValue();
            if (stripCells.Concat(slots.Select(s => s.box)).Any(r => r && RectTransformUtility.RectangleContainsScreenPoint(r, at, cam))) return;
            Close();
        }

        /// <summary>Its unlocked active powers' nodes (rank: the points in them), on its own tree.</summary>
        internal static List<SkillNode> LearnedNodes()
        {
            var cls = RealPlayer.Current ? RealPlayer.Current.characterStats.equippedClass : null;
            if (!cls || !cls.ClassSkillTree) return new List<SkillNode>();
            return cls.ClassSkillTree.skillNodes.Where(n => n && n.skill && n.CurrentPoints > 0 &&
                n.skill.activationType != Skill.ActivationType.Passive && !n.skill.IsImproved).ToList();
        }

        internal static void Set(int slot, Skill skill)
        {
            var data = CompanionSave.Data;
            var p = RealPlayer.Current;
            if (data == null || !p) return;
            data.equippedSkills ??= new List<IntNamed>();
            data.equippedSkills.RemoveAll(e => e.Value == slot || skill && e.Key == skill.ID);   // (a skill in one slot only)
            if (skill) data.equippedSkills.Add(new IntNamed { Key = skill.ID, Value = slot });
            CompanionSave.Write();
            try { RealSkills.Load(p); } catch (System.Exception e) { Log.Warn($"Companion skills: {e.Message}"); }
            Refresh();
            Hooks.Say(skill ? $"{data.playerName} will use {NameOf(skill)} (power {slot + 1})." : $"{data.playerName}'s power {slot + 1} is empty.");
        }

        public static void Close()
        {
            if (menu) Object.Destroy(menu is Component comp ? comp.gameObject : menu);
            menu = null;
            if (strip) Object.Destroy(strip);
            strip = null;
            stripCells.Clear();
            openFor = -1;
        }

        static string NameOf(Skill s)
        {
            try { var n = s.Name.GetLocalizedString(); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return s.name;
        }
    }
}

/// <summary>A power slot (or an option in its row): left or right click.</summary>
internal sealed class PowerClick : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
{
    public System.Action On;

    public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e)
    {
        if (e.button == UnityEngine.EventSystems.PointerEventData.InputButton.Middle) return;
        try { On?.Invoke(); } catch (System.Exception x) { Log.Warn($"Companion powers: {x}"); }
    }
}

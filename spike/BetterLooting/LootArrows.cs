using System.Collections.Generic;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace BetterLooting;

/// <summary>
/// A green up arrow, bobbing over a weapon on the ground when it hits harder than the one in your hand: the same
/// numbers the inventory tooltip compares (CombatSystem.GetBaseDamage for you: min-max damage, here their average),
/// against what you hold in that weapon's slot. Nothing in that slot: any weapon is an upgrade. Checked twice a second.
/// </summary>
public static class LootArrows
{
    static ConfigEntry<bool> on;
    static readonly Dictionary<ItemDropObject, GameObject> arrows = new Dictionary<ItemDropObject, GameObject>();
    static Sprite sprite;
    static float next;

    public static void Bind(ConfigFile config) =>
        on = config.Bind("Loot", "UpgradeArrows", true, "A green arrow over items on the ground that are better than what you have on: weapons that hit harder, armour and gear with better stats");

    public static void DrawMenu()
    {
        ImGui.SeparatorText("Green arrows");
        bool v = on.Value;
        if (ImGui.Checkbox("A green arrow over items on the ground that are better than what you wear", ref v)) on.Value = v;
    }

    public static void Tick()
    {
        if (Time.unscaledTime < next) { Bob(); return; }
        next = Time.unscaledTime + 0.5f;
        var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
        var seen = new HashSet<ItemDropObject>();
        if (on.Value && me)
            foreach (var drop in Object.FindObjectsByType<ItemDropObject>(FindObjectsSortMode.None))
            {
                if (!drop || !drop.gameObject.activeInHierarchy) continue;
                seen.Add(drop);
                Show(drop, Better(drop.itemObj, me));
            }
        // gone, picked up, or the setting's off: no arrow
        var stale = new List<ItemDropObject>();
        foreach (var pair in arrows)
            if (!pair.Key || !seen.Contains(pair.Key)) { if (pair.Value) pair.Value.SetActive(false); if (!pair.Key || !pair.Value) stale.Add(pair.Key); }
        foreach (var k in stale) arrows.Remove(k);
        Bob();
    }

    static bool Better(ItemObject item, PlayerMaster me)
    {
        try
        {
            switch (item?.data)
            {
                case CollectableWeapon w when w.weapon != null:
                {
                    var mine = CombatSystem.GetBaseDamage(item, me);
                    if (!me.characterStats.GetItem(w.weapon.gearSlot, out var held) || held == null || held.data is not CollectableWeapon) return true;
                    var theirs = CombatSystem.GetBaseDamage(held, me);
                    return mine.x + mine.y > theirs.x + theirs.y;
                }
                case CollectableGear g when g.gear != null:   // armour (CollectableArmor is one), cloaks, trinkets, off-hands
                {
                    // against what's worn in that slot; a two-slot kind (trinkets) against the weaker of the two, or a free slot
                    var worn = new List<ItemObject>();
                    foreach (var x in me.characterStats.equippedItems)
                        if (x?.data is CollectableGear eg && eg.gear != null && (eg.gear.gearSlot & g.gear.gearSlot) != 0 && x != item) worn.Add(x);
                    int slots = CountBits((int)g.gear.gearSlot);
                    if (worn.Count < slots) return Stats(item).Count > 0;   // a free slot: anything with stats helps
                    foreach (var x in worn) if (Compare(Stats(item), Stats(x)) > 0.05f) return true;
                    return false;
                }
                default: return false;
            }
        }
        catch { return false; }
    }

    static int CountBits(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return Mathf.Max(1, n); }

    /// <summary>An item's rolled stats (what the tooltip lists): stat + kind of modifier -> total.</summary>
    static Dictionary<(SecondaryStat, ModifierType), float> Stats(ItemObject item)
    {
        var d = new Dictionary<(SecondaryStat, ModifierType), float>();
        var mods = item.randomValues?.modifiers;
        if (mods != null)
            foreach (var m in mods)
            {
                if (m?.modifier == null) continue;
                var k = (m.stat, m.modifier.type);
                d[k] = (d.TryGetValue(k, out float v) ? v : 0f) + m.modifier.Value;
            }
        return d;
    }

    /// <summary>Stat by stat, how much better (relative change, added up): positive = the new one is better overall.</summary>
    static float Compare(Dictionary<(SecondaryStat, ModifierType), float> a, Dictionary<(SecondaryStat, ModifierType), float> b)
    {
        float score = 0f;
        var keys = new HashSet<(SecondaryStat, ModifierType)>(a.Keys);
        keys.UnionWith(b.Keys);
        foreach (var k in keys)
        {
            a.TryGetValue(k, out float x);
            b.TryGetValue(k, out float y);
            score += (x - y) / Mathf.Max(Mathf.Abs(x), Mathf.Abs(y), 0.001f);
        }
        return score;
    }

    static void Show(ItemDropObject drop, bool better)
    {
        arrows.TryGetValue(drop, out var arrow);
        if (!better) { if (arrow) arrow.SetActive(false); return; }
        if (!arrow)
        {
            arrow = new GameObject("EG_UpgradeArrow");
            arrow.transform.SetParent(drop.transform, false);
            var sr = arrow.AddComponent<SpriteRenderer>();
            sr.sprite = Arrow();
            sr.color = new Color(0.35f, 1f, 0.35f, 1f);
            var icon = drop.itemIcon;
            if (icon) { sr.sortingLayerID = icon.sortingLayerID; sr.sortingOrder = icon.sortingOrder + 20; }
            arrows[drop] = arrow;
        }
        arrow.SetActive(true);
    }

    static void Bob()
    {
        float y = 0.85f + Mathf.Sin(Time.time * 4f) * 0.08f;
        foreach (var a in arrows.Values) if (a && a.activeSelf) a.transform.localPosition = new Vector3(0f, y, 0f);
    }

    /// <summary>A chunky pixel up-arrow with a dark outline, made once.</summary>
    static Sprite Arrow()
    {
        if (sprite) return sprite;
        string[] rows =
        {
            "....##....",
            "...#00#...",
            "..#0000#..",
            ".#000000#.",
            "#00000000#",
            "###0000###",
            "..#0000#..",
            "..#0000#..",
            "..#0000#..",
            "..######..",
        };
        int w = rows[0].Length, h = rows.Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                char c = rows[h - 1 - y][x];
                tex.SetPixel(x, y, c == '0' ? Color.white : c == '#' ? new Color(0.05f, 0.25f, 0.05f, 1f) : Color.clear);
            }
        tex.Apply();
        sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 24f);
        return sprite;
    }
}

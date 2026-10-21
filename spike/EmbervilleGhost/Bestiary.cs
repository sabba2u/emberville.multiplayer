using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.InputSystem;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The Bestiary (N): every creature the game has, as a grid. One you've never killed is a black silhouette with ???;
/// your first kill fills it in: the creature playing its idle animation, its name (the game's own: Aco_Tracker...)
/// above, its base stats beside it (its stats template: CharacterStats.baseClass), and how many you've killed.
/// Kills are the ones credited to you (your hits, your summons', your hook's), counted per character in
/// "&lt;character&gt;.egbestiary" next to the save.
/// The moving picture (with Better Pets and Summons installed): its creature stand-in (PetBody) standing far off the map,
/// its current sprites drawn into the page every frame; without it, the creature as its prefab is built, still.
/// </summary>
[HarmonyPatch]
internal static class Bestiary
{
    static ConfigEntry<Key> keyEntry;
    public static bool Visible { get; private set; }
    static List<(InputActionMap map, bool on)> savedMaps;

    public static void Bind(ConfigFile config)
    {
        keyEntry = config.Bind("Bestiary", "Key", Key.N, "Opens the Bestiary (every creature; filled in as you kill them)");
        if (keyEntry.Value == Key.B) keyEntry.Value = Key.N;   // (it was B: B is build mode now)
    }

    // ---- the creatures ----

    sealed class Entry
    {
        public string Key;
        public Enemy Prefab;
        public List<Part> Parts;   // its own look, from its prefab (the grid's picture)
    }

    static List<Entry> roster;

    /// <summary>"Mob_Glowbat_02(Clone)" / "Chicken_01 (3)" -> "Mob_Glowbat" / "Chicken": one entry per kind.</summary>
    internal static string KeyOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        name = name.Replace("(Clone)", "").Trim();
        int sp = name.LastIndexOf(" (");
        if (sp > 0 && name.EndsWith(")")) name = name.Substring(0, sp);
        int u = name.LastIndexOf('_');
        if (u > 0 && int.TryParse(name.Substring(u + 1), out _)) name = name.Substring(0, u);
        return name;
    }

    static List<Entry> Roster()
    {
        if (roster != null && roster.All(e => e.Prefab)) return roster;
        try { Dungeon.AllSettings(); } catch { }   // the biomes bring their creatures in
        var byKey = new Dictionary<string, Entry>();
        foreach (var e in Resources.FindObjectsOfTypeAll<Enemy>())
        {
            if (!e || e.gameObject.scene.IsValid()) continue;   // prefabs only
            string n = e.gameObject.name;
            if (n.StartsWith("NPC_") || n.StartsWith("EmbervilleGhost") || n.StartsWith("EG_")) continue;
            string key = KeyOf(n);
            if (byKey.TryGetValue(key, out var have) && have.Prefab.gameObject.name.Length <= n.Length) continue;   // the plainest name stands for the kind
            byKey[key] = new Entry { Key = key, Prefab = e };
        }
        roster = byKey.Values.OrderBy(x => x.Prefab.enemyType == EnemyType.None ? 1 : 0).ThenBy(x => x.Key).ToList();
        return roster;
    }

    // ---- kills (per character) ----

    static readonly Dictionary<string, int> kills = new Dictionary<string, int>();
    static string loadedFor;
    static readonly HashSet<Enemy> counted = new HashSet<Enemy>();

    static string FileFor(string prefix) => SaveLoad.GetPath(prefix + ".egbestiary");

    static void Load()
    {
        string prefix = GameData.HasPlayerData ? GameData.playerFilePrefix : null;
        if (prefix == loadedFor) return;
        loadedFor = prefix;
        kills.Clear();
        if (prefix == null) return;
        try
        {
            string path = FileFor(prefix);
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && int.TryParse(line.Substring(tab + 1), out int n)) kills[line.Substring(0, tab)] = n;
            }
        }
        catch (Exception e) { NetLog.Warn($"Bestiary: {e.Message}", toast: false); }
    }

    static void Save()
    {
        if (loadedFor == null) return;
        try { File.WriteAllLines(FileFor(loadedFor), kills.Select(kv => kv.Key + "\t" + kv.Value)); }
        catch (Exception e) { NetLog.Warn($"Bestiary: {e.Message}", toast: false); }
    }

    static int KillsOf(string key) { Load(); return kills.TryGetValue(key, out int n) ? n : 0; }

    /// <summary>A hit that kills, credited to our player (our weapon, our summons, our hook: all hit as us).</summary>
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyPostfix]
    static void AfterHit(Enemy __instance, DamageData damageData)
    {
        if (!__instance) return;
        if (!__instance.IsDead) { counted.Remove(__instance); return; }   // (a pooled one back to life counts again)
        var me = LocalPlayer.Get();
        if (!me || !ReferenceEquals(damageData.Aggressor, me) || !counted.Add(__instance)) return;
        if (__instance.name.StartsWith("NPC_")) return;
        Load();
        string key = KeyOf(__instance.gameObject.name);
        kills.TryGetValue(key, out int n);
        kills[key] = n + 1;
        Save();
        if (n == 0) NetLog.Success($"New in the Bestiary: {key} ({keyEntry.Value})", toast: true);
    }

    // ---- open / close ----

    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || !Application.isFocused || Chat.IsOpen || ImGuiUnity.Typing) return;
        // (another mod's text box has the keyboard, or a game screen is up: the game's controls are off then; "b" is a letter)
        if (!Visible && !MainInput.Actions.Player.enabled) return;
        if (kb[keyEntry.Value].wasPressedThisFrame && (Visible || (!PlayerStatusUtil.LocalBusy && GameData.HasPlayerData))) SetVisible(!Visible);
        else if (Visible && kb.escapeKey.wasPressedThisFrame) SetVisible(false);
    }

    static void SetVisible(bool on)
    {
        if (on == Visible) return;
        Visible = on;
        if (on)
        {
            Load();
            savedMaps = new List<(InputActionMap, bool)>();
            foreach (var map in MainInput.Actions.asset.actionMaps) { savedMaps.Add((map, map.enabled)); map.Disable(); }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            if (savedMaps != null) foreach (var (map, was) in savedMaps) if (was) map.Enable();
            savedMaps = null;
            if (Integrations.HasPets) ClearStage();
        }
    }

    // ---- the window ----

    static string selected;

    public static void Draw()
    {
        if (!Visible) return;
        var list = Roster();
        if (selected == null || list.All(e => e.Key != selected)) selected = list.FirstOrDefault()?.Key;
        int found = list.Count(e => KillsOf(e.Key) > 0);

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x * 0.5f, vp.WorkPos.y + vp.WorkSize.y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(820, 520), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.Begin($"Bestiary   ({found} of {list.Count} found)###bestiary", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            if (!open) SetVisible(false);
            return;
        }

        // left: the grid
        ImGui.BeginChild("grid", new Vector2(430, 0), ImGuiChildFlags.Border);
        const float cell = 76f;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().x + 6) / (cell + 6)));
        int i = 0;
        foreach (var e in list)
        {
            bool known = KillsOf(e.Key) > 0;
            if (i % perRow != 0) ImGui.SameLine();
            i++;
            ImGui.PushID(e.Key);
            var at = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("##cell", selected == e.Key, ImGuiSelectableFlags.None, new Vector2(cell, cell))) selected = e.Key;
            var dl = ImGui.GetWindowDrawList();
            uint tint = known ? 0xFFFFFFFF : ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));   // unknown: a black shape
            e.Parts ??= PartsOf(e.Prefab);
            if (e.Parts.Count > 0) DrawParts(dl, e.Parts, new Vector2(at.x + 6, at.y + 4), new Vector2(at.x + cell - 6, at.y + cell - 20), known);
            else dl.AddText(new Vector2(at.x + cell / 2 - 6, at.y + cell / 2 - 16), ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "?");
            string label = known ? e.Key : "???";
            var size = ImGui.CalcTextSize(label);
            if (size.x > cell - 4) { label = label.Substring(0, Math.Max(3, (int)(label.Length * (cell - 10) / size.x))) + ".."; size = ImGui.CalcTextSize(label); }
            dl.AddText(new Vector2(at.x + (cell - size.x) / 2, at.y + cell - 18), ImGui.ColorConvertFloat4ToU32(known ? new Vector4(1f, 0.88f, 0.7f, 1f) : new Vector4(0.55f, 0.55f, 0.55f, 1f)), label);
            ImGui.PopID();
        }
        ImGui.EndChild();

        // right: the one picked
        ImGui.SameLine();
        ImGui.BeginChild("page", new Vector2(0, 0), ImGuiChildFlags.Border);
        var sel = list.FirstOrDefault(e => e.Key == selected);
        if (sel != null) Page(sel);
        ImGui.EndChild();

        ImGui.End();
        if (!open) SetVisible(false);
    }

    static void Page(Entry e)
    {
        int n = KillsOf(e.Key);
        bool known = n > 0;
        string title = known ? e.Key : "???";
        ImGui.SetWindowFontScale(1.4f);
        float tw = ImGui.CalcTextSize(title).x;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetContentRegionAvail().x - tw) / 2));
        ImGui.TextColored(known ? new Vector4(1f, 0.8f, 0.45f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f), title);
        ImGui.SetWindowFontScale(1f);
        ImGui.Separator();

        // the picture: the creature itself, idling (a silhouette until you've killed one)
        const float pic = 170f;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetContentRegionAvail().x - pic) / 2));
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(pic, pic));
        var dlp = ImGui.GetWindowDrawList();
        dlp.AddRectFilled(at, new Vector2(at.x + pic, at.y + pic), ImGui.ColorConvertFloat4ToU32(new Vector4(0.17f, 0.13f, 0.2f, 1f)), 6f);   // the inventory preview's dark purple
        if (Integrations.HasPets) LiveStage(e.Prefab, known, at, new Vector2(at.x + pic, at.y + pic));   // idling (the pet engine)
        else
        {
            e.Parts ??= PartsOf(e.Prefab);   // still: as its prefab is built
            if (e.Parts.Count > 0) DrawParts(dlp, e.Parts, new Vector2(at.x + 16, at.y + 16), new Vector2(at.x + pic - 16, at.y + pic - 16), known);
        }

        ImGui.Separator();
        if (!known)
        {
            ImGui.TextDisabled("Kill one to learn about it.");
            foreach (var label in new[] { "Health", "Attack", "Attack rate", "Defense", "Move speed", "Experience" })
                ImGui.TextDisabled($"{label,-14} ???");
            return;
        }
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f), $"Killed: {n}");
        Stats(e.Prefab);
    }

    static void Stats(Enemy prefab)
    {
        CharacterStats cs = null;
        try { cs = prefab.GetComponentInChildren<CharacterStats>(true); } catch { }
        var bc = cs ? cs.baseClass : null;
        if (bc == null) { ImGui.TextDisabled("(no stats on this one)"); return; }
        float Sec(SecondaryStat s) { foreach (var b in bc.secondaryStats) if (b != null && b.stat.Equals(s)) return b.baseValue; return 0f; }
        void Row(string label, string value) { ImGui.Text(label); ImGui.SameLine(130); ImGui.TextColored(new Vector4(1f, 0.93f, 0.82f, 1f), value); }

        Row("Health", $"{Sec(SecondaryStat.HealthPoints):0}");
        Row("Attack", $"{Sec(SecondaryStat.AttackDamage):0.#}");
        Row("Attack rate", $"{Sec(SecondaryStat.AttackRate):0.##}");
        Row("Defense", $"{Sec(SecondaryStat.Defense):0.#}");
        Row("Move speed", $"{Sec(SecondaryStat.MoveSpeed):0.##}");
        float crit = Sec(SecondaryStat.CriticalChance), block = Sec(SecondaryStat.BlockChance), parry = Sec(SecondaryStat.ParryChance);
        if (crit > 0) Row("Critical", $"{crit:0.#}%");
        if (block > 0) Row("Block", $"{block:0.#}%");
        if (parry > 0) Row("Parry", $"{parry:0.#}%");
        Row("Experience", prefab.expRange.x == prefab.expRange.y ? $"{prefab.expRange.x}" : $"{prefab.expRange.x} - {prefab.expRange.y}");

        var prim = bc.primaryStats?.Where(b => b != null && b.baseValue != 0f).Select(b => $"{b.stat.ToString().Substring(0, 3)} {b.baseValue:0}").ToList();
        if (prim != null && prim.Count > 0) { ImGui.Spacing(); ImGui.TextDisabled(string.Join("   ", prim)); }

        var moves = new List<string>();
        try
        {
            if (prefab.attacks != null) foreach (var a in prefab.attacks) if (a.Attack) moves.Add(a.Attack.name);
            if (prefab.skills != null) foreach (var s in prefab.skills) if (s) moves.Add(s.name);
        }
        catch { }
        if (moves.Count > 0) { ImGui.Spacing(); ImGui.TextWrapped("Attacks: " + string.Join(", ", moves.Distinct())); }
    }

    // ---- the grid's pictures: each creature as its prefab is built (its parts' sprites where they sit) ----
    // (the creatures' own "icon" is mostly one shared placeholder, so it's not used)

    internal struct Part { public UnityEngine.Sprite Sprite; public UnityEngine.Rect Box; public bool Flip; public Color Colour; }

    static List<Part> PartsOf(Enemy prefab)
    {
        var parts = new List<Part>();
        if (!prefab) return parts;
        int minimap = LayerMask.NameToLayer("Minimap");
        var rootInv = prefab.transform.worldToLocalMatrix;
        var renderers = prefab.GetComponentsInChildren<SpriteRenderer>(true)
            .Where(r => r && r.sprite && r.enabled && r.gameObject.layer != minimap && ActiveUnder(r.transform, prefab.transform) && r.color.a > 0.05f
                        && r != prefab.attackHitboxRenderer)
            .OrderBy(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).ThenBy(r => r.sortingOrder);
        foreach (var r in renderers)
        {
            var m = rootInv * r.transform.localToWorldMatrix;   // the part relative to the creature
            var b = r.sprite.bounds;
            var a = m.MultiplyPoint3x4(b.min); var c = m.MultiplyPoint3x4(b.max);
            parts.Add(new Part
            {
                Sprite = r.sprite,
                Box = UnityEngine.Rect.MinMaxRect(Mathf.Min(a.x, c.x), Mathf.Min(a.y, c.y), Mathf.Max(a.x, c.x), Mathf.Max(a.y, c.y)),
                Flip = r.flipX ^ (m.m00 < 0f),
                Colour = r.color,
            });
        }
        return parts;
    }

    static bool ActiveUnder(Transform t, Transform root)
    {
        for (; t && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
        return true;
    }

    /// <summary>The parts into a box, keeping their shape (black when the creature isn't known yet).</summary>
    static void DrawParts(ImDrawListPtr dl, List<Part> parts, Vector2 min, Vector2 max, bool known)
    {
        var all = parts[0].Box;
        foreach (var p in parts) all = UnityEngine.Rect.MinMaxRect(Mathf.Min(all.xMin, p.Box.xMin), Mathf.Min(all.yMin, p.Box.yMin), Mathf.Max(all.xMax, p.Box.xMax), Mathf.Max(all.yMax, p.Box.yMax));
        float bw = max.x - min.x, bh = max.y - min.y;
        float scale = Mathf.Min(bw / Mathf.Max(0.01f, all.width), bh / Mathf.Max(0.01f, all.height));
        var centre = new Vector2((min.x + max.x) / 2, (min.y + max.y) / 2);
        foreach (var p in parts)
        {
            var a = new Vector2(centre.x + (p.Box.xMin - all.center.x) * scale, centre.y - (p.Box.yMax - all.center.y) * scale);
            var c = new Vector2(centre.x + (p.Box.xMax - all.center.x) * scale, centre.y - (p.Box.yMin - all.center.y) * scale);
            uint tint = known ? ImGui.ColorConvertFloat4ToU32(new Vector4(p.Colour.r, p.Colour.g, p.Colour.b, p.Colour.a))
                              : ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, p.Colour.a));
            Sprite(dl, p.Sprite, a, c, tint, p.Flip);
        }
    }

    /// <summary>A sprite (its part of its atlas) stretched over the box, mirrored if it's flipped.</summary>
    internal static void Sprite(ImDrawListPtr dl, UnityEngine.Sprite sprite, Vector2 a, Vector2 c, uint tint, bool flip)
    {
        if (!sprite.texture) return;
        UnityEngine.Rect r;
        try { r = sprite.textureRect; } catch { r = sprite.rect; }
        float w = sprite.texture.width, h = sprite.texture.height;
        float u0 = r.xMin / w, u1 = r.xMax / w;
        if (flip) (u0, u1) = (u1, u0);
        dl.AddImage(ImGuiUnity.TextureId(sprite.texture), a, c, new Vector2(u0, r.yMax / h), new Vector2(u1, r.yMin / h), tint);   // Unity's v is bottom-up
    }

    // (their own methods: the stage's types come from Better Pets and Summons, only touched when it's installed)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void LiveStage(Enemy prefab, bool known, Vector2 min, Vector2 max) => Stage.Draw(prefab, known, min, max);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void ClearStage() => Stage.Clear();

    // ---- the stage: the creature far off the map, drawn from its own sprites ----
    //
    // Our pet stand-in of the creature stands far off the map, its animators playing (told to keep going though no
    // camera sees it); every frame we draw whatever sprites its renderers show right then, each where it sits, into
    // the page (no camera, no render texture: the party portraits' camera never worked out).

    static class Stage
    {
        static readonly Vector3 Spot = new Vector3(-9000f, -9000f, 0f);
        static BetterPetsAndSummons.PetBody body;
        static Enemy showing;

        public static void Draw(Enemy prefab, bool known, Vector2 min, Vector2 max)
        {
            if (!prefab) return;
            if (showing != prefab || body == null || !body.Alive)
            {
                body?.Destroy();
                body = null;
                showing = prefab;
                string name = KeyOf(prefab.gameObject.name);
                var kind = new BetterPetsAndSummons.PetKind("bestiary_" + name, name, prefab.gameObject.name, name);
                if (BetterPetsAndSummons.PetBody.Available(kind) || BetterPetsAndSummons.PetBody.Learn(kind)) body = BetterPetsAndSummons.PetBody.Create(kind, Spot);
                if (body != null)
                {
                    body.Stop(Spot + Vector3.right);   // idle, facing right
                    foreach (var a in body.Root.GetComponentsInChildren<Animator>(true)) if (a) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
            }
            var dl = ImGui.GetWindowDrawList();
            if (body == null || !body.Root) { dl.AddText(new Vector2((min.x + max.x) / 2 - 4, (min.y + max.y) / 2 - 8), 0xFF888888, "?"); return; }
            body.Hover();

            var rs = body.Root.GetComponentsInChildren<SpriteRenderer>()
                .Where(r => r && r.enabled && r.sprite && r.gameObject.activeInHierarchy && r.gameObject.layer != LayerMask.NameToLayer("Minimap") && r.color.a > 0.05f)
                .OrderBy(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).ThenBy(r => r.sortingOrder).ToList();
            if (rs.Count == 0) return;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            // world -> the box, keeping its shape, a margin round it
            float bw = max.x - min.x, bh = max.y - min.y;
            float scale = Mathf.Min(bw / Mathf.Max(0.01f, b.size.x), bh / Mathf.Max(0.01f, b.size.y)) * 0.82f;
            var centre = new Vector2((min.x + max.x) / 2, (min.y + max.y) / 2);
            foreach (var r in rs)
            {
                var rb = r.bounds;
                var a = new Vector2(centre.x + (rb.min.x - b.center.x) * scale, centre.y - (rb.max.y - b.center.y) * scale);
                var c = new Vector2(centre.x + (rb.max.x - b.center.x) * scale, centre.y - (rb.min.y - b.center.y) * scale);
                var col = r.color;
                uint tint = known ? ImGui.ColorConvertFloat4ToU32(new Vector4(col.r, col.g, col.b, col.a))
                                  : ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, col.a));   // unknown: a black shape
                bool flip = r.flipX ^ (r.transform.lossyScale.x < 0f);
                Sprite(dl, r.sprite, a, c, tint, flip);
            }
        }

        public static void Clear()
        {
            body?.Destroy();
            body = null;
            showing = null;
        }
    }
}

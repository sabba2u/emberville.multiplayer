using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ImGuiNET;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>
/// The descent (Torchlight / Diablo style): generated floors that keep going down. Each floor is a generated dungeon
/// (Dungeon.cs) picked from the descent's seed and the floor number, so everyone in co-op builds the same one. You
/// arrive on a blue pad (back to town) and the stairs down (an ember pad) are in the farthest reachable spot of the
/// floor. The dungeon type, so the biome, changes every 3 floors; every floor down the creatures get more health, hit
/// harder and come in bigger groups. A pad in the world (put it where you like from the Insert menu) starts it.
/// </summary>
internal static class Delve
{
    static ConfigEntry<int> seed, deepest;
    static ConfigEntry<string> padScene;
    static ConfigEntry<float> padX, padY, healthGrowth, damageGrowth;
    static ConfigEntry<int> floors, maxEnemies, bossEvery;
    const int FloorsPerBiome = 3;

    public static void Bind(ConfigFile config)
    {
        seed = config.Bind("Descent", "Seed", 0, "The descent's seed (0 = pick one the first time). Same seed, same floors");
        deepest = config.Bind("Descent", "Deepest", 0, "The deepest floor reached with this seed");
        // by default: outside the starting area's door (TutorialArea), where Rory waits
        padScene = config.Bind("Descent", "PadScene", "TutorialArea", "The scene the descent's pads (and Rory) are in (empty = none; move them from the Insert menu)");
        padX = config.Bind("Descent", "PadX", -52.48f, "Where the descent's pad is");
        padY = config.Bind("Descent", "PadY", 66.57f, "Where the descent's pad is");
        healthGrowth = config.Bind("Descent", "HealthGrowth", 1.25f, "Creature health multiplier per floor down, compounding (1.25 = each floor 125% of the one above; 2 = 200%, doubling)");
        damageGrowth = config.Bind("Descent", "DamageGrowth", 1.15f, "Creature damage multiplier per floor down, compounding (1.15 = each floor 115% of the one above)");
        floors = config.Bind("Descent", "Floors", 10, "How many floors the descent has (0 = it never ends)");
        maxEnemies = config.Bind("Descent", "MaxEnemies", 60, "The most creatures on one floor (0 = no limit)");
        bossEvery = config.Bind("Descent", "BossEvery", 5, "A mini-boss guards the stairs every this many floors (0 = none)");
        SceneManager.sceneLoaded += (_, _) => { pads.Clear(); scaled.Clear(); worldPadChecked = false; MiniBoss.Clear(); };
    }

    // ---- how hard a floor is ----

    public static float HealthMul(int floor) => floor <= 1 ? 1f : Mathf.Min(1e6f, Mathf.Pow(Mathf.Max(1f, healthGrowth.Value), floor - 1));
    public static float DamageMul(int floor) => floor <= 1 ? 1f : Mathf.Min(1e6f, Mathf.Pow(Mathf.Max(1f, damageGrowth.Value), floor - 1));
    /// <summary>The most creatures on a floor (0 = no limit); generated dungeons outside the descent use it too.</summary>
    public static int MaxEnemies => maxEnemies.Value;
    public static int Floors => floors.Value;
    public static double ExtraChance(int floor) => floor <= 1 ? 0 : Mathf.Min(0.6f, 0.08f * (floor - 1));
    public static int GroupsPerArea(int floor) => floor <= 0 ? 1 : 1 + (floor - 1) / 4;

    /// <summary>A floor: the dungeon type from the seed and the floor's "biome tier"; Where.Seed is the descent's seed
    /// (the generator gets SeedOf), so whoever follows can work out the floors below too.</summary>
    public static Dungeon.Where FloorWhere(int baseSeed, int floor)
    {
        // the sub-dungeons (caves...): one region each, quick to make; the world map's own (every region at once) only if
        // there's nothing else
        var all = Dungeon.AllSettings();
        var pool = all.Where(x => x != Dungeon.MainSettings()).ToList();
        if (pool.Count == 0) pool = all;
        int tier = (floor - 1) / FloorsPerBiome;
        uint h = Hash(baseSeed, 1000 + tier);
        return new Dungeon.Where(pool[(int)(h % (uint)pool.Count)].name, baseSeed, -1, floor);
    }

    /// <summary>The generator's seed for a dungeon (a floor's own, from the descent's seed and the floor).</summary>
    public static int SeedOf(Dungeon.Where w) => w.Floor > 0 ? (int)Hash(w.Seed, w.Floor) : w.Seed;

    static uint Hash(int a, int b)
    {
        unchecked
        {
            uint h = 2166136261;
            h = (h ^ (uint)a) * 16777619; h ^= h >> 13;
            h = (h ^ (uint)b) * 16777619; h ^= h >> 15;
            return h * 2246822519;
        }
    }

    // ---- going down ----

    static int BaseSeed()
    {
        if (seed.Value == 0) seed.Value = new System.Random().Next(1, int.MaxValue);
        return seed.Value;
    }

    /// <summary>A new descent: the setup box first (seed, floors, how much harder each floor, creature limit).</summary>
    public static void Start(int floor)
    {
        if (floor <= 1) { OpenSetup(); return; }
        Begin(floor);
    }

    static void Begin(int floor)
    {
        var w = FloorWhere(BaseSeed(), Mathf.Max(1, floor));
        Dungeon.Enter(w.Settings, w.Seed, w.Region, true, w.Floor);
    }

    static void Down()
    {
        var c = Dungeon.Current;
        if (!c.HasValue || c.Value.Floor <= 0 || Dungeon.Busy) return;
        int next = c.Value.Floor + 1;
        if (floors.Value > 0 && next > floors.Value) { Finished(); return; }
        if (c.Value.Seed == seed.Value && next > deepest.Value) deepest.Value = next;
        NetLog.Info($"Down to floor {next}...", toast: true);
        Dungeon.GoTo(FloorWhere(c.Value.Seed, next));
    }

    static void Finished()
    {
        NetLog.Success($"The descent is complete: all {floors.Value} floors! Back to town", toast: true);
        Dungeon.Leave();
    }

    /// <summary>Following someone down: take on their descent (its seed is where the floors come from) and its rules.</summary>
    public static void Follow(Dungeon.Where w)
    {
        if (w.Seed != seed.Value) { seed.Value = w.Seed; deepest.Value = w.Floor; }
        if (theirRules.HasValue)
        {
            var r = theirRules.Value;
            floors.Value = r.floors; healthGrowth.Value = r.health; damageGrowth.Value = r.damage; maxEnemies.Value = r.max;
        }
    }

    // ---- the rules travel with the dungeon message (Dungeon.Announce / OnOpen) ----

    static (int floors, float health, float damage, int max)? theirRules;

    public static void WriteRules(LiteNetLib.Utils.NetDataWriter w)
    {
        w.Put(floors.Value); w.Put(healthGrowth.Value); w.Put(damageGrowth.Value); w.Put(maxEnemies.Value);
    }

    public static void ReadRules(LiteNetLib.Utils.NetDataReader r)
    {
        if (r.AvailableBytes < 16) { theirRules = null; return; }
        theirRules = (r.GetInt(), r.GetFloat(), r.GetFloat(), r.GetInt());
    }

    public static void Left() { scaled.Clear(); }

    // ---- a floor's boss ----

    static Vector3 stairsAt;
    static bool bossFloor;

    public static bool IsBossFloor(int floor) => bossEvery.Value > 0 && floor > 0 && floor % bossEvery.Value == 0;

    /// <summary>After the floor's creatures: on a boss floor, its mini-boss by the stairs (whoever runs the area spawns it).</summary>
    public static void AfterSpawn(Dungeon.Where where, BiomeSettings biome)
    {
        if (!bossFloor || !Dungeon.Root) return;
        if (Plugin != null && Plugin.Connected && !Plugin.IOwnHere) return;
        var look = MiniBoss.ForFloor(SeedOf(where), where.Floor, biome);
        var e = MiniBoss.Spawn(look, stairsAt + new Vector3(0f, 1.2f, 0f), Dungeon.Root.transform);   // goes with the floor
        if (e) NetLog.Warn($"{look.Name} guards the stairs on floor {where.Floor}!", toast: true);
    }

    /// <summary>A boss floor's stairs stay shut while anything's alive right by them (the guardian and its pack).</summary>
    static bool StairsGuarded(Vector3 at)
    {
        if (!bossFloor) return false;
        foreach (var e in EnemiesManager.AllEnemies)
            if (e && !e.IsDead && e.gameObject.activeInHierarchy && ((UnityEngine.Vector2)(e.transform.position - at)).sqrMagnitude < 8f * 8f) return true;
        return false;
    }

    // ---- a floor's arrival and stairs ----

    /// <summary>
    /// After a floor is built: where we arrive (the spot we'd have arrived at, if it's in the floor's main open part,
    /// else the nearest point of it), the stairs down (the farthest walk from there), and the pads. Returns the arrival.
    /// </summary>
    public static Vector3 SetUpFloor(Dungeon.Where where, Vector3 at)
    {
        pads.RemoveAll(p => !p || (p.Kind != PadKind.Entrance && p.Kind != PadKind.Arena));   // the last floor's (the entrance pad stays, back in the world)
        scaled.Clear();
        var cells = Dungeon.floorCells;
        var cs = Dungeon.cellSize;
        if (cells.Count == 0) return at;
        if (where.Seed == seed.Value && where.Floor > deepest.Value) deepest.Value = where.Floor;

        // the floor's biggest connected part (the generator connects its rooms; this skips odd bits behind walls)
        var seen = new HashSet<Vector3Int>();
        List<Vector3Int> biggest = null;
        foreach (var c in cells)
        {
            if (seen.Contains(c)) continue;
            var part = Flood(c, cells, seen);
            if (biggest == null || part.Count > biggest.Count) biggest = part;
        }
        var main = new HashSet<Vector3Int>(biggest);
        var atCell = new Vector3Int(Mathf.FloorToInt(at.x / cs.x), Mathf.FloorToInt(at.y / cs.y), 0);
        var start = main.Contains(atCell) ? atCell : biggest.OrderBy(c => (c - atCell).sqrMagnitude).First();
        // room around a pad: a 5x5 patch if the floor has those, else smaller
        int room = 2;
        while (room > 0 && !biggest.Any(c => Open(c, main, room))) room--;
        if (!Open(start, main, room)) start = biggest.Where(c => Open(c, main, room)).OrderBy(c => (c - start).sqrMagnitude).DefaultIfEmpty(start).First();

        // the stairs: the farthest walk, somewhere with room around it
        var dist = Distances(start, main);
        var stairs = dist.Where(p => Open(p.Key, main, room)).OrderByDescending(p => p.Value).Select(p => p.Key).DefaultIfEmpty(start).First();
        bool last = floors.Value > 0 && where.Floor >= floors.Value;
        bossFloor = IsBossFloor(where.Floor);
        MiniBoss.Clear();

        Vector3 Center(Vector3Int c) => new Vector3((c.x + 0.5f) * cs.x, (c.y + 0.5f) * cs.y, 0f);
        var arrive = Center(start);
        stairsAt = Center(stairs);
        var root = Dungeon.Root.transform;
        MakePad(root, arrive, PadKind.Home, armed: false);
        MakePad(root, Center(stairs), last ? PadKind.Finish : PadKind.Down, armed: true);   // the last floor: the way out instead
        NetLog.Info($"Floor {where.Floor}: stairs are {dist[stairs]} steps away ({cells.Count} floor cells, the main part {biggest.Count}, room {room})", toast: false);
        return arrive;
    }

    static List<Vector3Int> Flood(Vector3Int from, HashSet<Vector3Int> cells, HashSet<Vector3Int> seen)
    {
        var part = new List<Vector3Int>();
        var q = new Queue<Vector3Int>();
        q.Enqueue(from); seen.Add(from);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            part.Add(c);
            foreach (var d in Steps)
            {
                var n = c + d;
                if (cells.Contains(n) && seen.Add(n)) q.Enqueue(n);
            }
        }
        return part;
    }

    internal static Dictionary<Vector3Int, int> Distances(Vector3Int from, HashSet<Vector3Int> cells)
    {
        var dist = new Dictionary<Vector3Int, int> { [from] = 0 };
        var q = new Queue<Vector3Int>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var d in Steps)
            {
                var n = c + d;
                if (cells.Contains(n) && !dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
            }
        }
        return dist;
    }

    static readonly Vector3Int[] Steps = { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down };

    /// <summary>Floor all around (a 5x5 patch by default), so a pad or chest isn't squeezed against a wall.</summary>
    internal static bool Open(Vector3Int c, HashSet<Vector3Int> cells, int r = 2)
    {
        for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
                if (!cells.Contains(new Vector3Int(c.x + x, c.y + y, 0))) return false;
        return true;
    }

    // ---- pads ----

    enum PadKind { Down, Home, Entrance, Finish, Arena }

    sealed class Pad : MonoBehaviour
    {
        public PadKind Kind;
        public bool Armed;
        float stoodFor;
        SpriteRenderer glow;
        const float Radius = 0.9f, Hold = 0.8f;

        void Start() => glow = GetComponentInChildren<SpriteRenderer>();

        void Update()
        {
            var me = LocalPlayer.Get();
            if (!me) return;
            float pulse = 1f + Mathf.Sin(Time.time * 3f) * 0.06f;
            if (glow) glow.transform.localScale = Vector3.one * (Size * pulse * (1f + stoodFor / Hold * 0.35f));
            float away = ((UnityEngine.Vector2)(me.transform.position - transform.position)).magnitude;
            bool on = away < Radius;
            // armed once you've really walked off it (not nudged off by arriving next to a wall)
            if (!on) { if (away > Radius * 3f) Armed = true; stoodFor = 0f; return; }
            if (!Armed || Dungeon.Busy || ImGuiUnity.Typing) return;
            if ((Kind == PadKind.Down || Kind == PadKind.Finish) && StairsGuarded(transform.position)) { stoodFor = 0f; return; }
            stoodFor += Time.deltaTime;
            if (stoodFor < (Kind == PadKind.Home ? Hold * 2f : Hold)) return;   // home: a longer stand, no accidents
            Armed = false;
            stoodFor = 0f;
            PadFx.Burst(gameObject);
            switch (Kind)
            {
                case PadKind.Down: Down(); break;
                case PadKind.Home: Dungeon.Leave(); break;
                case PadKind.Finish: Finished(); break;
                case PadKind.Arena: Arena.Enter(); break;
                case PadKind.Entrance: Delve.Start(1); break;   // "Continue" is in the Insert menu
            }
        }
    }

    const float Size = 2.2f;
    static readonly List<Pad> pads = new List<Pad>();
    static Sprite ringSprite;

    static void MakePad(Transform parent, Vector3 at, PadKind kind, bool armed)
    {
        var go = new GameObject($"EG_DelvePad_{kind}");
        if (parent) go.transform.SetParent(parent, true);
        go.transform.position = at;
        var art = new GameObject("Glow");
        art.transform.SetParent(go.transform, false);
        var sr = art.AddComponent<SpriteRenderer>();
        sr.sprite = Ring();
        sr.color = kind == PadKind.Home ? new Color(0.45f, 0.75f, 1f, 0.9f) : kind == PadKind.Arena ? new Color(0.85f, 0.4f, 1f, 0.95f) : kind == PadKind.Finish ? new Color(1f, 0.9f, 0.35f, 1f) : new Color(1f, 0.55f, 0.15f, 0.95f);
        var playerSprite = LocalPlayer.Get() ? LocalPlayer.Get().GetComponentInChildren<SpriteRenderer>() : null;
        if (playerSprite) { sr.sortingLayerID = playerSprite.sortingLayerID; sr.sortingOrder = -500; }
        art.transform.localScale = Vector3.one * Size;
        PadFx.Add(go, Ring(), sr.color, sr.sortingLayerID);   // turning inner ring, rising motes, a glow on the ground
        var pad = go.AddComponent<Pad>();
        pad.Kind = kind;
        pad.Armed = armed;
        pads.Add(pad);
    }

    /// <summary>A soft glowing ring with a bright rim, made once.</summary>
    static Sprite Ring()
    {
        if (ringSprite) return ringSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float rim = Mathf.Exp(-Mathf.Pow((r - 0.8f) / 0.07f, 2f));
                float fill = r < 0.8f ? 0.28f * (1f - r / 0.8f * 0.5f) : 0f;
                float a = Mathf.Clamp01(rim + fill) * (r < 1f ? 1f : 0f);
                byte v = (byte)(200 + 55 * rim);
                px[y * n + x] = new Color32(v, v, v, (byte)(a * 255));
            }
        tex.SetPixels32(px);
        tex.Apply();
        ringSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new UnityEngine.Vector2(0.5f, 0.5f), n / 2f);
        ringSprite.name = "EG_DelveRing";
        return ringSprite;
    }

    // ---- the entrance pad in the world ----

    static bool worldPadChecked;
    static float nextCheck;

    static Rect leaveBox;
    static bool leaveHovered;

    public static void Tick()
    {
        ScaleCreatures();
        LeaveButton();
        if (worldPadChecked || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 1f;
        if (!LocalPlayer.Get() || !GameData.HasWorldData) return;
        worldPadChecked = true;
        if (padScene.Value == LocalPlayer.Scene) WorldPads(new Vector3(padX.Value, padY.Value, 0f), armed: true);
    }

    /// <summary>The "Leave" button at the top: hit-tested here from the mouse (like the party strip). While the mouse is on
    /// it the game's controls are paused, so the click doesn't swing a weapon too.</summary>
    static void LeaveButton()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        leaveHovered = false;
        if (mouse != null && Application.isFocused && Dungeon.Active && leaveBox.width > 0 && !PlayerStatusUtil.LocalBusy)
        {
            var p = mouse.position.ReadValue();
            leaveHovered = leaveBox.Contains(new UnityEngine.Vector2(p.x, Screen.height - p.y));
            if (leaveHovered && mouse.leftButton.wasReleasedThisFrame && !Dungeon.Busy) Dungeon.Leave();
        }
        if (leaveHovered) GameInput.Block("delve"); else GameInput.Unblock("delve");
    }

    static void PutPadHere()
    {
        var me = LocalPlayer.Get();
        if (!me) return;
        foreach (var p in pads.Where(p => p && (p.Kind == PadKind.Entrance || p.Kind == PadKind.Arena)).ToList()) { Object.Destroy(p.gameObject); pads.Remove(p); }
        var pos = me.transform.position + Vector3.down * 1.6f;
        padScene.Value = LocalPlayer.Scene;
        padX.Value = pos.x;
        padY.Value = pos.y;
        WorldPads(pos, armed: false);
        NetLog.Success($"The descent's pad is here ({LocalPlayer.Scene}). Stand on it to go down", toast: true);
    }

    static void RemovePad()
    {
        padScene.Value = "";
        foreach (var p in pads.Where(p => p && (p.Kind == PadKind.Entrance || p.Kind == PadKind.Arena)).ToList()) { Object.Destroy(p.gameObject); pads.Remove(p); }
        Rory.Remove();
    }

    /// <summary>The descent's pad, the arena's next to it, and Rory keeping watch beside them.</summary>
    static void WorldPads(Vector3 at, bool armed)
    {
        MakePad(null, at, PadKind.Entrance, armed);
        MakePad(null, at + new Vector3(3.2f, 0f, 0f), PadKind.Arena, armed);
        Rory.Spawn(at + new Vector3(-2.6f, 0.4f, 0f), at);
    }

    // ---- stronger creatures ----

    static readonly Dictionary<Enemy, float> scaled = new Dictionary<Enemy, float>();
    static float nextScale;

    /// <summary>Creatures on a floor (wherever they came from: our spawns, or the area owner's through the creature sync)
    /// get the floor's health; the pool re-arms them on reuse, so this checks again whenever their health resets.</summary>
    static void ScaleCreatures()
    {
        int floor = Dungeon.Floor;
        if (floor <= 1 || Time.unscaledTime < nextScale) return;
        nextScale = Time.unscaledTime + 0.25f;
        float mul = HealthMul(floor);
        var b = Dungeon.bounds;
        b.Expand(new Vector3(4f, 4f, 1000f));
        foreach (var e in EnemiesManager.AllEnemies)
        {
            if (!e || !e.gameObject.activeInHierarchy || e.IsDead || e.initHealth <= 0f || MiniBoss.IsBoss(e)) continue;
            var p = e.transform.position;
            if (!b.Contains(new Vector3(p.x, p.y, b.center.z))) continue;
            if (scaled.TryGetValue(e, out float done) && Mathf.Abs(done - e.initHealth) < 0.5f) continue;
            float frac = e.currentHealth / e.initHealth;
            e.initHealth *= mul;
            e.currentHealth = e.initHealth * frac;
            scaled[e] = e.initHealth;
        }
    }

    /// <summary>A floor's creatures hit harder (on top of the world's difficulty).</summary>
    [HarmonyPatch(typeof(EnemyDamageDifficultyModifier), nameof(EnemyDamageDifficultyModifier.Process))]
    static class DamagePatch
    {
        static void Postfix(ref DamageData data)
        {
            if (!(data.Aggressor is Enemy e)) return;
            int floor = Dungeon.Floor;
            float mul = MiniBoss.DamageOf(e);
            if (floor > 1 && (scaled.ContainsKey(e) || MiniBoss.IsBoss(e))) mul *= DamageMul(floor);
            if (mul != 1f) data.FAmount *= mul;
        }
    }

    // ---- on screen ----

    /// <summary>From Hud.Draw: the floor number, and labels over the pads.</summary>
    public static void Draw()
    {
        int floor = Dungeon.Floor;
        var vp = ImGui.GetMainViewport();
        leaveBox = default;
        if (Dungeon.Active)
        {
            // top middle: the floor, and a button home (clicked through Tick: ImGui doesn't get the mouse while playing)
            ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x / 2f, vp.WorkPos.y + 8f), ImGuiCond.Always, new Vector2(0.5f, 0f));
            ImGui.SetNextWindowBgAlpha(0.55f);
            if (ImGui.Begin("##delvefloor", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
            {
                if (floor > 0) ImGui.TextColored(new Vector4(1f, 0.7f, 0.35f, 1f), $"Floor {floor}{(floors.Value > 0 ? $" of {floors.Value}" : "")}   creatures {HealthMul(floor):0.##}x health, {DamageMul(floor):0.##}x damage");
                else ImGui.TextColored(new Vector4(1f, 0.7f, 0.35f, 1f), "Generated dungeon");
                ImGui.SameLine();
                if (leaveHovered) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonHovered));
                ImGui.SmallButton(Dungeon.Busy ? "  ...  " : "Leave: back to where you came in");
                if (leaveHovered) ImGui.PopStyleColor();
                var a = ImGui.GetItemRectMin(); var b = ImGui.GetItemRectMax();
                leaveBox = new Rect(a.x, a.y, b.x - a.x, b.y - a.y);
            }
            ImGui.End();
        }
        var cam = Camera.main;
        if (!cam) return;
        var dl = ImGui.GetBackgroundDrawList();
        foreach (var p in pads)
        {
            if (!p || !p.gameObject.activeInHierarchy) continue;
            string text = p.Kind switch
            {
                PadKind.Down or PadKind.Finish when StairsGuarded(p.transform.position) => "Guarded: defeat the boss to open the stairs",
                PadKind.Down => $"Stairs down to floor {floor + 1}",
                PadKind.Arena => "Boss arena (practice)",
                PadKind.Home => "Back to town",
                PadKind.Finish => "The bottom! Leave the descent",
                _ => deepest.Value > 1 ? $"The descent (deepest: floor {deepest.Value})" : "The descent"
            };
            var s = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 1.3f);
            if (s.z < 0) continue;
            var size = ImGui.CalcTextSize(text);
            float x = s.x, y = Screen.height - s.y;
            dl.AddRectFilled(new Vector2(x - size.x / 2 - 4, y - 1), new Vector2(x + size.x / 2 + 4, y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.55f)), 3f);
            dl.AddText(new Vector2(x - size.x / 2, y), ImGui.ColorConvertFloat4ToU32(p.Kind == PadKind.Home ? new Vector4(0.6f, 0.85f, 1f, 1f) : new Vector4(1f, 0.75f, 0.4f, 1f)), text);
        }
    }

    // ---- the setup box (before a new descent) ----

    public static bool SetupOpen { get; private set; }
    static bool startAfter;
    static string seedText = "";
    static int floorsEdit, maxEdit;
    static float healthEdit, damageEdit;

    static void OpenSetup(bool startAfter = true)
    {
        Delve.startAfter = startAfter;
        seedText = seed.Value != 0 ? seed.Value.ToString() : new System.Random().Next(1, int.MaxValue).ToString();
        floorsEdit = floors.Value; maxEdit = maxEnemies.Value;
        healthEdit = healthGrowth.Value * 100f; damageEdit = damageGrowth.Value * 100f;
        SetupOpen = true;
        GameInput.Block("delvesetup");
    }

    static void CloseSetup(bool apply)
    {
        SetupOpen = false;
        GameInput.Unblock("delvesetup");
        if (!apply) return;
        string t = seedText.Trim();
        int newSeed = int.TryParse(t, out int sv) && sv != 0 ? sv : t.Length > 0 ? StableHash(t) : new System.Random().Next(1, int.MaxValue);
        if (newSeed != seed.Value) deepest.Value = 0;
        seed.Value = newSeed;
        floors.Value = Mathf.Max(0, floorsEdit);
        maxEnemies.Value = Mathf.Max(0, maxEdit);
        healthGrowth.Value = Mathf.Max(100f, healthEdit) / 100f;
        damageGrowth.Value = Mathf.Max(100f, damageEdit) / 100f;
        if (startAfter) Begin(1);
    }

    /// <summary>A word as a seed ("spiders" is the same floors on every PC).</summary>
    static int StableHash(string text)
    {
        uint h = 2166136261;
        foreach (char ch in text) { h ^= ch; h *= 16777619; }
        return (int)(h | 1);
    }

    /// <summary>From Hud.Draw (always: it's a window you click).</summary>
    public static void DrawSetup()
    {
        if (!SetupOpen) return;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.x + vp.WorkSize.x / 2f, vp.WorkPos.y + vp.WorkSize.y / 2f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        if (!ImGui.Begin("The descent##delvesetup", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
        { ImGui.End(); return; }

        ImGui.TextColored(new Vector4(1f, 0.7f, 0.35f, 1f), "Generated floors, down and down. Every floor's creatures are stronger.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Seed", ref seedText, 16);
        ImGui.SameLine();
        if (ImGui.SmallButton("Random")) seedText = new System.Random().Next(1, int.MaxValue).ToString();
        ImGui.SameLine();
        ImGui.TextDisabled("same seed = same floors (a number or a word)");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Floors", ref floorsEdit, 1, 5);
        if (floorsEdit < 0) floorsEdit = 0;
        ImGui.SameLine();
        ImGui.TextDisabled(floorsEdit == 0 ? "endless" : $"the bottom is floor {floorsEdit}");

        ImGui.Separator();
        ImGui.Text("How much stronger each floor is than the one above:");
        ImGui.SetNextItemWidth(260);
        ImGui.SliderFloat("Creature health##hg", ref healthEdit, 100f, 400f, "%.0f%%");
        ImGui.SetNextItemWidth(260);
        ImGui.SliderFloat("Creature damage##dg", ref damageEdit, 100f, 400f, "%.0f%%");
        ImGui.TextDisabled("Presets:");
        Preset("Gentle", 110, 105); Preset("Normal", 125, 115); Preset("Hard", 150, 130); Preset("200%", 200, 200); Preset("300%", 300, 300);
        int show = floorsEdit > 0 ? floorsEdit : 10;
        float hm = Mathf.Pow(healthEdit / 100f, show - 1), dm = Mathf.Pow(damageEdit / 100f, show - 1);
        ImGui.TextDisabled($"Floor 2: {healthEdit / 100f:0.##}x health, {damageEdit / 100f:0.##}x damage.   Floor {show}: {Big(hm)}x health, {Big(dm)}x damage");

        ImGui.Separator();
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Most creatures on a floor", ref maxEdit, 5, 20);
        if (maxEdit < 0) maxEdit = 0;
        if (maxEdit == 0) { ImGui.SameLine(); ImGui.TextDisabled("no limit"); }

        ImGui.Spacing();
        if (ImGui.Button(startAfter ? "  Go down: floor 1  " : "  Save  ")) CloseSetup(true);
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) CloseSetup(false);
        if (Plugin != null && Plugin.Connected) { ImGui.SameLine(); ImGui.TextDisabled("Friends who follow you get these settings."); }
        ImGui.End();
    }

    static void Preset(string name, float h, float d)
    {
        ImGui.SameLine();
        if (ImGui.SmallButton(name)) { healthEdit = h; damageEdit = d; }
    }

    static string Big(float v) => v >= 1000f ? v.ToString("#,0") : v.ToString("0.##");

    public static Plugin Plugin;

    /// <summary>The Insert menu's descent section (inside Dungeon.DrawMenu).</summary>
    public static void DrawMenu()
    {
        ImGui.Text("The descent: floors that keep going down, creatures stronger every floor");
        if (Dungeon.Floor > 0)
            ImGui.TextDisabled($"    On floor {Dungeon.Floor}. Ember pad: stairs down, blue pad: back to town");
        else
        {
            if (ImGui.SmallButton("Start at floor 1")) Start(1);
            if (deepest.Value > 1)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Continue at floor {deepest.Value}")) Start(deepest.Value);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Descent settings...")) OpenSetup(startAfter: false);
        }
        if (Dungeon.Floor == 0)
        {
            if (ImGui.SmallButton("Put the descent's pads here")) PutPadHere();
            ImGui.SameLine();
            if (ImGui.SmallButton("Boss arena")) Arena.Enter();
            if (!string.IsNullOrEmpty(padScene.Value))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove the pad")) RemovePad();
                ImGui.SameLine();
                ImGui.TextDisabled($"(pad in {padScene.Value})");
            }
        }
        ImGui.Spacing();
    }
}

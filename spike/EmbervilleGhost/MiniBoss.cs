using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleGhost;

/// <summary>
/// Our own mini-bosses, made from the game's creatures: any of them, bigger, tinted, with more health and harder hits,
/// a name, the game's own mini-boss health bar at the top while you're near, and a pile of loot when it dies. Its
/// AI and attacks are the creature's own (the game's behaviour graphs). The descent puts one by the stairs every few
/// floors; the boss arena spawns whatever you set up.
/// Co-op: whoever spawns it runs it (the creature sync copies it to the others as usual) and tells everyone how it
/// looks (BossLook), so their copy is the same size, colour, name and toughness.
/// </summary>
internal static class MiniBoss
{
    public static Plugin Plugin;

    public class Look
    {
        public string Name = "Boss";
        public string Base = "";       // the creature it's made from (Enemy.ID)
        public Color Tint = Color.white;
        public float Scale = 1.6f;
        public float Health = 6f;      // x the creature's own (and the floor's)
        public float Damage = 1.5f;    // x the creature's own (and the floor's)
        public int LootFloor = 1;      // how good its loot is (as a descent floor)
        public string Family;          // the line under its name on the bar (none: "<the creature it's made from> mini-boss")
    }

    /// <summary>
    /// The roster: 30 mini-bosses from the creatures the demo ships, roughly weakest first (the boss rush goes down
    /// it in order). The creature is named by the start of its prefab name (Mob_...); if this game doesn't have it
    /// loaded, another one stands in.
    /// </summary>
    public static readonly (string label, string baseHint, Color tint, float scale, float health, float damage)[] Presets =
    {
        // waves 1-21: every creature kind once
        ("FistofTheWalrus",            "Mob_Rodent_Melee",           new Color(0.85f, 0.55f, 0.4f),  1.6f,  4f, 1.1f),
        ("PeacePigeon, Squeaker",      "Mob_Rodent_Ranged",          new Color(1f, 0.9f, 0.4f),      1.6f,  4f, 1.1f),
        ("Sporeleg the Foul",          "Mob_Toxic",                  new Color(0.85f, 0.75f, 1f),    2.2f,  5f, 1.2f),
        ("justjoe, Bowling Baron",     "Mob_Rodent_Bowler",          new Color(1f, 0.6f, 0.25f),     1.7f,  5f, 1.2f),
        ("Big Tick",                   "Mob_Tick",                   new Color(1f, 0.35f, 0.3f),     2.4f,  6f, 1.3f),
        ("Charlye, Spindle Queen",     "Mob_Spiderling",             new Color(0.7f, 0.4f, 1f),      2.4f,  6f, 1.3f),
        ("HiltonCriolla the Spore",    "Mob_Lilshroom",              new Color(0.5f, 1f, 0.45f),     2.6f,  7f, 1.3f),
        ("Lau, the Glowmother",        "Mob_Glowbat",                new Color(0.4f, 1f, 1f),        2.2f,  6f, 1.4f),
        ("Lil' Boom",                  "Mob_LilBomber",              new Color(1f, 0.5f, 0.2f),      2.4f,  6f, 1.5f),
        ("dafesage, Tick King",        "Mob_LilTick",                new Color(0.9f, 0.15f, 0.25f),  2.6f,  7f, 1.4f),
        ("DexDrakon, the Turret",      "Mob_Shooter",                new Color(0.75f, 0.8f, 0.9f),   1.8f,  7f, 1.5f),
        ("Amanda the Kaboom",          "Mob_Kamikaze",               new Color(1f, 0.4f, 0.1f),      2.0f,  6f, 1.8f),
        ("Sergeant FelipeQuevici",     "Mob_Scavenger_Soldier",      new Color(0.8f, 0.45f, 0.3f),   1.6f,  8f, 1.5f),
        ("Renato, the Thundering",     "Mob_Scavenger_Ranger",       new Color(0.55f, 0.8f, 1f),     1.6f,  8f, 1.6f),
        ("Gabriel, Weaver of Tales",   "Mob_Acolyte_Apotecary",      new Color(0.7f, 1f, 0.4f),      1.6f,  8f, 1.5f),
        ("Rory, the Hollow Hunter",    "Mob_Acolyte_Tracker",        new Color(0.55f, 0.65f, 0.8f),  1.6f,  8f, 1.6f),
        ("Daru Shi, Emerald Sage",     "Mob_Acolyte_Sage",           new Color(0.4f, 1f, 0.5f),      1.6f,  9f, 1.5f),
        ("shawna, the Iron Vow",       "Mob_Acolyte_Protector",      new Color(1f, 0.85f, 0.3f),     1.8f, 10f, 1.4f),
        ("Rosalind the Pink",          "Mob_Acolyte_Sorcerer",       new Color(1f, 0.45f, 0.85f),    1.7f,  9f, 1.6f),
        ("Vasharan, Painter of Flames","Mob_Acolyte_Sorcerer_Elite", new Color(1f, 0.45f, 0.2f),     2.0f, 11f, 1.8f),
        ("The Abomination",            "Mob_Abomination",            new Color(0.95f, 0.6f, 0.6f),   1.8f, 12f, 1.9f),
        // waves 22-30: the toughest return, bigger and in new colours (never two of a kind in a row)
        ("Cahlaflour the Rotten",      "Mob_Toxic",                  new Color(0.6f, 0.9f, 0.2f),    3.0f, 12f, 1.8f),
        ("Vex, the Rat King",          "Mob_Rodent_Bowler",          new Color(0.65f, 0.35f, 0.9f),  2.6f, 13f, 1.9f),
        ("Spider Matron",              "Mob_Spiderling",             new Color(0.45f, 0.1f, 0.15f),  3.2f, 14f, 1.9f),
        ("Diego Ras, the Lagging",     "Mob_Acolyte_Apotecary",      new Color(0.3f, 0.85f, 0.75f),  2.2f, 15f, 2.0f),
        ("Night Stalker",              "Mob_Acolyte_Tracker",        new Color(0.35f, 0.25f, 0.5f),  2.2f, 16f, 2.1f),
        ("Alex Wisp, Shadow Animator", "Mob_Acolyte_Sage",           new Color(0.45f, 0.35f, 0.65f), 2.2f, 17f, 2.1f),
        ("The Unbroken",               "Mob_Acolyte_Protector",      new Color(0.85f, 0.88f, 0.95f), 2.4f, 19f, 2.0f),
        ("The Gilded Sorcerer",        "Mob_Acolyte_Sorcerer_Elite", new Color(1f, 0.85f, 0.35f),    2.4f, 20f, 2.3f),
        ("Cohh, Lord of the Descent",  "Mob_Abomination",            new Color(0.45f, 0.75f, 0.35f), 2.8f, 22f, 2.5f),
    };

    static readonly Dictionary<Enemy, Look> bosses = new Dictionary<Enemy, Look>();
    static readonly Dictionary<Enemy, float> healthDone = new Dictionary<Enemy, float>();
    static readonly Dictionary<Enemy, (SpriteRenderer sr, Color orig)[]> tinted = new Dictionary<Enemy, (SpriteRenderer, Color)[]>();
    static readonly HashSet<Enemy> mine = new HashSet<Enemy>();     // spawned here: our copy drops the loot
    static readonly HashSet<Enemy> dead = new HashSet<Enemy>();
    static readonly Dictionary<Enemy, float> announceUntil = new Dictionary<Enemy, float>();
    static readonly Dictionary<uint, (Look look, float until)> waiting = new Dictionary<uint, (Look, float)>();

    public static bool IsBoss(Enemy e) => e && bosses.ContainsKey(e);
    public static float DamageOf(Enemy e) => e && bosses.TryGetValue(e, out var l) ? l.Damage : 1f;
    public static IEnumerable<Enemy> Alive => bosses.Keys.Where(e => e && !e.IsDead && e.gameObject.activeInHierarchy);

    public static void Clear()
    {
        bosses.Clear(); healthDone.Clear(); tinted.Clear(); mine.Clear(); dead.Clear(); announceUntil.Clear(); waiting.Clear();
        HideBar();
    }

    // ---- the game's creatures ----

    static List<Enemy> prefabs;

    /// <summary>Every creature the game has loaded as a prefab (the biomes pull them all in), one per id.</summary>
    public static List<Enemy> Prefabs()
    {
        if (prefabs != null && prefabs.All(p => p)) return prefabs;
        Dungeon.AllSettings();   // loads the biomes, and with them their creatures
        // prefabs (what the biomes pulled in), and the creatures placed in the scene for kinds that aren't (they're copied
        // the same way); by name, so variants (Sorcerer / Sorcerer_Elite) are both there. Not ours, not NPCs.
        prefabs = Resources.FindObjectsOfTypeAll<Enemy>()
            .Where(e => e && !e.name.Contains("(Clone)") && !e.name.StartsWith("NPC_") && !e.name.StartsWith("Boss_") && !e.name.StartsWith("Chicken") && !IsBoss(e))
            .GroupBy(e => BaseName(e.name))
            .Select(g => g.OrderBy(e => e.gameObject.scene.IsValid() ? 1 : 0).First())
            .OrderBy(e => e.name).ToList();
        NetLog.Info($"Boss creatures: {string.Join(", ", prefabs.Select(e => e.name))}", toast: false);
        return prefabs;
    }

    /// <summary>"Mob_Acolyte_Sorcerer_01" -> "Mob_Acolyte_Sorcerer" (the numbered copies are the same creature).</summary>
    internal static string BaseName(string n)
    {
        int u = n.LastIndexOf('_');
        return u > 0 && int.TryParse(n.Substring(u + 1), out _) ? n.Substring(0, u) : n;
    }

    /// <summary>By prefab name (exact, then the start, then anywhere), else its id or creature type.</summary>
    public static Enemy Find(string idOrName)
    {
        var all = Prefabs();
        var cmp = System.StringComparison.OrdinalIgnoreCase;
        return all.FirstOrDefault(e => string.Equals(BaseName(e.name), idOrName, cmp))
               ?? all.FirstOrDefault(e => e.name.StartsWith(idOrName, cmp))
               ?? all.FirstOrDefault(e => e.ID == idOrName)
               ?? all.FirstOrDefault(e => e.name.IndexOf(idOrName, cmp) >= 0)
               ?? all.FirstOrDefault(e => e.enemyType.ToString().IndexOf(idOrName, cmp) >= 0);
    }

    /// <summary>A preset's toughness for a one-off spawn: the roster's 4x..22x squeezed to 2x..5x (and its damage to
    /// 1x..1.75x), so the first ones are a fair fight.</summary>
    public static float PresetHealth(int i) => 2f + (Presets[i].health - 4f) / 18f * 3f;
    public static float PresetDamage(int i) => 1f + (Presets[i].damage - 1.1f) / 1.4f * 0.75f;

    /// <summary>A preset's creature, or (not in this game) a stand-in that still varies from preset to preset.</summary>
    public static Enemy ForPreset(int i)
    {
        var all = Prefabs();
        if (all.Count == 0) return null;
        return Find(Presets[i].baseHint) ?? all[(i * 7) % all.Count];
    }

    public static Look FromPreset(int i)
    {
        var p = Presets[Mathf.Clamp(i, 0, Presets.Length - 1)];
        var e = ForPreset(Mathf.Clamp(i, 0, Presets.Length - 1));
        return new Look { Name = p.label, Base = e ? e.name : "", Tint = p.tint, Scale = p.scale, Health = PresetHealth(i), Damage = PresetDamage(i) };
    }

    // ---- a floor's boss ----

    static readonly string[] Titles = { "the Pink", "the Ember-Eyed", "the Hollow", "the Gilded", "the Rotten", "the Unbroken", "the Hungry", "the Old" };
    static readonly string[] Names = { "Rosalind", "Grubb", "Morwen", "Kael", "Ysolde", "Thorn", "Bramble", "Ashka", "Vex", "Old Moss" };
    static readonly Color[] Tints = { new Color(1f, 0.45f, 0.85f), new Color(1f, 0.4f, 0.3f), new Color(0.5f, 0.75f, 1f), new Color(1f, 0.85f, 0.3f), new Color(0.5f, 1f, 0.5f), new Color(0.65f, 0.45f, 1f) };

    /// <summary>A floor boss from the floor's own creatures (the biome's), picked from the floor's seed.</summary>
    public static Look ForFloor(int seed, int floor, BiomeSettings biome)
    {
        var rng = new System.Random(seed ^ 0xB055);
        var pool = new List<Enemy>();
        if (biome)
            foreach (var c in new[] { biome.BigSpawnCollection, biome.MediumSpawnCollection, biome.SmallSpawnCollection })
                if (c && c.Groups != null)
                    foreach (var g in c.Groups) if (g && g.Enemies != null) pool.AddRange(g.Enemies.Where(e => e));
        pool = pool.GroupBy(e => e.ID).Select(g => g.First()).OrderBy(e => e.ID).ToList();
        if (pool.Count == 0) pool = Prefabs();
        var baseE = pool[rng.Next(pool.Count)];
        return new Look
        {
            Name = $"{Names[rng.Next(Names.Length)]} {Titles[rng.Next(Titles.Length)]}",
            Base = baseE.name,
            Tint = Tints[rng.Next(Tints.Length)],
            Scale = 1.6f + (float)rng.NextDouble() * 0.4f,
            Health = 6f + floor * 0.5f,
            Damage = 1.4f,
            LootFloor = floor + 3,
        };
    }

    // ---- spawning ----

    public static Enemy Spawn(Look look, Vector3 at, Transform parent = null, Enemy from = null)
    {
        var prefab = from ? from : Find(look.Base);
        if (!prefab) { NetLog.Warn($"Boss: no creature called {look.Base}", toast: true); return null; }
        if (Plugin != null && Plugin.Connected) EnemySync.ExpectSpawn(prefab.gameObject.name);   // ours to run and announce, even where someone else owns the area
        var go = Object.Instantiate(prefab.gameObject, at, Quaternion.identity, parent);
        var e = go.GetComponent<Enemy>();
        e.ForceAlive();
        go.SetActive(true);
        Apply(e, look);
        mine.Add(e);
        if (Plugin != null && Plugin.Connected) announceUntil[e] = Time.unscaledTime + 10f;
        NetLog.Info($"Boss: {look.Name} ({prefab.name}, {look.Scale:0.#}x size, {look.Health:0.#}x health, {look.Damage:0.#}x damage)", toast: false);
        return e;
    }

    /// <summary>Plain creature (arena): no boss looks.</summary>
    public static Enemy SpawnPlain(string baseId, Vector3 at, Transform parent = null, Enemy from = null)
    {
        var prefab = from ? from : Find(baseId);
        if (!prefab) return null;
        if (Plugin != null && Plugin.Connected) EnemySync.ExpectSpawn(prefab.gameObject.name);
        var go = Object.Instantiate(prefab.gameObject, at, Quaternion.identity, parent);
        var e = go.GetComponent<Enemy>();
        e.ForceAlive();
        go.SetActive(true);
        return e;
    }

    static void Apply(Enemy e, Look look)
    {
        if (bosses.ContainsKey(e)) { bosses[e] = look; return; }   // already dressed (a repeat look): don't tint the tint
        bosses[e] = look;
        e.transform.localScale = Vector3.one * look.Scale;
        tinted[e] = e.GetComponentsInChildren<SpriteRenderer>(true).Select(sr => (sr, sr.color)).ToArray();
        Tint(e);
    }

    static void Tint(Enemy e)
    {
        if (!tinted.TryGetValue(e, out var list) || !bosses.TryGetValue(e, out var look)) return;
        foreach (var (sr, orig) in list)
        {
            if (!sr) continue;
            var c = orig * look.Tint;
            c.a = sr.color.a;   // fades (dying, spawning) stay the game's
            sr.color = c;
        }
    }

    // ---- every frame ----

    static float nextSlow;

    public static void Tick()
    {
        if (bosses.Count == 0 && waiting.Count == 0) { if (barFor) HideBar(); return; }
        bool slow = Time.unscaledTime >= nextSlow;
        if (slow) nextSlow = Time.unscaledTime + 0.25f;
        float floorMul = Dungeon.Floor > 1 ? Delve.HealthMul(Dungeon.Floor) : 1f;

        foreach (var e in bosses.Keys.ToList())
        {
            if (!e) { bosses.Remove(e); continue; }
            var look = bosses[e];
            if (!e.IsDead && e.initHealth > 0f && (!healthDone.TryGetValue(e, out float done) || Mathf.Abs(done - e.initHealth) > 0.5f))
            {
                float frac = e.currentHealth / e.initHealth;
                e.initHealth *= look.Health * floorMul;
                e.currentHealth = e.initHealth * frac;
                healthDone[e] = e.initHealth;
            }
            if (slow) { Tint(e); Notice(e); }
            if (e.IsDead && dead.Add(e)) Died(e, look);
            if (announceUntil.TryGetValue(e, out float until))
            {
                if (Time.unscaledTime > until) announceUntil.Remove(e);
                else if (slow && SendLook(e, look)) announceUntil.Remove(e);
            }
        }

        // looks that arrived before the creature did
        if (slow && waiting.Count > 0)
            foreach (var id in waiting.Keys.ToList())
            {
                var (look, until) = waiting[id];
                var e = EnemySync.FindById(id);
                if (e) { Apply(e, look); waiting.Remove(id); }
                else if (Time.unscaledTime > until) waiting.Remove(id);
            }

        Bar();
    }

    /// <summary>A guardian comes for you once you're close (it doesn't wait to catch sight of you).</summary>
    static void Notice(Enemy e)
    {
        if (e.IsDead || e.Group == null || !mine.Contains(e)) return;
        var me = LocalPlayer.Get();
        if (!me || ((Vector2)(me.transform.position - e.transform.position)).sqrMagnitude > 12f * 12f) return;
        e.Group.ForceEngage = true;
        e.Group.LastPlayerSeenPosition = me.transform.position;
    }

    static void Died(Enemy e, Look look)
    {
        NetLog.Success($"{look.Name} is defeated!", toast: true);
        if (!mine.Contains(e)) return;
        Integrations.NpcEvent("boss_kill", Dungeon.Floor.ToString());   // Better NPCs and Quests' rules ("when boss_kill 5 -> ...")
        if (Dungeon.Floor > 0) { Integrations.MaybeDrop(e.transform.position, Dungeon.Floor); HunterHook.MaybeDrop(e.transform.position, Dungeon.Floor); }   // now and then: a Staff of Clucking
        // its loot, on the ground (the drop sync shares it)
        var items = DungeonLoot.Roll(new System.Random(e.GetInstanceID() ^ (int)(Time.time * 1000)), Mathf.Max(1, look.LootFloor));
        if (items == null) { DungeonLoot.Preload(); return; }
        var at = e.transform.position;
        foreach (var it in items)
        {
            try { it.itemObject.DropToWorld(at, it.itemObject.amount, 1.6f); }
            catch (System.Exception ex) { NetLog.Warn($"Boss loot: {ex.Message}", toast: false); }
        }
    }

    // ---- the game's mini-boss bar ----

    static Enemy barFor;
    static int barHp = -1, barMax = -1;

    static void Bar()
    {
        var me = LocalPlayer.Get();
        Enemy near = null;
        if (me)
        {
            float best = 16f * 16f;
            foreach (var e in Alive)
            {
                float d = ((Vector2)(e.transform.position - me.transform.position)).sqrMagnitude;
                if (d < best) { best = d; near = e; }
            }
        }
        if (near != barFor)
        {
            if (barFor) HideBar();
            barFor = near;
            if (!near) return;
            var look = bosses[near];
            barHp = barMax = -1;
            Bus<UIBossEvent>.Invoke(new UIBossEvent { IsMiniBoss = true, EventType = UIBossEvent.Event_Type.InitalSetup, BossName = look.Name, BossFamily = Family(near, look), TotalHealth = (int)near.MaxHealth, CurrentHealth = (int)near.CurrentHealth });
            Bus<UIBossEvent>.Invoke(new UIBossEvent { IsMiniBoss = true, EventType = UIBossEvent.Event_Type.Show });
        }
        if (!barFor) return;
        int hp = Mathf.Max(0, (int)barFor.CurrentHealth), max = (int)barFor.MaxHealth;
        if (hp == barHp && max == barMax) return;
        barHp = hp; barMax = max;
        Bus<UIBossEvent>.Invoke(new UIBossEvent { IsMiniBoss = true, EventType = UIBossEvent.Event_Type.Update, TotalHealth = max, CurrentHealth = hp });
    }

    static void HideBar()
    {
        if (barFor || barHp >= 0) Bus<UIBossEvent>.Invoke(new UIBossEvent { IsMiniBoss = true, EventType = UIBossEvent.Event_Type.Hide });
        barFor = null; barHp = barMax = -1;
    }

    /// <summary>"Rodent Melee mini-boss": the creature it's made from, by its prefab's name. Not Enemy.enemyType: the
    /// game's list of kinds has no rats, so they carry a leftover one (a rat boss would say "Spiderling").</summary>
    /// <summary>One of our bosses?</summary>
    public static bool Is(Enemy e) => e && bosses.ContainsKey(e);

    static string Family(Enemy e, Look look)
    {
        if (!string.IsNullOrEmpty(look.Family)) return look.Family;
        var prefab = string.IsNullOrEmpty(look.Base) ? null : Find(look.Base);
        string n = BaseName((prefab ? prefab.name : e.name).Replace("(Clone)", "").Trim());
        if (n.StartsWith("Mob_")) n = n.Substring(4);
        n = System.Text.RegularExpressions.Regex.Replace(n, "^Lil(?=[A-Za-z])", "Lil ");
        n = n.Replace('_', ' ').Trim();
        if (n.Length == 0) return "Mini-boss";
        return char.ToUpperInvariant(n[0]) + n.Substring(1) + " mini-boss";
    }

    // ---- co-op ----

    /// <summary>A player who just arrived: the looks of the bosses here (their name, colour, size, bar), which were only
    /// announced when they appeared.</summary>
    public static void SendSnapshot(int playerId)
    {
        if (Plugin == null || !Plugin.Connected) return;
        foreach (var kv in bosses)
        {
            var e = kv.Key;
            if (!e || e.IsDead) continue;
            uint id = EnemySync.NetId(e);
            if (id == 0) continue;
            Plugin.SendTo(playerId, LookMessage(id, kv.Value));
        }
    }

    static NetDataWriter LookMessage(uint id, Look look)
    {
        var w = new NetDataWriter();
        w.Put((byte)MsgType.BossLook);
        w.Put(id);
        w.Put(look.Name); w.Put(look.Base);
        w.Put(look.Tint.r); w.Put(look.Tint.g); w.Put(look.Tint.b);
        w.Put(look.Scale); w.Put(look.Health); w.Put(look.Damage);
        return w;
    }

    static bool SendLook(Enemy e, Look look)
    {
        uint id = EnemySync.NetId(e);
        if (id == 0) return false;
        var w = new NetDataWriter();
        w.Put((byte)MsgType.BossLook);
        w.Put(id);
        w.Put(look.Name); w.Put(look.Base);
        w.Put(look.Tint.r); w.Put(look.Tint.g); w.Put(look.Tint.b);
        w.Put(look.Scale); w.Put(look.Health); w.Put(look.Damage);
        Plugin.SendToPlace(w, DeliveryMethod.ReliableOrdered);
        return true;
    }

    public static void OnLook(NetDataReader r)
    {
        uint id = r.GetUInt();
        var look = new Look { Name = r.GetString(), Base = r.GetString(), Tint = new Color(r.GetFloat(), r.GetFloat(), r.GetFloat()), Scale = r.GetFloat(), Health = r.GetFloat(), Damage = r.GetFloat() };
        var e = EnemySync.FindById(id);
        if (e) Apply(e, look); else waiting[id] = (look, Time.unscaledTime + 10f);
    }
}

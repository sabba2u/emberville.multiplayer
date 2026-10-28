using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace EmbervilleGhost;

/// <summary>
/// New creatures from sprite sheets: MonsterArt.dll (spike/MonsterArt: one sheet per creature, made from the Tiny Swords
/// enemy pack at Emberville's pixel size, each animation a row, and a text file saying which). Not in the mod pack (the
/// art isn't ours to pass on): without the DLL there are none. Each is one of the game's own creatures underneath (the
/// Bumblebee: a Glowbat: its flying, health, attacks, loot), its body hidden and the sheet's frames drawn where it
/// floats. Its mind and frames are ours (Monster): it wanders, chases you, winds up and stings.
/// The Bumblebee hovers near Ellie in the tutorial area, the Giant Bat a little further on (back each visit once
/// killed); /monster bumblebee, /monster giant bat put one beside you. (Teammates see the creature underneath for now.)
/// </summary>
[HarmonyLib.HarmonyPatch]
internal static class Monsters
{
    /// <summary>The game turns an enemy's behaviour graph back on after each hit: not ours.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Enemy), nameof(Enemy.RestartGraph)), HarmonyLib.HarmonyPrefix]
    static bool KeepOurMind(Enemy __instance) => !__instance.GetComponent<Monster>();

    /// <summary>A creature of ours in its shell (guarding) takes no damage: "Blocked".</summary>
    [HarmonyLib.HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyLib.HarmonyPrefix]
    static bool Shell(Enemy __instance, DamageData damageData)
    {
        var m = __instance.GetComponent<Monster>();
        if (!m) return true;
        if (m.Guarding)
        {
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Blocked", __instance.CenterOfMass.position, DamageResult.Block));
            return false;
        }
        m.Struck();   // (it can go into its shell once the hit has played)
        return true;
    }
    /// <summary>A bomb's blast is the game's explosion for its looks only: its own damage step (run by its animation as
    /// well as by us) skipped, its effects (shake, flash) kept. The bomb does the damage (Bomb.Blow).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Explosion), nameof(Explosion.Explode)), HarmonyLib.HarmonyPrefix]
    static bool HarmlessBlast(Explosion __instance)
    {
        if (!__instance || !__instance.GetComponent<BlastLooks>()) return true;
        try { __instance.onExplode?.Invoke(); } catch (System.Exception e) { NetLog.Warn($"Monsters: a blast's looks: {e.Message}", toast: false); }
        return false;
    }

    /// <summary>The game resets every explosion it has seen when an area resets: ours are gone by then (destroyed after
    /// they play), so they're skipped.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Explosion), nameof(Explosion.ResetToLastState)), HarmonyLib.HarmonyPrefix]
    static bool GoneBlast(Explosion __instance) => __instance;

    internal sealed class Anim
    {
        public Sprite[] Frames;
        public float Fps;
        public bool Loop;
    }

    internal sealed class Kind
    {
        public string Name, Base;      // our name; the game's creature it's built on
        public Anim Idle, Move, Attack;
        public int WindFrames = 6, StrikeFrames = 2;   // (the attack row: wind-up, the strike, then the recovery)
        public float WanderSpeed = 1.4f, ChaseSpeed = 3.4f, Lunge = 9f;   // (Lunge: the strike's dash speed; 0: where it stands)
        public Anim GuardIn, GuardOut;   // (null: it can't guard)
        public bool Flies;   // (on a flyer: drawn where that one flies; else its feet on the ground)
        public float Feet;   // (from its middle down to its feet, in units)
        public int Bleed;   // (its strike leaves a bleed: damage a second; 0: none)
        public float BleedFor;   // (seconds)
        public int Burn;   // (its strike sets you on fire: damage a second; 0: none)
        public int Poison;   // (its strike poisons you: damage a second; 0: none)
        public float PoisonFor;   // (seconds)
        public Anim Shot, ShotHit, Hit;   // (what it shoots, flying and landing; its flinch when hit)
        public float ShotSpeed;   // (units a second)
        public bool ShotAim;   // (its shot points the way it flies: a harpoon)
        public float ShotAngle;   // (the way its shot's picture points, degrees: the harpoon's, up-right, 45)
        public float BurnFor;   // (seconds)
        public float AttackTime, HitEvery;   // (its attack row held that long, hitting that often: the Imp's breath; 0: once)
        public float Knock, Stun;   // (its strike knocks you back that far, in units, and leaves you dizzy that long)
        public Anim Windup, Recovery, Dead;   // (its own rows for those: the Troll; null: cut from the attack row, a drop)
        public float WindupTime;   // (how long it winds up: its own windup held that long; 0: as the row plays)
        public int HitFrame;   // (with its own rows: the attack row's frame the strike lands on)
        public float Size = 1f;   // (drawn that much bigger)
        public float Reach, Radius;   // (how close it strikes from, how far its strike reaches; 0: as its kind)
        public float ThrowRange, KeepAway;   // (a thrower: it throws from that far, backs off when you're closer than KeepAway)
        public float Fuse, Blast;   // (its bomb: seconds lit on the ground, how far its blast reaches)
        public Anim BombSpin, BombFuse, Boom;   // (its bomb flying, lit, blowing up)
        public Anim Guard;   // (a block stance: the Minotaur's, the Panda's)
        public float BlockFor, BlockEvery;   // (hit, it blocks that long, then hits back; not again for BlockEvery)
        public float Trap;   // (its strike webs you up, the game's cocoon, at most this often in seconds; 0: never)
        public float BossScale, BossHealth, BossDamage;   // (a boss: the creature under it that much bigger, tougher, harder; 0: not one)
    }

    static readonly Dictionary<string, string> bases = new(System.StringComparer.OrdinalIgnoreCase) { ["Bumblebee"] = "Mob_Glowbat", ["Giant Bat"] = "Mob_Glowbat", ["Turtle"] = "Mob_Rodent_Melee", ["Paddle Shark"] = "Mob_Rodent_Melee", ["Troll"] = "Mob_Rodent_Melee", ["Spider"] = "Mob_Spiderling", ["Bomb Fish"] = "Mob_Rodent_Ranged", ["Minotaur"] = "Mob_Rodent_Melee", ["Panda"] = "Mob_Rodent_Melee", ["Imp"] = "Mob_Glowbat" };
    static readonly Dictionary<string, Kind> kinds = new(System.StringComparer.OrdinalIgnoreCase);

    // (by reflection: the compiler can't pick between its overloads against this game's libraries)
    static readonly System.Reflection.MethodInfo LoadImage =
        typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });

    static System.Reflection.Assembly art;
    static bool artLooked;

    /// <summary>MonsterArt.dll, anywhere under the plugins folder (null: not installed).</summary>
    static System.Reflection.Assembly Art()
    {
        if (artLooked) return art;
        artLooked = true;
        try
        {
            var file = Directory.GetFiles(BepInEx.Paths.PluginPath, "MonsterArt.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (file != null) art = System.Reflection.Assembly.LoadFrom(file);
            NetLog.Info(art != null ? $"Monsters: art from {file} ({string.Join(", ", art.GetManifestResourceNames())})" : "Monsters: no MonsterArt.dll", toast: false);
        }
        catch (System.Exception e) { NetLog.Warn($"Monsters: MonsterArt.dll: {e.Message}", toast: false); }
        return art;
    }

    /// <summary>A file in MonsterArt.dll, its name matched loosely ("giantbat.png": "Giant Bat.png").</summary>
    static byte[] Resource(string file)
    {
        string Plain(string x) => x.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        var real = Art()?.GetManifestResourceNames().FirstOrDefault(n => Plain(n) == Plain("MonsterArt." + file));
        using var s = real == null ? null : Art().GetManifestResourceStream(real);
        if (s == null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static Kind Load(string name)
    {
        if (kinds.TryGetValue(name, out var k)) return k;
        var png = Resource(name + ".png");
        var txt = Resource(name + ".txt");
        if (png == null || txt == null) { NetLog.Info($"Monsters: no art for {name} (MonsterArt.dll: spike/MonsterArt)", toast: true); return kinds[name] = null; }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!(bool)LoadImage.Invoke(null, new object[] { tex, png, false })) return kinds[name] = null;
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.name = "EG_" + name;
        Object.DontDestroyOnLoad(tex);
        // its text: cell 64 / ppu 32 / pivot x y / anim Name row frames fps loop|once
        int cell = 64, wind = 6, strike = 2;
        float wander = 1.4f, chase = 3.4f, lunge = 9f;
        float ppu = 32f;
        var pivot = new Vector2(0.5f, 0.5f);
        float feet = 0f, bleedFor = 0f;
        int bleed = 0;
        float knock = 0f, stun = 0f, windup = 0f, size = 1f, reach = 0f, radius = 0f, bossScale = 0f, bossHealth = 0f, bossDamage = 0f;
        int hitFrame = 0, burn = 0, poison = 0;
        float poisonFor = 0f, shotSpeed = 8f;
        bool shotAim = false;
        float shotAngle = 0f;
        string under = null;
        float burnFor = 0f, attackTime = 0f, hitEvery = 0f;
        float trap = 0f, throwRange = 0f, keepAway = 0f, fuse = 1.2f, blast = 1.3f, blockFor = 0f, blockEvery = 0f;
        var anims = new Dictionary<string, Anim>(System.StringComparer.OrdinalIgnoreCase);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in System.Text.Encoding.UTF8.GetString(txt).Split('\n'))
        {
            var w = line.Trim().Split(' ');
            if (w[0] == "cell") cell = int.Parse(w[1]);
            else if (w[0] == "ppu") ppu = float.Parse(w[1], inv);
            else if (w[0] == "strike" && w.Length >= 3) { wind = int.Parse(w[1]); strike = int.Parse(w[2]); }
            else if (w[0] == "speed" && w.Length >= 3) { wander = float.Parse(w[1], inv); chase = float.Parse(w[2], inv); }
            else if (w[0] == "lunge" && w.Length >= 2) lunge = float.Parse(w[1], inv);
            else if (w[0] == "knock" && w.Length >= 3) { knock = float.Parse(w[1], inv); stun = float.Parse(w[2], inv); }
            else if (w[0] == "base" && w.Length >= 2) under = w[1];
            else if (w[0] == "poison" && w.Length >= 3) { poison = int.Parse(w[1]); poisonFor = float.Parse(w[2], inv); }
            else if (w[0] == "shot" && w.Length >= 3) { shotSpeed = float.Parse(w[1], inv); shotAim = w[2] == "1"; if (w.Length >= 4) shotAngle = float.Parse(w[3], inv); }
            else if (w[0] == "burn" && w.Length >= 3) { burn = int.Parse(w[1]); burnFor = float.Parse(w[2], inv); }
            else if (w[0] == "breath" && w.Length >= 3) { attackTime = float.Parse(w[1], inv); hitEvery = float.Parse(w[2], inv); }
            else if (w[0] == "bleed" && w.Length >= 3) { bleed = int.Parse(w[1]); bleedFor = float.Parse(w[2], inv); }
            else if (w[0] == "windup" && w.Length >= 2) windup = float.Parse(w[1], inv);
            else if (w[0] == "block" && w.Length >= 3) { blockFor = float.Parse(w[1], inv); blockEvery = float.Parse(w[2], inv); }
            else if (w[0] == "ranged" && w.Length >= 3) { throwRange = float.Parse(w[1], inv); keepAway = float.Parse(w[2], inv); }
            else if (w[0] == "bomb" && w.Length >= 3) { fuse = float.Parse(w[1], inv); blast = float.Parse(w[2], inv); }
            else if (w[0] == "trap" && w.Length >= 2) trap = float.Parse(w[1], inv);
            else if (w[0] == "hit" && w.Length >= 2) hitFrame = int.Parse(w[1]);
            else if (w[0] == "size" && w.Length >= 2) size = float.Parse(w[1], inv);
            else if (w[0] == "reach" && w.Length >= 3) { reach = float.Parse(w[1], inv); radius = float.Parse(w[2], inv); }
            else if (w[0] == "boss" && w.Length >= 4) { bossScale = float.Parse(w[1], inv); bossHealth = float.Parse(w[2], inv); bossDamage = float.Parse(w[3], inv); }
            else if (w[0] == "feet" && w.Length >= 2) feet = float.Parse(w[1], inv);
            else if (w[0] == "pivot") pivot = new Vector2(float.Parse(w[1], inv), float.Parse(w[2], inv));
            else if (w[0] == "anim" && w.Length >= 6)
            {
                int row = int.Parse(w[2]), n = int.Parse(w[3]);
                int y = tex.height - (row + 1) * cell;   // (rows counted from the top; the texture from the bottom)
                var frames = new Sprite[n];
                for (int i = 0; i < n; i++)
                {
                    frames[i] = Sprite.Create(tex, new Rect(i * cell, y, cell, cell), pivot, ppu, 0, SpriteMeshType.FullRect);
                    frames[i].name = $"EG_{name}_{w[1]}_{i}";
                    Object.DontDestroyOnLoad(frames[i]);
                }
                anims[w[1]] = new Anim { Frames = frames, Fps = float.Parse(w[4], inv), Loop = w[5] == "loop" };
            }
        }
        if (!anims.TryGetValue("Idle", out var idle)) return kinds[name] = null;
        k = new Kind
        {
            Name = name, Base = under ?? (bases.TryGetValue(name, out var b) ? b : "Mob_Glowbat"), Idle = idle, WindFrames = wind, StrikeFrames = strike,
            WanderSpeed = wander, ChaseSpeed = chase, Lunge = lunge, Bleed = bleed, BleedFor = bleedFor, Knock = knock, Stun = stun,
            Flies = (under ?? (bases.TryGetValue(name, out var listed) ? listed : "Mob_Glowbat")) == "Mob_Glowbat", Feet = (pivot.y - feet) * cell / ppu,
            GuardIn = anims.TryGetValue("Guard_In", out var gi) ? gi : null, GuardOut = anims.TryGetValue("Guard_Out", out var go) ? go : null,
            Move = anims.TryGetValue("Move", out var mv) ? mv : anims.TryGetValue("Walk", out var wk) ? wk : anims.TryGetValue("Run", out var run) ? run : idle,
            // (the Imp's attack comes in three: its start a windup, its loop the attack, its end a recovery)
            Attack = new[] { "Attack", "Attack_Fast", "Attack_Loop", "Shoot", "Throw" }.Select(x => anims.TryGetValue(x, out var a) ? a : null).FirstOrDefault(a => a != null) ?? idle,
            Windup = anims.TryGetValue("Windup", out var wu) ? wu : anims.TryGetValue("Attack_Start", out var ast) ? ast : null,
            Recovery = anims.TryGetValue("Recovery", out var rc) ? rc : anims.TryGetValue("Attack_End", out var ae) ? ae : null,
            Burn = burn, BurnFor = burnFor, AttackTime = attackTime, HitEvery = hitEvery, Poison = poison, PoisonFor = poisonFor,
            Shot = anims.TryGetValue("Shot", out var st) ? st : null, ShotHit = anims.TryGetValue("ShotHit", out var shh) ? shh : null,
            Hit = anims.TryGetValue("Hit", out var ht) ? ht : null, ShotSpeed = shotSpeed, ShotAim = shotAim, ShotAngle = shotAngle,
            Dead = anims.TryGetValue("Dead", out var dd) ? dd : null,
            WindupTime = windup, HitFrame = hitFrame, Size = size, Reach = reach, Radius = radius, Trap = trap,
            ThrowRange = throwRange, KeepAway = keepAway, Fuse = fuse, Blast = blast, BlockFor = blockFor, BlockEvery = blockEvery,
            Guard = anims.TryGetValue("Guard", out var gd) ? gd : null,
            BombSpin = anims.TryGetValue("BombSpin", out var bs) ? bs : null, BombFuse = anims.TryGetValue("BombFuse", out var bf) ? bf : null,
            Boom = anims.TryGetValue("Boom", out var bm) ? bm : null,
            BossScale = bossScale, BossHealth = bossHealth, BossDamage = bossDamage,
        };
        NetLog.Info($"Monsters: {name} loaded ({string.Join(", ", anims.Select(x => $"{x.Key} {x.Value.Frames.Length}"))}), on {k.Base}", toast: false);
        return kinds[name] = k;
    }

    /// <summary>One at that spot: the game's creature, dressed as this one.</summary>
    public static Enemy Spawn(string name, Vector3 at)
    {
        var kind = Load(name);
        if (kind == null) return null;
        var prefab = MiniBoss.Find(kind.Base) ?? MiniBoss.Find(kind.Base + "_Tut");
        if (!prefab) { NetLog.Warn($"Monsters: the game's {kind.Base} isn't loaded here", toast: true); return null; }
        if (NavMesh.SamplePosition(at, out var hit, 3f, NavMesh.AllAreas)) at = hit.position;
        // a boss (the Troll): the boss maker's (bigger, tougher, harder hitting, its name on the boss bar); else plain
        var e = kind.BossScale > 0f
            ? MiniBoss.Spawn(new MiniBoss.Look { Name = "The " + kind.Name, Base = prefab.name, Tint = Color.white, Scale = kind.BossScale, Health = kind.BossHealth, Damage = kind.BossDamage, LootFloor = 4 }, at, null, prefab)
            : MiniBoss.SpawnPlain(null, at, null, prefab);
        if (!e) return null;
        e.gameObject.name = $"EG_{kind.Name}";
        e.gameObject.AddComponent<Monster>().Kind = kind;
        NetLog.Info($"Monsters: a {kind.Name} at {at.x:0.0}, {at.y:0.0} ({prefab.name} underneath)", toast: false);
        return e;
    }

    /// <summary>Every creature of ours MonsterArt.dll has (none: it isn't installed).</summary>
    public static List<string> Names() =>
        Art()?.GetManifestResourceNames().Where(n => n.EndsWith(".txt")).Select(n => n.Substring("MonsterArt.".Length, n.Length - "MonsterArt.".Length - 4)).OrderBy(n => n).ToList()
        ?? new List<string>();

    /// <summary>/monsters: every creature of ours MonsterArt.dll has (clickable in the ~ console, as /dungeons).</summary>
    public static void List()
    {
        var names = Names();
        if (names == null || names.Count == 0) { Chat.System("No monsters: MonsterArt.dll isn't installed (spike/MonsterArt)."); return; }
        Chat.System("Monsters: " + string.Join(", ", names) + " (/monster name puts one beside you)");
        if (GameConsole.IsOpen) GameConsole.Links("Monsters (click one, then Enter):", names.Select(n => (n, "/monster " + n.ToLowerInvariant())));
    }

    /// <summary>/monster [name]: one beside you.</summary>
    public static void Command(string arg)
    {
        var me = LocalPlayer.Get();
        if (!me) return;
        string name = string.IsNullOrWhiteSpace(arg) ? "Bumblebee" : arg.Trim();
        name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());   // ("queen bee": "Queen Bee")
        if (Spawn(name, me.transform.position + new Vector3(2f, 1f, 0f))) Chat.System($"A {name}!");
    }

    // ---- ours in the tutorial area ----

    // who, and where (each back on the next visit once killed): the Bumblebee by Ellie, the Giant Bat by Rory, the
    // Turtle below the camp, the rest
    // further on (the Troll on his own)
    static readonly (string name, Vector3 at)[] Residents =
    {
        ("Bumblebee", new Vector3(-35.5f, 75.5f, 0f)),
        ("Giant Bat", new Vector3(-46.0f, 70.9f, 0f)),   // (by Rory, 2 above him)
        ("Turtle", new Vector3(-38.8f, 66.9f, 0f)),   // (6 south of Ellie)
        ("Paddle Shark", new Vector3(-36.0f, 63.5f, 0f)),   // (south of the camp)
        ("Spider", new Vector3(-32.4f, 59.5f, 0f)),
        ("Bomb Fish", new Vector3(-30.6f, 58.6f, 0f)),   // (beside the Spider)
        ("Imp", new Vector3(-18.3f, 62.6f, 0f)),
        ("Minotaur", new Vector3(-19.6f, 61.0f, 0f)),
        ("Panda", new Vector3(-17.0f, 61.0f, 0f)),   // (the three together)
        ("Troll", new Vector3(-13.8f, 72.3f, 0f)),   // (the boss)
        // the rest of the pack, near those (each lands on the nearest walkable ground)
        ("Queen Bee", new Vector3(-37.5f, 77.0f, 0f)),   // (above the Bumblebee)
        ("Snake", new Vector3(-41.0f, 66.0f, 0f)),   // (by the Turtle)
        ("Lizard", new Vector3(-42.5f, 68.0f, 0f)),
        ("Harpoon Shark", new Vector3(-38.5f, 62.5f, 0f)),   // (by the Paddle Shark)
        ("Gnome", new Vector3(-34.5f, 60.5f, 0f)),   // (by the Spider)
        ("Slingshot Gnome", new Vector3(-35.5f, 58.5f, 0f)),
        ("Thief", new Vector3(-28.5f, 60.0f, 0f)),   // (by the Bomb Fish)
        ("Spear Goblin", new Vector3(-26.0f, 61.0f, 0f)),
        ("Torch Goblin", new Vector3(-24.0f, 60.0f, 0f)),
        ("Gnoll", new Vector3(-22.0f, 62.5f, 0f)),   // (on the way to the Imp)
        ("Skull", new Vector3(-21.0f, 59.5f, 0f)),   // (by the Minotaur)
        ("Hex Shaman", new Vector3(-15.0f, 63.0f, 0f)),   // (by the Panda)
        ("Bear", new Vector3(-16.5f, 66.5f, 0f)),   // (on the way to the Troll)
        ("Great Bear", new Vector3(-17.5f, 70.0f, 0f)),   // (a boss, short of the Troll)
    };
    static readonly Dictionary<string, Enemy> residents = new();
    static string sceneWas;
    static float spawnAt = -1f;

    public static void Tick()
    {
        string scene = LocalPlayer.Scene;
        if (scene != sceneWas)
        {
            sceneWas = scene;
            spawnAt = scene == "TutorialArea" ? Time.unscaledTime + 3f : -1f;   // (once the area has settled)
        }
        if (spawnAt < 0f || Time.unscaledTime < spawnAt) return;
        spawnAt = -1f;
        if (MiniBoss.Plugin != null && MiniBoss.Plugin.IsClient) return;   // (the host's world has them)
        if (!LocalPlayer.Get()) { spawnAt = Time.unscaledTime + 1f; return; }
        foreach (var (name, at) in Residents)
        {
            if (residents.TryGetValue(name, out var e) && e && !e.IsDead) continue;
            residents[name] = Spawn(name, at);
        }
    }
}


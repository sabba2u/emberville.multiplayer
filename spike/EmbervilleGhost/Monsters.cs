using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
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

    /// <summary>One of ours has just died (where: its transform).</summary>
    public static System.Action<Monster> Died;

    /// <summary>An unseen creature's health bar (the Thief faded out): not shown, whatever asks.</summary>
    [HarmonyLib.HarmonyPatch(typeof(UIManager), nameof(UIManager.OnShowFloatingBar)), HarmonyLib.HarmonyPrefix]
    static bool NoBarUnseen(UIFloatingBarEvent barEvent) =>
        barEvent.Hide || !barEvent.Owner || !(barEvent.Owner.GetComponent<Monster>() is Monster m) || !m.Unseen;

    /// <summary>A creature of ours in its shell (guarding) takes no damage: "Blocked".</summary>
    [HarmonyLib.HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage)), HarmonyLib.HarmonyPrefix]
    static bool Shell(Enemy __instance, ref DamageData damageData)
    {
        var m = __instance.GetComponent<Monster>();
        if (!m) return true;
        // thorns (the Turtle): your blow hurts you a little too (its shell as well)
        if (m.Kind != null && m.Kind.Thorns > 0f && damageData.Aggressor is PlayerMaster hitter && hitter == LocalPlayer.Get() && damageData.Amount > 0 && !__instance.IsDead)
        {
            int back = Mathf.Max(1, Mathf.RoundToInt(damageData.Amount * m.Kind.Thorns));
            try { CombatSystem.Resolve(new DamageData(__instance, hitter, back, DamageHitType.Impact, KnockBack.None)); Statuses.Pop("Thorns!"); }
            catch (System.Exception x) { NetLog.Warn($"Monsters: thorns: {x.Message}", toast: false); }
        }
        if (m.Guarding)
        {
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Blocked", __instance.CenterOfMass.position, DamageResult.Block));
            return false;
        }
        m.Armour(ref damageData);   // (the King's armour: a tenth gets through, unless his back's open or he's dazed)
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
        public float Heal, HealRange;   // (a healer: heals the most hurt creature that near by that share of its health; never hits you)
        public float HealEvery;   // (a healer: not again for that long)
        public Anim HealFx;   // (what shows on the one it heals)
        public float Height;   // (how tall it's drawn at size 1, in world units: its first idle frame's visible pixels)
        public float SummonEvery;   // (a summoner (the King): calls in SummonCount of its colour's Summons that often; 0: no)
        public int SummonCount;
        public string[] SummonsAlways;   // (called in every time as well: the King's Monk and Bowman)
        public string[] Summons;   // (troop names without the colour: "Bowman", "Knight"...)
        public float BarrageEvery;   // (calls in its catapults: that often, BarrageCount stones on marked spots around you)
        public int BarrageCount;
        public float Blink;   // (hit, it teleports away from you; not again for that long: the King; 0: no)
        public float Armour, StuckFor, WallDaze;   // (armoured: that share of damage gets through; its weapon stuck that long after a swing (its back open); its dash into a wall dazes it that long)
        public float Backwards;   // (its stunning hit leaves your controls backwards that long after the stun)
        public float ChargeTime, ChargeSpeed, ChargeWindup;   // (its attack a rush at you: at most that long, that fast, after that windup; it hits once, on reaching you: the Lancer)
        public float Thrust;   // (a second blow that long into its follow-through: the Swordsman's thrust after his swing; 0: none)
        public float Disarm, Silence, Root, SlowTo, SlowFor, Blind, Fists;   // (its hit leaves you unable to attack / to use skills that long: the Gnome, the Hex Shaman)
        public float DeathBomb;   // (dying, it drops a bomb lit that long: the Bomb Fish)
        public float Thorns, Mark, Polymorph;   // (your blows on it hurt you that share of them; its hit marks you that long (its hits: harder); its hit makes you a chicken that long)
        public int Split;
        public float SplitSize = 0.75f, SplitStats = 0.5f;   // (killed, it splits into that many smaller ones, which don't split again: the Gnome)
        public int Spread;   // (its bombs thrown that many at once, in a fan: the TNT Goblin)
        public float SpreadAngle;
        public int Venom;   // (its bites build venom: that many and you're paralyzed VenomStun seconds: the Snake)
        public float VenomStun;
        public float ShotBounce;   // (its shots bounce off walls for that long: the Gnoll's bones)
        public bool Pull;   // (its shot reels you in: the Harpoon Shark)
        public int StrongEvery;   // (every that many attacks its strong one, a charge: the Spear Goblin)
        public float StrongTime, StrongSpeed, StrongDamage, StrongReach;
        public bool Trample;   // (its charge runs through you: the Pig Rider, the Horseman)
        public bool Pound;   // (its blow a ground pound: dust all round, a shake: the Great Bear)
        public int BashEvery;   // (every that many attacks a shield bash: stunned BashStun: the Warrior, the Knight)
        public float BashStun;
        public Anim Dust, Dust2, Splash, PoundFx, Strong;   // (behind a charge, small puffs and big clouds; on you when it soaks you; the pound's; its strong attack)
        public bool Stealth;   // (unseen but for a shimmer, except as it strikes: the Thief)
        public bool Spin;   // (its attack is a spin after you, no damage taken while it spins: the Berserker)
        public float Ambush;   // (waits hidden in its Guard_In (no damage) till you're this near: the Barrel Goblin; 0: no)
        public bool Kamikaze;   // (its strike is blowing itself up: its blast, then it's dead)
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
        public bool OwnBoom;   // (its bomb lands as its own Boom (the Catapult's stone shattering), not the game's fire)
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

    internal static Kind Load(string name)
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
        float shotAngle = 0f, ambush = 0f, heal = 0f, healRange = 0f, healEvery = 0f;
        bool ownBoom = false;
        float summonEvery = 0f, keep = 0f, barrageEvery = 0f;
        int barrageCount = 0;
        int summonCount = 0;
        string[] summons = null, summonsAlways = null;
        bool kamikaze = false, spin = false, stealth = false;
        int split = 0;
        float deathBomb = 0f, thorns = 0f, mark = 0f, polymorph = 0f, spreadAngle = 30f;
        int spread = 0, venom = 0, strongEvery = 0, bashEvery = 0;
        float venomStun = 0f, shotBounce = 0f, strongTime = 0f, strongSpeed = 0f, strongDamage = 1f, strongReach = 0f, bashStun = 0f;
        bool pull = false, trample = false, pound = false;
        float splitSize = 0.75f, splitStats = 0.5f;
        float fists = 0f, blind = 0f, disarm = 0f, silence = 0f, root = 0f, slowTo = 1f, slowFor = 0f, thrust = 0f, chargeTime = 0f, chargeSpeed = 0f, chargeWindup = 0f, backwards = 0f, blink = 0f, armour = 0f, stuckFor = 0f, wallDaze = 0f;
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
            else if (w[0] == "ambush" && w.Length >= 2) ambush = float.Parse(w[1], inv);
            else if (w[0] == "kamikaze") kamikaze = true;
            else if (w[0] == "spin") spin = true;
            else if (w[0] == "stealth") stealth = true;
            else if (w[0] == "venom" && w.Length >= 3) { venom = int.Parse(w[1]); venomStun = float.Parse(w[2], inv); }
            else if (w[0] == "bounce" && w.Length >= 2) shotBounce = float.Parse(w[1], inv);
            else if (w[0] == "pull") pull = true;
            else if (w[0] == "trample") trample = true;
            else if (w[0] == "pound") pound = true;
            else if (w[0] == "strong" && w.Length >= 6) { strongEvery = int.Parse(w[1]); strongTime = float.Parse(w[2], inv); strongSpeed = float.Parse(w[3], inv); strongDamage = float.Parse(w[4], inv); strongReach = float.Parse(w[5], inv); }
            else if (w[0] == "bash" && w.Length >= 3) { bashEvery = int.Parse(w[1]); bashStun = float.Parse(w[2], inv); }
            else if (w[0] == "spread" && w.Length >= 3) { spread = int.Parse(w[1]); spreadAngle = float.Parse(w[2], inv); }
            else if (w[0] == "disarm" && w.Length >= 2) disarm = float.Parse(w[1], inv);
            else if (w[0] == "silence" && w.Length >= 2) silence = float.Parse(w[1], inv);
            else if (w[0] == "root" && w.Length >= 2) root = float.Parse(w[1], inv);
            else if (w[0] == "split" && w.Length >= 2) { split = int.Parse(w[1]); if (w.Length >= 4) { splitSize = float.Parse(w[2], inv); splitStats = float.Parse(w[3], inv); } }
            else if (w[0] == "blind" && w.Length >= 2) blind = float.Parse(w[1], inv);
            else if (w[0] == "deathbomb" && w.Length >= 2) deathBomb = float.Parse(w[1], inv);
            else if (w[0] == "thorns" && w.Length >= 2) thorns = float.Parse(w[1], inv);
            else if (w[0] == "mark" && w.Length >= 2) mark = float.Parse(w[1], inv);
            else if (w[0] == "polymorph" && w.Length >= 2) polymorph = float.Parse(w[1], inv);
            else if (w[0] == "fists" && w.Length >= 2) fists = float.Parse(w[1], inv);
            else if (w[0] == "slow" && w.Length >= 3) { slowTo = float.Parse(w[1], inv); slowFor = float.Parse(w[2], inv); }
            else if (w[0] == "thrust" && w.Length >= 2) thrust = float.Parse(w[1], inv);
            else if (w[0] == "charge" && w.Length >= 4) { chargeTime = float.Parse(w[1], inv); chargeSpeed = float.Parse(w[2], inv); chargeWindup = float.Parse(w[3], inv); }
            else if (w[0] == "backwards" && w.Length >= 2) backwards = float.Parse(w[1], inv);
            else if (w[0] == "armour" && w.Length >= 4) { armour = float.Parse(w[1], inv); stuckFor = float.Parse(w[2], inv); wallDaze = float.Parse(w[3], inv); }
            else if (w[0] == "blink" && w.Length >= 2) blink = float.Parse(w[1], inv);
            else if (w[0] == "summon" && w.Length >= 4) { summonEvery = float.Parse(w[1], inv); summonCount = int.Parse(w[2]); summons = w.Skip(3).ToArray(); }
            else if (w[0] == "summonalso" && w.Length >= 2) summonsAlways = w.Skip(1).ToArray();
            else if (w[0] == "keep" && w.Length >= 2) keep = float.Parse(w[1], inv);
            else if (w[0] == "barrage" && w.Length >= 3) { barrageEvery = float.Parse(w[1], inv); barrageCount = int.Parse(w[2]); }
            else if (w[0] == "heal" && w.Length >= 3) { heal = float.Parse(w[1], inv); healRange = float.Parse(w[2], inv); if (w.Length >= 4) healEvery = float.Parse(w[3], inv); }
            else if (w[0] == "poison" && w.Length >= 3) { poison = int.Parse(w[1]); poisonFor = float.Parse(w[2], inv); }
            else if (w[0] == "shot" && w.Length >= 3) { shotSpeed = float.Parse(w[1], inv); shotAim = w[2] == "1"; if (w.Length >= 4) shotAngle = float.Parse(w[3], inv); }
            else if (w[0] == "burn" && w.Length >= 3) { burn = int.Parse(w[1]); burnFor = float.Parse(w[2], inv); }
            else if (w[0] == "breath" && w.Length >= 3) { attackTime = float.Parse(w[1], inv); hitEvery = float.Parse(w[2], inv); }
            else if (w[0] == "bleed" && w.Length >= 3) { bleed = int.Parse(w[1]); bleedFor = float.Parse(w[2], inv); }
            else if (w[0] == "windup" && w.Length >= 2) windup = float.Parse(w[1], inv);
            else if (w[0] == "block" && w.Length >= 3) { blockFor = float.Parse(w[1], inv); blockEvery = float.Parse(w[2], inv); }
            else if (w[0] == "ranged" && w.Length >= 3) { throwRange = float.Parse(w[1], inv); keepAway = float.Parse(w[2], inv); }
            else if (w[0] == "bomb" && w.Length >= 3) { fuse = float.Parse(w[1], inv); blast = float.Parse(w[2], inv); ownBoom = w.Length >= 4 && w[3] == "1"; }
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
        float height = 0f;
        try   // (its visible height: the first idle frame's rows with anything in them)
        {
            var r = idle.Frames[0].rect;
            var px = tex.GetPixels32();
            int top = -1, bottom = -1;
            for (int yy = (int)r.y; yy < (int)(r.y + r.height); yy++)
                for (int xx = (int)r.x; xx < (int)(r.x + r.width); xx++)
                    if (px[yy * tex.width + xx].a > 20) { if (bottom < 0) bottom = yy; top = yy; break; }
            if (top >= 0) height = (top - bottom + 1) / ppu;
        }
        catch { }
        k = new Kind
        {
            Height = height,
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
            Hit = anims.TryGetValue("Hit", out var ht) ? ht : null, ShotSpeed = shotSpeed, ShotAim = shotAim, ShotAngle = shotAngle, Ambush = ambush, Kamikaze = kamikaze, Spin = spin, Stealth = stealth, Venom = venom, VenomStun = venomStun, ShotBounce = shotBounce, Pull = pull, Trample = trample, Pound = pound,
            StrongEvery = strongEvery, StrongTime = strongTime, StrongSpeed = strongSpeed, StrongDamage = strongDamage, StrongReach = strongReach,
            BashEvery = bashEvery, BashStun = bashStun,
            Dust = anims.TryGetValue("Dust", out var du) ? du : null, Dust2 = anims.TryGetValue("Dust2", out var du2) ? du2 : null, Splash = anims.TryGetValue("Splash", out var spl) ? spl : null,
            PoundFx = anims.TryGetValue("Pound", out var pfx) ? pfx : null, Strong = anims.TryGetValue("Attack_Strong", out var stg) ? stg : null, Spread = spread, SpreadAngle = spreadAngle, Blink = blink, Disarm = disarm, Silence = silence, Root = root, Split = split, DeathBomb = deathBomb, Thorns = thorns, Mark = mark, Polymorph = polymorph, SplitSize = splitSize, SplitStats = splitStats, Blind = blind, Fists = fists, SlowTo = slowTo, SlowFor = slowFor, Thrust = thrust, ChargeTime = chargeTime, ChargeSpeed = chargeSpeed, ChargeWindup = chargeWindup, Backwards = backwards, Armour = armour, StuckFor = stuckFor, WallDaze = wallDaze, SummonEvery = summonEvery, BarrageEvery = barrageEvery, BarrageCount = barrageCount, SummonCount = summonCount, Summons = summons, SummonsAlways = summonsAlways, OwnBoom = ownBoom, Heal = heal, HealRange = healRange, HealEvery = healEvery, HealFx = anims.TryGetValue("HealFx", out var hf) ? hf : null,
            Dead = anims.TryGetValue("Dead", out var dd) ? dd : null,
            WindupTime = windup, HitFrame = hitFrame, Size = size, Reach = reach, Radius = radius, Trap = trap,
            ThrowRange = throwRange, KeepAway = keep > 0f ? keep : keepAway, Fuse = fuse, Blast = blast, BlockFor = blockFor, BlockEvery = blockEvery,
            Guard = anims.TryGetValue("Guard", out var gd) ? gd : null,
            BombSpin = anims.TryGetValue("BombSpin", out var bs) ? bs : null, BombFuse = anims.TryGetValue("BombFuse", out var bf) ? bf : null,
            Boom = anims.TryGetValue("Boom", out var bm) ? bm : null,
            BossScale = bossScale, BossHealth = bossHealth, BossDamage = bossDamage,
        };
        NetLog.Info($"Monsters: {name} loaded ({string.Join(", ", anims.Select(x => $"{x.Key} {x.Value.Frames.Length}"))}), on {k.Base}", toast: false);
        return kinds[name] = k;
    }

    /// <summary>One of ours for a boss: any creature or troop but the King, the catapults and the water ones. Null:
    /// MonsterArt isn't installed.</summary>
    public static string BossCandidate(System.Random rng)
    {
        var have = names ??= Names();
        var fit = have.Where(n => !n.EndsWith(" King") && !n.EndsWith(" Catapult") && !n.Contains("Shark") && n != "Bomb Fish" && n != "Queen Bee" && n != "Troll" && n != "Great Bear").ToList();
        return fit.Count > 0 ? fit[rng.Next(fit.Count)] : null;
    }

    /// <summary>One of ours as a boss (a dungeon's end): bigger, tougher, harder hitting, its name on the boss bar. A
    /// boss kind (the Troll, the Queen Bee) as its own boss values say.</summary>
    public static Enemy SpawnBoss(string name, Vector3 at, string title, string family, float health = 9f, float damage = 1.5f, Transform parent = null)
    {
        var kind = Load(name);
        if (kind == null) return null;
        if (kind.BossScale > 0f && parent == null && health == 9f) return Spawn(name, at, title, family);   // (a boss kind as its own values say)
        var prefab = MiniBoss.Find(kind.Base) ?? MiniBoss.Find(kind.Base + "_Tut");
        if (!prefab) return null;
        if (NavMesh.SamplePosition(at, out var hit, 3f, NavMesh.AllAreas)) at = hit.position;
        float size = kind.BossScale > 0f ? kind.BossScale : BossSize;   // (a boss kind: its own size; already big to draw)
        var e = MiniBoss.Spawn(new MiniBoss.Look { Name = title, Family = family, Base = prefab.name, Tint = Color.white, Scale = size, Health = health, Damage = damage, LootFloor = 8 }, at, parent, prefab);
        if (!e) return null;
        e.gameObject.name = $"EG_{kind.Name}";
        var m = e.gameObject.AddComponent<Monster>();
        m.Kind = kind;
        m.SizeMul = kind.BossScale > 0f ? 1f : BossSize;   // (drawn bigger too: ours are drawn at their own size, whatever the body underneath)
        NetLog.Info($"Monsters: {title}, a {kind.Name} boss, at {at.x:0.0}, {at.y:0.0}", toast: false);
        return e;
    }

    const float BossSize = 1.7f;

    /// <summary>One at that spot: the game's creature, dressed as this one.</summary>
    public static Enemy Spawn(string name, Vector3 at, string title = null, string family = null)
    {
        var kind = Load(name);
        if (kind == null) return null;
        var prefab = MiniBoss.Find(kind.Base) ?? MiniBoss.Find(kind.Base + "_Tut");
        if (!prefab) { NetLog.Warn($"Monsters: the game's {kind.Base} isn't loaded here", toast: true); return null; }
        if (NavMesh.SamplePosition(at, out var hit, 3f, NavMesh.AllAreas)) at = hit.position;
        // a boss (the Troll): the boss maker's (bigger, tougher, harder hitting, its name on the boss bar); else plain
        var e = kind.BossScale > 0f
            ? MiniBoss.Spawn(new MiniBoss.Look { Name = title ?? "The " + kind.Name, Family = family ?? kind.Name + " boss", Base = prefab.name, Tint = Color.white, Scale = kind.BossScale, Health = kind.BossHealth, Damage = kind.BossDamage, LootFloor = 4 }, at, null, prefab)
            : MiniBoss.SpawnPlain(null, at, null, prefab);
        if (!e) return null;
        e.gameObject.name = $"EG_{kind.Name}";
        e.gameObject.AddComponent<Monster>().Kind = kind;
        NetLog.Info($"Monsters: a {kind.Name} at {at.x:0.0}, {at.y:0.0} ({prefab.name} underneath)", toast: false);
        return e;
    }

    // ---- how big they're drawn: all of them at once, live (/monsterscale), and the sizes measured (/sizes) ----

    static ConfigEntry<float> scaleAll, dungeonShare;
    static ConfigEntry<bool> residentsOn;

    /// <summary>All our creatures drawn this much bigger (1: the pack's pixels at the game's size; 1.15 by default).</summary>
    public static float ScaleAll => scaleAll?.Value ?? 1f;

    public static void Bind(ConfigFile config)
    {
        residentsOn = config.Bind("Monsters", "TutorialArea", false, "Our creatures (the pack's, and the troops' camps) living in the tutorial area (off: test them in the boss arena's Tiny Swords waves)");
        dungeonShare = config.Bind("Monsters", "DungeonShare", 0.4f, "Of the creature groups in the random dungeons (/dungeon, the descent), this share are ours: a squad of one colour's troops, or a pack of the pack's creatures (0: none)");
        scaleAll = config.Bind("Monsters", "Scale", 1.15f, "Our creatures (MonsterArt) drawn this much bigger (1: the pack's pixels at the game's size); /monsterscale 1.5 tries it live");
    }

    /// <summary>/monsterscale [x]: all of ours drawn x times their pack size (kept in the config).</summary>
    public static void ScaleCommand(string arg)
    {
        if (float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) && x >= 0.25f && x <= 4f)
        {
            scaleAll.Value = x;
            Chat.System($"Monsters drawn {x:0.##}x (saved). /sizes compares them with you and the game's creatures.");
        }
        else Chat.System($"Monsters are drawn {ScaleAll:0.##}x. /monsterscale 1.5 makes them half again as big (0.25 to 4).");
    }

    /// <summary>/sizes: how tall you, the game's creatures and ours near you are drawn (world units), to compare.</summary>
    public static void Sizes()
    {
        var me = LocalPlayer.Get();
        if (!me) return;
        var lines = new List<string> { $"You: {Tall(me.gameObject):0.00}" };
        var near = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None)
            .Where(x => x && !x.IsDead && (x.transform.position - me.transform.position).magnitude < 15f)
            .OrderBy(x => (x.transform.position - me.transform.position).magnitude).Take(12);
        foreach (var x in near)
        {
            var m = x.GetComponent<Monster>();
            if (m && m.Kind != null) lines.Add($"{m.Kind.Name} (ours): {m.Kind.Height * m.Kind.Size * ScaleAll:0.00}");
            else lines.Add($"{x.name.Replace("(Clone)", "").Trim()} (the game's): {Tall(x.gameObject):0.00}");
        }
        // and what else stands near that's animated: dead creatures still about, people, anything not a creature
        var seenRoots = new HashSet<Transform>(near.Select(x => x.transform));
        foreach (var x in Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None))
            if (x && x.IsDead && x.gameObject.activeInHierarchy && (x.transform.position - me.transform.position).magnitude < 8f)
            {
                seenRoots.Add(x.transform);
                var m = x.GetComponent<Monster>();
                lines.Add($"{x.name.Replace("(Clone)", "").Trim()} (dead{(m ? ", ours" : "")}, {(x.transform.position - me.transform.position).magnitude:0.0} away, drawn {x.GetComponentsInChildren<SpriteRenderer>().Count(r => r.enabled)})");
            }
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            if (!a || !a.isActiveAndEnabled) continue;
            var root = a.transform.root;
            if (seenRoots.Contains(root) || root == me.transform.root || (a.transform.position - me.transform.position).magnitude > 8f) continue;
            if (!a.GetComponentsInChildren<SpriteRenderer>().Any(r => r.enabled && r.sprite && r.bounds.size.y > 0.8f)) continue;   // (people-sized)
            seenRoots.Add(root);
            lines.Add($"{a.name} on {root.name} (not a creature, {(a.transform.position - me.transform.position).magnitude:0.0} away)");
        }
        string all = $"Sizes (tall, world units; ours at {ScaleAll:0.##}x): " + string.Join(", ", lines);
        NetLog.Info(all, toast: false);
        Chat.System(all);
    }

    /// <summary>How tall it's drawn: its biggest sprite's visible shape (a tight sprite's outline; a full one's rect),
    /// shadows and effects left out.</summary>
    static float Tall(GameObject go)
    {
        float best = 0f;
        foreach (var r in go.GetComponentsInChildren<SpriteRenderer>())
        {
            if (!r || !r.enabled || !r.sprite || r.name.ToLowerInvariant().Contains("shadow") || r.name == "EG_Look") continue;
            var v = r.sprite.vertices;
            if (v == null || v.Length == 0) continue;
            float lo = v.Min(p => p.y), hi = v.Max(p => p.y);
            best = Mathf.Max(best, (hi - lo) * Mathf.Abs(r.transform.lossyScale.y));
        }
        return best;
    }

    // ---- in the random dungeons: some of the spawn areas' groups are ours ----

    /// <summary>The pack's creatures that come in packs in the dungeons (not the bosses: the Queen Bee, the Great Bear, the Troll).</summary>
    static readonly string[] Critters =
    {
        "Bumblebee", "Pig", "Gnome", "Snake", "Giant Bat", "Slingshot Gnome", "Thief", "Spear Goblin", "Pig Rider", "Lizard",
        "Gnoll", "Imp", "Turtle", "Spider", "Paddle Shark", "Harpoon Shark", "Bomb Fish", "Hex Shaman", "Skull", "Panda", "Minotaur", "Bear",
    };
    static readonly string[] TroopColours = { "Black", "Blue", "Purple", "Red", "Yellow" };

    /// <summary>
    /// One group for a dungeon spawn area, or null (the game's own group instead: 1 - DungeonShare of the time, or no
    /// MonsterArt). Half squads: 3-5 troops of one colour (any of that colour's, never its King, at most one catapult);
    /// half packs: 2-4 of one creature, a third of the time with 1-2 of another.
    /// </summary>
    /// <summary>A dungeon whose groups lean to some of ours (Nagedra's forest: bees and gnomes): those kinds, and the
    /// share of groups that are ours there. Null: none.</summary>
    public static (string[] kinds, double share)? Favour;

    public static List<string> DungeonGroup(System.Random rng, bool always = false)
    {
        double share = Favour?.share ?? dungeonShare?.Value ?? 0f;
        if (!always && (share <= 0 || rng.NextDouble() >= share)) return null;
        var have = names ??= Names();
        if (have.Count == 0) return null;
        var list = new List<string>();
        if (rng.NextDouble() < (Favour != null ? 0.3 : 0.5))   // (a squad of troops: fewer where others are favoured, but still)
        {
            string colour = TroopColours[rng.Next(TroopColours.Length)];
            var troops = have.Where(n => n.StartsWith(colour + " ") && !n.EndsWith(" King")).ToList();
            if (troops.Count > 0)
            {
                int count = rng.Next(3, 6);
                for (int i = 0; i < count; i++)
                {
                    var t = troops[rng.Next(troops.Count)];
                    if (t.EndsWith(" Catapult") && list.Any(x => x.EndsWith(" Catapult"))) { i--; troops.Remove(t); if (troops.Count == 0) break; continue; }
                    list.Add(t);
                }
                return list;
            }
        }
        var critters = Critters.Where(have.Contains).ToList();
        if (critters.Count == 0) return null;
        var favoured = Favour != null ? Favour.Value.kinds.Where(have.Contains).ToList() : null;
        var kind = favoured != null && favoured.Count > 0 && rng.NextDouble() < 0.7 ? favoured[rng.Next(favoured.Count)] : critters[rng.Next(critters.Count)];
        for (int i = rng.Next(2, 5); i > 0; i--) list.Add(kind);
        if (rng.NextDouble() < 0.33)
        {
            var other = critters[rng.Next(critters.Count)];
            for (int i = rng.Next(1, 3); i > 0; i--) list.Add(other);
        }
        return list;
    }

    static List<string> names;

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
        ("Pig Rider", new Vector3(-27.5f, 63.0f, 0f)),   // (with the goblins)
        ("Pig", new Vector3(-29.0f, 64.5f, 0f)),   // (the goblins' pig, by its rider)
        ("Gnoll", new Vector3(-22.0f, 62.5f, 0f)),   // (on the way to the Imp)
        ("Skull", new Vector3(-21.0f, 59.5f, 0f)),   // (by the Minotaur)
        ("Hex Shaman", new Vector3(-15.0f, 63.0f, 0f)),   // (by the Panda)
        ("Bear", new Vector3(-16.5f, 66.5f, 0f)),   // (on the way to the Troll)
        ("Great Bear", new Vector3(-17.5f, 70.0f, 0f)),   // (a boss, short of the Troll)
    };
    // the troops in colours, a camp each: spread out from its middle onto walkable ground
    internal static readonly string[] Colours = { "Black", "Blue", "Purple", "Red", "Yellow" };
    static bool Troop(string name, params string[] kinds) =>
        Colours.Any(c => name.StartsWith(c + " ")) && kinds.Any(k => name.Substring(name.IndexOf(' ') + 1) == k);
    static readonly (string camp, System.Func<string, bool> who, Vector3 middle)[] Camps =
    {
        ("the Knights", n => Troop(n, "Warrior", "Archer", "Pawn"), new Vector3(-40.0f, 60.5f, 0f)),
        ("the Goblins", n => Troop(n, "Torch Goblin", "TNT Goblin", "Barrel Goblin"), new Vector3(-26.0f, 64.5f, 0f)),
        ("the Free Pack's", n => Troop(n, "Knight", "Bowman", "Lancer", "Monk", "Peasant"), new Vector3(-44.0f, 65.5f, 0f)),
        ("the Warfare army", n => Troop(n, "Berserker", "Horseman", "King", "Swordsman", "Catapult"), new Vector3(-20.0f, 68.0f, 0f)),
    };
    const float CampGap = 1.6f;

    /// <summary>That many spots around the middle, CampGap apart, each on walkable ground (rings outward).</summary>
    static List<Vector3> Spots(Vector3 middle, int count)
    {
        var spots = new List<Vector3>();
        for (int ring = 0; ring <= 14 && spots.Count < count; ring++)
            for (int x = -ring; x <= ring && spots.Count < count; x++)
                for (int y = -ring; y <= ring && spots.Count < count; y++)
                {
                    if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) != ring) continue;   // (this ring's edge only)
                    var p = middle + new Vector3(x * CampGap, y * CampGap, 0f);
                    if (NavMesh.SamplePosition(p, out var hit, 0.4f, NavMesh.AllAreas) && spots.All(o => (o - hit.position).magnitude > CampGap * 0.8f))
                        spots.Add(hit.position);
                }
        return spots;
    }

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
        if (residentsOn is not { Value: true }) return;   // (off: none in the tutorial area; the arena's waves have them)
        if (MiniBoss.Plugin != null && MiniBoss.Plugin.IsClient) return;   // (the host's world has them)
        if (!LocalPlayer.Get()) { spawnAt = Time.unscaledTime + 1f; return; }
        foreach (var (name, at) in Residents)
        {
            if (residents.TryGetValue(name, out var e) && e && !e.IsDead) continue;
            residents[name] = Spawn(name, at);
        }
        var have = Names();
        foreach (var (camp, who, middle) in Camps)
        {
            var names = have.Where(who).ToList();
            var spots = Spots(middle, names.Count);
            for (int i = 0; i < names.Count && i < spots.Count; i++)
            {
                if (residents.TryGetValue(names[i], out var e) && e && !e.IsDead) continue;
                residents[names[i]] = Spawn(names[i], spots[i]);
            }
            NetLog.Info($"Monsters: {camp} camp: {Mathf.Min(names.Count, spots.Count)} of {names.Count} around {middle.x:0.0}, {middle.y:0.0}", toast: false);
        }
    }
}


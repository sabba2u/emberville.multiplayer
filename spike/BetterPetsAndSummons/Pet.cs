using System.Collections.Generic;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using UnityEngine.AI;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace BetterPetsAndSummons;

/// <summary>One kind of pet: a creature of the game (its prefab name) and how it moves.</summary>
public sealed class PetKind
{
    public readonly string Id, Label, Prefab, DefaultName;
    public readonly bool Flies;
    public PetKind(string id, string label, string prefab, string defaultName, bool flies = false)
    { Id = id; Label = label; Prefab = prefab; DefaultName = defaultName; Flies = flies; }
}

/// <summary>
/// Pets (/pet). The demo has no dog or cat; these are its small creatures with full animation sets: the chicken, the
/// Glowbat (flies), the Lilshrooms, Lil Ticks and Lil Bombers (three colours each), the kamikaze spiderling and the
/// bug fuzz. (The crow and the magic wisp aren't creatures, just scenery and an effect.)
///
/// The body is a copy of the creature with every game script, collider and rigidbody stripped (like our player
/// stand-ins): no AI, nothing can hit it, so it's immune to everyone. We move it ourselves and drive its own animator
/// (Speed: walk / idle, DirFactor: up / down / side, Attack; left = the "graphics" child turned 180° on Y, as
/// Enemy.IsFlipped does). Flyers hover above the ground with a gentle bob. A kind can only be made once the game has
/// that creature loaded (its prefab, or one in the scene): from then on our stripped template of it is kept for good.
///
/// Brain (ours only): follows a little behind us, catches up when far. An enemy close to us (or to it) gets attacked:
/// the hit goes through CombatSystem.Resolve with our player as the aggressor, so it counts like one of our hits
/// (EnemySync makes it a request to the creature's runner; a kill gives us the XP). XP from its hits and kills, levels
/// (more damage). Saved in the config: kind, name, colour, level, XP, out or not (one pet: level and name stay when
/// you switch kinds).
///
/// Others see it (with the multiplayer mod): its player state says "kind, level, name, colour" (KindByte, Level, Name,
/// Tint), and their copy runs a follower behind our stand-in (its RemotePets).
/// </summary>
public static class Pet
{
    public static readonly PetKind[] Kinds =
    {
        new PetKind("chicken", "Chicken", "Mob_Chicken", "Clucky"),
        new PetKind("glowbat", "Glowbat", "Mob_Glowbat", "Fang", flies: true),
        new PetKind("pinkshroom", "Pink Lilshroom", "Mob_LilShroom_Pink", "Sporey"),
        new PetKind("greenshroom", "Green Lilshroom", "Mob_LilShroom_Green", "Mossy"),
        new PetKind("grayshroom", "Gray Lilshroom", "Mob_LilShroom_Gray", "Pebble"),
        new PetKind("bluetick", "Blue Lil Tick", "Mob_LilTick_Blue", "Blueberry"),
        new PetKind("greentick", "Green Lil Tick", "Mob_LilTick_Green", "Pickle"),
        new PetKind("purpletick", "Purple Lil Tick", "Mob_LilTick_Purple", "Grape"),
        new PetKind("redbomber", "Red Lil Bomber", "Mob_LilBomber_Red", "Boom"),
        new PetKind("greenbomber", "Green Lil Bomber", "Mob_LilBomber_DarkGreen", "Fuse"),
        new PetKind("salmonbomber", "Salmon Lil Bomber", "Mob_LilBomber_Salmon", "Kaboom"),
        new PetKind("spiderling", "Spiderling", "Mob_Kamikaze", "Webby"),
        new PetKind("bugfuzz", "Bug Fuzz", "Mob_BugFuzz", "Fuzzy"),
    };

    internal static ConfigEntry<bool> enabled, owned, outNow;
    internal static ConfigEntry<string> petName, petColor, petKind;
    internal static ConfigEntry<int> level, xp;

    public const float FollowGap = 1.1f, WalkSpeed = 2.4f, RunSpeed = 5.5f, TeleportDistance = 12f;
    const float AggroRange = 3.5f, PeckRange = 0.8f, PeckCooldown = 1.1f;

    static PetBody mine;
    static Enemy target;
    static float nextPeck, nextLook, lastHitAt;
    static Enemy lastHit;
    static bool killCounted;

    public static void Bind(ConfigFile config)
    {
        enabled = config.Bind("Pet", "Enabled", false, "Pets on (an early feature, off to start with: /pet on or the Insert menu)");
        owned = config.Bind("Pet", "Owned", false, "You have a pet");
        outNow = config.Bind("Pet", "Out", true, "Your pet is out with you");
        petKind = config.Bind("Pet", "Kind", "chicken", "Which creature your pet is (/pet list)");
        petName = config.Bind("Pet", "Name", "Clucky", "Your pet's name (each kind has a default: Clucky the chicken, Fang the Glowbat...)");
        level = config.Bind("Pet", "Level", 1, "Your pet's level");
        xp = config.Bind("Pet", "Xp", 0, "Your pet's XP towards the next level");
        petColor = config.Bind("Pet", "Color", "FFFFFF", "Your pet's tint (hex RRGGBB; FFFFFF = its own colours)");
    }

    public static bool Out => enabled.Value && owned.Value && outNow.Value;
    public static string Name => petName.Value;
    public static int Level => Mathf.Max(1, level.Value);
    internal static int XpNeeded => 15 * Level;
    static int Damage => 2 + Level;

    public static PetKind Kind => KindOf(petKind.Value) ?? Kinds[0];
    /// <summary>On the wire: 0 = no pet, else index + 1.</summary>
    public static byte KindByte => (byte)(Out ? System.Array.IndexOf(Kinds, Kind) + 1 : 0);
    public static PetKind KindFromByte(byte b) => b > 0 && b <= Kinds.Length ? Kinds[b - 1] : null;

    static PetKind KindOf(string id)
    {
        id = (id ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        foreach (var k in Kinds) if (k.Id == id || k.Label.ToLowerInvariant().Replace(" ", "") == id) return k;
        return null;
    }

    /// <summary>Our pet's tint (white = its own colours), sent with our player state.</summary>
    public static Color Tint
    {
        get => ColorUtility.TryParseHtmlString("#" + petColor.Value, out var c) ? c : Color.white;
        set => petColor.Value = ColorUtility.ToHtmlStringRGB(value);
    }

    internal static readonly (string name, string hex)[] Colours =
    {
        ("white", "FFFFFF"), ("red", "FF6B6B"), ("orange", "FFA94D"), ("yellow", "FFE066"), ("green", "8CE99A"),
        ("blue", "74C0FC"), ("purple", "C9A0FF"), ("pink", "FFA8D8"), ("gray", "9E9E9E"), ("black", "4A4A4A"), ("gold", "FFD43B"),
    };

    public static string ColourNames => string.Join(", ", System.Array.ConvertAll(Colours, c => c.name));

    // ---- /pet ----

    public static void Command(string args)
    {
        args = args.Trim();
        if (args == "on" || args == "off") { SetEnabled(args == "on"); return; }
        if (!enabled.Value) { Chat.System("Pets are off: /pet on (or the Insert menu) turns them on."); return; }
        if (args.StartsWith("color") || args.StartsWith("colour"))
        {
            string want = args.Substring(args.StartsWith("color") ? 5 : 6).Trim().TrimStart('#').ToLowerInvariant();
            string hex = null;
            foreach (var (n, h) in Colours) if (n == want) hex = h;
            if (hex == null && want.Length == 6 && ColorUtility.TryParseHtmlString("#" + want, out _)) hex = want.ToUpperInvariant();
            if (hex == null) { Chat.System($"/pet color <{ColourNames}> or /pet color #RRGGBB"); return; }
            petColor.Value = hex;
            Chat.System($"{Name} is {(want.Length == 6 ? "#" + hex : want)} now.");
            return;
        }
        if (args.StartsWith("name "))
        {
            string n = args.Substring(5).Trim();
            if (n.Length > 0) { petName.Value = n.Length > 20 ? n.Substring(0, 20) : n; Chat.System($"Your pet is now called {petName.Value}."); }
            return;
        }
        if (args == "scan")
        {
            // Which creature prefabs the game has in memory right now (spawn groups, the world map's spawn tables,
            // the dev spawn panel, creatures in the scene pull them in): what we could make a pet from here.
            var prefabs = new SortedSet<string>();
            int inScene = 0;
            foreach (var e in Resources.FindObjectsOfTypeAll<Enemy>())
            {
                if (!e) continue;
                if (e.gameObject.scene.IsValid()) inScene++;
                else prefabs.Add(e.gameObject.name);
            }
            int groups = Resources.FindObjectsOfTypeAll<EnemySpawnGroup>().Length;
            Chat.System($"In memory: {prefabs.Count} creature prefabs, {inScene} creatures in the scene, {groups} spawn groups (full list in the log).");
            NetLog.Info("Creature prefabs in memory: " + string.Join(", ", prefabs));
            return;
        }
        if (args.StartsWith("new "))   // a fresh pet of this kind: its own name, its own colours, level 1 (an NPC's gift)
        {
            var fresh = KindOf(args.Substring(4).Trim());
            if (fresh == null) { Chat.System($"No pet called \"{args.Substring(4).Trim()}\". /pet list shows them."); return; }
            owned.Value = false;   // (so Switch names it as a first pet)
            petName.Value = fresh.DefaultName;
            petColor.Value = (string)petColor.DefaultValue;
            level.Value = 1;       // and it starts out at level 1
            xp.Value = 0;
            Switch(fresh);
            return;
        }
        if (args == "release")
        {
            if (!owned.Value) { Chat.System("You don't have a pet."); return; }
            string was = Name;
            owned.Value = false;
            outNow.Value = false;
            Despawn();
            Chat.System($"You said goodbye to {was}.");
            return;
        }
        if (args == "list")
        {
            var parts = new List<string>();
            foreach (var k in Kinds) parts.Add(k.Id + (PetBody.Available(k) ? "" : " (not seen yet)"));
            Chat.System("Pets: " + string.Join(", ", parts) + ". /pet <kind> to switch, /pet scan: what's loaded here.");
            return;
        }
        if (args.Length > 0)
        {
            var kind = KindOf(args);
            if (kind == null) { Chat.System($"No pet called \"{args}\". /pet list shows them."); return; }
            Switch(kind);
            return;
        }
        if (!owned.Value) { Switch(Kind); return; }
        outNow.Value = !outNow.Value;
        Chat.System(outNow.Value ? $"{Name} is coming along." : $"{Name} went home.");
    }

    /// <summary>Befriend (first time) or switch to this kind.</summary>
    internal static void Switch(PetKind kind)
    {
        if (!PetBody.Available(kind) && !PetBody.Learn(kind))   // (not in memory: loaded for this one)
        { Chat.System($"No {kind.Label} anywhere to befriend yet: get near one once (it has to be loaded), then try again."); return; }
        bool first = !owned.Value;
        bool defaultName = first || System.Array.Exists(Kinds, k => k.DefaultName == petName.Value);
        petKind.Value = kind.Id;
        if (defaultName) petName.Value = kind.DefaultName;   // a name you picked yourself stays
        owned.Value = true;
        outNow.Value = true;
        Despawn();   // the new body comes next frame
        Chat.System(first
            ? $"You befriended a {kind.Label}! Say hi to {Name}. (/pet: send home / call, /pet name, /pet color, /pet list)"
            : $"{Name} is a {kind.Label} now.");
    }

    /// <summary>Insert menu: befriend / out, kind, colour.</summary>
    internal static void SetEnabled(bool on)
    {
        enabled.Value = on;
        Chat.System(on ? "Pets on: /pet to befriend one or call yours." : "Pets off.");
    }

    public static void DrawMenu()
    {
        ImGui.Separator();
        if (ImGui.SmallButton("Give me the Staff of Clucking")) CluckStaff.Give();
        ImGui.SameLine();
        if (ImGui.SmallButton("Give me the Staff of the Risen")) RisenStaff.Give();
        ImGui.Separator();
        bool on = enabled.Value;
        if (ImGui.Checkbox("Pets (early feature)", ref on)) SetEnabled(on);
        if (!enabled.Value) return;
        if (owned.Value)
        {
            bool isOut = outNow.Value;
            if (ImGui.Checkbox($"Pet out (level {Level}, {xp.Value}/{XpNeeded} XP)", ref isOut)) outNow.Value = isOut;
            ImGui.SameLine();
            string name = petName.Value;
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputTextWithHint("Name##pet", Kind.DefaultName, ref name, 20))
                petName.Value = string.IsNullOrWhiteSpace(name) ? Kind.DefaultName : name;
            var c = Tint;
            var col = new ImGuiNET.Vector3(c.r, c.g, c.b);
            ImGui.SameLine();
            if (ImGui.ColorEdit3("Colour##pet", ref col, ImGuiColorEditFlags.NoInputs)) Tint = new Color(col.x, col.y, col.z, 1f);
            ImGui.SameLine();
            if (ImGui.SmallButton("Own colours")) Tint = Color.white;
        }
        else ImGui.Text("Pet: pick one to befriend (it has to have been loaded once: be near one)");

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("Kind##pet", owned.Value ? Kind.Label : "choose..."))
        {
            foreach (var k in Kinds)
            {
                bool can = PetBody.Available(k);
                if (!can) ImGui.BeginDisabled();
                if (ImGui.Selectable(k.Label + (k.Flies ? "  (flies)" : "") + (can ? "" : "  (not seen yet)"), owned.Value && k == Kind)) Switch(k);
                if (!can) ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
    }

    // ---- every frame ----

    static string learnedIn;

    /// <summary>
    /// Once per scene: every pet kind whose creature the game has in memory now (a prefab: wild creatures come along
    /// with the world map's spawn tables; or one standing in the scene) is stripped into our template and kept for the
    /// session, so it's there later wherever we are. (They can't be loaded on demand: not addressable, not Resources.)
    /// </summary>
    static void Learn()
    {
        if (learnedIn == LocalPlayer.Scene || !LocalPlayer.Get()) return;
        learnedIn = LocalPlayer.Scene;
        int n = 0;
        foreach (var k in Kinds) if (PetBody.Learn(k, load: false)) n++;   // (only what's here: loading them all would stall the game)
        if (n > 0) NetLog.Info($"Pets: {n} new kind{(n == 1 ? "" : "s")} available here");
    }

    public static void Tick()
    {
        if (enabled.Value) Learn();
        var me = LocalPlayer.Get();
        if (!Out || !me || me.IsDead && mine == null) { Despawn(); return; }
        if (mine == null || !mine.Alive || mine.Kind != Kind)
        {
            mine?.Destroy();
            mine = PetBody.Create(Kind, me.transform.position + Behind(me, FollowGap));
            if (mine == null) return;
        }
        mine.SetTint(Tint);

        float dt = Time.deltaTime;
        if (UnityEngine.Vector2.Distance(Flat(mine.Position), Flat(me.transform.position)) > TeleportDistance)
            mine.Teleport(me.transform.position + Behind(me, FollowGap));

        if (Time.unscaledTime >= nextLook) { nextLook = Time.unscaledTime + 0.25f; target = PickTarget(me); }
        if (target && (!target.isActiveAndEnabled || target.IsDead)) target = null;

        if (target && !me.IsDead)
        {
            var at = target.transform.position;
            if (UnityEngine.Vector2.Distance(Flat(mine.Position), Flat(at)) > PeckRange) mine.MoveToward(at, RunSpeed, dt, PeckRange * 0.9f);
            else
            {
                mine.Stop(at);
                if (Time.time >= nextPeck) { nextPeck = Time.time + PeckCooldown; Peck(me, target); }
            }
        }
        else
        {
            var spot = me.transform.position + Behind(me, FollowGap);
            float far = UnityEngine.Vector2.Distance(Flat(mine.Position), Flat(spot));
            if (far > 0.35f) mine.MoveToward(spot, far > 3f ? RunSpeed : WalkSpeed, dt, 0.2f);
            else mine.Stop(me.transform.position);
        }
        mine.Hover();

        // A kill shortly after our attack (on a creature someone else runs, the death arrives a moment later).
        if (lastHit && !killCounted && lastHit.IsDead && Time.unscaledTime - lastHitAt < 3f) { killCounted = true; GainXp(10); }
    }

    static Enemy PickTarget(PlayerMaster me)
    {
        Enemy best = null;
        float bestD = float.MaxValue;
        foreach (var e in EnemiesManager.AliveEnemies)
        {
            if (!e || !e.isActiveAndEnabled || e.IsDead || !e.CanTakeDamage || EnemySync.LocalOnly(e)) continue;
            float dMe = UnityEngine.Vector2.Distance(Flat(e.transform.position), Flat(me.transform.position));
            float dPet = UnityEngine.Vector2.Distance(Flat(e.transform.position), Flat(mine.Position));
            if (dMe > AggroRange && dPet > AggroRange * 0.6f) continue;
            if (dMe < bestD) { bestD = dMe; best = e; }
        }
        return best;
    }

    /// <summary>An attack: the game's own hit, with our player as the one hitting (so it counts as ours everywhere).</summary>
    static void Peck(PlayerMaster me, Enemy e)
    {
        mine.Attack();
        var d = new DamageData(me, e, Damage, DamageHitType.Impact, KnockBack.Low);
        CombatSystem.Resolve(d);
        if (lastHit != e) { lastHit = e; killCounted = false; }
        lastHitAt = Time.unscaledTime;
        GainXp(1);
    }

    static void GainXp(int amount)
    {
        xp.Value += amount;
        while (xp.Value >= XpNeeded)
        {
            xp.Value -= XpNeeded;
            level.Value = Level + 1;
            Chat.System($"{Name} reached level {Level}! (hits for {Damage} now)");
            if (mine != null && mine.Alive) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Level up!", mine.Position, DamageResult.Critical));
        }
    }

    static void Despawn()
    {
        mine?.Destroy();
        mine = null;
        target = null;
    }

    /// <summary>Behind us, a little to the side, on the ground.</summary>
    static Vector3 Behind(PlayerMaster me, float gap) => new Vector3(me.isFliped ? gap : -gap, -0.25f, 0f);

    static UnityEngine.Vector2 Flat(Vector3 v) => new UnityEngine.Vector2(v.x, v.y);

    // ---- name tags ----

    public static void DrawLabels()
    {
        var cam = Camera.main;
        if (!cam) return;
        if (mine != null && mine.Alive) Label(cam, mine.Position, $"{Name}  Lv{Level}", xp.Value / (float)XpNeeded);
    }

    /// <summary>A pet's name tag (and XP bar, if xpFrac >= 0) under it; the multiplayer mod uses it for teammates' pets too.</summary>
    public static void Label(Camera cam, Vector3 at, string text, float xpFrac)
    {
        var s = cam.WorldToScreenPoint(at);
        float x = s.x, y = Screen.height - s.y + 4f;
        var dl = ImGui.GetBackgroundDrawList();
        var size = ImGui.CalcTextSize(text);
        dl.AddRectFilled(new Vector2(x - size.x / 2 - 4, y - 1), new Vector2(x + size.x / 2 + 4, y + size.y + 1), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.5f)), 3f);
        dl.AddText(new Vector2(x - size.x / 2, y), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.9f, 0.7f, 1f)), text);
        if (xpFrac < 0f) return;
        float w = size.x, by = y + size.y + 2;
        dl.AddRectFilled(new Vector2(x - w / 2, by), new Vector2(x + w / 2, by + 3), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.6f)));
        dl.AddRectFilled(new Vector2(x - w / 2, by), new Vector2(x - w / 2 + w * Mathf.Clamp01(xpFrac), by + 3), ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.8f, 0.3f, 1f)));
    }
}

/// <summary>A creature's body without its brain: the animator and sprites, moved by us. Position = its spot on the ground.</summary>
public sealed class PetBody
{
    sealed class Template { public GameObject Go; public string GraphicsPath; }
    static readonly Dictionary<string, Template> templates = new Dictionary<string, Template>();

    const float HoverHeight = 0.9f, BobHeight = 0.12f;

    public PetKind Kind { get; private set; }
    GameObject root;
    Transform graphics;
    Animator[] animators;
    Vector3 graphicsLocal;
    bool flipped;
    float bobPhase;

    public bool Alive => root;
    public GameObject Root => root;
    public Vector3 Position => root.transform.position;

    /// <summary>Can we make this kind (we have its template, or the game has the creature loaded now)?</summary>
    /// <summary>One we have, or one in memory now (looking only: nothing is loaded for this).</summary>
    public static bool Available(PetKind kind) => templates.TryGetValue(kind.Prefab, out var t) && t.Go || Source(kind, load: false);

    /// <summary>The creature as the game has it: its prefab if loaded, else one in the scene.</summary>
    /// <summary>
    /// "Mob_Chicken" is the scene's "Chicken" (or "Chicken_01", "Chicken (2)", "Chicken(Clone)"): names compared without the
    /// Mob_ prefix, a copy number or "(Clone)".
    /// </summary>
    static bool SameCreature(string name, string wanted)
    {
        string Norm(string n)
        {
            n = n.Replace("(Clone)", "").Trim();
            int sp = n.IndexOf(" (", System.StringComparison.Ordinal);
            if (sp > 0) n = n.Substring(0, sp);
            if (n.StartsWith("Mob_", System.StringComparison.OrdinalIgnoreCase)) n = n.Substring(4);
            int u = n.LastIndexOf('_');
            if (u > 0 && int.TryParse(n.Substring(u + 1), out _)) n = n.Substring(0, u);
            return n.ToLowerInvariant();
        }
        return name == wanted || Norm(name) == Norm(wanted);
    }

    static Enemy Source(PetKind kind, bool load = true)
    {
        var found = Search(kind);
        if (found || !load) return found;   // a prefab, or one in the scene (its faded-out death look is undone in TemplateFor)
        // none in memory at all: the biomes (Resources "AllBiomes") carry the chicken, glowbat and tick prefabs with
        // them in any scene, so load them and look again (a synchronous load: a short hitch, only in this case)
        Dungeon.AllSettings();
        var again = Search(kind);
        return again ? again : found;
    }

    static Enemy Search(PetKind kind)
    {
        // best a prefab; else one alive in the scene; else any (a corpse)
        Enemy prefab = null, alive = null, any = null;
        foreach (var e in Resources.FindObjectsOfTypeAll<Enemy>())
        {
            if (!e || !SameCreature(e.gameObject.name, kind.Prefab)) continue;
            if (!e.gameObject.scene.IsValid()) { prefab = e; break; }
            if (!e.IsDead && e.gameObject.activeInHierarchy) alive ??= e;
            any ??= e;
        }
        return prefab ? prefab : alive ? alive : any;
    }

    /// <summary>Make this kind's template now if the game has the creature loaded. True = newly learned.</summary>
    /// <summary>Make this kind's template now. load = false: only from what's in memory already (no loading hitch:
    /// the background check of every kind); true: the biomes are loaded for it if need be (someone asked for this one).</summary>
    public static bool Learn(PetKind kind, bool load = true)
    {
        if (templates.TryGetValue(kind.Prefab, out var t) && t.Go) return false;
        return TemplateFor(kind, load) != null;
    }

    /// <summary>Strip the creature into our template for this kind (once; kept for good).</summary>
    static Template TemplateFor(PetKind kind, bool load = true)
    {
        if (templates.TryGetValue(kind.Prefab, out var t) && t.Go) return t;
        var source = Source(kind, load);
        if (!source) return null;
        var holder = new GameObject("EmbervilleGhost_PetTemplate_" + kind.Id);
        holder.SetActive(false);   // nothing in the copy wakes up (no AI registers itself)
        Object.DontDestroyOnLoad(holder);
        holder.hideFlags = HideFlags.HideAndDontSave;
        var copy = Object.Instantiate(source.gameObject, holder.transform);
        copy.name = "EmbervilleGhost_Pet_" + kind.Id;
        string path = RemoteAvatar.PathFrom(source.transform, source.graphics);
        RemoteAvatar.Strip(copy, null);
        copy.transform.localScale = source.transform.localScale;
        if (source.gameObject.scene.IsValid()) Unfade(source, copy);
        templates[kind.Prefab] = t = new Template { Go = copy, GraphicsPath = path };
        return t;
    }

    /// <summary>A creature copied from the scene may be a corpse: the game fades a dead one's sprites out (Enemy.FadeSprites),
    /// and a copy keeps that (invisible). Put back the alpha each fading sprite had when the creature woke up.</summary>
    static void Unfade(Enemy source, GameObject copy)
    {
        var renderers = source.fadableRenderers;
        var alphas = source.renderersAlphas;
        if (renderers == null || alphas == null) return;
        for (int i = 0; i < renderers.Length && i < alphas.Length; i++)
        {
            var path = RemoteAvatar.PathFrom(source.transform, renderers[i] ? renderers[i].transform : null);
            var t = path == null ? null : path.Length == 0 ? copy.transform : copy.transform.Find(path);
            var sr = t ? t.GetComponent<SpriteRenderer>() : null;
            if (!sr) continue;
            var c = sr.color;
            c.a = alphas[i];
            sr.color = c;
        }
    }

    public static PetBody Create(PetKind kind, Vector3 at)
    {
        var t = TemplateFor(kind);
        if (t == null) return null;
        var go = Object.Instantiate(t.Go);   // no parent: in the current scene, active (nothing left to wake)
        go.name = "EmbervilleGhost_Pet_" + kind.Id;
        go.transform.position = at;
        var body = new PetBody { root = go, Kind = kind, bobPhase = Random.value * 6f };
        body.graphics = string.IsNullOrEmpty(t.GraphicsPath) ? null : go.transform.Find(t.GraphicsPath);
        if (body.graphics) body.graphicsLocal = body.graphics.localPosition;
        body.animators = go.GetComponentsInChildren<Animator>(true);
        foreach (var a in body.animators)
        {
            if (!a) continue;
            a.fireEvents = false;
            PuppetStateBehaviours.Puppets.Add(a);
        }
        body.Hover();
        return body;
    }

    public void MoveToward(Vector3 to, float speed, float dt, float stopAt)
    {
        var from = root.transform.position;
        var d = new UnityEngine.Vector2(to.x - from.x, to.y - from.y);
        float dist = d.magnitude;
        if (dist <= stopAt) { Stop(to); return; }
        if (Route(from, to)) { FollowRoute(from, to, speed, dt); return; }
        // no walkable map here (or we're off it): straight there, stopped by walls
        float step = Mathf.Min(speed * dt, dist - stopAt);
        var dir = d / dist;
        var next = from + new Vector3(dir.x, dir.y, 0f) * step;
        if (Blocks(next) && !Blocks(from))   // (one a door closed on, or stuck in a wall, can still walk out)
        {
            // a wall, water or a closed door: slide along it (one axis at a time), or wait there (far behind, we catch up by teleport)
            var alongX = from + new Vector3(dir.x * step, 0f, 0f);
            var alongY = from + new Vector3(0f, dir.y * step, 0f);
            if (Mathf.Abs(dir.x) > 0.1f && !Blocks(alongX)) next = alongX;
            else if (Mathf.Abs(dir.y) > 0.1f && !Blocks(alongY)) next = alongY;
            else { Stop(to); return; }
        }
        root.transform.position = next;
        Face(dir);
        Set("Speed", speed);
    }

    static bool Blocks(Vector3 at) => DoorBlocks(at) || WallBlocks(at);

    // ---- the way there: the walkable map the game's creatures walk on (its navmesh), so cliffs, pits and water are
    // walked around; a spot that can't be reached is walked to as near as it gets ----

    readonly NavMeshPath path = new NavMeshPath();
    Vector3[] corners = new Vector3[0];
    int corner;
    Vector3 pathTo;
    float pathAt = -10f;
    bool hasPath;

    static int agentType;
    static string agentScene;

    static NavMeshQueryFilter Filter()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (scene != agentScene)
        {
            var agent = Object.FindAnyObjectByType<NavMeshAgent>();
            if (!agent) return new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };   // (no creatures yet: look again)
            agentScene = scene;
            agentType = agent.agentTypeID;
            Plugin.Logger_.LogInfo($"Pets: walking the creatures' walkable map in {scene} (agent type {agentType}, from {agent.name})");
        }
        return new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };
    }

    /// <summary>A route to there (worked out again as it moves, a few times a second). False: no walkable map here.</summary>
    bool Route(Vector3 from, Vector3 to)
    {
        if (Time.time - pathAt < 0.4f && ((UnityEngine.Vector2)(to - pathTo)).sqrMagnitude < 0.25f) return hasPath;
        pathAt = Time.time;
        pathTo = to;
        hasPath = false;
        var filter = Filter();
        if (!NavMesh.SamplePosition(new Vector3(from.x, from.y, 0f), out var start, 1f, filter)) return false;
        if (!NavMesh.SamplePosition(new Vector3(to.x, to.y, 0f), out var end, 6f, filter)) return false;
        if (!NavMesh.CalculatePath(start.position, end.position, filter, path) || path.status == NavMeshPathStatus.PathInvalid) return false;
        corners = path.corners;
        corner = corners.Length > 1 ? 1 : corners.Length;
        hasPath = true;
        return true;
    }

    void FollowRoute(Vector3 from, Vector3 to, float speed, float dt)
    {
        var pos = from;
        float left = speed * dt;
        var moved = UnityEngine.Vector2.zero;
        while (corner < corners.Length && left > 0f)
        {
            var c = corners[corner];
            var d = new UnityEngine.Vector2(c.x - pos.x, c.y - pos.y);
            float m = d.magnitude;
            if (m <= left) { pos.x = c.x; pos.y = c.y; left -= m; corner++; moved += d; }
            else { var s = d / m * left; pos.x += s.x; pos.y += s.y; moved += s; left = 0f; }
        }
        if (moved.sqrMagnitude < 1e-8f || DoorBlocks(pos) && !DoorBlocks(from)) { Stop(to); return; }   // (as near as it gets, or a closed door)
        root.transform.position = pos;
        Face(moved.normalized);
        Set("Speed", speed);
    }

    // ---- walls and water stop pets: whatever would stop the player there (the layers their body collides with) ----

    const float WallRadius = 0.2f;
    static int wallMask;
    static float maskAt = -1f;
    static bool loggedMask;

    static bool WallBlocks(Vector3 at)
    {
        if (Time.unscaledTime >= maskAt)
        {
            maskAt = Time.unscaledTime + 2f;
            var me = Singleton<GameManager>.HasMain ? Singleton<GameManager>.main.player : null;
            var col = me && me.rb ? System.Linq.Enumerable.FirstOrDefault(me.rb.GetComponents<Collider2D>(), c => c && !c.isTrigger) : null;
            wallMask = me ? Physics2D.GetLayerCollisionMask(col ? col.gameObject.layer : me.gameObject.layer) : 0;
            if (wallMask != 0 && !loggedMask)
            {
                loggedMask = true;
                var names = new List<string>();
                for (int l = 0; l < 32; l++) if ((wallMask & (1 << l)) != 0) names.Add(LayerMask.LayerToName(l));
                Plugin.Logger_.LogInfo($"Pets: walls are what the player bumps into, layers {string.Join(", ", names)}");
            }
        }
        if (wallMask == 0) return false;
        foreach (var c in Physics2D.OverlapCircleAll(new UnityEngine.Vector2(at.x, at.y), WallRadius, wallMask))
        {
            if (!c || c.isTrigger) continue;
            bool mapWall = c is UnityEngine.Tilemaps.TilemapCollider2D || c is CompositeCollider2D;   // (the map's walls, on whatever body the map hangs them)
            if (!mapWall && c.attachedRigidbody && c.attachedRigidbody.bodyType != RigidbodyType2D.Static) continue;   // (creatures, players, pets: things that move)
            if (c.GetComponentInParent<Enemy>() || c.GetComponentInParent<PlayerMaster>()) continue;
            if (c.transform.root.name.StartsWith("EmbervilleGhost_Pet_")) continue;   // (a pet, chicken or risen: our copies)
            return true;
        }
        return false;
    }

    // ---- closed doors stop pets (GateDoor turns its closedColliders on while it's shut) ----

    static readonly List<Collider2D> closedDoors = new List<Collider2D>();
    static float doorsAt = -1f;
    const float DoorRadius = 0.3f;

    static bool DoorBlocks(Vector3 at)
    {
        if (Time.unscaledTime >= doorsAt)
        {
            doorsAt = Time.unscaledTime + 0.5f;
            closedDoors.Clear();
            foreach (var door in Object.FindObjectsByType<GateDoor>(FindObjectsSortMode.None))
            {
                if (!door || door.state == DoorState.Opened || !door.closedColliders || !door.closedColliders.activeInHierarchy) continue;
                foreach (var c in door.closedColliders.GetComponentsInChildren<Collider2D>())
                    if (c && c.enabled && !c.isTrigger) closedDoors.Add(c);
            }
        }
        var p = new UnityEngine.Vector2(at.x, at.y);
        foreach (var c in closedDoors)
            if (c && (c.ClosestPoint(p) - p).sqrMagnitude < DoorRadius * DoorRadius) return true;
        return false;
    }

    public void Stop(Vector3 lookAt)
    {
        var from = root.transform.position;
        var d = new UnityEngine.Vector2(lookAt.x - from.x, lookAt.y - from.y);
        if (d.sqrMagnitude > 0.01f) Face(d.normalized);
        Set("Speed", 0f);
    }

    public void Teleport(Vector3 to)
    {
        root.transform.position = to;
        hasPath = false;
        pathAt = -10f;
    }

    public void Attack()
    {
        foreach (var a in animators)
            if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) { a.SetInteger("AttackType", 0); a.SetTrigger("Attack"); }
    }

    /// <summary>Where the sprites sit: mirrored when facing left (Enemy.IsFlipped); flyers up in the air, bobbing.</summary>
    public void Hover()
    {
        if (!graphics) return;
        var p = graphicsLocal;
        if (flipped) p.x = -graphicsLocal.x;
        if (Kind.Flies) p.y = graphicsLocal.y + HoverHeight + Mathf.Sin(Time.time * 3f + bobPhase) * BobHeight;
        graphics.localPosition = p;
    }

    /// <summary>As Enemy.IsFlipped / FacingDirection do it.</summary>
    void Face(UnityEngine.Vector2 dir)
    {
        bool left = dir.x < -0.05f ? true : dir.x > 0.05f ? false : flipped;
        if (left != flipped && graphics)
        {
            flipped = left;
            graphics.rotation = Quaternion.Euler(0f, left ? 180f : 0f, 0f);
            Hover();
        }
        Set("DirFactor", Mathf.Abs(dir.y) > Mathf.Abs(dir.x) * 1.5f ? (dir.y > 0 ? 1f : -1f) : 0f);
    }

    void Set(string param, float value)
    {
        foreach (var a in animators)
            if (a && a.isActiveAndEnabled && a.runtimeAnimatorController) a.SetFloat(param, value);
    }

    SpriteRenderer[] sprites;
    Color[] baseColours;
    Color tint = Color.white;

    /// <summary>Tint the whole creature (its own colours times this; white = as it is). Only does work on a change.</summary>
    /// <summary>Bigger or smaller (a levelled-up summon).</summary>
    public void Scale(float s) { if (root) root.transform.localScale = Vector3.one * s; }

    public void SetTint(Color c)
    {
        c.a = 1f;
        if (c == tint && sprites != null) return;
        if (sprites == null)
        {
            sprites = root.GetComponentsInChildren<SpriteRenderer>(true);
            baseColours = new Color[sprites.Length];
            for (int i = 0; i < sprites.Length; i++) baseColours[i] = sprites[i] ? sprites[i].color : Color.white;
        }
        tint = c;
        for (int i = 0; i < sprites.Length; i++)
            if (sprites[i]) sprites[i].color = baseColours[i] * c;
    }

    public void Destroy()
    {
        if (animators != null) foreach (var a in animators) if (a) PuppetStateBehaviours.Puppets.Remove(a);
        if (root) Object.Destroy(root);
    }

    /// <summary>Gone in the creature's own gore death (its "DeathGore": the burst its kind does when hit hard), then
    /// removed once that's played. A kind without one is just gone.</summary>
    public void Burst()
    {
        if (!root) return;
        float length = 0f;
        foreach (var a in animators)
        {
            if (!a || !a.isActiveAndEnabled || !a.runtimeAnimatorController) continue;
            if (!System.Linq.Enumerable.Any(a.parameters, p => p.type == AnimatorControllerParameterType.Trigger && p.name == "DeathGore")) continue;
            a.SetFloat("Speed", 0f);
            a.SetTrigger("DeathGore");
            float clip = 0f;
            foreach (var c in a.runtimeAnimatorController.animationClips)
                if (c && c.name.IndexOf("gore", System.StringComparison.OrdinalIgnoreCase) >= 0) clip = Mathf.Max(clip, c.length);
            length = Mathf.Max(length, clip > 0f ? clip : 1.2f);
        }
        if (length <= 0f) { Destroy(); return; }
        RemoveAfter(length + 0.1f).Forget();
    }

    async Cysharp.Threading.Tasks.UniTaskVoid RemoveAfter(float seconds)
    {
        await Cysharp.Threading.Tasks.UniTask.Delay((int)(seconds * 1000f));
        Destroy();
    }
}

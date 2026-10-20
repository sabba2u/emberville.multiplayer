using System.Collections.Generic;
using BepInEx.Configuration;
using ImGuiNET;
using UnityEngine;
using Vector2 = ImGuiNET.Vector2;
using Vector4 = ImGuiNET.Vector4;
using Vector3 = UnityEngine.Vector3;

namespace EmbervilleGhost;

/// <summary>One kind of pet: a creature of the game (its prefab name) and how it moves.</summary>
internal sealed class PetKind
{
    public readonly string Id, Label, Prefab;
    public readonly bool Flies;
    public PetKind(string id, string label, string prefab, bool flies = false) { Id = id; Label = label; Prefab = prefab; Flies = flies; }
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
/// Others see it: our player state says "kind, level, name, colour"; their copy runs a follower behind our stand-in.
/// </summary>
internal static class Pet
{
    public static Plugin Plugin;

    public static readonly PetKind[] Kinds =
    {
        new PetKind("chicken", "Chicken", "Mob_Chicken"),
        new PetKind("glowbat", "Glowbat", "Mob_Glowbat", flies: true),
        new PetKind("pinkshroom", "Pink Lilshroom", "Mob_LilShroom_Pink"),
        new PetKind("greenshroom", "Green Lilshroom", "Mob_LilShroom_Green"),
        new PetKind("grayshroom", "Gray Lilshroom", "Mob_LilShroom_Gray"),
        new PetKind("bluetick", "Blue Lil Tick", "Mob_LilTick_Blue"),
        new PetKind("greentick", "Green Lil Tick", "Mob_LilTick_Green"),
        new PetKind("purpletick", "Purple Lil Tick", "Mob_LilTick_Purple"),
        new PetKind("redbomber", "Red Lil Bomber", "Mob_LilBomber_Red"),
        new PetKind("greenbomber", "Green Lil Bomber", "Mob_LilBomber_DarkGreen"),
        new PetKind("salmonbomber", "Salmon Lil Bomber", "Mob_LilBomber_Salmon"),
        new PetKind("spiderling", "Spiderling", "Mob_Kamikaze"),
        new PetKind("bugfuzz", "Bug Fuzz", "Mob_BugFuzz"),
    };

    static ConfigEntry<bool> owned, outNow;
    static ConfigEntry<string> petName, petColor, petKind;
    static ConfigEntry<int> level, xp;

    const float FollowGap = 1.1f, WalkSpeed = 2.4f, RunSpeed = 5.5f, TeleportDistance = 12f;
    const float AggroRange = 3.5f, PeckRange = 0.8f, PeckCooldown = 1.1f;

    static PetBody mine;
    static Enemy target;
    static float nextPeck, nextLook, lastHitAt;
    static Enemy lastHit;
    static bool killCounted;

    public static void Bind(ConfigFile config)
    {
        owned = config.Bind("Pet", "Owned", false, "You have a pet");
        outNow = config.Bind("Pet", "Out", true, "Your pet is out with you");
        petKind = config.Bind("Pet", "Kind", "chicken", "Which creature your pet is (/pet list)");
        petName = config.Bind("Pet", "Name", "Clucky", "Your pet's name");
        level = config.Bind("Pet", "Level", 1, "Your pet's level");
        xp = config.Bind("Pet", "Xp", 0, "Your pet's XP towards the next level");
        petColor = config.Bind("Pet", "Color", "FFFFFF", "Your pet's tint (hex RRGGBB; FFFFFF = its own colours)");
    }

    public static bool Out => owned.Value && outNow.Value;
    public static string Name => petName.Value;
    public static int Level => Mathf.Max(1, level.Value);
    static int XpNeeded => 15 * Level;
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

    static readonly (string name, string hex)[] Colours =
    {
        ("white", "FFFFFF"), ("red", "FF6B6B"), ("orange", "FFA94D"), ("yellow", "FFE066"), ("green", "8CE99A"),
        ("blue", "74C0FC"), ("purple", "C9A0FF"), ("pink", "FFA8D8"), ("gray", "9E9E9E"), ("black", "4A4A4A"), ("gold", "FFD43B"),
    };

    public static string ColourNames => string.Join(", ", System.Array.ConvertAll(Colours, c => c.name));

    // ---- /pet ----

    public static void Command(string args)
    {
        args = args.Trim();
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
    static void Switch(PetKind kind)
    {
        if (!PetBody.Available(kind))
        { Chat.System($"No {kind.Label} anywhere to befriend yet: get near one once (it has to be loaded), then try again."); return; }
        bool first = !owned.Value;
        petKind.Value = kind.Id;
        owned.Value = true;
        outNow.Value = true;
        Despawn();   // the new body comes next frame
        Chat.System(first
            ? $"You befriended a {kind.Label}! Say hi to {Name}. (/pet: send home / call, /pet name, /pet color, /pet list)"
            : $"{Name} is a {kind.Label} now.");
    }

    /// <summary>Insert menu: befriend / out, kind, colour.</summary>
    public static void DrawMenu()
    {
        ImGui.Separator();
        if (owned.Value)
        {
            bool isOut = outNow.Value;
            if (ImGui.Checkbox($"Pet: {Name} (level {Level}, {xp.Value}/{XpNeeded} XP)", ref isOut)) outNow.Value = isOut;
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
        foreach (var k in Kinds) if (PetBody.Learn(k)) n++;
        if (n > 0) NetLog.Info($"Pets: {n} new kind{(n == 1 ? "" : "s")} available here");
    }

    public static void Tick()
    {
        Learn();
        var me = LocalPlayer.Get();
        if (!Out || !me || me.IsDead && mine == null) { Despawn(); TickOthers(); return; }
        if (mine == null || !mine.Alive || mine.Kind != Kind)
        {
            mine?.Destroy();
            mine = PetBody.Create(Kind, me.transform.position + Behind(me, FollowGap));
            if (mine == null) { TickOthers(); return; }
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
        TickOthers();
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

    // ---- other players' pets (followers behind their stand-ins) ----

    static readonly Dictionary<int, PetBody> theirs = new Dictionary<int, PetBody>();
    static readonly Dictionary<int, float> retryAt = new Dictionary<int, float>();   // their kind isn't loaded here: look again later

    static void TickOthers()
    {
        var gone = new List<int>(theirs.Keys);
        if (Plugin != null && Plugin.Connected)
            foreach (var p in Plugin.Players)
            {
                var avatar = p.Avatar;
                var kind = KindFromByte(p.PetKind);
                if (kind == null || avatar == null || !avatar.Visible) continue;
                gone.Remove(p.Id);
                var owner = avatar.Root.transform.position;
                var spot = owner + new Vector3(p.Flip ? FollowGap : -FollowGap, -0.25f, 0f);
                if (!theirs.TryGetValue(p.Id, out var body) || body == null || !body.Alive || body.Kind != kind)
                {
                    if (body == null && retryAt.TryGetValue(p.Id, out float when) && Time.unscaledTime < when) continue;
                    retryAt[p.Id] = Time.unscaledTime + 2f;
                    body?.Destroy();
                    body = PetBody.Create(kind, spot);
                    theirs[p.Id] = body;
                    if (body == null) continue;   // we haven't got that creature loaded: no pet shown for now
                }
                body.SetTint(p.PetColor);
                float far = UnityEngine.Vector2.Distance(Flat(body.Position), Flat(spot));
                if (far > TeleportDistance) body.Teleport(spot);
                else if (far > 0.35f) body.MoveToward(spot, far > 3f ? RunSpeed : WalkSpeed, Time.deltaTime, 0.2f);
                else body.Stop(owner);
                body.Hover();
            }
        foreach (int id in gone) { theirs[id]?.Destroy(); theirs.Remove(id); retryAt.Remove(id); }
    }

    // ---- name tags ----

    public static void DrawLabels()
    {
        var cam = Camera.main;
        if (!cam) return;
        if (mine != null && mine.Alive) Label(cam, mine.Position, $"{Name}  Lv{Level}", xp.Value / (float)XpNeeded);
        if (Plugin != null)
            foreach (var p in Plugin.Players)
                if (theirs.TryGetValue(p.Id, out var body) && body != null && body.Alive)
                    Label(cam, body.Position, $"{p.PetName}  Lv{p.PetLevel}", -1f);
    }

    static void Label(Camera cam, Vector3 at, string text, float xpFrac)
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

    public static void ForgetAll()
    {
        foreach (var b in theirs.Values) b?.Destroy();
        theirs.Clear();
    }
}

/// <summary>A creature's body without its brain: the animator and sprites, moved by us. Position = its spot on the ground.</summary>
internal sealed class PetBody
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
    public Vector3 Position => root.transform.position;

    /// <summary>Can we make this kind (we have its template, or the game has the creature loaded now)?</summary>
    public static bool Available(PetKind kind) => templates.TryGetValue(kind.Prefab, out var t) && t.Go || Source(kind);

    /// <summary>The creature as the game has it: its prefab if loaded, else one in the scene.</summary>
    static Enemy Source(PetKind kind)
    {
        Enemy found = null;
        foreach (var e in Resources.FindObjectsOfTypeAll<Enemy>())
        {
            if (!e) continue;
            string n = e.gameObject.name;
            if (n != kind.Prefab && !n.StartsWith(kind.Prefab + " ") && !n.StartsWith(kind.Prefab + "(")) continue;
            found = e;
            if (!e.gameObject.scene.IsValid()) break;   // a prefab: best
        }
        return found;
    }

    /// <summary>Make this kind's template now if the game has the creature loaded. True = newly learned.</summary>
    public static bool Learn(PetKind kind)
    {
        if (templates.TryGetValue(kind.Prefab, out var t) && t.Go) return false;
        return TemplateFor(kind) != null;
    }

    /// <summary>Strip the creature into our template for this kind (once; kept for good).</summary>
    static Template TemplateFor(PetKind kind)
    {
        if (templates.TryGetValue(kind.Prefab, out var t) && t.Go) return t;
        var source = Source(kind);
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
        templates[kind.Prefab] = t = new Template { Go = copy, GraphicsPath = path };
        return t;
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
        float step = Mathf.Min(speed * dt, dist - stopAt);
        var dir = d / dist;
        root.transform.position = from + new Vector3(dir.x, dir.y, 0f) * step;
        Face(dir);
        Set("Speed", speed);
    }

    public void Stop(Vector3 lookAt)
    {
        var from = root.transform.position;
        var d = new UnityEngine.Vector2(lookAt.x - from.x, lookAt.y - from.y);
        if (d.sqrMagnitude > 0.01f) Face(d.normalized);
        Set("Speed", 0f);
    }

    public void Teleport(Vector3 to) => root.transform.position = to;

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
}
